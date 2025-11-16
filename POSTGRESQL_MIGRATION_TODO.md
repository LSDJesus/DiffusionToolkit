# PostgreSQL Migration - Remaining TODO Items

## Project Status: Core Backend Complete ✅

The PostgreSQL database layer, GPU embedding system, and intelligent deduplication are fully implemented and working. What remains is UI integration and migration tooling.

---

## ✅ Completed Items

### 1. PostgreSQL Database Layer
- ✅ `Diffusion.Database.PostgreSQL` project with Npgsql 8.x
- ✅ Connection pooling via NpgsqlDataSource
- ✅ pgvector extension support (1024D vectors)
- ✅ Migrations V1-V6 with automatic schema updates
- ✅ Partial classes: Album, Folder, MetaData, Search, VectorSearch, EmbeddingCache, Queue

### 2. GPU Embedding Service
- ✅ `Diffusion.Embeddings` project with ONNX Runtime GPU 1.20.1
- ✅ All 4 models converted and working:
  - BGE-large-en-v1.5 (1024D text embeddings)
  - CLIP-L (768D SDXL text encoder)
  - CLIP-G (1280D SDXL text encoder)
  - CLIP-H (1280D vision encoder)
- ✅ All models running on RTX 5090 (GPU 0)
- ✅ Performance: 286ms for 3 text vectors
- ✅ CUDA 12.9 + cuDNN 9.14 integration

### 3. Intelligent Deduplication System
- ✅ `EmbeddingProcessingService` with SHA256 prompt caching
- ✅ Metadata hash grouping (prompt+model+seed+params)
- ✅ Representative image selection (largest file = FINAL)
- ✅ Embedding source tracking (`embedding_source_id`)
- ✅ Orphan detection with database trigger
- ✅ Optimized for 773K images (425K unique) - saves 36 hours (60%)

### 4. User-Controlled Embedding Queue System
- ✅ Migration V6: `embedding_queue` and `embedding_worker_state` tables
- ✅ Priority queue system (0=normal, 100=embed now)
- ✅ `EmbeddingWorkerService` with state machine:
  - Stopped: Workers off, models unloaded from VRAM
  - Running: Workers active, models in VRAM, processing queue
  - Paused: Workers stopped, models kept in VRAM (instant resume)
- ✅ Queue operations: folder (recursive/non-recursive), images, priority
- ✅ PostgreSQLDataStore.Queue.cs with all queue methods
- ✅ State persistence across app restarts

### 5. Documentation
- ✅ `EMBEDDING_SYSTEM_STATUS.md` - Complete system status and test results
- ✅ `ONNX_GPU_SETUP.md` - CUDA setup and troubleshooting
- ✅ `INTELLIGENT_DEDUPLICATION_SYSTEM.md` - Architecture and performance
- ✅ Docker Compose with PostgreSQL 18 + pgvector
- ✅ Updated `.github/copilot-instructions.md`

---

## 🔧 Remaining Implementation Tasks

### Task 1: Create Embedding Control Bar UI (WPF)
**Priority:** High  
**Estimated Time:** 2-3 hours

**Requirements:**
- Top toolbar with control buttons:
  - **Start** (▶) - Load models + begin processing
  - **Pause** (⏸) - Stop workers, keep models in VRAM
  - **Stop** (⏹) - Stop workers, unload models from VRAM
  - **Clear** (✕) - Empty entire queue
  - **Settings** (⚙) - Batch size, auto-pause schedule
- Progress bar with real-time statistics:
  ```
  Queue: 125,000 (500 priority) | Processed: 1,250 | Failed: 2
  Cache: 95% | GPU: RTX 5090 (45%) | Speed: 5.2 img/s | ETA: 21h 15m
  ```
- Visual states for buttons (enabled/disabled based on worker status)
- Bind to `EmbeddingWorkerService` events for updates

**Files to Create/Modify:**
- Create: `Diffusion.Toolkit/Controls/EmbeddingControlBar.xaml`
- Create: `Diffusion.Toolkit/Controls/EmbeddingControlBar.xaml.cs`
- Create: `Diffusion.Toolkit/ViewModels/EmbeddingControlBarViewModel.cs`
- Modify: `MainWindow.xaml` (add control bar to top)

**Reference:**
- `EmbeddingWorkerService.StatusChanged` event
- `EmbeddingWorkerService.ProgressUpdated` event
- Worker states: `Stopped`, `Running`, `Paused`

---

### Task 2: Add Folder Context Menu for Embedding Queue
**Priority:** High  
**Estimated Time:** 2-3 hours

**Requirements:**

**Folder Right-Click Menu (4 options):**
1. **Queue Folder** - Add folder images to queue (priority=0, non-recursive)
2. **Embed Folder Now** - Add folder images to front of queue (priority=100, start worker, non-recursive)
3. **Queue Folder+Subfolders** - Add folder+subfolders images to queue (priority=0, recursive)
4. **Embed Folder+Subfolders Now** - Add folder+subfolders images to front of queue (priority=100, start worker, recursive)

**Image Right-Click Menu (2 options):**
1. **Queue Image** - Add image to queue (priority=0)
2. **Embed Image Now** - Add image to front of queue (priority=100, start worker)

**Visual Indicators:**
- Folder badge showing number of queued images in that folder
- Different icon/overlay for folders with high-priority items

**Files to Modify:**
- `MainWindow.xaml.cs` or `MainWindow.xaml.Folders.cs` - Add context menu handlers
- `MainWindow.xaml` - Add context menu items to folder tree
- `Services/FolderService.cs` - May need helper methods

**DataStore Methods to Use:**
```csharp
await dataStore.QueueFolderForEmbeddingAsync(folderId, priority: 0);
await dataStore.QueueFolderRecursiveForEmbeddingAsync(folderId, priority: 100);
await dataStore.QueueImagesForEmbeddingAsync(imageIds, priority: 0);
```

**Worker Control:**
```csharp
if (embedNow)
{
    await embeddingWorkerService.StartAsync(); // Auto-start worker
}
```

---

### Task 3: Wire Embedding System into MainWindow
**Priority:** High  
**Estimated Time:** 2-3 hours

**Requirements:**
- Initialize `EmbeddingWorkerService` in `App.xaml.cs` or `MainWindow.xaml.cs`
- Restore worker state on app startup:
  ```csharp
  await embeddingWorkerService.RestoreStateAsync();
  ```
- Add control bar to top of MainWindow (above image grid)
- Connect folder tree context menu events
- Connect image grid context menu events
- Handle worker events for UI updates
- Add to `ServiceLocator` for global access

**Files to Modify:**
- `App.xaml.cs` - Initialize worker service, add to ServiceLocator
- `MainWindow.xaml` - Add control bar at top
- `MainWindow.xaml.cs` - Wire up events and context menus
- `Services/ServiceLocator.cs` - Add `EmbeddingWorkerService` property

**Initialization Pattern:**
```csharp
// In App.xaml.cs or MainWindow constructor
var embeddingConfig = EmbeddingConfig.CreateDefault(modelsDirectory);
var embeddingService = new EmbeddingService(embeddingConfig);
var workerService = new EmbeddingWorkerService(dataStore, embeddingService);
await workerService.RestoreStateAsync();
ServiceLocator.EmbeddingWorkerService = workerService;
```

---

### Task 4: Replace SQLite-Specific Code
**Priority:** Medium  
**Estimated Time:** 3-4 hours

**Requirements:**
- Remove dependency on `path0.dll` SQLite extension
- Replace `RANDOM()` with PostgreSQL `RANDOM()` (already compatible)
- Update any SQLite-specific SQL syntax to PostgreSQL
- Search for `Diffusion.Database.DataStore` usage and replace with `PostgreSQLDataStore`
- Update connection string handling in configuration

**Files to Search:**
- All files importing `Diffusion.Database.DataStore`
- Any `.sql` files or SQL string literals
- Configuration loading code

**SQL Differences to Handle:**
- SQLite `AUTOINCREMENT` → PostgreSQL `SERIAL`
- SQLite `datetime('now')` → PostgreSQL `NOW()`
- SQLite `||` (concat) → PostgreSQL `||` (same)
- SQLite `LIMIT x OFFSET y` → PostgreSQL `LIMIT x OFFSET y` (same)

**Search Commands:**
```bash
# Find DataStore usage
grep -r "new DataStore" --include="*.cs"
grep -r "using Diffusion.Database;" --include="*.cs"

# Find potential SQLite-specific syntax
grep -r "AUTOINCREMENT" --include="*.cs"
grep -r "datetime(" --include="*.cs"
```

---

### Task 5: Create One-Time Migration Tool
**Priority:** Medium  
**Estimated Time:** 4-6 hours

**Requirements:**
- Console application to migrate existing SQLite database to PostgreSQL
- Bulk insert images with progress reporting
- Copy folder structure and albums
- Optionally generate embeddings during migration (or queue for later)
- Handle large datasets (773K+ images)
- Resume capability if interrupted

**Features:**
```
Diffusion.MigrationTool.exe migrate --source <sqlite.db> --dest <postgres-connection>
  --batch-size 1000
  --with-embeddings (optional - generates embeddings during migration)
  --skip-embeddings (default - queue for later with UI)
  --resume (continue from last checkpoint)
```

**Files to Create:**
- Create: `Diffusion.MigrationTool/` project (console app)
- Create: `Diffusion.MigrationTool/Program.cs`
- Create: `Diffusion.MigrationTool/SqliteMigrator.cs`
- Create: `Diffusion.MigrationTool/PostgresImporter.cs`

**Migration Steps:**
1. Read from SQLite DataStore (existing code)
2. Compute `metadata_hash` for all images
3. Bulk insert into PostgreSQL (use `COPY` for speed)
4. Copy folders, albums, relationships
5. Optionally: Queue all images for embedding or generate during migration

**Performance Target:**
- Without embeddings: ~10-20 minutes for 773K images
- With embeddings: ~22-23 hours (using intelligent deduplication)

---

### Task 6: Update Configuration for PostgreSQL
**Priority:** Medium  
**Estimated Time:** 1-2 hours

**Requirements:**
- Add PostgreSQL connection string to app settings
- Add embedding model paths to configuration
- Add GPU device selection (for multi-GPU setups)
- Add batch size and performance tuning settings
- Support for both SQLite (legacy) and PostgreSQL modes

**Configuration Keys:**
```json
{
  "DatabaseType": "PostgreSQL",  // or "SQLite" for legacy
  "ConnectionString": "Host=localhost;Port=5432;Database=diffusion_images;Username=diffusion;Password=...",
  "EmbeddingModels": {
    "ModelsDirectory": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx",
    "GpuDevice": 0,
    "BatchSize": 32
  },
  "EmbeddingWorker": {
    "AutoStartOnLaunch": false,
    "AutoPauseSchedule": "09:00-17:00",  // Pause during work hours
    "MaxRetries": 3
  }
}
```

**Files to Modify:**
- `Diffusion.Common/Configuration.cs` - Add new config properties
- `appsettings.json` or equivalent config file
- `App.xaml.cs` - Load and apply configuration

---

### Task 7: Update ServiceLocator for PostgreSQL DataStore
**Priority:** High  
**Estimated Time:** 1-2 hours

**Requirements:**
- Replace SQLite DataStore with PostgreSQL DataStore in ServiceLocator
- Initialize connection pool in `App.xaml.cs`
- Handle migration detection (if SQLite DB exists, prompt to migrate)
- Graceful fallback if PostgreSQL unavailable

**Files to Modify:**
- `Services/ServiceLocator.cs` - Replace DataStore initialization
- `App.xaml.cs` - Initialize PostgreSQL connection and migrations

**Pattern:**
```csharp
// In App.xaml.cs
var connectionString = Configuration.GetConnectionString("PostgreSQL");
var dataStore = new PostgreSQLDataStore(connectionString);
await dataStore.Create(() => new object(), _ => { }); // Run migrations
ServiceLocator.DataStore = dataStore;
```

**Migration Prompt:**
```csharp
if (File.Exists("old_database.db") && !CheckPostgreSQLHasData())
{
    var result = MessageBox.Show(
        "SQLite database detected. Would you like to migrate to PostgreSQL?",
        "Database Migration",
        MessageBoxButton.YesNo);
    
    if (result == MessageBoxResult.Yes)
    {
        // Launch migration tool or run inline
    }
}
```

---

## 🏷️ Image Captioning and Tagging Tasks

### Task 8: COMPLETE JoyTag Integration (ONNX Auto-Tagging) (Also added three WDTag implementations)
**Priority:** High  
**Estimated Time:** 3-4 hours

**What is JoyTag?**
- ONNX-based image tagging model (similar to DeepDanbooru/WD14 tagger)
- Generates descriptive tags for images (characters, objects, styles, etc.)
- Model: `models/onnx/joytag/jtModel.onnx`
- Tags CSV: `models/onnx/joytag/jtTags.csv`

**Requirements:**
- Create `Diffusion.Tagging` project for auto-tagging services
- Port code from `_import_joytag` folder:
  - `BaseAutoTaggerService.cs` - Base class for tagging services
  - `JoyTagAutoTaggerService.cs` - JoyTag-specific implementation
  - `ImageProcessorService.cs` - Image preprocessing for model input
  - `JoyTagInputData.cs` / `JoyTagOutputData.cs` - Model I/O data structures
- Adapt to DiffusionToolkit architecture:
  - Replace `SmartData.Lib` namespaces with `Diffusion.Tagging`
  - Remove dependencies on external interfaces (or create minimal equivalents)
  - Use existing `Diffusion.Common` utilities where possible
  - Integrate with `ServiceLocator` pattern
- Add UI components:
  - Context menu: "Tag Image" (single), "Tag Folder" (batch)
  - Settings: Threshold slider (0.0-1.0, default 0.2), tag filtering options
  - Progress dialog for batch operations
- Database integration:
  - Store generated tags in PostgreSQL (new `image_tags` table or JSON column)
  - Link tags to images with confidence scores
  - Search/filter by tags

**Key Modifications Needed:**
```csharp
// Original namespace
namespace SmartData.Lib.Services.MachineLearning

// New namespace
namespace Diffusion.Tagging.Services

// Update interface references
IImageProcessorService → Create minimal interface in Diffusion.Tagging
ITagProcessorService → Create tag processing service
IAutoTaggerService → Keep as base interface
```

**Files to Create:**
- `Diffusion.Tagging/Diffusion.Tagging.csproj` - New project
- `Diffusion.Tagging/Services/JoyTagService.cs` - Adapted from JoyTagAutoTaggerService
- `Diffusion.Tagging/Services/ImagePreprocessor.cs` - Simplified from ImageProcessorService
- `Diffusion.Tagging/Models/TagResult.cs` - Tag with confidence score
- `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.Tags.cs` - Tag storage operations
- `Diffusion.Toolkit/Controls/TaggingControlBar.xaml` - UI for batch tagging

**Database Schema Addition:**
```sql
CREATE TABLE image_tags (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    tag TEXT NOT NULL,
    confidence REAL NOT NULL,
    source TEXT NOT NULL DEFAULT 'joytag',
    created_at TIMESTAMP DEFAULT NOW(),
    UNIQUE(image_id, tag, source)
);
CREATE INDEX idx_image_tags_image_id ON image_tags(image_id);
CREATE INDEX idx_image_tags_tag ON image_tags(tag);
CREATE INDEX idx_image_tags_confidence ON image_tags(confidence DESC);
```

**Usage Pattern:**
```csharp
var joyTagService = ServiceLocator.JoyTagService;
var tags = await joyTagService.TagImageAsync(imagePath, threshold: 0.2f);
// Returns: [("1girl", 0.98), ("blue_hair", 0.87), ("anime", 0.95), ...]

await dataStore.StoreImageTagsAsync(imageId, tags, source: "joytag");
```

**Resources:**
- Existing code in `_import_joytag/` folder
- ONNX model: `models/onnx/joytag/jtModel.onnx`
- Tags CSV: `models/onnx/joytag/jtTags.csv`

---

### Task 9: COMPLETE JoyCaption Integration (GGUF Image Captioning)
**Priority:** High  
**Estimated Time:** 5-6 hours

**What is JoyCaption?**
- Large Language Model (LLM) with vision capabilities for detailed image captioning
- Generates natural language descriptions (150-200 words)
- Model format: GGUF (llama.cpp, not ONNX)
- Models:
  - Main: `models/Joycaption/llama-joycaption-beta-one-hf-llava.i1-Q6_K.gguf`
  - Vision: `models/Joycaption/llama-joycaption-beta-one-llava-mmproj-model-f16.gguf`

**LLamaSharp (Recommended - Pure C# Integration)**
- ✅ Native C# wrapper around llama.cpp
- ✅ GPU acceleration (CUDA/Vulkan)
- ✅ Direct GGUF model loading
- ❌ Multimodal (vision) support still maturing
- Package: `LLamaSharp`, `LLamaSharp.Backend.Cuda12`

**Requirements:**
- Create `Diffusion.Captioning` project for captioning services
- Implement server management:
  - Start/stop llama.cpp server on app launch/exit
  - HTTP client for caption requests
  - Queue system for batch captioning (similar to embedding queue)
- Add UI components:
  - Context menu: "Caption Image", "Caption Folder"
  - Settings: Server port, GPU layers, prompt templates
  - Caption editor/viewer (edit generated captions before saving)
- Database integration:
  - Store captions in PostgreSQL (new `image_captions` table or text column)
  - Multiple captions per image (user-edited, AI-generated)
  - Caption history/versioning

**Files to Create:**
- `Diffusion.Captioning/Diffusion.Captioning.csproj` - New project
- `Diffusion.Captioning/Services/LlamaServerManager.cs` - Manage llama-server process
- `Diffusion.Captioning/Services/JoyCaptionService.cs` - HTTP client for captioning
- `Diffusion.Captioning/Models/CaptionResult.cs` - Caption with metadata
- `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.Captions.cs` - Caption storage
- `Diffusion.Toolkit/Controls/CaptionEditor.xaml` - UI for viewing/editing captions
- `Diffusion.Toolkit/Windows/CaptionQueueWindow.xaml` - Batch captioning queue

**Database Schema Addition:**
```sql
CREATE TABLE image_captions (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    caption TEXT NOT NULL,
    source TEXT NOT NULL DEFAULT 'joycaption',
    prompt_used TEXT,
    is_user_edited BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW()
);
CREATE INDEX idx_image_captions_image_id ON image_captions(image_id);
CREATE INDEX idx_image_captions_source ON image_captions(source);
```

**LlamaServerManager Pattern:**
```csharp
public class LlamaServerManager : IDisposable
{
    private Process? _serverProcess;
    
    public async Task StartAsync(string modelPath, string mmprojPath, int port = 8080, int gpuLayers = 35)
    {
        var args = $"-m \"{modelPath}\" --mmproj \"{mmprojPath}\" " +
                   $"--host 127.0.0.1 --port {port} -ngl {gpuLayers} -c 4096";
        
        _serverProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "llama-server.exe", // From llama.cpp releases
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false
        });
        
        // Wait for server to be ready
        await WaitForServerReadyAsync(port);
    }
    
    public void Stop()
    {
        _serverProcess?.Kill();
        _serverProcess?.Dispose();
    }
}
```

**JoyCaptionService Pattern:**
```csharp
public class JoyCaptionService
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;
    
    public async Task<string> CaptionImageAsync(string imagePath, string prompt = null)
    {
        prompt ??= "Describe this image in detail with approximately 150-200 words.";
        
        var imageBytes = await File.ReadAllBytesAsync(imagePath);
        var base64Image = Convert.ToBase64String(imageBytes);
        
        var request = new
        {
            prompt = $"<image>\n{prompt}",
            image_data = new[] { new { data = base64Image, id = 1 } },
            temperature = 0.7,
            max_tokens = 300
        };
        
        var response = await _httpClient.PostAsJsonAsync($"{_serverUrl}/completion", request);
        var result = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        return result?.Content?.Trim() ?? string.Empty;
    }
}
```

**Resources:**
- Integration guide: `JOYCAPTION_INTEGRATION_GUIDE.md`
- GGUF models: `models/Joycaption/` folder
- llama.cpp downloads: https://github.com/ggerganov/llama.cpp/releases
- LLamaSharp: https://github.com/SciSharp/LLamaSharp

---

### Task 10: Combined Tag + Caption Workflow
**Priority:** Medium  
**Estimated Time:** 2-3 hours

**Requirements:**
- Single UI action: "Tag & Caption Image" / "Tag & Caption Folder"
- Run JoyTag first (fast, ~100-200ms per image)
- Then run JoyCaption (slower, ~2-5s per image)
- Combined results stored in database
- Progress UI shows both operations

**Workflow:**
```
User: Right-click folder → "Tag & Caption Folder"
  ↓
Step 1: Queue all images for tagging
  ↓ (Fast: ~200ms per image)
Store tags in database
  ↓
Step 2: Queue all images for captioning
  ↓ (Slow: ~2-5s per image)
Store captions in database
  ↓
Complete: Show summary (tags generated, captions generated, failed)
```

**UI Components:**
- Combined progress dialog showing both operations
- Settings: Run in parallel vs sequential
- Option to skip already-tagged/captioned images

**Files to Create:**
- `Diffusion.Toolkit/Services/ComboWorkflowService.cs` - Orchestrate tag + caption
- `Diffusion.Toolkit/Windows/ComboProgressWindow.xaml` - Progress UI

---

## 📋 Optional Enhancements (Post-MVP)

### Enhancement 1: Visual Similarity Clustering
**Benefit:** Skip embedding similar images (same prompt + different models)
**Time Savings:** Additional 10-15% reduction
**Complexity:** Medium

### Enhancement 2: Auto-Pause Scheduler
**Benefit:** Automatically pause embedding during specified hours
**User Experience:** Don't interfere with gaming/work
**Complexity:** Low

### Enhancement 3: Distributed Processing
**Benefit:** Process queue across multiple machines
**Use Case:** Very large datasets (millions of images)
**Complexity:** High

### Enhancement 4: Embedding Preview
**Benefit:** Show similar images before queueing entire folder
**User Experience:** Confidence in results before long process
**Complexity:** Medium

### Enhancement 5: Progress Persistence
**Benefit:** Detailed logging of what's been processed
**Use Case:** Resume after crashes, audit trail
**Complexity:** Low

### Enhancement 6: Tag-Based Filtering
**Benefit:** Search images by JoyTag-generated tags
**Use Case:** "Show me all images tagged 'blue_hair' AND '1girl'"
**Complexity:** Low

### Enhancement 7: Caption Search
**Benefit:** Full-text search across JoyCaption-generated descriptions
**Use Case:** "Find images with 'sunset over mountains'"
**Complexity:** Low (PostgreSQL full-text search)

### Enhancement 8: Batch Caption Editing
**Benefit:** Edit multiple captions at once, apply templates
**Use Case:** Consistent formatting, metadata cleanup
**Complexity:** Medium

---

## 🧪 Testing Checklist

### Before Production Use:
- [ ] Run migration V6 on test database
- [ ] Test queue operations (add folder, add recursive, clear)
- [ ] Test worker state machine (start → pause → resume → stop)
- [ ] Verify VRAM management (pause keeps models, stop unloads)
- [ ] Test priority queue (embed now moves to front)
- [ ] Test orphan handling (delete FINAL image → ORIG re-queued)
- [ ] Test large dataset (773K images, 22-23 hours)
- [ ] Verify cache hit rates (95%+ for batch prompts)
- [ ] Test context menu on folders and images
- [ ] Test control bar buttons and statistics display
- [ ] Test app restart (state restoration)
- [ ] Monitor GPU memory usage (should be ~8-10GB with all models)

### Performance Validation:
- [ ] Text embeddings: ~286ms for 3 vectors
- [ ] Image embeddings: ~150-200ms per image
- [ ] Batch throughput: 5-6 images/second
- [ ] Cache hit rate: 85-95%
- [ ] Total time for 425K unique images: ~22-23 hours

---

## 📚 Key Documentation Files

- `EMBEDDING_SYSTEM_STATUS.md` - System architecture and test results
- `ONNX_GPU_SETUP.md` - CUDA installation and troubleshooting
- `INTELLIGENT_DEDUPLICATION_SYSTEM.md` - Deduplication strategy and performance
- `POSTGRESQL_MIGRATION_TODO.md` - This file
- `.github/copilot-instructions.md` - Project overview for AI assistance

---

## 🚀 Quick Start for Next Developer

### 1. Database Setup
```bash
cd D:\AI\Github_Desktop\DiffusionToolkit
docker compose up -d  # Starts PostgreSQL 18 + pgvector
```

### 2. Apply Migrations
```csharp
var dataStore = new PostgreSQLDataStore(connectionString);
await dataStore.Create(() => new object(), _ => { });
// This applies all migrations V1-V6 automatically
```

### 3. Initialize Embedding Service
```csharp
var config = EmbeddingConfig.CreateDefault(modelsDirectory);
var embeddingService = new EmbeddingService(config);
var workerService = new EmbeddingWorkerService(dataStore, embeddingService);
await workerService.RestoreStateAsync();
```

### 4. Test Queue Operations
```csharp
// Queue a folder
await dataStore.QueueFolderForEmbeddingAsync(folderId, priority: 0);

// Start worker
await workerService.StartAsync();

// Monitor progress
workerService.ProgressUpdated += (s, e) => {
    Console.WriteLine($"Processed: {e.TotalProcessed}, Queue: {e.QueueSize}");
};
```

---

## 📞 Support & Questions

**GPU Issues:**
- RTX 3080 Ti (compute 8.6) incompatible with ONNX Runtime 1.20.1 + CUDA 12.9
- Use RTX 5090 (compute 12.0) or newer, or try CUDA 11.8
- All models fit on single 24GB GPU

**Performance Issues:**
- Check cache hit rate (should be 85-95%)
- Verify batch size (32-64 recommended)
- Monitor GPU utilization (should be 70-90%)
- Check for orphaned images slowing queries

**Database Issues:**
- Ensure pgvector extension installed
- Check connection pooling (default 100 connections)
- Monitor index usage with `EXPLAIN ANALYZE`
- Vacuum database periodically for large datasets

---

**Last Updated:** November 15, 2025  
**Status:** Core backend complete, UI integration pending  
**Next Priority:** Task 1 (Control Bar UI) or Task 3 (MainWindow Integration)
