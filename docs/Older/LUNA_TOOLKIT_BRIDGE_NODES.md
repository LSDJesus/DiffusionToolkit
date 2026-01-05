# Luna Toolkit Bridge Nodes

## Overview

Custom ComfyUI nodes designed to bridge DiffusionToolkit and ComfyUI, enabling seamless image retrieval, generation tracking, and round-trip workflows.

These nodes communicate with DiffusionToolkit's REST API (when DT is running with API server enabled).

---

## Node Summary

| Node | Purpose | Category |
|------|---------|----------|
| **Luna DT Image Loader** | Load images directly from DT database by ID or query | Luna/Toolkit |
| **Luna DT Similar Search** | Find similar images using 5-layer embeddings | Luna/Toolkit |
| **Luna DT Cluster Sampler** | Sample images from a character cluster | Luna/Toolkit |
| **Luna DT Caption Fetcher** | Get captions/tags from database | Luna/Toolkit |
| **Luna DT Metadata Writer** | Save generation metadata back to DT | Luna/Toolkit |
| **Luna DT Batch Queue** | Queue multiple operations with DT tracking | Luna/Toolkit |
| **Luna DT Prompt Injector** | Load and inject prompt from DT image | Luna/Toolkit |
| **Luna DT ControlNet Cache** | Fetch cached ControlNet preprocessings | Luna/Toolkit |

---

## Implementation

### luna_toolkit_bridge.py

```python
"""
Luna Toolkit Bridge Nodes
Communication layer between ComfyUI and DiffusionToolkit

Requires DiffusionToolkit API server running (default: http://localhost:5000)
"""

import os
import json
import requests
import numpy as np
from PIL import Image
import torch
import io
import base64
from typing import Optional, List, Tuple, Dict, Any

import folder_paths

# Configuration
DT_API_URL = os.environ.get("DT_API_URL", "http://localhost:5000")
DT_API_TIMEOUT = 30


class DTApiClient:
    """Client for DiffusionToolkit REST API"""
    
    def __init__(self, base_url: str = DT_API_URL):
        self.base_url = base_url.rstrip("/")
        self._session = requests.Session()
    
    def _request(self, method: str, endpoint: str, **kwargs) -> dict:
        """Make API request with error handling"""
        url = f"{self.base_url}{endpoint}"
        kwargs.setdefault("timeout", DT_API_TIMEOUT)
        
        try:
            response = self._session.request(method, url, **kwargs)
            response.raise_for_status()
            return response.json()
        except requests.ConnectionError:
            raise ConnectionError(
                f"Cannot connect to DiffusionToolkit at {self.base_url}. "
                "Ensure DT is running with API server enabled."
            )
        except requests.HTTPError as e:
            raise RuntimeError(f"DT API error: {e.response.text}")
    
    def get_image(self, image_id: int) -> dict:
        """Get image data and metadata by ID"""
        return self._request("GET", f"/api/images/{image_id}")
    
    def search_similar(
        self,
        query_type: str,
        embedding: Optional[List[float]] = None,
        image_id: Optional[int] = None,
        text_query: Optional[str] = None,
        top_k: int = 20,
        min_similarity: float = 0.5
    ) -> List[dict]:
        """Search for similar images using embeddings"""
        payload = {
            "query_type": query_type,
            "top_k": top_k,
            "min_similarity": min_similarity
        }
        if embedding:
            payload["embedding"] = embedding
        if image_id:
            payload["image_id"] = image_id
        if text_query:
            payload["text_query"] = text_query
        
        return self._request("POST", "/api/images/similar", json=payload)
    
    def get_cluster_images(
        self,
        cluster_id: int,
        limit: int = 50,
        min_quality: Optional[int] = None,
        sort: str = "confidence"
    ) -> List[dict]:
        """Get images from a character cluster"""
        params = {"limit": limit, "sort": sort}
        if min_quality:
            params["min_quality"] = min_quality
        return self._request("GET", f"/api/clusters/{cluster_id}/images", params=params)
    
    def get_clusters(self) -> List[dict]:
        """Get all character clusters"""
        return self._request("GET", "/api/clusters")
    
    def get_caption(self, image_id: int) -> dict:
        """Get captions/tags for an image"""
        return self._request("GET", f"/api/images/{image_id}/caption")
    
    def save_generation(self, data: dict) -> dict:
        """Save generation metadata back to DT"""
        return self._request("POST", "/api/generations", json=data)
    
    def get_controlnet_cache(self, image_id: int, cn_type: str) -> dict:
        """Get cached ControlNet preprocessing"""
        return self._request("GET", f"/api/images/{image_id}/controlnet/{cn_type}")
    
    def check_connection(self) -> bool:
        """Check if DT API is available"""
        try:
            self._request("GET", "/api/health")
            return True
        except:
            return False


# Singleton client instance
_dt_client: Optional[DTApiClient] = None

def get_dt_client() -> DTApiClient:
    global _dt_client
    if _dt_client is None:
        _dt_client = DTApiClient()
    return _dt_client


def image_from_base64(b64_string: str) -> torch.Tensor:
    """Convert base64 image to ComfyUI tensor format"""
    image_data = base64.b64decode(b64_string)
    image = Image.open(io.BytesIO(image_data))
    image = image.convert("RGB")
    
    # Convert to tensor [B, H, W, C] format
    np_image = np.array(image).astype(np.float32) / 255.0
    tensor = torch.from_numpy(np_image).unsqueeze(0)
    return tensor


def image_to_base64(tensor: torch.Tensor) -> str:
    """Convert ComfyUI tensor to base64 string"""
    # Handle batch dimension
    if tensor.ndim == 4:
        tensor = tensor[0]
    
    np_image = (tensor.cpu().numpy() * 255).astype(np.uint8)
    image = Image.fromarray(np_image)
    
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return base64.b64encode(buffer.getvalue()).decode()


# ============================================================================
# NODE: Luna DT Image Loader
# ============================================================================

class LunaDTImageLoader:
    """
    Load an image directly from DiffusionToolkit database.
    
    Returns the image along with its metadata (prompt, settings, etc.)
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("IMAGE", "STRING", "STRING", "INT", "INT", "METADATA")
    RETURN_NAMES = ("image", "prompt", "negative", "width", "height", "metadata")
    FUNCTION = "load_image"
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT", {
                    "default": 0,
                    "min": 0,
                    "tooltip": "Database ID of the image to load"
                }),
            },
            "optional": {
                "api_url": ("STRING", {
                    "default": DT_API_URL,
                    "tooltip": "DiffusionToolkit API URL"
                }),
            }
        }
    
    def load_image(self, image_id: int, api_url: str = DT_API_URL):
        client = DTApiClient(api_url)
        
        try:
            data = client.get_image(image_id)
        except Exception as e:
            raise RuntimeError(f"Failed to load image {image_id}: {e}")
        
        # Load image from path or base64
        if "image_base64" in data:
            image = image_from_base64(data["image_base64"])
        elif "path" in data:
            img = Image.open(data["path"]).convert("RGB")
            np_img = np.array(img).astype(np.float32) / 255.0
            image = torch.from_numpy(np_img).unsqueeze(0)
        else:
            raise RuntimeError(f"No image data for ID {image_id}")
        
        prompt = data.get("prompt", "")
        negative = data.get("negative_prompt", "")
        width = data.get("width", image.shape[2])
        height = data.get("height", image.shape[1])
        
        # Build metadata dict
        metadata = {
            "source": "diffusion_toolkit",
            "image_id": image_id,
            "prompt": prompt,
            "negative_prompt": negative,
            "width": width,
            "height": height,
            "model": data.get("model"),
            "sampler": data.get("sampler"),
            "steps": data.get("steps"),
            "cfg": data.get("cfg"),
            "seed": data.get("seed"),
            "rating": data.get("rating"),
            "tags": data.get("tags", []),
        }
        
        return (image, prompt, negative, width, height, metadata)


# ============================================================================
# NODE: Luna DT Similar Search
# ============================================================================

class LunaDTSimilarSearch:
    """
    Find similar images in DiffusionToolkit using 5-layer embeddings.
    
    Search modes:
    - visual: CLIP-H image similarity
    - semantic: BGE prompt semantic similarity  
    - composition: CLIP-G composition/style
    - style: CLIP-L style matching
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("IMAGE", "STRING", "INT", "METADATA")
    RETURN_NAMES = ("images", "prompts", "count", "results_metadata")
    FUNCTION = "search_similar"
    OUTPUT_IS_LIST = (True, True, False, False)
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "search_mode": (["visual", "semantic", "composition", "style"], {
                    "default": "visual",
                    "tooltip": "Which embedding layer to use for similarity"
                }),
                "top_k": ("INT", {
                    "default": 10,
                    "min": 1,
                    "max": 100,
                    "tooltip": "Number of results to return"
                }),
                "min_similarity": ("FLOAT", {
                    "default": 0.5,
                    "min": 0.0,
                    "max": 1.0,
                    "step": 0.05,
                    "tooltip": "Minimum similarity threshold"
                }),
            },
            "optional": {
                "reference_image": ("IMAGE", {
                    "tooltip": "Image to find similar matches for"
                }),
                "reference_image_id": ("INT", {
                    "default": 0,
                    "tooltip": "DT database ID to find similar matches for"
                }),
                "text_query": ("STRING", {
                    "default": "",
                    "multiline": True,
                    "tooltip": "Text query for semantic search"
                }),
                "exclude_source": ("BOOLEAN", {
                    "default": True,
                    "tooltip": "Exclude the source image from results"
                }),
            }
        }
    
    def search_similar(
        self,
        search_mode: str,
        top_k: int,
        min_similarity: float,
        reference_image: Optional[torch.Tensor] = None,
        reference_image_id: int = 0,
        text_query: str = "",
        exclude_source: bool = True
    ):
        client = get_dt_client()
        
        # Build search request
        kwargs = {
            "query_type": search_mode,
            "top_k": top_k,
            "min_similarity": min_similarity
        }
        
        if reference_image_id > 0:
            kwargs["image_id"] = reference_image_id
        elif reference_image is not None:
            # TODO: Encode image to embedding and send
            # For now, save temp and let DT process
            pass
        elif text_query:
            kwargs["text_query"] = text_query
        else:
            raise ValueError("Must provide reference_image, reference_image_id, or text_query")
        
        try:
            results = client.search_similar(**kwargs)
        except Exception as e:
            raise RuntimeError(f"Search failed: {e}")
        
        # Filter out source if requested
        if exclude_source and reference_image_id > 0:
            results = [r for r in results if r["id"] != reference_image_id]
        
        # Load images and extract data
        images = []
        prompts = []
        
        for result in results[:top_k]:
            try:
                img_data = client.get_image(result["id"])
                
                if "path" in img_data and os.path.exists(img_data["path"]):
                    img = Image.open(img_data["path"]).convert("RGB")
                    np_img = np.array(img).astype(np.float32) / 255.0
                    tensor = torch.from_numpy(np_img).unsqueeze(0)
                    images.append(tensor)
                    prompts.append(img_data.get("prompt", ""))
            except:
                continue
        
        metadata = {
            "search_mode": search_mode,
            "result_count": len(images),
            "results": results[:top_k]
        }
        
        return (images, prompts, len(images), metadata)


# ============================================================================
# NODE: Luna DT Cluster Sampler
# ============================================================================

class LunaDTClusterSampler:
    """
    Sample images from a DiffusionToolkit character cluster.
    
    Useful for:
    - Getting reference images for a specific character
    - Building training datasets
    - IP-Adapter conditioning
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("IMAGE", "STRING", "INT")
    RETURN_NAMES = ("images", "prompts", "count")
    FUNCTION = "sample_cluster"
    OUTPUT_IS_LIST = (True, True, False)
    
    @classmethod
    def INPUT_TYPES(cls):
        # Try to get cluster list from DT
        try:
            client = get_dt_client()
            clusters = client.get_clusters()
            cluster_names = [f"{c['id']}: {c['name']} ({c['image_count']})" for c in clusters]
        except:
            cluster_names = ["(DT not connected)"]
        
        return {
            "required": {
                "cluster": (cluster_names, {
                    "tooltip": "Character cluster to sample from"
                }),
                "sample_count": ("INT", {
                    "default": 5,
                    "min": 1,
                    "max": 50,
                    "tooltip": "Number of images to sample"
                }),
                "min_quality": ("INT", {
                    "default": 3,
                    "min": 0,
                    "max": 5,
                    "tooltip": "Minimum quality rating (0-5)"
                }),
                "sort_by": (["confidence", "quality", "random"], {
                    "default": "quality",
                    "tooltip": "How to sort/select images"
                }),
            }
        }
    
    def sample_cluster(
        self,
        cluster: str,
        sample_count: int,
        min_quality: int,
        sort_by: str
    ):
        # Parse cluster ID from selection
        try:
            cluster_id = int(cluster.split(":")[0])
        except:
            raise ValueError(f"Invalid cluster selection: {cluster}")
        
        client = get_dt_client()
        
        try:
            results = client.get_cluster_images(
                cluster_id,
                limit=sample_count,
                min_quality=min_quality if min_quality > 0 else None,
                sort=sort_by
            )
        except Exception as e:
            raise RuntimeError(f"Failed to get cluster images: {e}")
        
        images = []
        prompts = []
        
        for result in results:
            try:
                if "path" in result and os.path.exists(result["path"]):
                    img = Image.open(result["path"]).convert("RGB")
                    np_img = np.array(img).astype(np.float32) / 255.0
                    tensor = torch.from_numpy(np_img).unsqueeze(0)
                    images.append(tensor)
                    prompts.append(result.get("prompt", ""))
            except:
                continue
        
        return (images, prompts, len(images))


# ============================================================================
# NODE: Luna DT Caption Fetcher
# ============================================================================

class LunaDTCaptionFetcher:
    """
    Fetch captions and tags from DiffusionToolkit for an image.
    
    Returns multiple caption types:
    - WD14 tags
    - JoyTag tags
    - JoyCaption natural language
    - Combined/deduplicated tags
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("STRING", "STRING", "STRING", "STRING")
    RETURN_NAMES = ("wd14_tags", "joytag_tags", "joycaption", "combined")
    FUNCTION = "fetch_caption"
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT", {
                    "default": 0,
                    "min": 0,
                    "tooltip": "Database ID of the image"
                }),
            },
            "optional": {
                "prepend_trigger": ("STRING", {
                    "default": "",
                    "tooltip": "Trigger word to prepend to captions"
                }),
            }
        }
    
    def fetch_caption(self, image_id: int, prepend_trigger: str = ""):
        client = get_dt_client()
        
        try:
            data = client.get_caption(image_id)
        except Exception as e:
            raise RuntimeError(f"Failed to get caption for {image_id}: {e}")
        
        wd14 = data.get("wd14_tags", "")
        joytag = data.get("joytag_tags", "")
        joycaption = data.get("joycaption", "")
        combined = data.get("combined", wd14)
        
        # Prepend trigger if specified
        if prepend_trigger:
            if wd14:
                wd14 = f"{prepend_trigger}, {wd14}"
            if joytag:
                joytag = f"{prepend_trigger}, {joytag}"
            if joycaption:
                joycaption = f"{prepend_trigger}, {joycaption}"
            if combined:
                combined = f"{prepend_trigger}, {combined}"
        
        return (wd14, joytag, joycaption, combined)


# ============================================================================
# NODE: Luna DT Metadata Writer
# ============================================================================

class LunaDTMetadataWriter:
    """
    Write generation metadata back to DiffusionToolkit.
    
    Creates a record linking the generated image to its source and parameters.
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("image",)
    FUNCTION = "write_metadata"
    OUTPUT_NODE = True
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image": ("IMAGE",),
                "source_image_id": ("INT", {
                    "default": 0,
                    "tooltip": "DT ID of source image (for variations/upscales)"
                }),
                "operation": (["txt2img", "img2img", "upscale", "inpaint", "variation"], {
                    "default": "txt2img"
                }),
            },
            "optional": {
                "prompt": ("STRING", {"default": "", "multiline": True}),
                "negative": ("STRING", {"default": "", "multiline": True}),
                "model": ("STRING", {"default": ""}),
                "seed": ("INT", {"default": 0}),
                "steps": ("INT", {"default": 20}),
                "cfg": ("FLOAT", {"default": 7.0}),
                "denoise": ("FLOAT", {"default": 1.0}),
                "metadata": ("METADATA", {}),
            }
        }
    
    def write_metadata(
        self,
        image: torch.Tensor,
        source_image_id: int,
        operation: str,
        prompt: str = "",
        negative: str = "",
        model: str = "",
        seed: int = 0,
        steps: int = 20,
        cfg: float = 7.0,
        denoise: float = 1.0,
        metadata: Optional[dict] = None
    ):
        client = get_dt_client()
        
        # Build generation record
        record = {
            "source_image_id": source_image_id if source_image_id > 0 else None,
            "operation": operation,
            "prompt": prompt,
            "negative_prompt": negative,
            "model": model,
            "seed": seed,
            "steps": steps,
            "cfg": cfg,
            "denoise": denoise,
            "width": image.shape[2],
            "height": image.shape[1],
        }
        
        # Merge additional metadata
        if metadata:
            record["extra_metadata"] = metadata
        
        try:
            client.save_generation(record)
        except Exception as e:
            print(f"[Luna DT] Warning: Failed to save metadata: {e}")
        
        return (image,)


# ============================================================================
# NODE: Luna DT Prompt Injector
# ============================================================================

class LunaDTPromptInjector:
    """
    Load prompt and settings from a DT image and output for use in workflow.
    
    Useful for recreating or varying existing images.
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("STRING", "STRING", "INT", "INT", "FLOAT", "INT", "STRING", "STRING")
    RETURN_NAMES = ("prompt", "negative", "width", "height", "cfg", "steps", "sampler", "model")
    FUNCTION = "inject_prompt"
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT", {
                    "default": 0,
                    "min": 0,
                    "tooltip": "Database ID of source image"
                }),
            },
            "optional": {
                "prompt_override": ("STRING", {
                    "default": "",
                    "multiline": True,
                    "tooltip": "Override the stored prompt (leave empty to use original)"
                }),
                "negative_override": ("STRING", {
                    "default": "",
                    "multiline": True,
                    "tooltip": "Override the stored negative (leave empty to use original)"
                }),
            }
        }
    
    def inject_prompt(
        self,
        image_id: int,
        prompt_override: str = "",
        negative_override: str = ""
    ):
        client = get_dt_client()
        
        try:
            data = client.get_image(image_id)
        except Exception as e:
            raise RuntimeError(f"Failed to load image {image_id}: {e}")
        
        prompt = prompt_override if prompt_override else data.get("prompt", "")
        negative = negative_override if negative_override else data.get("negative_prompt", "")
        width = data.get("width", 1024)
        height = data.get("height", 1024)
        cfg = data.get("cfg", 7.0)
        steps = data.get("steps", 20)
        sampler = data.get("sampler", "euler")
        model = data.get("model", "")
        
        return (prompt, negative, width, height, cfg, steps, sampler, model)


# ============================================================================
# NODE: Luna DT ControlNet Cache
# ============================================================================

class LunaDTControlNetCache:
    """
    Fetch cached ControlNet preprocessings from DiffusionToolkit.
    
    If the preprocessing doesn't exist, it can be generated on-demand.
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("IMAGE", "BOOLEAN")
    RETURN_NAMES = ("controlnet_image", "was_cached")
    FUNCTION = "get_controlnet"
    
    CN_TYPES = [
        "openpose", "openpose_face", "openpose_hand", "openpose_full",
        "canny", "depth_midas", "depth_zoe",
        "normal_bae", "lineart", "lineart_anime",
        "softedge", "scribble", "segmentation"
    ]
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT", {
                    "default": 0,
                    "min": 0,
                    "tooltip": "Database ID of source image"
                }),
                "controlnet_type": (cls.CN_TYPES, {
                    "default": "openpose",
                    "tooltip": "Type of ControlNet preprocessing"
                }),
            },
            "optional": {
                "generate_if_missing": ("BOOLEAN", {
                    "default": True,
                    "tooltip": "Generate preprocessing if not cached"
                }),
            }
        }
    
    def get_controlnet(
        self,
        image_id: int,
        controlnet_type: str,
        generate_if_missing: bool = True
    ):
        client = get_dt_client()
        
        try:
            data = client.get_controlnet_cache(image_id, controlnet_type)
            
            if "image_base64" in data:
                image = image_from_base64(data["image_base64"])
                return (image, data.get("was_cached", True))
            elif "path" in data and os.path.exists(data["path"]):
                img = Image.open(data["path"]).convert("RGB")
                np_img = np.array(img).astype(np.float32) / 255.0
                tensor = torch.from_numpy(np_img).unsqueeze(0)
                return (tensor, data.get("was_cached", True))
            else:
                raise RuntimeError("No controlnet data returned")
                
        except Exception as e:
            if not generate_if_missing:
                raise RuntimeError(f"ControlNet cache not found: {e}")
            
            # TODO: Generate on the fly using ComfyUI preprocessors
            raise NotImplementedError(
                f"On-demand ControlNet generation not yet implemented. "
                f"Pre-generate in DiffusionToolkit first."
            )


# ============================================================================
# NODE: Luna DT Connection Status
# ============================================================================

class LunaDTConnectionStatus:
    """
    Check connection status to DiffusionToolkit API.
    
    Useful as a workflow validation node.
    """
    
    CATEGORY = "Luna/Toolkit"
    RETURN_TYPES = ("BOOLEAN", "STRING")
    RETURN_NAMES = ("connected", "status_message")
    FUNCTION = "check_connection"
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {},
            "optional": {
                "api_url": ("STRING", {
                    "default": DT_API_URL,
                    "tooltip": "DiffusionToolkit API URL to check"
                }),
            }
        }
    
    def check_connection(self, api_url: str = DT_API_URL):
        client = DTApiClient(api_url)
        
        try:
            connected = client.check_connection()
            if connected:
                return (True, f"Connected to DiffusionToolkit at {api_url}")
            else:
                return (False, f"DiffusionToolkit not responding at {api_url}")
        except Exception as e:
            return (False, f"Connection error: {e}")


# ============================================================================
# Node Registration
# ============================================================================

NODE_CLASS_MAPPINGS = {
    "LunaDTImageLoader": LunaDTImageLoader,
    "LunaDTSimilarSearch": LunaDTSimilarSearch,
    "LunaDTClusterSampler": LunaDTClusterSampler,
    "LunaDTCaptionFetcher": LunaDTCaptionFetcher,
    "LunaDTMetadataWriter": LunaDTMetadataWriter,
    "LunaDTPromptInjector": LunaDTPromptInjector,
    "LunaDTControlNetCache": LunaDTControlNetCache,
    "LunaDTConnectionStatus": LunaDTConnectionStatus,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "LunaDTImageLoader": "Luna DT Image Loader",
    "LunaDTSimilarSearch": "Luna DT Similar Search",
    "LunaDTClusterSampler": "Luna DT Cluster Sampler",
    "LunaDTCaptionFetcher": "Luna DT Caption Fetcher",
    "LunaDTMetadataWriter": "Luna DT Metadata Writer",
    "LunaDTPromptInjector": "Luna DT Prompt Injector",
    "LunaDTControlNetCache": "Luna DT ControlNet Cache",
    "LunaDTConnectionStatus": "Luna DT Connection Status",
}
```

---

## DiffusionToolkit API Endpoints Required

For these nodes to work, DT needs to expose these REST endpoints:

### Core Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/health` | GET | Connection check |
| `/api/images/{id}` | GET | Get image + metadata |
| `/api/images/similar` | POST | Embedding similarity search |
| `/api/clusters` | GET | List character clusters |
| `/api/clusters/{id}/images` | GET | Get images from cluster |
| `/api/images/{id}/caption` | GET | Get captions/tags |
| `/api/images/{id}/controlnet/{type}` | GET | Get cached ControlNet |
| `/api/generations` | POST | Save generation metadata |

### Example API Server (C# Minimal API)

```csharp
// In DiffusionToolkit startup
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Enable CORS for ComfyUI
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

// Health check
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Get image by ID
app.MapGet("/api/images/{id}", async (long id, IImageDataStore dataStore) =>
{
    var image = await dataStore.GetImageAsync(id);
    if (image == null) return Results.NotFound();
    
    return Results.Ok(new
    {
        id = image.Id,
        path = image.Path,
        prompt = image.Prompt,
        negative_prompt = image.NegativePrompt,
        width = image.Width,
        height = image.Height,
        model = image.ModelName,
        sampler = image.Sampler,
        steps = image.Steps,
        cfg = image.CfgScale,
        seed = image.Seed,
        rating = image.Rating
    });
});

// Similar search
app.MapPost("/api/images/similar", async (SimilarSearchRequest request, IEmbeddingService embeddings) =>
{
    var results = await embeddings.SearchSimilarAsync(
        request.QueryType,
        request.Embedding,
        request.ImageId,
        request.TextQuery,
        request.TopK,
        request.MinSimilarity);
    
    return Results.Ok(results);
});

// ... additional endpoints

app.Run();
```

---

## Usage Examples

### Example 1: Regenerate with Different Model

```
[Luna DT Image Loader] (image_id=12345)
        │
        ├──► prompt ──► [CLIP Encode]
        ├──► negative ──► [CLIP Encode]
        ├──► width/height ──► [Empty Latent]
        │
        └──► [KSampler] (different checkpoint)
                │
                └──► [Luna DT Metadata Writer] (source=12345, op="variation")
```

### Example 2: Character Reference from Cluster

```
[Luna DT Cluster Sampler] (cluster="Luna", count=3, quality=4)
        │
        └──► images ──► [IP-Adapter]
                            │
                            └──► [KSampler] (new generation with character consistency)
```

### Example 3: Find Similar for Training Data

```
[Luna DT Image Loader] (best image from training set)
        │
        └──► [Luna DT Similar Search] (visual, top_k=50, min_sim=0.7)
                    │
                    └──► Review candidates for training dataset
```

---

## Benefits

1. **No file management** - Query database directly, no export/import
2. **Smart search** - 5-layer embeddings for different similarity types
3. **Caption reuse** - JoyTag/WDv3/JoyCaption already computed
4. **Metadata tracking** - Full generation lineage in database
5. **Character awareness** - Cluster-based image selection
6. **ControlNet caching** - Avoid redundant preprocessing
