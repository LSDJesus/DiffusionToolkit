# Diffusion Toolkit (Enhanced Fork)

Diffusion Toolkit is an image metadata-indexer and viewer for AI-generated images. It helps you organize, search, and sort your ever-growing collection of AI art.

> **This is an enhanced fork** of [RupertAvery/DiffusionToolkit](https://github.com/RupertAvery/DiffusionToolkit) with significant architectural changes for scale and advanced features.

## Original Features

- **Metadata Indexing**: Scan and index prompts and PNGInfo from images
- **Search**: Simple query or advanced filter-based search
- **Image Viewer**: Preview images with metadata toggle
- **Tagging**: Favorite, Rating (1-10), NSFW marking
- **Albums**: Organize images into collections
- **Folder View**: Browse by directory structure
- **Drag & Drop**: Move/copy images between folders

### Supported Formats
- JPG/JPEG (EXIF), PNG, WebP, TXT sidecars

### Supported AI Frameworks
AUTOMATIC1111, InvokeAI, NovelAI, Stable Diffusion, EasyDiffusion, Fooocus, RuinedFooocus, FooocusMRE, Stable Swarm, ComfyUI, Tensor.Art, SDNext

---

# What We Changed and Why

## 🗄️ PostgreSQL Migration (from SQLite)

**Why**: SQLite struggles with 500K+ images and doesn't support vector similarity search.

**What Changed**:
- Complete database layer rewrite to PostgreSQL 16+ with pgvector extension
- Docker-based deployment (`docker-compose.yml` included)
- Connection pooling for high concurrency (50 connections)
- Snake_case column naming convention for PostgreSQL compatibility
- 11 partial class files for modular DataStore organization

**Benefits**:
- Scales to 1M+ images without performance degradation
- Enables semantic/vector similarity search
- Better concurrent access for background operations
- Production-ready database with proper backup tools (`pg_dump`)

## ⚡ Two-Phase Scanning System

**Why**: Scanning 500K images took 4-8 hours, blocking all operations.

**What Changed**:
- **Phase 1 (Quick Scan)**: Index basic file info in 10-20 minutes
- **Phase 2 (Deep Scan)**: Extract full metadata in background
- New `scan_phase` column tracks completion status
- PostgreSQL COPY command for bulk inserts (~1000 files/second)

**Benefits**:
- Browse your library immediately after Phase 1
- Background metadata extraction doesn't block usage
- Progressive enhancement as metadata becomes available

## 🧠 Multi-Modal Embedding System (5 Vectors)

**Why**: Enable semantic search and ComfyUI workflow integration.

**What Changed**:
Added 5 embedding vectors per image stored in pgvector:

| Embedding | Model | Dimensions | Purpose |
|-----------|-------|------------|---------|
| Prompt | BGE-large-en-v1.5 | 1024D | Natural language prompt search |
| Negative Prompt | BGE-large-en-v1.5 | 1024D | Search by things to avoid |
| Image | CLIP-ViT-H/14 | 1024D | Visual similarity search |
| CLIP-L | SDXL Text Encoder | 768D | ComfyUI SDXL conditioning |
| CLIP-G | SDXL Text Encoder | 1280D | ComfyUI SDXL conditioning |

**Benefits**:
- "Find images like this" visual search
- Natural language search ("dramatic lighting at sunset")
- Direct export to ComfyUI for generating variations
- Multi-modal hybrid search (text + visual combined)

## 🎥 Video File Support

**Why**: Many AI tools now generate video content (AnimateDiff, SVD, etc.)

**What Changed**:
- Catalog MP4, AVI, WEBM alongside images
- Extract video metadata (duration, codec, framerate, bitrate)
- FFmpeg-based thumbnail generation from first frame
- Visual 🎥 indicator in gallery view
- `is_video` flag and 6 new metadata columns

**Benefits**:
- Unified library for all AI-generated media
- Search and organize videos with same workflow as images

## 🔄 Batch Image Conversion (WebP)

**Why**: Save disk space on large collections without manual conversion.

**What Changed**:
- New "Convert Images to WebP..." context menu option
- WebP Lossy (smaller) and Lossless (perfect quality) modes
- Quality slider (50-100%)
- Automatic metadata preservation during conversion
- Real-time progress with space-saved calculation
- Optional original file deletion with confirmation

**Benefits**:
- 20-70% space savings depending on source format
- Preserves all generation parameters and tags
- Database paths automatically updated
- Non-destructive with metadata backup

## 🔧 Architecture Improvements

### Service Layer Refactoring
- `ServiceLocator` pattern for dependency access
- `DatabaseWriterService` with async queue (System.Threading.Channels)
- `ScanningService` with cancellation token propagation
- Folder cache pre-loading to avoid query conflicts during bulk operations

### Code Organization
- Partial classes split by feature (MainWindow.xaml.*.cs)
- Modular project structure (15+ projects)
- PostgreSQL models separate from SQLite models
- Type aliases for disambiguation (`PgSorting`, `PgPaging`)

### Error Handling
- Global exception handler with logging
- Toast notifications for user feedback
- Graceful degradation when optional features unavailable

---

## Requirements

- **Windows 10/11** (64-bit only)
- **.NET 6 Desktop Runtime**
- **Docker Desktop** (for PostgreSQL + pgvector)
- **FFmpeg** (optional, for video thumbnails)
- **~60GB disk space** for 1M images with all embeddings

## Quick Start

```powershell
# Start PostgreSQL database
docker-compose up -d

# Run Diffusion Toolkit
.\Diffusion.Toolkit.exe
```

---

## Screenshots

![Screenshot 2024-02-09 183808](https://github.com/RupertAvery/DiffusionToolkit/assets/1910659/437781da-e905-412a-bbe6-e179f51ac020)

![Screenshot 2024-02-09 183625](https://github.com/RupertAvery/DiffusionToolkit/assets/1910659/20e57f5a-be4e-468f-9bfb-fe309ecfe5f1)

---

## Documentation

See the `/docs` folder for detailed guides:
- `BATCH_CONVERSION.md` - WebP conversion feature
- `EMBEDDING_ARCHITECTURE.md` - Multi-modal embedding system
- `COMFYUI_INTEGRATION.md` - ComfyUI workflow export
- `TWO_PHASE_SCANNING.md` - Scanning system details
- `VIDEO_SUPPORT.md` - Video file handling
- `POSTGRESQL_MIGRATION_STATUS.md` - Migration progress

---

## Credits

Original project by [RupertAvery](https://github.com/RupertAvery/DiffusionToolkit)

<a href="https://www.buymeacoffee.com/rupertavery" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-green.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a> 

