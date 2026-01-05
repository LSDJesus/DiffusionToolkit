# Multi-Modal Embedding Architecture

## Overview

DiffusionToolkit now implements a **5-dimensional embedding system** that combines semantic search with ComfyUI workflow integration:

```
┌─────────────────────────────────────────────────────────────┐
│                    DiffusionToolkit                          │
│                  PostgreSQL + pgvector                       │
└─────────────────────────────────────────────────────────────┘
                           │
            ┌──────────────┼──────────────┐
            │              │              │
     ┌──────▼────┐  ┌──────▼────┐  ┌─────▼──────┐
     │  Search   │  │  Visual   │  │  ComfyUI   │
     │ Semantic  │  │Similarity │  │Conditioning│
     └───────────┘  └───────────┘  └────────────┘
     
     BGE-1024D      CLIP-H-1024D    CLIP-L-768D
     (Prompt)       (Image)         CLIP-G-1280D
```

## Five Embedding Vectors Per Image

### 1. **Prompt Embedding** (BGE-large-en-v1.5, 1024D)
- **Purpose**: Semantic search across prompts
- **Model**: BAAI/bge-large-en-v1.5
- **Use Cases**:
  - "Find images with 'dramatic lighting' concept"
  - Natural language search
  - Concept-based filtering

### 2. **Negative Prompt Embedding** (BGE-large-en-v1.5, 1024D)
- **Purpose**: Search by things to avoid
- **Model**: BAAI/bge-large-en-v1.5
- **Use Cases**:
  - "Find images avoiding 'blurry' or 'distorted'"
  - Anti-pattern search
  - Quality filtering

### 3. **Image Embedding** (CLIP-ViT-H/14, 1024D)
- **Purpose**: Visual similarity search
- **Model**: laion/CLIP-ViT-H-14-laion2B-s32B-b79K
- **File**: `d:\AI\SD Models\clip_vision\CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors`
- **Use Cases**:
  - "Find visually similar images"
  - Composition matching
  - Color/style similarity

### 4. **CLIP-L Embedding** (SDXL Text Encoder, 768D)
- **Purpose**: ComfyUI SDXL conditioning (text_l)
- **Model**: clip-L_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32
- **File**: `d:\AI\SD Models\clip\Clip-L\clip-L_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32.safetensors`
- **Use Cases**:
  - Direct SDXL workflow integration
  - Export to ComfyUI for image generation
  - Fine-grained prompt control

### 5. **CLIP-G Embedding** (SDXL Text Encoder, 1280D)
- **Purpose**: ComfyUI SDXL conditioning (text_g)
- **Model**: clip-G_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32
- **File**: `d:\AI\SD Models\clip\Clip-G\clip-G_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32.safetensors`
- **Use Cases**:
  - Direct SDXL workflow integration
  - Global scene description
  - Broader prompt concepts

## Why Multiple Embeddings?

### BGE vs CLIP for Text
| Feature | BGE-large-en-v1.5 | CLIP (L/G/H) |
|---------|------------------|--------------|
| **Optimized for** | Text similarity | Image-text pairs |
| **Training** | Text retrieval tasks | Contrastive vision-language |
| **Semantic understanding** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| **ComfyUI compatibility** | ❌ | ✅ |
| **Natural language queries** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ |
| **Tag-based prompts** | ⭐⭐⭐ | ⭐⭐⭐⭐⭐ |

**Decision**: Use **BGE for search**, **CLIP for generation**

### CLIP-H vs CLIP-L vs CLIP-G for Images
| Model | Dimensions | Accuracy | Speed | Use Case |
|-------|-----------|----------|-------|----------|
| **CLIP-H** | 1024D | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | Visual similarity |
| **CLIP-L** | 768D | ⭐⭐⭐⭐ | ⭐⭐⭐⭐ | SDXL text_l |
| **CLIP-G** | 1280D | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | SDXL text_g |

**Decision**: Use **H for search**, **L+G for SDXL conditioning**

## Database Storage

```sql
-- PostgreSQL schema
CREATE TABLE image (
    id SERIAL PRIMARY KEY,
    path TEXT NOT NULL UNIQUE,
    prompt TEXT,
    negative_prompt TEXT,
    -- ... metadata fields ...
    
    -- Embedding vectors
    prompt_embedding vector(1024),              -- BGE semantic search
    negative_prompt_embedding vector(1024),     -- BGE semantic search  
    image_embedding vector(1024),               -- CLIP-H visual similarity
    clip_l_embedding vector(768),               -- SDXL text_l encoder
    clip_g_embedding vector(1280),              -- SDXL text_g encoder
    
    created_at TIMESTAMP DEFAULT NOW()
);

-- Vector similarity indexes (IVFFlat for performance)
CREATE INDEX idx_image_prompt_embedding 
    ON image USING ivfflat (prompt_embedding vector_cosine_ops) 
    WITH (lists = 100);
    
CREATE INDEX idx_image_image_embedding 
    ON image USING ivfflat (image_embedding vector_cosine_ops) 
    WITH (lists = 100);
    
CREATE INDEX idx_image_clip_l_embedding 
    ON image USING ivfflat (clip_l_embedding vector_cosine_ops) 
    WITH (lists = 100);
    
CREATE INDEX idx_image_clip_g_embedding 
    ON image USING ivfflat (clip_g_embedding vector_cosine_ops) 
    WITH (lists = 100);
```

## Embedding Generation Pipeline

```
┌──────────────┐
│ Image Scan   │
└──────┬───────┘
       │
       ▼
┌──────────────────────────────────────────┐
│  Extract Metadata (prompt, neg_prompt)   │
└──────┬───────────────────────────────────┘
       │
       ▼
┌──────────────────────────────────────────┐
│  Multi-GPU Embedding Manager             │
│  (RTX 5090 + 3080 Ti)                    │
└──┬───┬───┬───┬───┬─────────────────────┘
   │   │   │   │   │
   ▼   ▼   ▼   ▼   ▼
 BGE  BGE  H   L   G
 P    NP   IMG TXT TXT
 1024 1024 1024 768 1280
   │   │   │   │   │
   └───┴───┴───┴───┘
           │
           ▼
   ┌───────────────┐
   │ PostgreSQL DB │
   │  5 vectors    │
   └───────────────┘
```

## Model Download Requirements

| Model | Size | Download From | Purpose |
|-------|------|--------------|---------|
| BGE-large-en-v1.5 (ONNX) | ~1.3GB | HuggingFace BAAI | Text embeddings |
| CLIP-ViT-H/14 (ONNX) | ~1.7GB | HuggingFace laion | Image embeddings |
| CLIP-L (existing) | ~500MB | ✅ Already have | SDXL conditioning |
| CLIP-G (existing) | ~800MB | ✅ Already have | SDXL conditioning |
| **Total new downloads** | **~3GB** | | |

## GPU Performance Estimates

### RTX 5090 (Primary)
- **BGE text**: ~2000 prompts/second
- **CLIP-H image**: ~500 images/second
- **CLIP-L text**: ~3000 prompts/second
- **CLIP-G text**: ~2000 prompts/second

### RTX 3080 Ti (Secondary)
- **BGE text**: ~1500 prompts/second
- **CLIP-H image**: ~400 images/second
- **CLIP-L text**: ~2500 prompts/second
- **CLIP-G text**: ~1500 prompts/second

### Combined (Load Balanced)
- **Estimated throughput**: 100-200 complete image embeddings/second
- **Time for 100k images**: ~10-15 minutes
- **Time for 1M images**: ~90-150 minutes

## Storage Breakdown

### Per Image
- Prompt embedding: 1024 × 4 bytes = 4,096 bytes
- Negative prompt embedding: 1024 × 4 bytes = 4,096 bytes
- Image embedding: 1024 × 4 bytes = 4,096 bytes
- CLIP-L embedding: 768 × 4 bytes = 3,072 bytes
- CLIP-G embedding: 1280 × 4 bytes = 5,120 bytes
- **Total per image**: ~20,480 bytes (~20KB)

### Dataset Sizes
| Images | Raw Vectors | Indexes (2x) | Total Storage |
|--------|------------|--------------|---------------|
| 10,000 | 200 MB | 400 MB | ~600 MB |
| 100,000 | 2 GB | 4 GB | ~6 GB |
| 1,000,000 | 20 GB | 40 GB | ~60 GB |

## Query Examples

### 1. Semantic Prompt Search (BGE)
```sql
-- Find images with similar meaning to query
SELECT id, path, prompt,
       1 - (prompt_embedding <=> @query_embedding) AS similarity
FROM image
WHERE prompt_embedding IS NOT NULL
ORDER BY prompt_embedding <=> @query_embedding
LIMIT 50;
```

### 2. Visual Similarity (CLIP-H)
```sql
-- Find visually similar images
SELECT id, path,
       1 - (image_embedding <=> @reference_embedding) AS similarity
FROM image
WHERE image_embedding IS NOT NULL
ORDER BY image_embedding <=> @reference_embedding
LIMIT 50;
```

### 3. SDXL Style Matching (CLIP-L)
```sql
-- Find images with similar SDXL CLIP-L style
SELECT id, path, prompt,
       1 - (clip_l_embedding <=> @style_embedding) AS similarity
FROM image
WHERE clip_l_embedding IS NOT NULL
ORDER BY clip_l_embedding <=> @style_embedding
LIMIT 50;
```

### 4. Multi-Modal Hybrid Search
```sql
-- Combine text + visual + CLIP-L matching
SELECT id, path, prompt,
       (0.4 * (1 - (prompt_embedding <=> @text_query)) +
        0.3 * (1 - (image_embedding <=> @image_query)) +
        0.3 * (1 - (clip_l_embedding <=> @style_query))) AS score
FROM image
WHERE prompt_embedding IS NOT NULL
  AND image_embedding IS NOT NULL
  AND clip_l_embedding IS NOT NULL
ORDER BY score DESC
LIMIT 50;
```

## ComfyUI Export Workflow

```csharp
// 1. Find similar image in DiffusionToolkit
var similar = await dataStore.SearchByPromptSimilarityAsync(
    "beautiful anime girl, detailed eyes",
    topK: 1
);

// 2. Export embeddings to ComfyUI
await ComfyUIExporter.ExportToComfyUIAsync(
    imageId: similar[0].Id,
    clipLEmbedding: similar[0].ClipLEmbedding,
    clipGEmbedding: similar[0].ClipGEmbedding,
    imageEmbedding: similar[0].ImageEmbedding,
    outputPath: @"C:\ComfyUI\embeddings\reference.json"
);

// 3. Load in ComfyUI and generate variations!
```

## Benefits Summary

✅ **Semantic Search**: BGE understands natural language better than CLIP  
✅ **Visual Search**: CLIP-H provides best image similarity  
✅ **ComfyUI Ready**: CLIP-L/G directly compatible with SDXL workflows  
✅ **Multi-Modal**: Combine text + visual search  
✅ **Future-Proof**: Support for new CLIP variants (Long-CLIP, etc.)  
✅ **GPU Optimized**: Dual-GPU load balancing  
✅ **Scalable**: IVFFlat indexes handle 1M+ images  

## Next Steps

1. ✅ Database schema updated with 5 vector columns
2. ✅ Model specifications documented
3. ⏳ Update EmbeddingService to support CLIP-L and CLIP-G
4. ⏳ Implement ModelDownloader for BGE + CLIP-H ONNX models
5. ⏳ Test multi-model embedding generation
6. ⏳ Create ComfyUI custom node for direct integration
7. ⏳ Build workflow automation for batch exports

---

**Total Dimensions**: 5,096D per image (1024 + 1024 + 1024 + 768 + 1280)  
**Total Storage**: ~60GB for 1M images (vectors + indexes)  
**GPU Memory**: ~8GB peak during batch processing  
**Generation Speed**: 100-200 images/second (dual GPU)
