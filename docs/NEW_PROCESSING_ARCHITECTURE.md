# New Processing Architecture (Experimental)

## Overview

The new processing architecture is a modular, three-tier system for GPU-accelerated image processing. It replaces the legacy `BackgroundTaggingService` and `BackgroundFaceDetectionService` with a unified, scalable approach.

**Enable via:** Settings → Tagging & Captioning → GPU Settings → ☑ "Use New Processing Architecture (Experimental)"

---

## Architecture Hierarchy

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           UI (MainWindow.xaml.cs)                           │
│  • Checkboxes: EnableTagging, EnableCaptioning, EnableEmbedding, EnableFace │
│  • Start/Stop/Pause buttons                                                 │
│  • Reads GPU allocation settings (ConcurrentTaggingAllocation, etc.)        │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    TIER 1: GlobalProcessingOrchestrator                     │
│  Location: Services/Processing/GlobalProcessingOrchestrator.cs              │
│                                                                             │
│  RESPONSIBILITIES:                                                          │
│  • Receives enabled services from UI (Dictionary<ProcessingType, GpuAlloc>) │
│  • Checks queue counts BEFORE creating service orchestrators                │
│  • Creates/manages service orchestrators only for non-empty queues          │
│  • Determines ProcessingMode (Solo vs Concurrent)                           │
│  • Aggregates progress events from all services for UI display              │
│  • Handles global Start/Stop/Pause/Resume commands                          │
│  • Raises AllServicesCompleted event when everything is done                │
│                                                                             │
│  KEY DECISION LOGIC (StartServicesAsync):                                   │
│    for each (type, gpuAllocation) in serviceAllocations:                    │
│      1. Check queue count → skip if queue empty                             │
│      2. Build ServiceAllocation from gpuAllocation                          │
│      3. Create or get cached service orchestrator                           │
│      4. Start orchestrator with allocation                                  │
│                                                                             │
│  DOES NOT:                                                                  │
│  • Load ML models (service orchestrators do that)                           │
│  • Access database directly for queue items                                 │
│  • Process individual images                                                │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                    ┌─────────────────┼─────────────────┬─────────────────┐
                    ▼                 ▼                 ▼                 ▼
┌────────────────────────┐ ┌────────────────────────┐ ┌────────────────────────┐
│  TaggingOrchestrator   │ │ CaptioningOrchestrator │ │ EmbeddingOrchestrator  │ ...
└────────────────────────┘ └────────────────────────┘ └────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    TIER 2: Service Orchestrators                            │
│  Base: Services/Processing/BaseServiceOrchestrator.cs                       │
│  Implementations:                                                           │
│    TaggingOrchestrator.cs, CaptioningOrchestrator.cs,                       │
│    EmbeddingOrchestrator.cs, FaceDetectionOrchestrator.cs                   │
│                                                                             │
│  RESPONSIBILITIES:                                                          │
│  • LOAD ML MODELS based on GPU allocation (owns model lifecycle)            │
│  • Manage job queue (System.Threading.Channels)                             │
│  • Spawn workers that use the loaded models                                 │
│  • Populate queue using cursor-based pagination from database               │
│  • Track progress, ETA calculation, skip counts                             │
│  • Handle Pause/Resume (workers wait in loop)                               │
│  • Write results back to database after worker processes image              │
│  • Raise ProgressChanged/StatusChanged/Completed events                     │
│  • UNLOAD MODELS on shutdown (free GPU memory)                              │
│                                                                             │
│  MODEL LOADING STRATEGY:                                                    │
│    - Orchestrator loads N model instances based on allocation               │
│    - Example: allocation "2,1" → 2 models on GPU0, 1 on GPU1 = 3 total      │
│    - Workers receive reference to model, don't own it                       │
│    - Some models can be shared across workers (if thread-safe)              │
│    - Others need 1:1 worker:model ratio (ONNX sessions often not safe)      │
│                                                                             │
│  ABSTRACT METHODS (subclasses must implement):                              │
│  • CountItemsNeedingProcessingAsync() → int                                 │
│  • GetItemsNeedingProcessingAsync(batchSize, lastId) → List<int>            │
│  • GetJobForImageAsync(imageId) → ProcessingJob?                            │
│  • WriteResultAsync(ProcessingResult) → void                                │
│  • CreateModelInstance(gpuId) → object (model/service instance)             │
│  • CreateWorker(gpuId, workerId, model) → IProcessingWorker                 │
│                                                                             │
│  QUEUE FLOW:                                                                │
│    1. StartAsync receives ServiceAllocation                                 │
│    2. Counts total items needing processing                                 │
│    3. If count = 0, exits early (raises Completed)                          │
│    4. Loads models based on allocation (N per GPU)                          │
│    5. Creates bounded Channel<ProcessingJob>(capacity: 1000)                │
│    6. Starts PopulateQueueAsync task (fills queue from DB)                  │
│    7. Spawns N workers, each with reference to a model                      │
│    8. Workers consume from queue until empty                                │
│    9. Raises Completed when all workers finish                              │
│   10. Unloads all models, frees GPU memory                                  │
│                                                                             │
│  DOES NOT:                                                                  │
│  • Know about global coordination (that's Tier 1)                           │
│  • Process images directly (that's workers)                                 │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                        TIER 3: Workers                                      │
│  Interface: Services/Processing/IProcessingWorker.cs                        │
│  Implementations: TaggingWorker, CaptioningWorker, etc.                     │
│                                                                             │
│  RESPONSIBILITIES:                                                          │
│  • Process single images via ProcessAsync(job) → ProcessingResult           │
│  • Use model reference provided by orchestrator (does NOT own model)        │
│  • Return results (tags, captions, embeddings, faces) as ProcessingResult   │
│                                                                             │
│  KEY PROPERTIES:                                                            │
│    GpuId    - Which GPU this worker is assigned to                          │
│    WorkerId - Unique ID within the orchestrator                             │
│    Model    - Reference to model owned by orchestrator                      │
│    IsBusy   - Currently processing an image                                 │
│                                                                             │
│  CRITICAL RULES:                                                            │
│  • NO DATABASE ACCESS - workers only process images                         │
│  • NO MODEL LOADING - orchestrator owns model lifecycle                     │
│  • STATELESS - workers are lightweight, can be recycled                     │
│                                                                             │
│  Workers only:                                                              │
│    • Receive ProcessingJob (ImageId, ImagePath, AdditionalData)             │
│    • Use orchestrator-provided model to process                             │
│    • Return ProcessingResult (Success, Data, ErrorMessage)                  │
│                                                                             │
│  EXAMPLE (TaggingWorker):                                                   │
│    Constructor: Receives JoyTagService reference from orchestrator          │
│    ProcessAsync:                                                            │
│      - Read image from disk                                                 │
│      - Call model.TagImageAsync(imagePath)                                  │
│      - Return List<(string Tag, float Confidence)>                          │
│    Dispose: NO-OP (doesn't own model)                                       │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Model Ownership Clarification

**Current Implementation (needs refactoring):**
```
Worker creates model → Worker owns model → Worker disposes model
```

**Correct Design:**
```
Orchestrator creates model → Worker receives reference → Orchestrator disposes model
```

**Why This Matters:**
1. **Multiple workers per model**: Some models are thread-safe and can be shared
2. **Expensive initialization**: Model loading is slow; avoid doing it per-worker
3. **Memory management**: Orchestrator knows total allocation, can manage GPU memory
4. **Graceful shutdown**: Orchestrator ensures all workers stop before unloading models

**Model Sharing Patterns:**
| Service | Thread-Safe? | Workers per Model | Notes |
|---------|--------------|-------------------|-------|
| JoyTag ONNX | **Yes** | N:1 (10 workers share 1 model) | ONNX Runtime Run() is thread-safe with CUDA EP |
| WD ONNX | **Yes** | N:1 (10 workers share 1 model) | Same as JoyTag |
| JoyCaption GGUF | **Batched** | Special | See LlamaSharp note below |
| BGE Embedding | **Yes** | N:1 | ONNX with CUDA EP |
| CLIP Embedding | **Yes** | N:1 | ONNX with CUDA EP |
| Face Detection | **Yes** | N:1 | ONNX with CUDA EP (RetinaFace, ArcFace, YOLO11) |

**All ONNX models use CUDA execution provider** - thread-safe, no locks needed.

**LlamaSharp/GGUF Captioning - Special Case:**
LlamaSharp's `BatchedExecutor` supports batched inference but is NOT the same as parallel workers:

| Approach | VRAM Usage | Throughput | Latency | Best For |
|----------|------------|------------|---------|----------|
| Batched (1 model, N conversations) | Low | Good | Same | VRAM-constrained |
| Parallel (N models, N workers) | High (N × model) | Better | Lower | VRAM-rich, speed priority |

**Why multiple models can be faster:**
- LLM inference is often ~30-50% GPU compute utilization
- Autoregressive generation is sequential per-token regardless of batching
- Multiple model instances = true parallel generation
- JoyCaption Q4 is ~8GB → 3-4 instances fit on 32GB GPU

**Current Implementation:** Supports multiple model instances (CaptioningAllocation setting)
**Recommendation:** For 32GB GPU, use 2-3 captioning model instances for best latency

**Legacy Implementation Reference:**
The legacy `BackgroundTaggingService.RunTaggingGpuOrchestrator()` uses exactly this pattern:
- Creates 1 model instance per 10 workers (`const int workersPerInstance = 10`)
- Workers receive reference to shared model
- No locks needed - ONNX Runtime handles thread safety internally

```csharp
// From BackgroundTaggingService.cs lines 1067-1120
const int workersPerInstance = 10;
int instanceCount = Math.Max(1, (workerCount + workersPerInstance - 1) / workersPerInstance);

// Create instance pools
for (int pool = 0; pool < instanceCount; pool++)
{
    var joyTagService = new JoyTagService(...);
    await joyTagService.InitializeAsync();
    joyTagInstances.Add(joyTagService);
}

// Start workers, assigning each to an instance pool
for (int i = 0; i < workerCount; i++)
{
    int poolIndex = workerId / workersPerInstance;  // Assign worker to pool
    var joyTag = joyTagInstances[poolIndex];        // Share model reference
    var workerTask = Task.Run(() => RunTaggingWorker(gpuId, workerId, joyTag, wdTag, ct));
}
```

**TODO:** The new architecture should adopt this same pattern - orchestrator loads models, workers receive shared references.

---

## Data Types

### ProcessingType (enum)
```csharp
Tagging, Captioning, Embedding, FaceDetection
```

### ProcessingMode (enum)
```csharp
Solo      // Single service running - can use more VRAM
Concurrent // Multiple services running - must share VRAM
```

### ServiceAllocation
```csharp
ProcessingType ProcessingType
ProcessingMode Mode
GpuAllocation[] GpuAllocations  // One per GPU being used
int TotalWorkers  // Sum of all WorkerCounts
int TotalModels   // Sum of all ModelCounts
```

### GpuAllocation
```csharp
int GpuId              // 0, 1, 2...
int WorkerCount        // Number of parallel workers on this GPU
int ModelCount         // Number of model instances:
                       //   - ONNX services: Always 1 (workers share)
                       //   - Captioning: Same as WorkerCount (1:1)
double VramCapacityGb  // Total VRAM (e.g., 32)
double MaxUsagePercent // e.g., 90
```

### Allocation Semantics by Service Type

| Service | Allocation Value | Workers | Models | VRAM Calculation |
|---------|------------------|---------|--------|------------------|
| **Tagging** | Worker count | N | 1 per GPU | Fixed 2.6GB per GPU |
| **Embedding** | Worker count | N | 1 per GPU | Fixed 7.6GB per GPU |
| **FaceDetection** | Worker count | N | 1 per GPU | Fixed 0.8GB per GPU |
| **Captioning** | Model count | N (=models) | N per GPU | N × 5.6GB per GPU |

**Example:** `ConcurrentTaggingAllocation = "10,5"`
- GPU0: 10 workers sharing 1 model (2.6GB VRAM)
- GPU1: 5 workers sharing 1 model (2.6GB VRAM)

**Example:** `ConcurrentCaptioningAllocation = "2,1"`
- GPU0: 2 model instances, 2 workers (11.2GB VRAM)
- GPU1: 1 model instance, 1 worker (5.6GB VRAM)

### ProcessingJob
```csharp
int ImageId
string ImagePath
object? AdditionalData  // Service-specific (e.g., prompt text for embedding)
```

### ProcessingResult
```csharp
int ImageId
bool Success
string? ErrorMessage
object? Data  // Service-specific output:
              //   Tagging: List<(string Tag, float Confidence)>
              //   Captioning: string caption
              //   Embedding: float[] vector
              //   FaceDetection: List<FaceData>
```

---

## Settings Integration

### UI Checkbox → Service Enabled
| Checkbox | ProcessingType |
|----------|----------------|
| `EnableTagging` | `ProcessingType.Tagging` |
| `EnableCaptioning` | `ProcessingType.Captioning` |
| `EnableEmbedding` | `ProcessingType.Embedding` |
| `EnableFaceDetection` | `ProcessingType.FaceDetection` |

### VRAM Requirements per Model Type
| Model Type | VRAM (approx) | Speed | Time/Image | Shareable | Load Priority |
|------------|---------------|-------|------------|-----------|---------------|
| Tagging (JoyTag + WD) | ~2.6 GB | Very Fast | ~27ms | Yes (N:1) | 1 (First) |
| Face Detection (YOLO + ArcFace) | ~0.8 GB | Very Fast | ~27ms | Yes (N:1) | 1 (First) |
| Embedding (BGE + CLIP) | ~7.6 GB | Fast | ~150-200ms | Yes (N:1) | 2 |
| Captioning (JoyCaption Q4) | ~5.6 GB | Slow | ~1-2s | No (1:1) | 3 (Last, fills remaining) |

### Throughput Analysis (per GPU, single model instance)

| Service | Time/Image | Images/Hour | Queue of 5000 |
|---------|------------|-------------|---------------|
| Tagging | 27ms | ~133,000 | ~2.3 min |
| Face Detection | 27ms | ~133,000 | ~2.3 min |
| Embedding | 175ms | ~20,500 | ~14.5 min |
| Captioning (×1) | 1.5s | ~2,400 | ~35 min |
| Captioning (×2) | 1.5s | ~4,800 | ~17 min |
| Captioning (×4) | 1.5s | ~9,600 | ~8.5 min |

**Key Insight:** Captioning is the bottleneck by 50-75×. Priority loading ensures fast services complete quickly, then all VRAM shifts to maximizing captioning instances.

### Priority-Based Loading Strategy

**Philosophy:** Load smallest/fastest services first, then fill remaining VRAM with captioning instances.

**Load Order:**
1. **Priority 1 - Small & Fast:** Tagging (~2.6GB) + Face Detection (~0.8GB) = ~3.4GB
   - Load on ALL GPUs with sufficient VRAM
   - Many workers share one model set
   - Will complete quickly, freeing workers for other tasks

2. **Priority 2 - Medium & Fast:** Embedding (~7.6GB)
   - Load on GPUs with 7.6GB+ headroom after Priority 1
   - Many workers share one model set
   - Faster than captioning despite larger VRAM

3. **Priority 3 - Fill Remaining:** Captioning (~5.6GB per instance)
   - Each instance needs 1:1 worker (not shareable)
   - Load as many instances as VRAM allows
   - When Priority 2 completes → load additional captioning instance in freed VRAM

### Example: 32GB + 12GB Setup with Priority Loading

**Initial Load (all queues have items):**

| GPU | Priority 1 | Priority 2 | Priority 3 | Total | Headroom |
|-----|------------|------------|------------|-------|----------|
| GPU0 (32GB) | Tag+Face (3.4GB) | Embed (7.6GB) | Caption ×2 (11.2GB) | 22.2GB | 9.8GB |
| GPU1 (12GB) | Tag+Face (3.4GB) | Embed (7.6GB) | - | 11.0GB | 1.0GB |

*GPU1 cannot fit Captioning (5.6GB) while Embedding (7.6GB) is loaded*

**After Embedding Queue Empties:**

| GPU | Priority 1 | Priority 2 | Priority 3 | Total | Headroom |
|-----|------------|------------|------------|-------|----------|
| GPU0 (32GB) | Tag+Face (3.4GB) | *(freed)* | Caption ×3 (16.8GB) | 20.2GB | 11.8GB |
| GPU1 (12GB) | Tag+Face (3.4GB) | *(freed)* | Caption ×1 (5.6GB) | 9.0GB | 3.0GB |

*Total captioning instances: 2 → 4 (100% throughput increase)*

**After Tagging Queue Empties:**

| GPU | Priority 1 | Priority 2 | Priority 3 | Total | Headroom |
|-----|------------|------------|------------|-------|----------|
| GPU0 (32GB) | Face (0.8GB) | - | Caption ×4 (22.4GB) | 23.2GB | 8.8GB |
| GPU1 (12GB) | Face (0.8GB) | - | Caption ×2 (11.2GB) | 12.0GB | 0GB |

*Total captioning instances: 4 → 6 (additional 50% throughput)*

### Dynamic Reallocation Algorithm

```
ON queue_empty(service):
    FOR each GPU:
        freed_vram = service.vram_usage[GPU]
        Unload service models from GPU
        
        # Fill freed VRAM with captioning instances
        WHILE freed_vram >= 5.6GB AND captioning_queue > 0:
            Load additional CaptioningModel on GPU
            freed_vram -= 5.6GB
            captioning_instances++
        
        Log("GPU{id}: Loaded {n} additional captioning instances")
```

**Reallocation Events:**
| Event | GPU0 Action | GPU1 Action |
|-------|-------------|-------------|
| Embedding completes | +1 Caption (7.6GB freed) | +1 Caption (7.6GB freed) |
| Tagging completes | +1 Caption (2.6GB freed, now have enough) | Already at max |
| Face Detection completes | Minimal gain (0.8GB) | Minimal gain |

### Settings Schema

**Current Implementation:**
```json
{
  // Captioning: value = model count (each needs 5.6GB VRAM)
  "ConcurrentCaptioningAllocation": "2,0",   // 2 models on GPU0, 0 on GPU1
  "SoloCaptioningAllocation": "3,1",         // 3 models on GPU0, 1 on GPU1 when running alone
  
  // ONNX services: value = worker count (all share 1 model per GPU)
  "ConcurrentTaggingAllocation": "10,5",     // 10 workers on GPU0, 5 on GPU1 (fixed 2.6GB each)
  "ConcurrentEmbeddingAllocation": "8,4",    // 8 workers on GPU0, 4 on GPU1 (fixed 7.6GB each)
  "ConcurrentFaceDetectionAllocation": "6,6", // 6 workers on GPU0, 6 on GPU1 (fixed 0.8GB each)
  
  // Same pattern for Solo mode
  "SoloTaggingAllocation": "15,10",
  "SoloEmbeddingAllocation": "12,8",
  "SoloFaceDetectionAllocation": "10,10"
}
```

**VRAM Calculation:**
- **Captioning:** `count × 5.6GB` (scales with allocation value)
- **Tagging/Embedding/FaceDetection:** Fixed VRAM per GPU if count > 0

**Example: Concurrent mode on 32GB + 12GB GPUs:**
```
GPU0 (32GB @ 90% = 28.8GB available):
  Captioning:     2 × 5.6 = 11.2GB
  Tagging:        1 × 2.6 =  2.6GB (10 workers share)
  Embedding:      1 × 7.6 =  7.6GB (8 workers share)
  FaceDetection:  1 × 0.8 =  0.8GB (6 workers share)
  Total: 22.2GB ✓

GPU1 (12GB @ 90% = 10.8GB available):
  Captioning:     0 × 5.6 =  0.0GB
  Tagging:        1 × 2.6 =  2.6GB (5 workers share)
  Embedding:      1 × 7.6 =  7.6GB (4 workers share)
  FaceDetection:  1 × 0.8 =  0.8GB (6 workers share)
  Total: 11.0GB ✓ (just fits!)
```

**Dynamic Allocation Mode (Implemented):**

When `EnableDynamicVramAllocation = true`, the system automatically:

1. **Priority-Based Initial Loading:**
   - Loads services by priority order (1→2→3)
   - Only loads services that fit in available VRAM
   - Captioning may be deferred if no VRAM available initially

2. **Dynamic Reallocation on Queue Completion:**
   - When a service's queue empties, its VRAM is freed
   - Freed VRAM is used to start/expand captioning instances
   - Captioning that was deferred initially can now start

```json
{
  "UseNewProcessingArchitecture": true,
  "EnableDynamicVramAllocation": true,
  "DefaultOnnxWorkersPerGpu": 8,
  "MaxVramUsagePercent": 90,
  "GpuVramCapacity": "32,12",
  "GpuDevices": "0,1"
}
```

**API:**
```csharp
// Start with dynamic allocation (recommended for large queues)
await globalOrchestrator.StartWithDynamicAllocationAsync(
    enableTagging: true,
    enableCaptioning: true,
    enableEmbedding: true,
    enableFaceDetection: true,
    defaultWorkersPerOnnxService: 8,
    cancellationToken);
```

**Reallocation Events (logged):**
```
GlobalOrchestrator: Embedding completed, checking for reallocation
GlobalOrchestrator: Released 7.6GB from GPU0 (Embedding)
GlobalOrchestrator: Released 7.6GB from GPU1 (Embedding)
GlobalOrchestrator: Can add 1 captioning instance(s) on GPU0 (7.6GB available)
GlobalOrchestrator: Can add 1 captioning instance(s) on GPU1 (7.6GB available)
```

**Future Enhancement (WorkersPerModel):**
```json
{
  "WorkersPerModel": {
    "Tagging": 10,
    "FaceDetection": 6,
    "Embedding": 8,
    "Captioning": 1
  }
}
```

---

## Queue Flag Semantics (Three-State Logic)

| Flag Value | Meaning |
|------------|---------|
| `needs_* = true` | Queued for processing |
| `needs_* = false` | Has been processed |
| `needs_* = null` | Not queued, not yet processed |

**Clear Queue Operation:** Sets `true → null` (not `true → false`)

---

## Database Queue Columns

### Generic Processing Flags
| Column | Purpose |
|--------|---------|
| `needs_tagging` | Queue for JoyTag/WD tagger |
| `needs_captioning` | Queue for JoyCaption |
| `needs_embedding` | Queue for BGE/CLIP embeddings (legacy) |
| `needs_face_detection` | Queue for face detection |

### Granular Embedding Flags (V10 Schema)
| Column | Purpose |
|--------|---------|
| `needs_bge_prompt_embedding` | BGE embedding of prompt |
| `needs_bge_caption_embedding` | BGE embedding of caption |
| `needs_bge_tags_embedding` | BGE embedding of tags |
| `needs_clip_l_prompt_embedding` | CLIP-L embedding of prompt (SDXL regen) |
| `needs_clip_g_prompt_embedding` | CLIP-G embedding of prompt (SDXL regen) |
| `needs_clip_vision_embedding` | CLIP vision embedding of image pixels |
| `needs_t5xxl_prompt_embedding` | T5-XXL embedding (Flux stub) |
| `needs_t5xxl_caption_embedding` | T5-XXL caption embedding (Flux stub) |

---

## Event Flow

```
User clicks "Start All"
        │
        ▼
MainWindow.StartUnifiedProcessing()
        │
        ├─ Build serviceAllocations dictionary from enabled checkboxes + settings
        │
        ▼
GlobalProcessingOrchestrator.StartServicesAsync(serviceAllocations, mode)
        │
        ├─ For each service in dictionary:
        │     ├─ Check queue count (skip if 0)
        │     ├─ Create ServiceAllocation
        │     └─ Start service orchestrator
        │
        ▼
BaseServiceOrchestrator.StartAsync(allocation)
        │
        ├─ Count total items
        ├─ Create Channel<ProcessingJob>
        ├─ Start PopulateQueueAsync (background)
        ├─ Spawn N workers (foreach GPU × ModelCount)
        │     │
        │     ▼
        │   Worker.InitializeAsync() ─── Load model onto GPU
        │     │
        │     ▼
        │   Worker.ProcessAsync(job) ─── Process image
        │     │
        │     ▼
        │   Orchestrator.WriteResultAsync() ─── Write to DB
        │     │
        │     (repeat until queue empty)
        │     │
        │     ▼
        │   Worker.ShutdownAsync() ─── Unload model
        │
        ▼
Orchestrator raises Completed event
        │
        ▼
GlobalProcessingOrchestrator receives ServiceCompleted
        │
        ├─ When all services done: raises AllServicesCompleted
        │
        ▼
UI updates status
```

---

## File Locations

```
Diffusion.Toolkit/Services/Processing/
├── GlobalProcessingOrchestrator.cs    # Tier 1: Global coordinator
├── BaseServiceOrchestrator.cs         # Tier 2: Abstract base class
├── TaggingOrchestrator.cs             # Tier 2.5 + TaggingWorker
├── CaptioningOrchestrator.cs          # Tier 2.5 + CaptioningWorker
├── EmbeddingOrchestrator.cs           # Tier 2.5 + EmbeddingWorker
├── FaceDetectionOrchestrator.cs       # Tier 2.5 + FaceDetectionWorker
├── IServiceOrchestrator.cs            # Interface for Tier 2
├── IProcessingWorker.cs               # Interface for Tier 3
└── ProcessingTypes.cs                 # Enums and data classes
```

---

## Key Design Principles

1. **Separation of Concerns**
   - GlobalOrchestrator: UI integration, service selection, mode determination
   - ServiceOrchestrator: Queue management, worker lifecycle, progress tracking
   - Worker: Model loading, image processing, no DB access

2. **Lazy Resource Loading**
   - Orchestrators only created when service is enabled AND queue is non-empty
   - Workers only load models during InitializeAsync (not constructor)

3. **Cursor-Based Pagination**
   - Queue populated in batches of 1000 using `lastId` cursor
   - Prevents memory issues with large queues (1M+ images)

4. **Thread-Safe Progress**
   - `Interlocked.Increment/Decrement` for counters
   - `Channel<T>` for thread-safe job queue

5. **Graceful Shutdown**
   - CancellationToken propagated through all layers
   - Workers complete current job before stopping
   - Models properly unloaded to free GPU memory

---

## Legacy Services (Being Replaced)

| Legacy Service | Replacement |
|----------------|-------------|
| `BackgroundTaggingService` | `TaggingOrchestrator` + `CaptioningOrchestrator` + `EmbeddingOrchestrator` |
| `BackgroundFaceDetectionService` | `FaceDetectionOrchestrator` |

**Note:** Legacy services are still used when "Use New Processing Architecture (Experimental)" is disabled.

---

## Known Limitations / TODO

1. **Model Ownership Refactor (HIGH PRIORITY)**: Current workers create their own models. Should refactor so orchestrators own models and workers receive references. This enables:
   - Multiple workers sharing one model (when thread-safe)
   - Better GPU memory management
   - Proper cleanup order (workers stop → models unload)

2. **Queue Count Reporting**: Currently reads from legacy services. Should add unified queue count event to GlobalProcessingOrchestrator.

3. **Progress Bar**: Individual service progress works; global aggregated progress not yet implemented.

4. **Error Recovery**: Workers that fail initialization don't retry; service continues with remaining workers.

5. **Hot Reload Settings**: Changing GPU allocation requires restart; should support runtime reconfiguration.

6. **IProcessingWorker Interface**: Needs update to remove `InitializeAsync()` and `ShutdownAsync()` since model lifecycle moves to orchestrator. Replace with constructor injection of model reference.

---

*Last Updated: January 11, 2026*
*Schema Version: V10 (Granular Embeddings)*
