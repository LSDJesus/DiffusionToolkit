# DAAM (Diffusion Attentive Attribution Maps) Database Integration

## Overview
Complete database schema and API for storing and querying DAAM heatmaps - cross-attention attribution maps that reveal **where the model placed each prompt token** during image generation.

## What is DAAM?

**DAAM = Introspective Semantic Awareness**

During diffusion, cross-attention layers map prompt tokens to spatial locations. DAAM extracts these maps as heatmaps (one per token) by hooking into the model's existing attention mechanism.

### Key Insight
Instead of using external models (SAM3/YOLO) to **detect** regions post-generation, DAAM uses the model's **own cross-attention** to understand what it placed where. This is not detection—it's **introspection**.

### Benefits
- ✅ Zero additional models (uses existing UNet attention)
- ✅ Zero detection errors (model's ground truth)
- ✅ Zero extra VRAM (attention maps already computed)
- ✅ Perfect for booru-tagged prompts (one tag = one heatmap)

## Database Schema (V6 Migration)

### Tables

#### 1. `daam_heatmap`
Stores individual per-token attention heatmaps

**Key Fields:**
- `token` - The prompt token ("blue eyes", "forest background")
- `token_index` - Position in prompt (for ordering)
- `is_negative` - True for negative prompt tokens
- `heatmap_data` - Compressed 2D array (zlib)
- `heatmap_width/height` - Dimensions (typically 64×64 latent space)
- `max_attention`, `mean_attention`, `total_attention` - Statistics
- `coverage_area` - Percentage of image with significant attention
- `bbox_x/y/width/height` - Bounding box of attention region
- `sampling_config` - JSON: skip_early_ratio, stride, optimization preset

**Use Cases:**
- Visualize what tokens the model "paid attention to"
- Export heatmaps for LSD region refinement
- Debug prompt understanding

#### 2. `daam_semantic_group`
Merged overlapping tokens (e.g., "blue eyes" + "eyes" + "detailed eyes" → one group)

**Key Fields:**
- `group_name` - Representative name ("eyes")
- `member_tokens[]` - All merged tokens
- `merged_heatmap_data` - Union of all member heatmaps
- `attention_density` - `total_attention / coverage_area` (for auto-weighting)
- `auto_weight` - Calculated conditioning strength (0.0-1.0)
- `overlap_threshold` - Threshold used for merging (default 0.9)
- `merge_count` - Number of tokens merged

**Use Cases:**
- Reduce redundant tags (3 eye-related tags → 1 semantic region)
- Calculate automatic conditioning weights for LSD
- Cleaner compositional refinement

#### 3. `daam_spatial_index`
Grid-based spatial index for fast location queries

**Key Fields:**
- `token` - Indexed token
- `grid_size` - 4×4 or 8×8 grid
- `grid_cell_id` - Cell number (0-15 for 4×4)
- `cell_attention` - Average attention in this cell

**Use Cases:**
- Query: "Find images where 'eyes' has high attention in top-left quadrant"
- Query: "Find images where 'dragon' appears in center"
- Spatial filtering in search UI

### Functions

#### `find_images_by_token_location(token, grid_cells[], min_attention, max_results)`
Find images where a token appears in specific grid regions

**Example:**
```sql
-- Find images with "blue eyes" in top-left quadrant
SELECT * FROM find_images_by_token_location(
    'blue eyes',
    ARRAY[0, 1, 4, 5],  -- top-left cells in 4×4 grid
    0.3,  -- minimum attention threshold
    100   -- max results
);
```

#### `get_daam_summary(image_id)`
Get all tokens and their attention stats for an image

**Example:**
```sql
SELECT * FROM get_daam_summary(12345);
-- Returns: token, is_negative, max_attention, coverage_area, bbox
```

### Views

#### `daam_stats`
Global DAAM statistics:
- `images_with_daam` - Count of images with DAAM data
- `total_heatmaps` - Total heatmap count
- `positive_heatmaps`, `negative_heatmaps` - Breakdown by type
- `avg_mean_attention` - Average attention across all heatmaps
- `total_semantic_groups` - Count of merged groups
- `avg_merge_count` - Average tokens per group

## Data Access Layer (PostgreSQLDataStore.DAAM.cs)

### CRUD Operations

```csharp
// Store heatmaps
await dataStore.StoreDaamHeatmapsAsync(imageId, heatmaps);

// Retrieve heatmaps
var allHeatmaps = await dataStore.GetDaamHeatmapsAsync(imageId);
var positiveOnly = await dataStore.GetDaamHeatmapsAsync(imageId, isNegative: false);
var eyesHeatmap = await dataStore.GetDaamHeatmapByTokenAsync(imageId, "blue eyes");

// Delete heatmaps
await dataStore.DeleteDaamHeatmapsAsync(imageId);

// Get summary
var summary = await dataStore.GetDaamSummaryAsync(imageId);
```

### Semantic Groups

```csharp
// Store merged semantic groups
await dataStore.StoreDaamSemanticGroupsAsync(imageId, groups);

// Retrieve groups
var groups = await dataStore.GetDaamSemanticGroupsAsync(imageId);
// Ordered by attention_density DESC
```

### Spatial Queries

```csharp
// Find images by token location
var results = await dataStore.FindImagesByTokenLocationAsync(
    token: "blue eyes",
    gridCells: new[] { 0, 1, 4, 5 },  // top-left quadrant
    minAttention: 0.3f,
    maxResults: 100
);

// Helper for named regions
var imageIds = await dataStore.FindImagesByTokenInRegionAsync(
    token: "dragon",
    region: "center",  // "top-left", "top-right", "bottom", etc.
    minAttention: 0.3f
);
```

### Statistics

```csharp
var stats = await dataStore.GetDaamStatsAsync();
// → ImagesWithDaam, TotalHeatmaps, AvgMergeCount, etc.

var count = await dataStore.GetImageCountWithDaamAsync();
```

### Helper Methods

```csharp
// Compress/decompress heatmap data
var compressed = PostgreSQLDataStore.CompressHeatmap(floatArray);
var decompressed = PostgreSQLDataStore.DecompressHeatmap(compressed, width, height);

// Calculate spatial index
var entries = PostgreSQLDataStore.CalculateSpatialIndex(
    heatmap: floatArray,
    width: 64,
    height: 64,
    token: "blue eyes",
    gridSize: 4  // 4×4 grid = 16 cells
);
await dataStore.StoreDaamSpatialIndexAsync(imageId, entries);
```

## Integration with ComfyUI Watcher

### Workflow

1. **ComfyUI generates image** with DAAM enabled (Luna DAAM KSampler)
2. **DAAM heatmaps extracted** during sampling (bidirectional: pos + neg)
3. **Watcher daemon detects new image** 
4. **Parse DAAM metadata** from PNG/workflow JSON
5. **Store in database**:
   - Raw per-token heatmaps → `daam_heatmap`
   - Merged semantic groups → `daam_semantic_group`
   - Spatial index → `daam_spatial_index`

### Expected Metadata Format (from ComfyUI)

```json
{
  "daam_heatmaps": {
    "positive": [
      {
        "token": "blue eyes",
        "token_index": 2,
        "heatmap": "base64_encoded_compressed_array",
        "width": 64,
        "height": 64,
        "max_attention": 0.85,
        "mean_attention": 0.42,
        "total_attention": 172.5,
        "coverage_area": 0.15,
        "bbox": [12, 8, 20, 16]
      }
    ],
    "negative": [...],
    "config": {
      "skip_early_ratio": 0.3,
      "stride": 1,
      "preset": "balanced"
    }
  },
  "daam_semantic_groups": [
    {
      "group_name": "eyes",
      "member_tokens": ["blue eyes", "eyes", "detailed eyes"],
      "attention_density": 2.8,
      "auto_weight": 1.0,
      "merged_heatmap": "base64_compressed",
      ...
    }
  ]
}
```

### Parser Implementation (TODO)

```csharp
public class DaamMetadataParser
{
    public static async Task ParseAndStoreAsync(
        int imageId, 
        string metadataJson, 
        PostgreSQLDataStore dataStore)
    {
        var metadata = JsonSerializer.Deserialize<DaamMetadata>(metadataJson);
        
        // Parse and store individual heatmaps
        var heatmaps = new List<DaamHeatmapEntity>();
        foreach (var heatmap in metadata.PositiveHeatmaps)
        {
            heatmaps.Add(new DaamHeatmapEntity
            {
                Token = heatmap.Token,
                TokenIndex = heatmap.TokenIndex,
                IsNegative = false,
                HeatmapWidth = heatmap.Width,
                HeatmapHeight = heatmap.Height,
                HeatmapData = Convert.FromBase64String(heatmap.Heatmap),
                CompressionType = "zlib",
                MaxAttention = heatmap.MaxAttention,
                MeanAttention = heatmap.MeanAttention,
                TotalAttention = heatmap.TotalAttention,
                CoverageArea = heatmap.CoverageArea,
                // Parse bbox...
            });
        }
        
        await dataStore.StoreDaamHeatmapsAsync(imageId, heatmaps);
        
        // Store semantic groups
        // Store spatial index
        // ...
    }
}
```

## Use Cases

### 1. Prompt Analysis
"What did the model actually understand from my prompt?"
```csharp
var summary = await dataStore.GetDaamSummaryAsync(imageId);
// Shows which tokens got high/low attention
```

### 2. Region-Based Search
"Find all images where 'dragon' appears in the center"
```csharp
var imageIds = await dataStore.FindImagesByTokenInRegionAsync("dragon", "center");
```

### 3. LSD Integration
Export DAAM data for Luna LSD compositional refinement:
```csharp
var groups = await dataStore.GetDaamSemanticGroupsAsync(imageId);
// Use merged heatmaps as spatial masks for region conditioning
```

### 4. Token Effectiveness Analysis
"Which tags are most effective in my prompts?"
```sql
SELECT token, AVG(max_attention) as avg_max, AVG(coverage_area) as avg_coverage
FROM daam_heatmap
WHERE is_negative = false
GROUP BY token
ORDER BY avg_max DESC;
```

### 5. Spatial Pattern Discovery
"Where do certain concepts typically appear?"
```sql
SELECT grid_cell_id, AVG(cell_attention)
FROM daam_spatial_index
WHERE token = 'eyes'
GROUP BY grid_cell_id
ORDER BY avg DESC;
-- Find typical spatial location for "eyes"
```

## File Summary

**New Files:**
- `Diffusion.Database.PostgreSQL/PostgreSQLMigrations_DAAM.sql`
- `Diffusion.Database.PostgreSQL/Models/DaamHeatmap.cs`
- `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.DAAM.cs`

**Modified Files:**
- `Diffusion.Database.PostgreSQL/PostgreSQLMigrations.cs` (added V6)
- `Diffusion.Database.PostgreSQL/Diffusion.Database.PostgreSQL.csproj` (embedded resource)

## Next Steps

1. **Watcher Integration** - Parse DAAM metadata from ComfyUI outputs
2. **UI Visualization** - Show heatmap overlays in preview pane
3. **Search Filters** - Add spatial token filters to search UI
4. **LSD Export** - Export semantic groups for region refinement
5. **Analytics Dashboard** - Token effectiveness, spatial patterns

---

**Status: Database Foundation Complete ✅**
Ready for ComfyUI watcher integration and UI implementation.
