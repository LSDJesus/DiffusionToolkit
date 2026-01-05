# ONNX Implementation - Next Steps

## ✅ What's Complete

1. **Python Conversion Script** - `scripts/convert_models_to_onnx.py`
   - Converts BGE-large-en-v1.5 to ONNX
   - Converts CLIP-ViT-H vision encoder
   - Attempts to convert CLIP-L and CLIP-G text encoders from your safetensors files

2. **C# ONNX Encoders Created**:
   - `BGETextEncoder.cs` - BGE-large-en-v1.5 (1024D) for semantic search
   - `CLIPTextEncoder.cs` - CLIP-L (768D) and CLIP-G (1280D) text encoders
   
3. **NuGet Packages Added**:
   - `Microsoft.ML.OnnxRuntime.Gpu` v1.20.1
   - `Microsoft.ML.Tokenizers` v0.22.0
   - `SixLabors.ImageSharp` v3.1.8
   - `System.Numerics.Tensors` v9.0.0

4. **PostgreSQL Port Updated** to 5436 (no conflicts)

---

## ⏳ What's Next

### **Step 1: Convert Models to ONNX**

```powershell
# Install Python dependencies
pip install torch transformers optimum onnx onnxruntime-gpu sentence-transformers safetensors

# Run conversion script
cd D:\AI\Github_Desktop\DiffusionToolkit
python scripts/convert_models_to_onnx.py
```

**Expected Output:**
```
D:\AI\Github_Desktop\DiffusionToolkit\models\onnx\
├── bge-large-en-v1.5\
│   ├── model.onnx       (~1.3 GB)
│   ├── vocab.txt
│   └── tokenizer.json
├── clip-vit-h\
│   ├── model.onnx       (~600 MB)
│   └── preprocessor_config.json
├── clip-l\
│   └── model.onnx       (~300 MB)
└── clip-g\
    └── model.onnx       (~1.5 GB)
```

**Note:** CLIP-L and CLIP-G conversion from safetensors may fail. Alternative:
- Download pre-converted ONNX models from HuggingFace
- OR use PyTorch embedding via Python interop (faster to implement, slightly slower inference)

---

### **Step 2: Create Remaining Encoders**

Need to create:

1. **CLIPVisionEncoder.cs** - CLIP-ViT-H for image embeddings
2. **EmbeddingService.cs** - High-level API combining all encoders
3. **Configuration** - Model paths, GPU device assignment

Example implementation needed:

```csharp
// CLIPVisionEncoder.cs
public class CLIPVisionEncoder : IDisposable
{
    // Load CLIP-ViT-H ONNX model
    // Process images with ImageSharp
    // Generate 1024D image embeddings
    // GPU device 0 (RTX 5090)
}

// EmbeddingService.cs
public class EmbeddingService : IDisposable
{
    private BGETextEncoder _bge;          // GPU 0
    private CLIPTextEncoder _clipL;       // GPU 1
    private CLIPTextEncoder _clipG;       // GPU 1
    private CLIPVisionEncoder _clipH;     // GPU 0
    
    public async Task<Embeddings5D> GenerateAllEmbeddingsAsync(string prompt, string negativeProm, string imagePath)
    {
        // Parallel execution on both GPUs
        var promptTask = Task.WhenAll(
            _bge.EncodeAsync(prompt),      // GPU 0
            _clipL.EncodeAsync(prompt),    // GPU 1
            _clipG.EncodeAsync(prompt)     // GPU 1
        );
        
        var imageTask = _clipH.EncodeAsync(imagePath); // GPU 0
        
        await Task.WhenAll(promptTask, imageTask);
        
        return new Embeddings5D { ... };
    }
}
```

---

### **Step 3: Wire Up to Database**

Update `EmbeddingCacheService` to use ONNX encoders:

```csharp
public class EmbeddingCacheService
{
    private readonly EmbeddingService _embeddingService;
    private readonly PostgreSQLDataStore _dataStore;
    
    public async Task<long> GetOrCreateTextEmbeddingAsync(string text, string contentType)
    {
        var hash = ComputeSHA256(text);
        
        // Check cache
        var existing = await _dataStore.GetEmbeddingByHashAsync(hash);
        if (existing != null)
        {
            await _dataStore.IncrementEmbeddingReferenceCountAsync(existing.Id);
            return existing.Id;
        }
        
        // Generate new embeddings (parallel on both GPUs)
        var bge = await _embeddingService.GetBGEEmbedding(text);
        var clipL = await _embeddingService.GetCLIPLEmbedding(text);
        var clipG = await _embeddingService.GetCLIPGEmbedding(text);
        
        // Store in cache
        var id = await _dataStore.InsertEmbeddingCacheAsync(new EmbeddingCache
        {
            ContentHash = hash,
            ContentType = contentType,
            ContentText = text,
            BgeEmbedding = bge,
            ClipLEmbedding = clipL,
            ClipGEmbedding = clipG
        });
        
        return id;
    }
}
```

---

### **Step 4: Configuration**

Create `appsettings.json` or configuration class:

```json
{
  "Embeddings": {
    "ModelPaths": {
      "BGE": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx/bge-large-en-v1.5/model.onnx",
      "BGEVocab": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx/bge-large-en-v1.5/vocab.txt",
      "CLIPL": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx/clip-l/model.onnx",
      "CLIPLVocab": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx/clip-l/vocab.txt",
      "CLIPG": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx/clip-g/model.onnx",
      "CLIPGVocab": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx/clip-g/vocab.txt",
      "CLIPH": "D:/AI/Github_Desktop/DiffusionToolkit/models/onnx/clip-vit-h/model.onnx"
    },
    "GPUDevices": {
      "BGE": 0,
      "CLIPH": 0,
      "CLIPL": 1,
      "CLIPG": 1
    },
    "BatchSizes": {
      "Text": 64,
      "Image": 32
    }
  },
  "PostgreSQL": {
    "ConnectionString": "Host=localhost;Port=5436;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025"
  }
}
```

---

### **Step 5: Test Embeddings**

Create test script:

```csharp
// TestEmbeddings.cs
var service = new EmbeddingService(config);

// Test text embeddings
var bge = await service.GetBGEEmbedding("a beautiful landscape");
Console.WriteLine($"BGE: {bge.Length}D, norm: {bge.Sum(x => x * x)}");

var clipL = await service.GetCLIPLEmbedding("a beautiful landscape");
Console.WriteLine($"CLIP-L: {clipL.Length}D");

var clipG = await service.GetCLIPGEmbedding("a beautiful landscape");
Console.WriteLine($"CLIP-G: {clipG.Length}D");

// Test image embedding
var clipH = await service.GetCLIPHEmbedding("test_image.png");
Console.WriteLine($"CLIP-H: {clipH.Length}D");

// Test deduplication
var cache = new EmbeddingCacheService(service, dataStore);
var id1 = await cache.GetOrCreateTextEmbeddingAsync("same text", "prompt");
var id2 = await cache.GetOrCreateTextEmbeddingAsync("same text", "prompt");
Console.WriteLine($"Cache hit: {id1 == id2}"); // Should be true
```

---

### **Step 6: Bulk Import Pipeline**

```csharp
// BulkEmbeddingPipeline.cs
public class BulkEmbeddingPipeline
{
    private readonly EmbeddingCacheService _cache;
    private readonly PostgreSQLDataStore _dataStore;
    
    public async Task ProcessImagesAsync(IEnumerable<string> imagePaths, int batchSize = 64)
    {
        var batches = imagePaths.Chunk(batchSize);
        
        foreach (var batch in batches)
        {
            var tasks = batch.Select(async imagePath =>
            {
                // Read metadata
                var metadata = Metadata.ReadFromFile(imagePath);
                
                // Generate/retrieve embeddings (with deduplication)
                var promptEmbId = await _cache.GetOrCreateTextEmbeddingAsync(
                    metadata.Prompt, "prompt");
                var negativeEmbId = await _cache.GetOrCreateTextEmbeddingAsync(
                    metadata.NegativePrompt, "negative");
                var imageEmbId = await _cache.GetOrCreateVisualEmbeddingAsync(imagePath);
                
                // Store image record
                await _dataStore.InsertImageAsync(new ImageEntity
                {
                    Path = imagePath,
                    PromptEmbeddingId = promptEmbId,
                    NegativePromptEmbeddingId = negativeEmbId,
                    ImageEmbeddingId = imageEmbId,
                    // ... other metadata ...
                });
            });
            
            await Task.WhenAll(tasks);
            
            Console.WriteLine($"Processed {batch.Length} images...");
        }
    }
}
```

---

## 📊 Expected Performance

### **Your Dual-GPU Setup:**

**GPU 0 (RTX 5090):**
- BGE text encoding: ~64 texts @ 5ms each = 320ms per batch
- CLIP-H image encoding: ~32 images @ 15ms each = 480ms per batch

**GPU 1 (RTX 3080 Ti):**
- CLIP-L encoding: ~64 texts @ 3ms each = 192ms per batch
- CLIP-G encoding: ~64 texts @ 5ms each = 320ms per batch

**Parallel Throughput:**
- ~500ms per 64-image batch (all 5 embeddings)
- ~200K images in **~1.4 hours** (vs 4.4 hours with Python interop)

---

## 🚀 Quick Start

```powershell
# 1. Convert models
python scripts/convert_models_to_onnx.py

# 2. Build project
dotnet build Diffusion.Embeddings

# 3. Test single embedding
dotnet run --project TestEmbeddings

# 4. Bulk import
dotnet run --project Diffusion.Toolkit -- --bulk-embed --folder "E:\Output\Images"
```

---

## ⚠️ Known Issues & Alternatives

### **CLIP Safetensors Conversion**

Your CLIP-L/CLIP-G safetensors files may require manual weight mapping. If conversion fails:

**Option A:** Use pre-converted ONNX models from HuggingFace
```python
from optimum.onnxruntime import ORTModelForFeatureExtraction
model = ORTModelForFeatureExtraction.from_pretrained(
    "laion/CLIP-ViT-L-14-laion2B-s32B-b79K",
    export=True
)
model.save_pretrained("models/onnx/clip-l")
```

**Option B:** Python interop fallback (slower but works)
```csharp
// Falls back to calling your existing Python embedding_function.py
var pythonEmbedder = new PythonEmbeddingService();
var clipL = await pythonEmbedder.GetCLIPLEmbedding(text);
```

---

## 📝 Summary

✅ **Completed:**
- ONNX Runtime GPU infrastructure
- BGE and CLIP text encoders (C#)
- Conversion script for models
- PostgreSQL port fix (5436)

⏳ **Next 3 Tasks:**
1. Run `convert_models_to_onnx.py` to generate ONNX files
2. Create `CLIPVisionEncoder.cs` for image embeddings
3. Create `EmbeddingService.cs` to orchestrate all encoders

🎯 **Goal:** 1.4-hour bulk embedding for 200K images with 82% storage savings!
