# PostgreSQL Migration Guide

## Quick Start

### 1. Start PostgreSQL with pgvector

```powershell
docker-compose up -d
```

Connection string:
```
Host=localhost;Port=5436;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025
```

### 2. Install Python Dependencies (for embeddings)

```powershell
pip install torch torchvision --index-url https://download.pytorch.org/whl/cu124
pip install transformers sentence-transformers pillow onnxruntime-gpu
```

### 3. GPU Configuration

The embedding service will automatically use:
- **Primary GPU**: RTX 5090 (CUDA device 0) for image embeddings
- **Secondary GPU**: RTX 3080 Ti (CUDA device 1) for text embeddings

You can configure this in `appsettings.json`:

```json
{
  "EmbeddingService": {
    "TextModel": "BAAI/bge-large-en-v1.5",
    "ImageModel": "openai/clip-vit-base-patch32",
    "TextDevice": "cuda:1",
    "ImageDevice": "cuda:0",
    "BatchSize": 32
  }
}
```

## Migration from SQLite

### One-Time Migration Script

```powershell
dotnet run --project Diffusion.Migration -- `
  --source "path/to/images.db" `
  --target "Host=localhost;Port=5436;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025" `
  --generate-embeddings true `
  --batch-size 100
```

This will:
1. Copy all images metadata from SQLite to PostgreSQL
2. Generate embeddings for all prompts and images using your GPUs
3. Create vector indexes for fast similarity search

Estimated time for 1M images: ~6-8 hours (depending on GPU throughput)

## New Features

### Similarity Search

Find images with similar prompts:
```csharp
var similar = await dataStore.SearchByPromptSimilarityAsync(
    promptEmbedding: embedding,
    threshold: 0.85f,
    limit: 50
);
```

Find visually similar images:
```csharp
var similar = await dataStore.SearchSimilarImagesAsync(
    imageId: 12345,
    threshold: 0.75f,
    limit: 50
);
```

Cross-modal search (text → images):
```csharp
var images = await dataStore.CrossModalSearchAsync(
    textEmbedding: textEmb,
    threshold: 0.70f,
    limit: 50
);
```

## Performance Tuning

### Vector Index Optimization

For 1M+ images, adjust the `lists` parameter in IVFFlat indexes:

```sql
-- Rebuild indexes with optimal list count
DROP INDEX idx_image_prompt_embedding;
CREATE INDEX idx_image_prompt_embedding ON image 
USING ivfflat (prompt_embedding vector_cosine_ops) 
WITH (lists = 1000);  -- sqrt(1M) = 1000
```

### Connection Pool Settings

In `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=localhost;Port=5436;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025;Maximum Pool Size=50;Connection Idle Lifetime=60"
  }
}
```

## Troubleshooting

### GPU Out of Memory

Reduce batch size in embedding service:
```json
{
  "EmbeddingService": {
    "BatchSize": 16
  }
}
```

### Slow Vector Queries

1. Check index usage: `EXPLAIN ANALYZE SELECT ...`
2. Increase `lists` parameter for larger datasets
3. Consider HNSW indexes for better accuracy (slower build, faster query)

### Connection Issues

Verify PostgreSQL is running:
```powershell
docker logs diffusion_toolkit_db
docker exec -it diffusion_toolkit_db psql -U diffusion -d diffusion_images
```
