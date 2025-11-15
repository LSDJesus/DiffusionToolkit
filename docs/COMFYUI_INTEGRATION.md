# ComfyUI Integration Guide

## Overview

DiffusionToolkit now supports **5-dimensional multi-modal embeddings** perfectly aligned with your SDXL/FLUX workflows:

| Embedding Type | Model | Dimensions | Purpose |
|---------------|-------|------------|---------|
| **Semantic Search (Prompt)** | BGE-large-en-v1.5 | 1024D | Natural language search across prompts |
| **Semantic Search (Negative)** | BGE-large-en-v1.5 | 1024D | Search negative prompts |
| **Image Similarity** | CLIP-ViT-H/14 | 1024D | Visual similarity search |
| **SDXL CLIP-L** | clip-L_noMERGE_Universal_CLIP_FLUX_illustrious | 768D | SDXL text_l encoder |
| **SDXL CLIP-G** | clip-G_noMERGE_Universal_CLIP_FLUX_illustrious | 1280D | SDXL text_g encoder |

**Total: 5,096 dimensions per image** (1024 + 1024 + 1024 + 768 + 1280)

## Your Model Configuration

### Text Encoders (SDXL/FLUX)
- **CLIP-L**: `d:\AI\SD Models\clip\Clip-L\clip-L_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32.safetensors`
- **CLIP-G**: `d:\AI\SD Models\clip\Clip-G\clip-G_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32.safetensors`

### Vision Models
- **CLIP-H**: `d:\AI\SD Models\clip_vision\CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors`
- **CLIP-H (ComfyUI)**: `d:\AI\SD Models\clip_vision\clip_vision_h.safetensors`

### Semantic Search
- **BGE**: BGE-large-en-v1.5 (ONNX) - Download via ModelDownloader

## Database Schema

```sql
CREATE TABLE image (
    id SERIAL PRIMARY KEY,
    path TEXT NOT NULL UNIQUE,
    prompt TEXT,
    negative_prompt TEXT,
    -- ... other metadata ...
    
    -- Embeddings for different purposes
    prompt_embedding vector(1024),              -- BGE semantic search
    negative_prompt_embedding vector(1024),     -- BGE semantic search
    image_embedding vector(1024),               -- CLIP-H visual similarity
    clip_l_embedding vector(768),               -- SDXL text_l (ComfyUI)
    clip_g_embedding vector(1280)               -- SDXL text_g (ComfyUI)
);

-- Optimized vector indexes
CREATE INDEX idx_image_prompt_embedding ON image USING ivfflat (prompt_embedding vector_cosine_ops);
CREATE INDEX idx_image_clip_l_embedding ON image USING ivfflat (clip_l_embedding vector_cosine_ops);
CREATE INDEX idx_image_clip_g_embedding ON image USING ivfflat (clip_g_embedding vector_cosine_ops);
CREATE INDEX idx_image_image_embedding ON image USING ivfflat (image_embedding vector_cosine_ops);
```

## Use Cases

### 1. Find Similar Images in DiffusionToolkit
```csharp
// Search by visual similarity using CLIP-H
var similarImages = await dataStore.SearchSimilarImagesAsync(
    referenceImageId: 123,
    topK: 20,
    threshold: 0.75f
);

// Search by prompt semantic similarity using BGE
var semanticMatches = await dataStore.SearchByPromptSimilarityAsync(
    queryPrompt: "beautiful landscape with mountains",
    topK: 50,
    threshold: 0.7f
);
```

### 2. Export to ComfyUI Workflow
```csharp
// Get embeddings for an image
var image = await dataStore.GetImageAsync(imageId);

// Export all embeddings to ComfyUI-compatible JSON
await ComfyUIExporter.ExportToComfyUIAsync(
    imageId: image.Id,
    clipLEmbedding: image.ClipLEmbedding,   // 768D
    clipGEmbedding: image.ClipGEmbedding,   // 1280D
    imageEmbedding: image.ImageEmbedding,   // 1024D
    outputPath: @"C:\ComfyUI\embeddings\image_123.json",
    width: image.Width,
    height: image.Height
);
```

### 3. Generate Similar Images in ComfyUI

**Workflow A: Use Stored CLIP-L/G Embeddings**
```json
{
  "1": {
    "inputs": {
      "text_l": "original prompt here",
      "embedding_override": [/* 768D CLIP-L embedding from database */],
      "clip": ["4", 0]
    },
    "class_type": "CLIPTextEncode"
  },
  "2": {
    "inputs": {
      "text_g": "original prompt here",
      "embedding_override": [/* 1280D CLIP-G embedding from database */],
      "clip": ["4", 0]
    },
    "class_type": "CLIPTextEncode"
  }
}
```

**Workflow B: Image-to-Image with CLIP-H Embeddings**
```python
# In ComfyUI custom node or script
import numpy as np
from comfy import model_management

# Load stored embeddings from DiffusionToolkit export
with open("embeddings/image_123.json") as f:
    data = json.load(f)
    
clip_l_embedding = np.array(data["ClipL"])  # 768D
clip_g_embedding = np.array(data["ClipG"])  # 1280D
image_embedding = np.array(data["ImageEmbedding"])  # 1024D

# Use with IPAdapter or similar conditioning nodes
# to generate variations of the original image
```

### 4. Batch Export for Dataset Creation
```csharp
// Export all favorite images for training dataset
var favorites = await dataStore.SearchAsync(new SearchOptions 
{ 
    Favorite = true 
});

var embeddingBatch = favorites.Select(img => new ImageEmbeddingSet
{
    ImageId = img.Id,
    ImagePath = img.Path,
    ClipL = img.ClipLEmbedding,
    ClipG = img.ClipGEmbedding,
    ImageEmbedding = img.ImageEmbedding,
    Width = img.Width,
    Height = img.Height
});

await ComfyUIExporter.ExportBatchToComfyUIAsync(
    embeddingBatch,
    @"C:\ComfyUI\training_data\embeddings"
);
```

## Advanced Queries

### Find Images with Similar Style (CLIP-L)
```sql
SELECT 
    id, 
    path, 
    prompt,
    1 - (clip_l_embedding <=> @query_clip_l) AS similarity
FROM image
WHERE clip_l_embedding IS NOT NULL
ORDER BY clip_l_embedding <=> @query_clip_l
LIMIT 50;
```

### Find Images with Similar Composition (CLIP-H)
```sql
SELECT 
    id, 
    path, 
    1 - (image_embedding <=> @query_image) AS similarity
FROM image
WHERE image_embedding IS NOT NULL
ORDER BY image_embedding <=> @query_image
LIMIT 50;
```

### Multi-Modal Search (Combined Prompt + Visual)
```sql
-- Weighted combination of semantic and visual similarity
SELECT 
    id,
    path,
    prompt,
    (0.5 * (1 - (prompt_embedding <=> @query_prompt)) + 
     0.5 * (1 - (image_embedding <=> @query_image))) AS combined_score
FROM image
WHERE prompt_embedding IS NOT NULL 
  AND image_embedding IS NOT NULL
ORDER BY combined_score DESC
LIMIT 50;
```

## Performance Considerations

### Vector Index Tuning
```sql
-- For larger datasets (100k+ images), increase list count
CREATE INDEX idx_image_clip_l_embedding 
ON image USING ivfflat (clip_l_embedding vector_cosine_ops) 
WITH (lists = 500);  -- Scales with dataset size

-- Optimal lists = sqrt(total_rows)
-- 100k images → lists=316
-- 1M images → lists=1000
```

### Storage Requirements
- **Per image**: ~20KB for all 5 embeddings
- **1M images**: ~20GB vector storage
- **Indexes**: ~2x raw data size (~40GB total)

### Query Performance
- **IVFFlat index**: ~10-50ms for 1M images
- **Exact search**: ~500-2000ms for 1M images
- **Batch processing**: 100-200 images/second (GPU)

## Integration with Existing Workflows

### ComfyUI Custom Node (Proposed)
```python
class DiffusionToolkitLoader:
    """Load embeddings from DiffusionToolkit database"""
    
    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "image_id": ("INT", {"default": 0}),
                "embedding_type": (["clip_l", "clip_g", "image"],)
            }
        }
    
    RETURN_TYPES = ("CONDITIONING",)
    FUNCTION = "load_embedding"
    
    def load_embedding(self, image_id, embedding_type):
        # Connect to PostgreSQL
        # Load embedding from database
        # Return as ComfyUI CONDITIONING
        pass
```

## Best Practices

1. **Generate all 5 embeddings during scan**: Maximizes search flexibility
2. **Use BGE for text search**: Better natural language understanding than CLIP
3. **Use CLIP-H for visual search**: Highest quality image embeddings
4. **Use CLIP-L/G for ComfyUI**: Direct compatibility with SDXL conditioning
5. **Index by use case**: Create partial indexes for frequently queried subsets

## Migration from Existing Setup

If you already have images without embeddings:

```csharp
// Batch re-process to generate all embeddings
var imagesWithoutEmbeddings = await dataStore.GetImagesWithoutEmbeddingsAsync();

var embeddingManager = new MultiGpuEmbeddingManager(
    new[] { 0, 1 },  // RTX 5090 + 3080 Ti
    modelPaths
);

foreach (var batch in imagesWithoutEmbeddings.Chunk(100))
{
    var results = await embeddingManager.GenerateAllEmbeddingsAsync(
        batch.Select(img => (img.Path, img.Prompt, img.NegativePrompt)),
        CancellationToken.None
    );
    
    // Update database with all 5 embeddings
    await dataStore.UpdateAllEmbeddingsAsync(results);
}
```

## Future Enhancements

- [ ] Direct ComfyUI API integration (auto-export on image add)
- [ ] Embedding interpolation for style transfer
- [ ] CLIP-guided prompt generation from embeddings
- [ ] Automatic workflow generation from similar images
- [ ] Fine-tuned CLIP models for specific art styles

---

**Model Sources:**
- CLIP-L/G: Your existing ComfyUI models (illustrious fine-tune)
- CLIP-H: OpenCLIP laion2B-s32B-b79K
- BGE: BAAI/bge-large-en-v1.5

**Total Disk Space Required:**
- Models: ~3.5GB (BGE 1.3GB + CLIP-H 1.7GB + CLIP-L 500MB + CLIP-G 800MB)
- Embeddings (1M images): ~60GB (vectors + indexes)
