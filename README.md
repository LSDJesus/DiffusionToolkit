# Diffusion Toolkit (Enhanced Fork) v1.9.1

Diffusion Toolkit is an image metadata-indexer and viewer for AI-generated images. It helps you organize, search, and sort your ever-growing collection of AI art.

> **This is an enhanced fork** of [RupertAvery/DiffusionToolkit](https://github.com/RupertAvery/DiffusionToolkit) with significant architectural changes for scale and advanced features.

This fork adds a number of workflow and UX improvements focused on large libraries, fast auto‑tagging/captioning, and flexible caption providers. The sections below document all major changes from the base version in detail.

## Original Features

- **Metadata Indexing**: Scan and index prompts and PNGInfo from images
- **Search**: Simple query or advanced filter-based search
- **Image Viewer**: Preview images with metadata toggle
- **Tagging**: Favorite, Rating (1-10), NSFW marking
- **Albums**: Organize images into collections
- **Folder View**: Browse by directory structure
- **Drag & Drop**: Move/copy images between folders

### Supported Formats
- **Images**: JPG/JPEG (EXIF), PNG, WebP, AVIF, GIF, BMP, TIFF
- **Video**: MP4, AVI, WebM (with FFmpeg for thumbnails)
- **Sidecars**: TXT metadata files

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
- Thread-safe caching to prevent connection pool exhaustion
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

## 🏷️ AI Auto-Tagging & Captioning (NEW)

**Why**: Manually tagging thousands of images is impractical.

**What Changed**:
- **JoyTag**: Fast booru-style tag prediction using ONNX Runtime
- **WDv3 Large**: High-accuracy anime/illustration tagging
- **JoyCaption**: Natural language image descriptions via LLamaSharp
- GPU acceleration with CUDA 12.x and cuDNN 9.x support
- Configurable confidence thresholds
- Batch processing with progress tracking
- Tags stored in PostgreSQL and optionally written to image metadata

**Benefits**:
- Automatic tag generation for searchability
- Natural language captions for semantic search
- GPU-accelerated inference (10-50 images/second)
- No external API required - runs locally

### Caption Provider Abstraction (Local + OpenAI-Compatible API)

- Introduced `ICaptionService` with two implementations:
   - `JoyCaptionService` (local, LLamaSharp + GGUF/LLaVA models)
   - `HttpCaptionService` (external, OpenAI-compatible HTTP API e.g., LM Studio, Ollama, cloud)
- Provider selection added to Settings: `Local JoyCaption` or `OpenAI-Compatible HTTP API`.
- When an external provider is selected:
   - Base URL, Model, and optional API Key fields are available.
   - A new “Test Connection” button probes `/v1/models` or `/models`, falling back to `/v1/chat/completions`.
   - On success, the models list is parsed and used to populate a Model dropdown (editable ComboBox).
   - “Send Sample Image” lets you pick an image and receive a caption using the currently configured provider.
- TaggingWindow now calls `ServiceLocator.CaptionService` so the UI is provider‑agnostic.

### Caption Handling Modes

- New setting: `CaptionHandlingMode` with options: Overwrite, Append, Refine.
- Applied when saving captions returned by JoyCaption or the external provider.
- Mode is honored consistently across manual captioning and batch operations.

### Tag + Caption UX Improvements

- Image Preview: Added a “Tags/Caption” tab with quick copy buttons to copy tags, caption, and parameters.
- Context Menu: Split the single action into two entries:
   - “Auto‑Tag…” → opens TaggingWindow with tagging options only (JoyTag / WDv3 Large)
   - “Caption…” → opens TaggingWindow with captioning options only (JoyCaption / HTTP API)
- TaggingWindow supports mode flags (`taggingOnly`, `captioningOnly`) that tailor the UI (title, enabled checkboxes) to the chosen operation.

### Tag Deduplication Overhaul

- Precomputed `tag_deduplication_map.json` generated from JoyTag and WDv3 Large lists to normalize common aliases.
- Centralized deduplication in the PostgreSQL DataStore layer so duplicates are eliminated regardless of source:
   - Auto‑tagging
   - Sidecar ingestion
   - Manual edits/imports
- Ensures consistent tags across the library even when mixing taggers and sources.

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
- Thread-safe lazy initialization for FileSystemWatcher handlers

### Code Organization
- Partial classes split by feature (MainWindow.xaml.*.cs)
- Modular project structure (15+ projects)
- PostgreSQL models separate from SQLite models
- Type aliases for disambiguation (`PgSorting`, `PgPaging`)

### Settings & Navigation UX

- Settings page additions:
   - Caption Provider selection + fields for external provider (Base URL, Model, API Key)
   - “Test Connection” and “Send Sample Image” utilities
   - Model field upgraded to an editable ComboBox populated from the `/models` endpoint
   - Model Root path management with “Scan Models” (registers typical model subfolders)
   - Collection (schema) switcher with quick “Create Schema” action for multi‑collection setups
- Settings button moved from the left sidebar to the top toolbar for quicker access; dirty indicator (red dot) follows the button.

### Error Handling
- Global exception handler with logging
- Toast notifications for user feedback
- Graceful degradation when optional features unavailable
- Diagnostic logging for troubleshooting

---

## Requirements

- **Windows 10/11** (64-bit only)
- **.NET 6 Desktop Runtime** (or .NET 10 for latest builds)
- **Docker Desktop** (for PostgreSQL + pgvector)
- **CUDA 12.x + cuDNN 9.x** (optional, for GPU-accelerated tagging)
- **FFmpeg** (optional, for video thumbnails)
- **~60GB disk space** for 1M images with all embeddings

### GPU Acceleration Setup

For JoyTag, WDv3, and JoyCaption GPU acceleration:
1. Install CUDA Toolkit 12.x
2. Install cuDNN v9.x
3. Set environment variables:
   ```
   CUDA_HOME=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x
   Add to PATH: %CUDA_HOME%\bin
   Add to PATH: C:\Program Files\NVIDIA\CUDNN\v9.x\bin
   ```

See `docs/ONNX_GPU_SETUP.md` for detailed instructions.

## Quick Start

```powershell
# Start PostgreSQL database
docker-compose up -d

# Run Diffusion Toolkit
.\Diffusion.Toolkit.exe
```

### Configure Caption Provider

1. Open Settings (top toolbar gear icon).
2. Under “Caption Provider”, choose one:
    - Local JoyCaption: set Model path + mmproj (LLaVA projection) if needed.
    - OpenAI‑Compatible HTTP API: set Base URL (e.g., http://localhost:1234/v1), leave Model blank initially.
3. Click “Test Connection”. On success, the Model dropdown will populate with available models. Pick one or type manually.
4. Optionally click “Send Sample Image” to verify the full captioning flow.

### Use Auto‑Tagging or Captioning

- Right‑click selected images in the grid:
   - Choose “Auto‑Tag…” for JoyTag / WDv3 Large tagging.
   - Choose “Caption…” for JoyCaption or your configured HTTP provider.
- In TaggingWindow, progress and results update live; captions are saved according to the configured handling mode.

---

## Screenshots

![Screenshot 2024-02-09 183808](https://github.com/RupertAvery/DiffusionToolkit/assets/1910659/437781da-e905-412a-bbe6-e179f51ac020)

![Screenshot 2024-02-09 183625](https://github.com/RupertAvery/DiffusionToolkit/assets/1910659/20e57f5a-be4e-468f-9bfb-fe309ecfe5f1)

---

## Documentation

See the `/docs` folder for detailed guides:

### Core Features
- `BATCH_CONVERSION.md` - WebP conversion feature
- `VIDEO_SUPPORT.md` - Video file support with FFmpeg thumbnails
- `TWO_PHASE_SCANNING.md` - Scanning system details
- `SIDECAR_AND_METADATA_EXTRACTION.md` - Metadata handling

### AI & Embeddings
- `EMBEDDING_ARCHITECTURE.md` - Multi-modal embedding system
- `EMBEDDING_SYSTEM_STATUS.md` - Current implementation status
- `JOYCAPTION_INTEGRATION_GUIDE.md` - JoyCaption setup
- `llamasharp.md` / `llamasharp_multimodal.md` - Local model setup notes
- `ONNX_GPU_SETUP.md` - GPU acceleration configuration
- `TEXTUAL_EMBEDDING_LIBRARY.md` - Embedding model details

### Integration
- `COMFYUI_INTEGRATION.md` - ComfyUI workflow export

### Database
- `README.PostgreSQL.md` - PostgreSQL setup guide
- `POSTGRESQL_MIGRATION_STATUS.md` - Migration progress

---

## Roadmap

- [x] Video file support (MP4, WebM, AVI) - ✅ **Complete**
- [x] FFmpeg thumbnail generation for videos - ✅ **Complete**
- [x] Duplicate detection using image embeddings - ✅ **Complete**
- [ ] Batch metadata editing
- [ ] Plugin system for custom metadata extractors
- [ ] Post‑operation review modal with Accept / Retry / Cancel for tags and captions

---

## Credits

Original project by [RupertAvery](https://github.com/RupertAvery/DiffusionToolkit)

<a href="https://www.buymeacoffee.com/rupertavery" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-green.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a> 

