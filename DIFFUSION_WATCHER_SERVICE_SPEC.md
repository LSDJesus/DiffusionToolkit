# Diffusion Watcher Service - Integration Specification
**Date:** January 30, 2026  
**Version:** 2.0.0 (Headless Service Architecture)  
**Target Client:** AI-Evolution PyQt6 Frontend

---

## Executive Summary

**Diffusion Watcher** is a headless Windows background service that manages AI-generated image databases, metadata extraction, and GPU-accelerated processing. It exposes a REST API + WebSocket interface for frontend clients.

**Core Identity:** Headless worker service, not a UI application.

---

## Architecture Overview

### Service Role
- **Windows Service** - Auto-starts with system, runs in background
- **File System Monitor** - Watches folders for new/changed images 24/7
- **Database Manager** - Owns PostgreSQL + pgvector schema (civitai_downloads, public)
- **ONNX Inference Engine** - GPU-accelerated tagging, captioning, face detection, embeddings
- **Metadata Extractor** - Parses EXIF, PNG chunks, WebP for generation parameters

### Technology Stack
- **Runtime:** .NET 10.0 (C#)
- **Database:** PostgreSQL 18 + pgvector extension
- **HTTP Server:** Built-in HttpListener (port 19284)
- **ONNX Runtime:** GPU-accelerated inference
- **File Watching:** FileSystemWatcher for real-time detection

### Data Storage
```
PostgreSQL Database (diffusion_images)
├── civitai_downloads schema (86,181 folders, 1M+ images)
├── public schema (user images)
└── pgvector extension (semantic similarity search)
```

---

## What's Being Kept from Diffusion Toolkit

### ✅ Backend Services (All C#)
1. **PostgreSQL Data Layer** (`Diffusion.Database.PostgreSQL`)
   - Schema-aware queries (public + civitai_downloads)
   - Vector similarity search (pgvector)
   - Album management
   - Tag/favorite/rating operations
   - Folder hierarchy management

2. **Metadata Scanner** (`Diffusion.Scanner`)
   - Multi-format parser (PNG, JPEG, WebP, MP4)
   - Framework support: AUTOMATIC1111, ComfyUI, InvokeAI, NovelAI, Fooocus, SwarmUI
   - Civitai embedded metadata (UTF-16LE support)
   - Stealth PNG extraction
   - EXIF/XMP parsing

3. **ONNX Inference Services**
   - **Tagging:** WD14 v3 tagger, JoyTag
   - **Captioning:** JoyCaption, HTTP caption services
   - **Face Detection:** YuNet + ArcFace embeddings
   - **Embeddings:** BGE-large-en-v1.5 (text), CLIP (image)

4. **File System Watcher** (`Diffusion.Watcher`)
   - Real-time folder monitoring
   - Batch processing (1000 files/batch)
   - Schema-aware routing
   - Intelligent queue management

5. **Background Processing Orchestrator**
   - GPU load balancing (cuda:0, cuda:1)
   - Task queuing (tagging, captioning, face detection, embedding)
   - Progress tracking with statistics
   - Cancellation support

### ❌ Being Replaced
- **WPF Desktop Application** → AI-Evolution PyQt6 UI
- **MainWindow.xaml** and all UI code
- **Navigation/Settings/Album UI windows**
- **ObservableCollection bindings**
- **XAML converters and data templates**

---

## REST API Specification

**Base URL:** `http://localhost:19284/api`

### Image Operations

#### Search Images
```http
POST /api/images/search
Content-Type: application/json

{
  "query": "cyberpunk cityscape",
  "folder": "F:/Civitai_downloads",
  "schema": "civitai_downloads",
  "filters": {
    "favorite": true,
    "nsfw": false,
    "minRating": 3,
    "minAestheticScore": 0.7,
    "models": ["pony_diffusion_v6"],
    "tags": ["1girl", "masterpiece"]
  },
  "sorting": {
    "field": "created_date",
    "ascending": false
  },
  "pagination": {
    "page": 0,
    "pageSize": 200
  }
}

Response 200:
{
  "images": [
    {
      "id": 12345,
      "path": "F:/Civitai_downloads/image.png",
      "width": 1024,
      "height": 1536,
      "prompt": "1girl, cyberpunk cityscape, neon lights",
      "negativePrompt": "blurry, lowres",
      "model": "pony_diffusion_v6",
      "sampler": "DPM++ 2M Karras",
      "steps": 28,
      "cfgScale": 7.0,
      "seed": 1234567890,
      "favorite": true,
      "rating": 5,
      "aestheticScore": 0.85,
      "nsfw": false,
      "createdDate": "2026-01-29T19:30:00Z",
      "fileSize": 2048576,
      "albumCount": 2
    }
  ],
  "totalCount": 1543,
  "page": 0,
  "pageSize": 200
}
```

#### Get Image Details
```http
GET /api/images/{id}?schema=civitai_downloads

Response 200:
{
  "id": 12345,
  "path": "F:/Civitai_downloads/image.png",
  "metadata": { /* full metadata object */ },
  "loras": [
    {"name": "add_detail", "strength": 1.0},
    {"name": "style_lora", "strength": 0.7}
  ],
  "faces": [
    {
      "id": 1,
      "bbox": {"x": 100, "y": 150, "width": 200, "height": 250},
      "confidence": 0.95,
      "clusterLabel": "character_luna"
    }
  ],
  "tags": [
    {"name": "1girl", "confidence": 0.98},
    {"name": "blue_eyes", "confidence": 0.92}
  ]
}
```

#### Get Image File
```http
GET /api/images/{id}/thumbnail?schema=civitai_downloads&size=256
GET /api/images/{id}/full?schema=civitai_downloads

Response 200: (image/png or image/jpeg binary)
```

#### Update Image Metadata
```http
PATCH /api/images/{id}?schema=civitai_downloads
Content-Type: application/json

{
  "favorite": true,
  "rating": 5,
  "nsfw": false,
  "forDeletion": false
}

Response 200: {"success": true}
```

#### Delete Image
```http
DELETE /api/images/{id}?schema=civitai_downloads&permanent=false

Response 200: {"success": true, "markedForDeletion": true}
```

### Album Operations

#### List Albums
```http
GET /api/albums?schema=civitai_downloads

Response 200:
{
  "albums": [
    {
      "id": 1,
      "name": "Best Portraits",
      "description": "Favorite portrait generations",
      "imageCount": 42,
      "coverImageId": 12345,
      "createdDate": "2026-01-15T10:00:00Z"
    }
  ]
}
```

#### Create Album
```http
POST /api/albums?schema=civitai_downloads
Content-Type: application/json

{
  "name": "Cyberpunk Collection",
  "description": "Neon cityscapes and tech noir"
}

Response 201: {"id": 2, "name": "Cyberpunk Collection"}
```

#### Add Images to Album
```http
POST /api/albums/{id}/images?schema=civitai_downloads
Content-Type: application/json

{
  "imageIds": [12345, 12346, 12347]
}

Response 200: {"success": true, "addedCount": 3}
```

### Folder Operations

#### Get Folder Tree
```http
GET /api/folders?schema=civitai_downloads

Response 200:
{
  "folders": [
    {
      "id": 1,
      "path": "F:/Civitai_downloads",
      "isRoot": true,
      "isRecursive": true,
      "imageCount": 543210,
      "hasChildren": true,
      "archived": false,
      "excluded": false
    }
  ]
}
```

#### Trigger Scan
```http
POST /api/folders/scan
Content-Type: application/json

{
  "folderPath": "F:/Civitai_downloads/new_folder",
  "recursive": true,
  "rebuild": false,
  "schema": "civitai_downloads"
}

Response 202: {"taskId": "scan-abc123", "status": "queued"}
```

#### Get Folder Statistics
```http
GET /api/folders/stats?path=F:/Civitai_downloads&schema=civitai_downloads

Response 200:
{
  "totalImages": 543210,
  "totalSize": 1099511627776,
  "folderCount": 86181,
  "lastScanDate": "2026-01-29T20:15:00Z"
}
```

### Model Operations

#### List Models
```http
GET /api/models?schema=civitai_downloads&type=checkpoint

Response 200:
{
  "models": [
    {
      "name": "pony_diffusion_v6",
      "hash": "abc123def456",
      "path": "E:/Models/checkpoints/pony_v6.safetensors",
      "imageCount": 15432,
      "civitaiModelId": 123456
    }
  ]
}
```

#### Search Models
```http
GET /api/models/search?query=pony&schema=civitai_downloads

Response 200: { /* filtered model list */ }
```

### Queue Management

#### Get Queue Status
```http
GET /api/queue/tagging?schema=civitai_downloads

Response 200:
{
  "queuedCount": 250,
  "processingCount": 5,
  "completedCount": 12000,
  "failedCount": 3,
  "isPaused": false
}
```

#### Add to Queue
```http
POST /api/queue/tagging?schema=civitai_downloads
Content-Type: application/json

{
  "imageIds": [12345, 12346],
  "skipAlreadyProcessed": true
}

Response 202: {"success": true, "queuedCount": 2}
```

#### Pause/Resume Queue
```http
POST /api/queue/pause
POST /api/queue/resume

Response 200: {"success": true, "isPaused": true}
```

### Search & Similarity

#### Vector Similarity Search
```http
POST /api/search/similar
Content-Type: application/json

{
  "imageId": 12345,
  "schema": "civitai_downloads",
  "limit": 50,
  "similarityThreshold": 0.8
}

Response 200:
{
  "results": [
    {
      "imageId": 12346,
      "similarity": 0.92,
      "path": "F:/Civitai_downloads/similar_image.png"
    }
  ]
}
```

### Service Control

#### Get Service Status
```http
GET /api/status

Response 200:
{
  "version": "1.9.1",
  "uptime": "2d 14h 32m",
  "watchedFolders": 2,
  "schemasActive": ["public", "civitai_downloads"],
  "gpuDevices": [
    {"id": 0, "name": "NVIDIA GeForce RTX 5090", "memoryUsed": 12000, "memoryTotal": 32000},
    {"id": 1, "name": "NVIDIA GeForce RTX 3090", "memoryUsed": 8000, "memoryTotal": 16000}
  ],
  "processingStats": {
    "newImages": 250,
    "successfulImports": 220,
    "failedImports": 2,
    "skippedImages": 28
  }
}
```

---

## WebSocket API

**URL:** `ws://localhost:19284/ws/status`

### Event Stream
```json
// Scanning progress
{"type": "scan_progress", "folder": "F:/Civitai_downloads", "processed": 1500, "total": 5000}

// New image detected
{"type": "image_added", "imageId": 12347, "path": "F:/Civitai_downloads/new.png", "schema": "civitai_downloads"}

// Processing complete
{"type": "processing_complete", "imageId": 12345, "operation": "tagging", "tags": [...]}

// Statistics update (every 30s)
{"type": "stats_update", "newImages": 50, "successfulImports": 48, "failedImports": 1, "skippedImages": 1}

// Queue status change
{"type": "queue_update", "queue": "tagging", "queuedCount": 150, "processingCount": 5}

// Error notification
{"type": "error", "message": "Failed to process image", "imageId": 12348, "details": "..."}
```

---

## Data Models

### ImageView
```typescript
interface ImageView {
  id: number;
  path: string;
  width: number;
  height: number;
  prompt?: string;
  negativePrompt?: string;
  model?: string;
  modelHash?: string;
  sampler?: string;
  steps?: number;
  cfgScale?: number;
  seed?: number;
  favorite: boolean;
  rating?: number;
  aestheticScore?: number;
  nsfw: boolean;
  forDeletion: boolean;
  hasError: boolean;
  createdDate: string;
  fileSize: number;
  albumCount: number;
}
```

### SearchFilters
```typescript
interface SearchFilters {
  query?: string;
  folder?: string;
  schema: "public" | "civitai_downloads";
  favorite?: boolean;
  nsfw?: boolean;
  minRating?: number;
  maxRating?: number;
  minAestheticScore?: number;
  models?: string[];
  tags?: string[];
  dateFrom?: string;
  dateTo?: string;
  minWidth?: number;
  minHeight?: number;
}
```

---

## Integration Pattern for AI-Evolution

### 1. Create `diffusion_client.py`
```python
# core/diffusion_client.py
import httpx
from typing import List, Optional
from dataclasses import dataclass

@dataclass
class ImageSearchRequest:
    query: Optional[str] = None
    folder: Optional[str] = None
    schema: str = "civitai_downloads"
    favorite: Optional[bool] = None
    page: int = 0
    page_size: int = 200

class DiffusionClient:
    def __init__(self, base_url: str = "http://localhost:19284"):
        self.base_url = base_url
        self.client = httpx.AsyncClient(timeout=30.0)
    
    async def search_images(self, request: ImageSearchRequest) -> dict:
        """Search images with filters"""
        response = await self.client.post(
            f"{self.base_url}/api/images/search",
            json=request.__dict__
        )
        response.raise_for_status()
        return response.json()
    
    async def get_image_thumbnail(self, image_id: int, schema: str = "civitai_downloads") -> bytes:
        """Get image thumbnail"""
        response = await self.client.get(
            f"{self.base_url}/api/images/{image_id}/thumbnail",
            params={"schema": schema, "size": 256}
        )
        response.raise_for_status()
        return response.content
    
    async def update_metadata(self, image_id: int, schema: str, **kwargs) -> bool:
        """Update image metadata (favorite, rating, etc.)"""
        response = await self.client.patch(
            f"{self.base_url}/api/images/{image_id}",
            params={"schema": schema},
            json=kwargs
        )
        return response.status_code == 200
    
    async def get_status(self) -> dict:
        """Get service status"""
        response = await self.client.get(f"{self.base_url}/api/status")
        return response.json()
```

### 2. Add to `core/config.py`
```python
@dataclass
class ServiceConfig:
    # ... existing services ...
    diffusion_watcher: str = "http://localhost:19284"
```

### 3. Create Image Gallery Window
```python
# gui/image_gallery_window.py
from PyQt6.QtWidgets import QMainWindow, QGridLayout
from core.diffusion_client import DiffusionClient

class ImageGalleryWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.client = DiffusionClient()
        self.init_ui()
    
    async def load_images(self, query: str = None):
        request = ImageSearchRequest(query=query, page_size=100)
        result = await self.client.search_images(request)
        self.display_images(result['images'])
```

### 4. Update `EXTERNAL_SERVICES_CONTRACT.md`
Add Diffusion Watcher as a new external service with full API spec.

---

## Service Configuration

### Auto-Start Setup (Windows Service)
```powershell
# Install as Windows service (using NSSM)
nssm install DiffusionWatcher "D:\AI\Github_Desktop\DiffusionToolkit\build\Diffusion.Watcher.exe"
nssm set DiffusionWatcher AppDirectory "D:\AI\Github_Desktop\DiffusionToolkit\build"
nssm set DiffusionWatcher Start SERVICE_AUTO_START
nssm start DiffusionWatcher
```

### Configuration File
`config.json` (in Watcher directory):
```json
{
  "HttpPort": 19284,
  "BackgroundMetadataEnabled": true,
  "AutoTagOnScan": true,
  "AutoCaptionOnScan": false,
  "AutoFaceDetectionOnScan": true,
  "AutoEmbeddingOnScan": false,
  "GpuDevices": [0, 1],
  "TaggingModel": "wd14_v3",
  "CaptionModel": "joycaption",
  "FaceDetectionModel": "yunet"
}
```

---

## Deployment Checklist

### Watcher Service Setup
- [ ] Build Watcher as release exe
- [ ] Install as Windows service
- [ ] Configure auto-start
- [ ] Verify PostgreSQL connection
- [ ] Test folder monitoring
- [ ] Validate HTTP API responses

### AI-Evolution Integration
- [ ] Add `diffusion_client.py` to `core/`
- [ ] Update `aegis_config.json` with Watcher URL
- [ ] Create image gallery window
- [ ] Add WebSocket listener for real-time updates
- [ ] Update `EXTERNAL_SERVICES_CONTRACT.md`
- [ ] Remove direct SQL queries from `vision_client.py`

### Testing
- [ ] API endpoint validation
- [ ] WebSocket event streaming
- [ ] Image search performance (1M+ images)
- [ ] Thumbnail loading speed
- [ ] Metadata update operations
- [ ] Queue management

---

## Performance Characteristics

- **Image Search:** <100ms for filtered queries (indexed columns)
- **Vector Similarity:** <500ms for 512-dim embeddings across 1M images
- **Thumbnail Serving:** <50ms per image (cached)
- **Metadata Update:** <10ms per image
- **Batch Processing:** 1000 files/batch, ~5-10s metadata extraction
- **Tag Inference:** ~100-200ms per image (GPU)
- **Caption Generation:** ~500ms-1s per image (GPU)

---

## Security Notes

- **Local-only API:** Binds to `localhost` only (no external access)
- **No authentication:** Assumes trusted local environment
- **File system access:** Watcher has full read/write to watched folders
- **Database credentials:** Stored in Watcher config (not exposed via API)

---

**Summary:** Diffusion Watcher becomes a production-grade headless service that owns all image database operations, GPU inference, and file monitoring. AI-Evolution consumes its REST API + WebSocket interface as a clean microservice integration, following the Lazarus Protocol's "router, not worker" philosophy.
