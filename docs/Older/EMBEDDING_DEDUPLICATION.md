# Embedding Deduplication System

## Overview

Cache and reuse embeddings for identical prompts to dramatically reduce storage and processing time.

---

## **Database Schema**

### **1. Embedding Cache Table**

```sql
CREATE TABLE embedding_cache (
    id SERIAL PRIMARY KEY,
    
    -- Content identification
    content_hash TEXT NOT NULL UNIQUE,  -- SHA256 of prompt text
    content_type TEXT NOT NULL,         -- 'prompt', 'negative_prompt', 'image'
    content_text TEXT,                  -- Original text (for prompts)
    
    -- Embeddings (only one set per unique content)
    bge_embedding vector(1024),         -- BGE for text
    clip_l_embedding vector(768),       -- CLIP-L for text
    clip_g_embedding vector(1280),      -- CLIP-G for text
    clip_h_embedding vector(1024),      -- CLIP-H for images
    
    -- Statistics
    reference_count INTEGER DEFAULT 0,  -- How many images use this
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    last_used_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Indexes
CREATE INDEX idx_embedding_cache_hash ON embedding_cache(content_hash);
CREATE INDEX idx_embedding_cache_type ON embedding_cache(content_type);
CREATE INDEX idx_embedding_cache_refcount ON embedding_cache(reference_count DESC);

-- Vector indexes (for searching cache itself)
CREATE INDEX idx_embedding_cache_bge 
    ON embedding_cache USING ivfflat (bge_embedding vector_cosine_ops)
    WITH (lists = 100);
```

### **2. Modified Image Table**

```sql
CREATE TABLE image (
    id SERIAL PRIMARY KEY,
    path TEXT NOT NULL UNIQUE,
    
    -- Metadata
    prompt TEXT,
    negative_prompt TEXT,
    width INTEGER,
    height INTEGER,
    -- ... other fields ...
    
    -- Foreign keys to embedding cache (nullable for backwards compat)
    prompt_embedding_id INTEGER REFERENCES embedding_cache(id),
    negative_prompt_embedding_id INTEGER REFERENCES embedding_cache(id),
    image_embedding_id INTEGER REFERENCES embedding_cache(id),
    
    -- Legacy direct embeddings (deprecated, null for new images)
    prompt_embedding vector(1024),          -- NULL if using cache
    negative_prompt_embedding vector(1024), -- NULL if using cache
    image_embedding vector(1024),           -- NULL if using cache
    clip_l_embedding vector(768),           -- NULL if using cache
    clip_g_embedding vector(1280),          -- NULL if using cache
    
    -- Flags
    needs_visual_embedding BOOLEAN DEFAULT true,  -- Skip for base images
    is_upscaled BOOLEAN DEFAULT false,            -- True for final versions
    base_image_id INTEGER REFERENCES image(id),   -- Link base → upscale
    
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Indexes
CREATE INDEX idx_image_prompt_cache ON image(prompt_embedding_id);
CREATE INDEX idx_image_negative_cache ON image(negative_prompt_embedding_id);
CREATE INDEX idx_image_visual_cache ON image(image_embedding_id);
CREATE INDEX idx_image_needs_visual ON image(needs_visual_embedding) WHERE needs_visual_embedding = true;
CREATE INDEX idx_image_base_id ON image(base_image_id);
```

---

## **Workflow**

### **Phase 1: Import Image Metadata**

```csharp
// Scan image file
var metadata = MetadataScanner.ScanImage("path/to/image.png");

// Check if this is an upscale (compare filenames)
bool isUpscale = metadata.Path.Contains("_upscaled") || 
                 metadata.Path.Contains("_detailed");

// Find base image if upscale
int? baseImageId = null;
if (isUpscale)
{
    var basePath = metadata.Path.Replace("_upscaled", "")
                                 .Replace("_detailed", "");
    baseImageId = await dataStore.GetImageIdByPathAsync(basePath);
}

// Create image record
var image = new ImageEntity
{
    Path = metadata.Path,
    Prompt = metadata.Prompt,
    NegativePrompt = metadata.NegativePrompt,
    IsUpscaled = isUpscale,
    BaseImageId = baseImageId,
    NeedsVisualEmbedding = isUpscale,  // Only embed upscales
    
    // Don't create embeddings yet, just store text
    PromptEmbeddingId = null,
    NegativePromptEmbeddingId = null,
    ImageEmbeddingId = null
};

await dataStore.InsertImageAsync(image);
```

### **Phase 2: Generate/Reuse Embeddings**

```csharp
public class EmbeddingCacheService
{
    /// <summary>
    /// Get or create embedding for text content
    /// </summary>
    public async Task<int> GetOrCreateTextEmbeddingAsync(
        string text,
        string contentType,  // "prompt" or "negative_prompt"
        CancellationToken cancellationToken = default)
    {
        // 1. Hash the content
        var contentHash = ComputeSHA256(text);
        
        // 2. Check cache
        var cached = await dataStore.GetEmbeddingByHashAsync(contentHash);
        if (cached != null)
        {
            // Cache hit! Reuse existing embedding
            await dataStore.IncrementEmbeddingReferenceCountAsync(cached.Id);
            return cached.Id;
        }
        
        // 3. Cache miss - generate new embeddings
        var bgeEmbedding = await bgeEncoder.EncodeAsync(text, cancellationToken);
        var clipLEmbedding = await clipLEncoder.EncodeAsync(text, cancellationToken);
        var clipGEmbedding = await clipGEncoder.EncodeAsync(text, cancellationToken);
        
        // 4. Store in cache
        var cacheEntry = new EmbeddingCache
        {
            ContentHash = contentHash,
            ContentType = contentType,
            ContentText = text,
            BgeEmbedding = bgeEmbedding,
            ClipLEmbedding = clipLEmbedding,
            ClipGEmbedding = clipGEmbedding,
            ReferenceCount = 1
        };
        
        var cacheId = await dataStore.InsertEmbeddingCacheAsync(cacheEntry);
        return cacheId;
    }
    
    /// <summary>
    /// Get or create visual embedding for image
    /// </summary>
    public async Task<int> GetOrCreateVisualEmbeddingAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        // 1. Hash image content (first 1MB to avoid loading full file)
        var imageHash = await ComputeImageHashAsync(imagePath);
        
        // 2. Check cache
        var cached = await dataStore.GetEmbeddingByHashAsync(imageHash);
        if (cached != null)
        {
            await dataStore.IncrementEmbeddingReferenceCountAsync(cached.Id);
            return cached.Id;
        }
        
        // 3. Generate CLIP-H visual embedding
        var clipHEmbedding = await clipHEncoder.EncodeImageAsync(imagePath, cancellationToken);
        
        // 4. Store in cache
        var cacheEntry = new EmbeddingCache
        {
            ContentHash = imageHash,
            ContentType = "image",
            ContentText = null,
            ClipHEmbedding = clipHEmbedding,
            ReferenceCount = 1
        };
        
        var cacheId = await dataStore.InsertEmbeddingCacheAsync(cacheEntry);
        return cacheId;
    }
    
    private string ComputeSHA256(string text)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
```

### **Phase 3: Batch Embedding Generation**

```csharp
public async Task GenerateMissingEmbeddingsAsync(
    int batchSize = 1000,
    CancellationToken cancellationToken = default)
{
    var embeddingCache = new EmbeddingCacheService();
    
    while (!cancellationToken.IsCancellationRequested)
    {
        // Get images missing embeddings
        var images = await dataStore.GetImagesWithoutEmbeddingsAsync(batchSize);
        if (images.Count == 0)
            break;
        
        foreach (var image in images)
        {
            // 1. Process prompt
            if (!string.IsNullOrEmpty(image.Prompt) && image.PromptEmbeddingId == null)
            {
                var promptEmbeddingId = await embeddingCache.GetOrCreateTextEmbeddingAsync(
                    image.Prompt,
                    "prompt",
                    cancellationToken
                );
                
                await dataStore.UpdateImageEmbeddingIdAsync(
                    image.Id,
                    "prompt",
                    promptEmbeddingId
                );
            }
            
            // 2. Process negative prompt
            if (!string.IsNullOrEmpty(image.NegativePrompt) && image.NegativePromptEmbeddingId == null)
            {
                var negEmbeddingId = await embeddingCache.GetOrCreateTextEmbeddingAsync(
                    image.NegativePrompt,
                    "negative_prompt",
                    cancellationToken
                );
                
                await dataStore.UpdateImageEmbeddingIdAsync(
                    image.Id,
                    "negative_prompt",
                    negEmbeddingId
                );
            }
            
            // 3. Process visual embedding (only if needed)
            if (image.NeedsVisualEmbedding && image.ImageEmbeddingId == null)
            {
                var visualEmbeddingId = await embeddingCache.GetOrCreateVisualEmbeddingAsync(
                    image.Path,
                    cancellationToken
                );
                
                await dataStore.UpdateImageEmbeddingIdAsync(
                    image.Id,
                    "image",
                    visualEmbeddingId
                );
                
                // Mark as complete
                await dataStore.SetNeedsVisualEmbeddingAsync(image.Id, false);
            }
            
            Console.WriteLine($"Processed embeddings for {image.Path}");
        }
    }
}
```

---

## **Smart Linking: Base ↔ Upscale**

### **Scenario 1: Import Base Image First**

```csharp
// Base image imported
var baseImage = new ImageEntity
{
    Path = "image_001.png",
    Prompt = "beautiful portrait, detailed",
    NegativePrompt = "BadPonyHD, bad hands",
    IsUpscaled = false,
    NeedsVisualEmbedding = false,  // Skip visual embedding for base
    BaseImageId = null
};
await dataStore.InsertImageAsync(baseImage);

// Generate text embeddings only
var promptId = await embeddingCache.GetOrCreateTextEmbeddingAsync(
    baseImage.Prompt, "prompt");
var negativeId = await embeddingCache.GetOrCreateTextEmbeddingAsync(
    baseImage.NegativePrompt, "negative_prompt");

// Link embeddings
await dataStore.UpdateImageEmbeddingIdAsync(baseImage.Id, "prompt", promptId);
await dataStore.UpdateImageEmbeddingIdAsync(baseImage.Id, "negative_prompt", negativeId);
// No visual embedding generated (saves 50ms + 4KB)
```

### **Scenario 2: Import Upscaled Image**

```csharp
// Upscaled image imported
var upscaledImage = new ImageEntity
{
    Path = "image_001_upscaled.png",
    Prompt = "beautiful portrait, detailed",  // Same prompt
    NegativePrompt = "BadPonyHD, bad hands",  // Same negative
    IsUpscaled = true,
    NeedsVisualEmbedding = true,   // Generate visual embedding
    BaseImageId = baseImage.Id     // Link to base
};
await dataStore.InsertImageAsync(upscaledImage);

// Reuse text embeddings (cache hit!)
var promptId = await embeddingCache.GetOrCreateTextEmbeddingAsync(
    upscaledImage.Prompt, "prompt");
// Returns same promptId as base image (instant, no generation)

var negativeId = await embeddingCache.GetOrCreateTextEmbeddingAsync(
    upscaledImage.NegativePrompt, "negative_prompt");
// Returns same negativeId (instant)

// Generate visual embedding (only for upscaled version)
var visualId = await embeddingCache.GetOrCreateVisualEmbeddingAsync(
    upscaledImage.Path);

// Link embeddings
await dataStore.UpdateImageEmbeddingIdAsync(upscaledImage.Id, "prompt", promptId);
await dataStore.UpdateImageEmbeddingIdAsync(upscaledImage.Id, "negative_prompt", negativeId);
await dataStore.UpdateImageEmbeddingIdAsync(upscaledImage.Id, "image", visualId);
```

---

## **Query Pattern: JOIN for Searches**

### **Similarity Search with Cached Embeddings**

```sql
-- Find images with similar prompts
SELECT 
    i.id,
    i.path,
    i.prompt,
    1 - (ec.bge_embedding <=> $1::vector) AS similarity
FROM image i
INNER JOIN embedding_cache ec ON i.prompt_embedding_id = ec.id
WHERE 
    i.is_upscaled = true  -- Only search final versions
    AND ec.content_type = 'prompt'
ORDER BY ec.bge_embedding <=> $1::vector
LIMIT 100;
```

### **Find All Images with Same Negative Prompt**

```sql
-- Find images using your standard negative
SELECT 
    i.*,
    ec.content_text as negative_prompt_text
FROM image i
INNER JOIN embedding_cache ec ON i.negative_prompt_embedding_id = ec.id
WHERE 
    ec.content_hash = $1  -- SHA256 of "BadPonyHD, bad hands, ugly"
    AND i.is_upscaled = true
ORDER BY i.created_at DESC;
```

### **Cache Statistics**

```sql
-- Show most-used embeddings
SELECT 
    content_type,
    LEFT(content_text, 50) as preview,
    reference_count,
    pg_size_pretty(
        pg_column_size(bge_embedding) + 
        pg_column_size(clip_l_embedding) + 
        pg_column_size(clip_g_embedding) + 
        pg_column_size(clip_h_embedding)
    ) as embedding_size
FROM embedding_cache
ORDER BY reference_count DESC
LIMIT 20;

-- Example output:
-- content_type | preview                              | reference_count | embedding_size
-- negative     | BadPonyHD, bad hands, ugly           | 127,543        | 12 KB
-- negative     | Stable_Yogis_PDXL_Negatives-neg      | 89,234         | 12 KB
-- prompt       | masterpiece, best quality, 1girl     | 15,678         | 12 KB
-- prompt       | photorealistic portrait, detailed    | 8,234          | 12 KB
```

---

## **Storage Savings Calculation**

### **Your Collection: 200,000 Images**

**Breakdown:**
- 100,000 base images (no visual embedding)
- 100,000 upscaled images (full embeddings)
- 10 unique negative prompts (reused heavily)
- 50,000 unique positive prompts (2 images each on average)

### **Without Deduplication:**

```
Prompt embeddings:       200,000 × (1024+768+1280)×4 bytes = 2.44 GB
Negative embeddings:     200,000 × (1024+768+1280)×4 bytes = 2.44 GB
Visual embeddings:       200,000 × 1024×4 bytes            = 800 MB
Total:                                                       5.68 GB
```

### **With Deduplication:**

```
embedding_cache table:
  - 10 negative prompts:  10 × 12 KB        = 120 KB
  - 50,000 prompts:       50,000 × 12 KB    = 600 MB
  - 100,000 visual:       100,000 × 4 KB    = 400 MB
  Subtotal:                                   1.00 GB

image table (just foreign keys):
  - 200,000 × 3 integers × 4 bytes           = 2.4 MB
  
Total:                                        1.00 GB
```

**Savings: 82% less storage (5.68 GB → 1.00 GB)**

---

## **Processing Time Savings**

### **Without Deduplication:**

```
200,000 images × embedding time:
  - BGE text (prompt):         200,000 × 15ms = 50 min
  - BGE text (negative):       200,000 × 15ms = 50 min
  - CLIP-L text (prompt):      200,000 × 10ms = 33 min
  - CLIP-L text (negative):    200,000 × 10ms = 33 min
  - CLIP-G text (prompt):      200,000 × 12ms = 40 min
  - CLIP-G text (negative):    200,000 × 12ms = 40 min
  - CLIP-H visual:             200,000 × 25ms = 83 min
Total:                                         329 minutes (5.5 hours)
```

### **With Deduplication:**

```
Unique embeddings × embedding time:
  - BGE text:              50,010 × 15ms = 12.5 min
  - CLIP-L text:           50,010 × 10ms = 8.3 min
  - CLIP-G text:           50,010 × 12ms = 10.0 min
  - CLIP-H visual:         100,000 × 25ms = 41.7 min
Total:                                     72.5 minutes (1.2 hours)
```

**Savings: 78% less processing time (5.5 hours → 1.2 hours)**

---

## **Cache Cleanup**

Remove unused embeddings when images are deleted:

```csharp
public async Task DeleteImageAsync(int imageId)
{
    var image = await dataStore.GetImageByIdAsync(imageId);
    
    // Decrement reference counts
    if (image.PromptEmbeddingId.HasValue)
        await dataStore.DecrementEmbeddingReferenceCountAsync(image.PromptEmbeddingId.Value);
    
    if (image.NegativePromptEmbeddingId.HasValue)
        await dataStore.DecrementEmbeddingReferenceCountAsync(image.NegativePromptEmbeddingId.Value);
    
    if (image.ImageEmbeddingId.HasValue)
        await dataStore.DecrementEmbeddingReferenceCountAsync(image.ImageEmbeddingId.Value);
    
    // Delete image
    await dataStore.DeleteImageAsync(imageId);
}

// Periodic cleanup (run monthly)
public async Task CleanupUnusedEmbeddingsAsync()
{
    await dataStore.ExecuteAsync(@"
        DELETE FROM embedding_cache
        WHERE reference_count = 0
    ");
}
```

---

## **Migration Strategy**

### **Phase 1: Add Cache Table**

```sql
-- Run migration to add embedding_cache table and new columns
-- Keep old embedding columns for backwards compatibility
```

### **Phase 2: Populate Cache**

```csharp
// One-time migration: Extract embeddings from existing images
var images = await dataStore.GetAllImagesAsync();
var embeddingCache = new EmbeddingCacheService();

foreach (var image in images)
{
    // Move prompt embedding to cache
    if (image.PromptEmbedding != null)
    {
        var cacheEntry = new EmbeddingCache
        {
            ContentHash = ComputeSHA256(image.Prompt),
            ContentType = "prompt",
            ContentText = image.Prompt,
            BgeEmbedding = image.PromptEmbedding,
            ClipLEmbedding = image.ClipLEmbedding,
            ClipGEmbedding = image.ClipGEmbedding,
            ReferenceCount = 1
        };
        
        var cacheId = await dataStore.InsertEmbeddingCacheAsync(cacheEntry);
        await dataStore.UpdateImageEmbeddingIdAsync(image.Id, "prompt", cacheId);
    }
    
    // Move negative embedding to cache
    // ... same for negative prompt and visual embedding ...
}
```

### **Phase 3: Drop Legacy Columns**

```sql
-- After verifying all embeddings migrated
ALTER TABLE image DROP COLUMN prompt_embedding;
ALTER TABLE image DROP COLUMN negative_prompt_embedding;
ALTER TABLE image DROP COLUMN image_embedding;
ALTER TABLE image DROP COLUMN clip_l_embedding;
ALTER TABLE image DROP COLUMN clip_g_embedding;
```

---

## **Summary**

### **Architecture Changes:**

✅ **embedding_cache table**: Store unique embeddings once  
✅ **Foreign key references**: Link images to cached embeddings  
✅ **SHA256 hashing**: Detect duplicate content instantly  
✅ **Reference counting**: Track usage, cleanup unused  
✅ **Smart visual embedding**: Only generate for upscaled images  
✅ **Base/upscale linking**: Share text embeddings automatically  

### **Benefits:**

📉 **82% storage savings** (5.68 GB → 1.00 GB for 200K images)  
⚡ **78% faster generation** (5.5 hours → 1.2 hours)  
🎯 **Instant cache hits** for repeated prompts  
🧹 **Automatic cleanup** when images deleted  
📊 **Usage statistics** for optimization  

### **Your Workflow Impact:**

- **Standard negative prompt** (100K uses): Generated once, cached forever
- **Base + upscale pairs**: Text embeddings reused, visual only for upscale
- **Similar prompts**: First generates, rest use cache
- **Search performance**: Identical (JOIN adds <1ms overhead)
