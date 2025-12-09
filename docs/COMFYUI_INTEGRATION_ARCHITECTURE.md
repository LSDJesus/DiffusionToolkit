# ComfyUI Integration Architecture

## Overview

This document describes the deep integration between DiffusionToolkit and ComfyUI, enabling a seamless workflow for image discovery, refinement, replication, and enhancement. The integration leverages DiffusionToolkit's 5-layer embedding system and PostgreSQL database to provide intelligent image operations that no standalone ComfyUI setup can achieve.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [5-Layer Embedding System](#5-layer-embedding-system)
3. [Luna Node Pack Integration](#luna-node-pack-integration)
4. [DiffusionToolkit API Endpoints](#diffusiontoolkit-api-endpoints)
5. [Suggested Custom Nodes](#suggested-custom-nodes)
6. [Feature Implementations](#feature-implementations)
7. [Data Flow Diagrams](#data-flow-diagrams)

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         DiffusionToolkit Ecosystem                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                    DiffusionToolkit (WPF Application)                   │ │
│  │  ├── Image Browser & Search                                            │ │
│  │  ├── Embedding Generation (BGE, CLIP-L, CLIP-G, CLIP-H)               │ │
│  │  ├── Character Discovery & Clustering                                  │ │
│  │  ├── Prompt Engine / Wildcard Editor                                   │ │
│  │  ├── Expression Pack Creator                                           │ │
│  │  └── REST API Server (for ComfyUI nodes)                               │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                    │                                         │
│                          REST API / WebSocket                                │
│                                    │                                         │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                         Background Services                             │ │
│  │  ├── Diffusion.Watcher (File indexing, metadata extraction)           │ │
│  │  ├── Luna Daemon (Shared VAE/CLIP on dedicated GPU)                   │ │
│  │  └── Face Detection Service (YOLO + CLIP-H embeddings)                │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                    │                                         │
│                            PostgreSQL + pgvector                             │
│                                    │                                         │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                    ComfyUI + Luna Node Pack                            │ │
│  │  ├── LunaToolkitLoader (fetch images/data from DiffusionToolkit)      │ │
│  │  ├── LunaConfigGateway (central parameter hub)                        │ │
│  │  ├── LunaPromptCraft (intelligent wildcard resolution)                │ │
│  │  ├── Luna Upscalers (Simple, Advanced, Ultimate SD)                   │ │
│  │  ├── LunaMultiSaver (metadata-aware saving)                           │ │
│  │  └── Luna Daemon nodes (shared VRAM)                                  │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Core Principles

1. **DiffusionToolkit is the brain** - All image metadata, embeddings, and discovery live here
2. **ComfyUI is the muscle** - All image generation, refinement, and processing happens here
3. **Luna Daemon is shared memory** - VAE/CLIP models loaded once, used by all instances
4. **PostgreSQL is the memory** - Persistent, queryable, vector-searchable storage

---

## 5-Layer Embedding System

DiffusionToolkit generates 5 distinct embeddings for each image, enabling different types of similarity search and matching:

### Layer Architecture

| Layer | Model | Dimensions | Purpose | Use Case |
|-------|-------|------------|---------|----------|
| **1. BGE Prompt** | BGE-large-en-v1.5 | 1024 | Semantic text understanding | "Find images with similar concepts" |
| **2. BGE Negative** | BGE-large-en-v1.5 | 1024 | Negative prompt matching | "Find images avoiding similar things" |
| **3. CLIP-L** | CLIP ViT-L/14 | 768 | SDXL text encoder (first) | "Match SDXL prompt interpretation" |
| **4. CLIP-G** | CLIP ViT-bigG/14 | 1280 | SDXL text encoder (second) | "Match SDXL composition/style" |
| **5. CLIP-H** | CLIP ViT-H/14 | 1280 | Visual similarity | "Find visually similar images" |

### How Each Layer Enhances ComfyUI Workflows

#### Layer 1: BGE Prompt Embedding (Semantic Search)
```
User Query: "cyberpunk girl with neon lights"
     │
     ▼
BGE encodes query → Search prompt_embedding column
     │
     ▼
Returns: Images whose prompts are semantically similar
         (even if they used different words like "sci-fi woman, glowing signs")
```

**ComfyUI Integration:**
- "Find reference images for this concept"
- "Show me successful prompts similar to what I'm trying"
- Feed results into IP-Adapter or ControlNet

#### Layer 2: BGE Negative Embedding
```
Your Negative: "blurry, deformed, watermark"
     │
     ▼
Search images that successfully avoided these issues
     │
     ▼
Returns: High-quality images with effective negative prompts
```

**ComfyUI Integration:**
- "What negative prompts work well for this style?"
- Auto-suggest negatives based on similar successful images

#### Layer 3: CLIP-L Embedding (SDXL Text Encoder 1)
```
Your Prompt → CLIP-L encoding → Match against stored CLIP-L embeddings
     │
     ▼
Find images where SDXL's first text encoder interpreted the prompt similarly
```

**ComfyUI Integration:**
- Style matching: "Find images with similar SDXL style interpretation"
- Debug: "Why does my prompt look different than expected?"
- Cross-reference: "What other prompts produce this CLIP-L pattern?"

#### Layer 4: CLIP-G Embedding (SDXL Text Encoder 2)
```
Your Prompt → CLIP-G encoding → Match against stored CLIP-G embeddings
     │
     ▼
Find images with similar composition, color palette, overall aesthetic
```

**ComfyUI Integration:**
- Composition matching: "Find images with similar layout/structure"
- Color palette discovery: "Images with this color mood"
- SDXL-specific tuning: "How CLIP-G interprets my concepts"

#### Layer 5: CLIP-H Visual Embedding (Image Similarity)
```
Your Image → CLIP-H encoding → Match against stored CLIP-H embeddings
     │
     ▼
Find visually similar images regardless of how they were prompted
```

**ComfyUI Integration:**
- "Find more images that look like this"
- Character discovery: "All images of this face/character"
- Style transfer: "Images with this visual style for IP-Adapter"
- Training data curation: "Similar images for LoRA dataset"

### Cross-Layer Queries

The power comes from combining layers:

```sql
-- "Find images that look similar AND were prompted similarly"
SELECT * FROM images
WHERE 
    -- Visual similarity (CLIP-H)
    image_embedding <=> $visual_query < 0.3
    AND
    -- Semantic similarity (BGE)
    prompt_embedding <=> $semantic_query < 0.4
ORDER BY 
    (image_embedding <=> $visual_query) + 
    (prompt_embedding <=> $semantic_query)
LIMIT 20;
```

```sql
-- "Find images with this composition but different subject"
SELECT * FROM images
WHERE
    -- Similar composition (CLIP-G)
    clip_g_embedding <=> $composition_query < 0.2
    AND
    -- Different semantics (BGE)
    prompt_embedding <=> $subject_query > 0.5
```

---

## Luna Node Pack Integration

### Existing Nodes & Their Role

#### LunaConfigGateway
**Role:** Central parameter hub that outputs complete metadata

```
Inputs:
├── model, clip, vae
├── positive/negative prompts
├── width, height, batch_size
├── seed, steps, cfg, denoise
├── clip_skip, sampler, scheduler
└── lora_stack (optional)

Outputs:
├── model, clip, vae (LoRA-modified)
├── positive, negative (encoded)
├── latent (empty, sized)
├── All input values (passthrough)
├── lora_stack (merged)
└── metadata ← THIS IS KEY
```

**Integration:** DiffusionToolkit intercepts the `metadata` output to:
- Store complete generation parameters with saved images
- Enable perfect replication of any image
- Build analytics on what settings produce highly-rated results

#### LunaBatchPromptExtractor / Loader
**Role:** Bridge between DiffusionToolkit's database and ComfyUI

**Current:** Reads/writes JSON files

**Enhanced:** Direct database integration
```
DiffusionToolkit exports selected images → JSON format
LunaBatchPromptLoader reads → Iterates through for batch generation
```

#### LunaPromptCraft
**Role:** Intelligent wildcard resolution with LoRA linking

**Integration with DiffusionToolkit:**
- Sync YAML wildcard files via database (versioned)
- LoRA usage analytics feed back into connection suggestions
- Error detection shown in both UIs

#### Luna Upscalers
**Role:** Image enhancement pipeline

**Integration:**
- DiffusionToolkit queues images for upscaling
- ComfyUI processes via Luna Ultimate SD Upscale
- Results saved back with upscale metadata

#### LunaMultiSaver
**Role:** Save images with complete metadata

**Integration:**
- Receives `metadata` from LunaConfigGateway
- Embeds in PNG/WebP
- DiffusionToolkit's Watcher indexes automatically
- Full round-trip: generate → save → index → searchable

#### Luna Daemon
**Role:** Shared VAE/CLIP across instances

**Integration:**
- Runs as Windows service alongside Diffusion.Watcher
- DiffusionToolkit's embedding generation uses same daemon
- Single VRAM allocation for VAE/CLIP, shared by all

---

## DiffusionToolkit API Endpoints

New REST API endpoints for ComfyUI custom nodes to query:

### Image Retrieval

#### `GET /api/images/similar`
Find similar images using any embedding layer.

```json
// Request
{
    "query_type": "visual" | "semantic" | "composition" | "style",
    "query_image": "base64 or path",  // for visual
    "query_text": "prompt text",       // for semantic
    "limit": 20,
    "min_rating": 3,                   // optional: only rated images
    "folder_filter": ["path/to/folder"], // optional
    "exclude_ids": [123, 456]          // optional: exclude specific images
}

// Response
{
    "images": [
        {
            "id": 789,
            "path": "D:/Images/image001.png",
            "thumbnail_base64": "...",
            "similarity": 0.92,
            "prompt": "original prompt...",
            "negative_prompt": "...",
            "metadata": { ... }
        }
    ]
}
```

#### `GET /api/images/{id}`
Get full image data and metadata.

```json
// Response
{
    "id": 789,
    "path": "D:/Images/image001.png",
    "image_base64": "...",  // full image
    "prompt": "...",
    "negative_prompt": "...",
    "width": 1024,
    "height": 1024,
    "seed": 12345,
    "steps": 20,
    "cfg": 7.0,
    "sampler": "euler",
    "scheduler": "normal",
    "model": "illustrious_v1",
    "loras": [
        {"name": "style_lora", "weight": 0.8}
    ],
    "rating": 5,
    "tags": ["favorite", "portfolio"],
    "embeddings": {
        "prompt_bge": [...],      // 1024D
        "clip_l": [...],          // 768D
        "clip_g": [...],          // 1280D
        "image_clip_h": [...]     // 1280D
    }
}
```

#### `GET /api/images/{id}/regions`
Get detected face/body regions for an image.

```json
// Response
{
    "image_id": 789,
    "regions": [
        {
            "id": 1001,
            "type": "face",
            "bbox": {"x": 100, "y": 50, "width": 200, "height": 200},
            "confidence": 0.95,
            "cluster_id": 47,
            "cluster_name": "Luna",
            "crop_base64": "..."  // cropped region
        }
    ]
}
```

### Character/Cluster Queries

#### `GET /api/clusters`
List all discovered character clusters.

```json
// Response
{
    "clusters": [
        {
            "id": 47,
            "name": "Luna",
            "auto_name": "Character_0047",
            "member_count": 847,
            "avg_confidence": 0.94,
            "centroid_thumbnail": "base64..."
        }
    ]
}
```

#### `GET /api/clusters/{id}/images`
Get all images containing a specific character.

```json
// Request params
?limit=50&offset=0&min_quality=3&sort=confidence

// Response
{
    "cluster_id": 47,
    "cluster_name": "Luna",
    "total_count": 847,
    "images": [
        {
            "id": 789,
            "path": "...",
            "thumbnail_base64": "...",
            "face_bbox": {...},
            "confidence": 0.98,
            "quality_rating": 5
        }
    ]
}
```

### Prompt & Wildcard

#### `GET /api/wildcards`
Get available wildcard categories.

```json
// Response
{
    "yaml_wildcards": [
        {
            "name": "body",
            "path": "body.yaml",
            "categories": ["hair", "eyes", "skin", "body_type"],
            "template_count": 15
        }
    ],
    "text_wildcards": [
        {"name": "colors", "path": "__colors__.txt", "entry_count": 50}
    ]
}
```

#### `POST /api/wildcards/resolve`
Resolve a wildcard template.

```json
// Request
{
    "template": "{body} wearing {clothing:casual}",
    "seed": 12345,
    "count": 10  // generate 10 variations
}

// Response
{
    "resolutions": [
        "1girl with long blonde hair wearing jeans and t-shirt",
        "1girl with short black hair wearing sundress",
        ...
    ],
    "lora_suggestions": [
        {"name": "casual_fashion", "weight": 0.6, "trigger": "casual style"}
    ]
}
```

### ControlNet Preprocessing

#### `GET /api/images/{id}/controlnet/{type}`
Get preprocessed ControlNet images.

```json
// Request
GET /api/images/789/controlnet/openpose

// Response
{
    "image_id": 789,
    "controlnet_type": "openpose",
    "preprocessed_base64": "...",
    "cached": true,
    "generated_at": "2025-12-03T10:30:00Z"
}

// Supported types:
// - openpose, openpose_face, openpose_hand, openpose_full
// - canny, depth_midas, depth_zoe
// - normal_bae, lineart, lineart_anime
// - softedge, scribble, segmentation
```

#### `POST /api/images/{id}/controlnet/batch`
Generate multiple ControlNet preprocessings.

```json
// Request
{
    "types": ["openpose", "depth_midas", "canny"],
    "cache": true
}

// Response
{
    "results": {
        "openpose": {"success": true, "base64": "..."},
        "depth_midas": {"success": true, "base64": "..."},
        "canny": {"success": true, "base64": "..."}
    }
}
```

### Caption & Tag Generation

#### `GET /api/images/{id}/caption`
Get or generate captions/tags.

```json
// Response
{
    "image_id": 789,
    "captions": {
        "sidecar_txt": "original sidecar content if exists",
        "wd14_tags": ["1girl", "long_hair", "blue_eyes", ...],
        "blip2_caption": "A woman with long blonde hair...",
        "joycaption": "Detailed natural language description..."
    },
    "generated_at": "2025-12-03T10:30:00Z"
}
```

#### `POST /api/images/{id}/caption/generate`
Generate new captions using specific models.

```json
// Request
{
    "models": ["wd14", "joycaption"],
    "wd14_threshold": 0.35,
    "joycaption_mode": "descriptive"
}
```

### Batch Operations

#### `POST /api/batch/export`
Export images for LoRA training or batch processing.

```json
// Request
{
    "image_ids": [789, 790, 791, ...],
    "format": "kohya" | "comfyui_batch" | "luna_loader",
    "include_captions": true,
    "caption_format": "txt_sidecar" | "json",
    "output_path": "D:/Training/character_luna"
}

// Response
{
    "success": true,
    "exported_count": 50,
    "output_path": "D:/Training/character_luna",
    "manifest": "D:/Training/character_luna/manifest.json"
}
```

#### `POST /api/batch/queue`
Queue images for ComfyUI processing.

```json
// Request
{
    "image_ids": [789, 790, 791],
    "workflow": "upscale_and_fix",
    "parameters": {
        "upscale_factor": 2.0,
        "denoise": 0.55,
        "face_fix": true
    }
}

// Response
{
    "queue_id": "batch_001",
    "queued_count": 3,
    "estimated_time": "5 minutes"
}
```

---

## Suggested Custom Nodes

Beyond your existing Luna nodes, these would complete the integration:

### 1. LunaToolkitImageLoader
**Purpose:** Load images directly from DiffusionToolkit database

```python
class LunaToolkitImageLoader:
    """
    Load image from DiffusionToolkit by ID, path, or similarity search
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "query_mode": (["by_id", "by_path", "similar_visual", "similar_prompt", "random_from_cluster"],),
            },
            "optional": {
                "image_id": ("INT", {"default": 0}),
                "image_path": ("STRING", {"default": ""}),
                "query_image": ("IMAGE",),
                "query_text": ("STRING", {"default": ""}),
                "cluster_id": ("INT", {"default": 0}),
                "min_rating": ("INT", {"default": 0, "min": 0, "max": 5}),
                "limit": ("INT", {"default": 1, "min": 1, "max": 100}),
            }
        }
    
    RETURN_TYPES = ("IMAGE", "STRING", "STRING", "INT", "TOOLKIT_METADATA")
    RETURN_NAMES = ("image", "prompt", "negative", "seed", "metadata")
    
    def load(self, query_mode, **kwargs):
        # Query DiffusionToolkit API
        response = requests.get(f"{TOOLKIT_API}/images/query", params={...})
        # Return image and metadata
```

**Use Cases:**
- "Load a random highly-rated image from character cluster 47"
- "Find an image visually similar to this one for reference"
- "Get the prompt that generated this image for modification"

### 2. LunaToolkitSimilaritySearch
**Purpose:** Multi-layer similarity search with UI preview

```python
class LunaToolkitSimilaritySearch:
    """
    Search DiffusionToolkit database using multiple embedding layers
    Returns grid of similar images for user selection
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "query_image": ("IMAGE",),
                "search_layers": (["visual_only", "semantic_only", "combined", "composition"],),
                "result_count": ("INT", {"default": 9, "min": 1, "max": 25}),
            },
            "optional": {
                "query_text": ("STRING", {"default": ""}),
                "weight_visual": ("FLOAT", {"default": 0.5, "min": 0, "max": 1}),
                "weight_semantic": ("FLOAT", {"default": 0.5, "min": 0, "max": 1}),
            }
        }
    
    RETURN_TYPES = ("IMAGE", "IMAGE", "STRING", "TOOLKIT_METADATA")
    RETURN_NAMES = ("selected_image", "all_results", "selected_prompt", "metadata")
```

### 3. LunaToolkitControlNetCache
**Purpose:** Fetch or generate cached ControlNet preprocessings

```python
class LunaToolkitControlNetCache:
    """
    Get ControlNet preprocessing from DiffusionToolkit cache
    Avoids redundant preprocessing for images already in database
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT",),
                "controlnet_type": ([
                    "openpose", "openpose_full", "depth_midas", "depth_zoe",
                    "canny", "lineart", "lineart_anime", "softedge", "normal_bae"
                ],),
                "generate_if_missing": ("BOOLEAN", {"default": True}),
            }
        }
    
    RETURN_TYPES = ("IMAGE", "BOOLEAN")
    RETURN_NAMES = ("controlnet_image", "was_cached")
```

**Benefits:**
- No redundant preprocessing for database images
- Instant ControlNet for any indexed image
- Background pre-generation for popular images

### 4. LunaToolkitCharacterSampler
**Purpose:** Sample images from a character cluster for training/reference

```python
class LunaToolkitCharacterSampler:
    """
    Get representative images from a character cluster
    For LoRA training, expression packs, or style reference
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "cluster_name_or_id": ("STRING", {"default": ""}),
                "sample_count": ("INT", {"default": 10, "min": 1, "max": 100}),
                "sample_strategy": (["random", "diverse", "highest_quality", "highest_confidence"],),
                "min_quality": ("INT", {"default": 3, "min": 1, "max": 5}),
            }
        }
    
    RETURN_TYPES = ("IMAGE", "STRING", "INT")
    RETURN_NAMES = ("images", "prompts", "count")
```

### 5. LunaToolkitCaptionFetcher
**Purpose:** Get captions/tags from database or generate on-demand

```python
class LunaToolkitCaptionFetcher:
    """
    Fetch or generate captions for images
    Supports multiple caption formats
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT",),
                "caption_type": (["sidecar", "wd14", "blip2", "joycaption", "combined"],),
            },
            "optional": {
                "generate_if_missing": ("BOOLEAN", {"default": True}),
                "wd14_threshold": ("FLOAT", {"default": 0.35}),
            }
        }
    
    RETURN_TYPES = ("STRING", "STRING")
    RETURN_NAMES = ("caption", "tags")
```

### 6. LunaToolkitBatchQueue
**Purpose:** Queue multiple images for processing with progress tracking

```python
class LunaToolkitBatchQueue:
    """
    Send batch of images to DiffusionToolkit queue
    For upscaling, face fixing, or other batch operations
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_ids": ("STRING", {"default": ""}),  # comma-separated or range
                "operation": (["upscale", "face_fix", "regenerate", "custom"],),
            },
            "optional": {
                "workflow_name": ("STRING", {"default": ""}),
                "parameters": ("STRING", {"default": "{}"}),  # JSON
            }
        }
    
    RETURN_TYPES = ("STRING", "INT")
    RETURN_NAMES = ("queue_id", "queued_count")
```

### 7. LunaToolkitMetadataWriter
**Purpose:** Write generation metadata back to DiffusionToolkit

```python
class LunaToolkitMetadataWriter:
    """
    Update image metadata in DiffusionToolkit after processing
    Tracks upscale history, refinement passes, etc.
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT",),
                "metadata": ("TOOLKIT_METADATA",),
                "operation_type": (["upscale", "face_fix", "inpaint", "regenerate"],),
            },
            "optional": {
                "new_image": ("IMAGE",),
                "save_as_new": ("BOOLEAN", {"default": False}),
            }
        }
```

### 8. LunaExpressionPackGenerator
**Purpose:** Generate character expression packs using cluster data

```python
class LunaExpressionPackGenerator:
    """
    Generate expression pack for a character cluster
    Uses reference images + ControlNet poses + face swapping
    """
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "cluster_id": ("INT",),
                "expression_set": (["standard_16", "extended_32", "custom"],),
                "model": ("MODEL",),
                "clip": ("CLIP",),
                "vae": ("VAE",),
            },
            "optional": {
                "custom_expressions": ("STRING", {"default": ""}),
                "face_swap_strength": ("FLOAT", {"default": 0.8}),
                "ip_adapter_weight": ("FLOAT", {"default": 0.7}),
                "output_format": (["sillytavern", "individual", "grid"],),
            }
        }
    
    RETURN_TYPES = ("IMAGE", "STRING")
    RETURN_NAMES = ("expression_pack", "manifest_json")
```

---

## Feature Implementations

### 1. Right-Click "Send to ComfyUI"

**DiffusionToolkit UI:**
```
Right-click image → "Send to ComfyUI" → Submenu:
├── "Upscale (2x)"
├── "Upscale (4x)"
├── "Fix Faces"
├── "Regenerate Similar"
├── "Use as ControlNet Reference"
├── "Use as IP-Adapter Reference"
├── "Add to Expression Pack Queue"
└── "Custom Workflow..."
```

**Implementation:**
1. DiffusionToolkit sends image ID + operation to local API
2. API constructs workflow JSON with appropriate Luna nodes
3. Workflow submitted to ComfyUI queue
4. Progress shown in DiffusionToolkit status bar
5. Result saved and indexed automatically

### 2. Character Discovery → Expression Pack

**Workflow:**
```
Character cluster selected in DiffusionToolkit
     │
     ▼
User clicks "Create Expression Pack"
     │
     ▼
Dialog: Select expressions, output format, settings
     │
     ▼
DiffusionToolkit fetches best reference images from cluster
     │
     ▼
Sends to ComfyUI via LunaExpressionPackGenerator
     │
     ├── Loads reference images
     ├── Generates expression prompts (LunaExpressionPromptBuilder)
     ├── Applies ControlNet poses
     ├── IP-Adapter style transfer
     ├── InsightFace identity swap
     └── Saves with LunaExpressionSlicerSaver
     │
     ▼
Expression pack saved to SillyTavern folder
```

### 3. Batch Fix Workflow

**Queue Processing:**
```
User marks 127 images with 🔧 (needs fix) in cluster view
     │
     ▼
Clicks "Batch Fix Faces" → Settings dialog
     │
     ▼
DiffusionToolkit queues to /api/batch/queue
     │
     ▼
Background worker iterates:
├── Load image from database (LunaToolkitImageLoader)
├── Detect face region (cached in face_regions table)
├── Apply inpaint mask to face area
├── Run img2img at specified denoise (0.45-0.65)
├── Optionally upscale if face < 512px
├── Save result (LunaMultiSaver)
├── Update database with new path/metadata
└── Re-generate CLIP-H embedding for updated face
     │
     ▼
Progress shown in DiffusionToolkit UI
```

### 4. Prompt Engine Sync

**Bidirectional Sync:**
```
┌─────────────────────────────────────────────────────────────────┐
│                      YAML Wildcard Files                         │
│  ├── body.yaml                                                   │
│  ├── clothing.yaml                                               │
│  └── locations.yaml                                              │
└─────────────────────────────────────────────────────────────────┘
                              ↕
                    Git-style versioning
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│                   PostgreSQL (wildcards table)                   │
│  ├── id, name, content, version, created_at                     │
│  └── Conflict resolution: latest wins, history preserved        │
└─────────────────────────────────────────────────────────────────┘
                              ↕
                         REST API
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│            Both UIs see the same wildcard data                   │
│  ├── DiffusionToolkit Prompt Engine                             │
│  └── ComfyUI LunaPromptCraft node                               │
└─────────────────────────────────────────────────────────────────┘
```

### 5. LoRA Training Export

**From Character Cluster:**
```
Cluster "Luna" (847 images) → Export for Training
     │
     ▼
Filter: Quality ≥ 4, Exclude marked bad
     │
     ▼
Result: 623 images selected
     │
     ▼
Export Options:
├── Format: Kohya / EveryDream / Flux
├── Caption source: Sidecar / Generated / Prompt-based
├── Include: Full images / Face crops / Both
├── Regularization: Auto-generate from similar non-character images
└── Output: D:/Training/Luna_LoRA/
     │
     ▼
Export Structure:
├── Luna_LoRA/
│   ├── 10_Luna/
│   │   ├── image001.png
│   │   ├── image001.txt (caption)
│   │   ├── image002.png
│   │   └── ...
│   ├── 1_regularization/
│   │   └── ...
│   └── training_config.toml
```

---

## Data Flow Diagrams

### Complete Generation → Index → Search Cycle

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        GENERATION PHASE                                   │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  LunaPromptCraft                LunaConfigGateway                        │
│       │                              │                                    │
│       ▼                              ▼                                    │
│  Resolved Prompt ──────────────► Parameters + Encoding                   │
│                                      │                                    │
│                                      ▼                                    │
│                               [KSampler]                                  │
│                                      │                                    │
│                                      ▼                                    │
│                             LunaMultiSaver                                │
│                                      │                                    │
│                    ┌─────────────────┼─────────────────┐                 │
│                    ▼                 ▼                 ▼                 │
│              image.png         metadata.json     workflow.json           │
│                                                                           │
└──────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                         INDEXING PHASE                                    │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  Diffusion.Watcher detects new file                                      │
│       │                                                                   │
│       ▼                                                                   │
│  Quick Scan (path, size, dates) → images table                           │
│       │                                                                   │
│       ▼                                                                   │
│  Metadata Extraction:                                                     │
│  ├── Read PNG/EXIF metadata                                              │
│  ├── Parse prompt, negative, parameters                                  │
│  ├── Extract LoRAs, ControlNets, workflow                                │
│  └── Store in images table                                               │
│       │                                                                   │
│       ▼                                                                   │
│  Embedding Generation (GPU):                                             │
│  ├── BGE: prompt → 1024D                                                 │
│  ├── BGE: negative → 1024D                                               │
│  ├── CLIP-L: prompt → 768D                                               │
│  ├── CLIP-G: prompt → 1280D                                              │
│  └── CLIP-H: image → 1280D                                               │
│       │                                                                   │
│       ▼                                                                   │
│  Face Detection:                                                          │
│  ├── YOLO/InsightFace → bbox coordinates                                 │
│  ├── Crop face regions                                                   │
│  ├── CLIP-H: face → 1280D (per face)                                     │
│  └── Store in face_regions table                                         │
│       │                                                                   │
│       ▼                                                                   │
│  Cluster Assignment:                                                      │
│  └── Match face embedding to existing clusters                           │
│                                                                           │
└──────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                          SEARCH PHASE                                     │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  User in DiffusionToolkit:                                               │
│  "Find images similar to this one with rating ≥ 4"                       │
│       │                                                                   │
│       ▼                                                                   │
│  Query Construction:                                                      │
│  ├── CLIP-H encode query image → 1280D                                   │
│  ├── Vector similarity search on image_embedding                          │
│  ├── Filter: rating >= 4                                                 │
│  └── Return top 20                                                        │
│       │                                                                   │
│       ▼                                                                   │
│  Results displayed in grid                                                │
│       │                                                                   │
│       ▼                                                                   │
│  User selects image → Right-click "Send to ComfyUI"                      │
│       │                                                                   │
│       ▼                                                                   │
│  LOOP BACK TO GENERATION PHASE                                           │
│                                                                           │
└──────────────────────────────────────────────────────────────────────────┘
```

### Character Discovery Pipeline

```
┌──────────────────────────────────────────────────────────────────────────┐
│                      FACE DETECTION (Per Image)                           │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  Image from database                                                      │
│       │                                                                   │
│       ▼                                                                   │
│  YOLO Face Detector                                                       │
│       │                                                                   │
│       ├── Face 1: bbox(100, 50, 200, 200), conf: 0.95                    │
│       ├── Face 2: bbox(400, 80, 180, 180), conf: 0.87                    │
│       └── (multiple faces per image possible)                             │
│       │                                                                   │
│       ▼ (for each face)                                                   │
│  Crop face + padding                                                      │
│       │                                                                   │
│       ▼                                                                   │
│  CLIP-H Vision Encoder                                                    │
│       │                                                                   │
│       ▼                                                                   │
│  1280D embedding stored in face_regions                                   │
│                                                                           │
└──────────────────────────────────────────────────────────────────────────┘
                                      │
                    (after N images processed)
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                         CLUSTERING (Batch)                                │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  Fetch all unclustered face embeddings                                    │
│       │                                                                   │
│       ▼                                                                   │
│  HDBSCAN Clustering                                                       │
│  ├── min_cluster_size: 10                                                │
│  ├── metric: cosine                                                       │
│  └── Automatically determines number of clusters                          │
│       │                                                                   │
│       ▼                                                                   │
│  Results:                                                                 │
│  ├── Cluster 0: 847 faces → "Character_0000" (user names: "Luna")        │
│  ├── Cluster 1: 623 faces → "Character_0001"                             │
│  ├── Cluster 2: 412 faces → "Character_0002"                             │
│  ├── ...                                                                  │
│  └── Noise: 1,204 faces (no cluster, outliers)                           │
│       │                                                                   │
│       ▼                                                                   │
│  For each cluster:                                                        │
│  ├── Calculate centroid (mean of member embeddings)                      │
│  ├── Calculate distances from centroid                                   │
│  └── Store in character_clusters table                                    │
│       │                                                                   │
│       ▼                                                                   │
│  Assign faces to clusters in face_regions table                           │
│                                                                           │
└──────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                      INCREMENTAL ASSIGNMENT                               │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  New face detected in new image                                           │
│       │                                                                   │
│       ▼                                                                   │
│  Query: nearest cluster centroid                                          │
│  SELECT id, 1 - (centroid <=> $embedding) as similarity                  │
│  FROM character_clusters                                                  │
│  ORDER BY centroid <=> $embedding                                         │
│  LIMIT 1                                                                  │
│       │                                                                   │
│       ▼                                                                   │
│  If similarity > 0.75:                                                    │
│  └── Assign to existing cluster                                          │
│  Else:                                                                    │
│  └── Mark as unclustered (for next batch clustering)                     │
│                                                                           │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## GPU Memory Strategy

### Dual-GPU Allocation

```
┌─────────────────────────────────────────────────────────────────────────┐
│                     GPU 0: RTX 5090 (32GB VRAM)                          │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Luna Daemon (Persistent):                                               │
│  ├── SDXL VAE (~2GB)                                                    │
│  ├── CLIP-L ViT-L/14 (~1GB)                                             │
│  └── CLIP-G ViT-bigG/14 (~3GB)                                          │
│  Subtotal: ~6GB                                                          │
│                                                                          │
│  DiffusionToolkit Embedding Service (Load on demand):                    │
│  ├── BGE-large-en-v1.5 (~1.5GB)                                         │
│  ├── CLIP-H ViT-H/14 for images (~2GB)                                  │
│  └── YOLO Face Detector (~200MB)                                        │
│  Subtotal: ~4GB                                                          │
│                                                                          │
│  Available for generation: ~22GB                                         │
│  └── Sufficient for SDXL UNet + overhead                                │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│                    GPU 1: RTX 3080 Ti (12GB VRAM)                        │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ComfyUI Generation (uses Daemon for VAE/CLIP):                          │
│  ├── SDXL UNet (~6GB)                                                   │
│  ├── ControlNet models (1-2GB each, loaded on demand)                   │
│  ├── IP-Adapter (~1GB)                                                  │
│  └── LoRAs (minimal VRAM impact)                                        │
│                                                                          │
│  Available: 12GB - models = 3-5GB headroom                               │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### TTL-Based Model Unloading

```python
# Luna Daemon TTL Configuration

MODEL_TTL = {
    "vae": 300,        # 5 minutes - frequently used
    "clip_l": 300,     # 5 minutes - frequently used
    "clip_g": 300,     # 5 minutes - frequently used
    "bge": 60,         # 1 minute - only during indexing
    "clip_h": 60,      # 1 minute - only during indexing
    "yolo_face": 120,  # 2 minutes - during face detection batches
}

# Daemon tracks last-use timestamp per model
# Background thread unloads models exceeding TTL
# Re-loads on next request (small latency, big VRAM savings)
```

---

## Security Considerations

### API Access Control

```yaml
# DiffusionToolkit API Configuration

api:
  enabled: true
  host: "127.0.0.1"  # Localhost only by default
  port: 19280
  
  # Optional: Allow ComfyUI on different machine
  # allowed_origins:
  #   - "192.168.1.100"
  
  # Rate limiting
  rate_limit:
    enabled: true
    requests_per_minute: 100
    burst: 20
  
  # Endpoints can be individually enabled/disabled
  endpoints:
    images: true
    clusters: true
    wildcards: true
    controlnet: true
    captions: true
    batch: true
```

### File Path Validation

All API endpoints that accept file paths validate:
- Path is within configured watched folders
- Path exists in database (not arbitrary file access)
- User has appropriate permissions for operation

---

## Future Enhancements

### Phase 2: Advanced Analytics

- **Rating Correlation:** "What settings produce 5-star images?"
- **LoRA Effectiveness:** "Which LoRAs improve quality for this character?"
- **Prompt Pattern Mining:** "Common patterns in successful prompts"

### Phase 3: AI-Assisted Curation

- **Auto Quality Scoring:** Predict rating based on embeddings
- **Duplicate Detection:** Find near-identical images for deduplication
- **Style Clustering:** Group images by visual style (beyond faces)

### Phase 4: Distributed Processing

- **Multi-Machine:** DiffusionToolkit coordinates multiple ComfyUI instances
- **Cloud Burst:** Overflow to cloud GPU when local is busy
- **Queue Priority:** High-priority jobs (user-initiated) vs background (batch)

---

## Summary

This integration creates a **closed-loop AI image workflow**:

1. **Generate** images in ComfyUI with Luna nodes
2. **Save** with complete metadata via LunaMultiSaver
3. **Index** automatically via Diffusion.Watcher
4. **Embed** with 5-layer system for multi-modal search
5. **Discover** characters via face clustering
6. **Curate** quality ratings and training datasets
7. **Search** using any combination of embeddings
8. **Refine** via right-click → ComfyUI workflows
9. **Train** LoRAs from curated character clusters
10. **Generate** better images with trained LoRAs

The result is an AI image management system that gets smarter the more you use it.

---

*Document Version: 1.0*
*Last Updated: December 2025*
*Author: DiffusionToolkit Development Team*
