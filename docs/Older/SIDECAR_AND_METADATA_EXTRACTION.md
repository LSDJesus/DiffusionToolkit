# Sidecar File Import & Metadata Extraction

## Overview

Enhanced metadata extraction system that:
1. **Imports .txt sidecar files** (from WD14 tagger, BLIP, etc.) as separate searchable column
2. **Extracts embedded PNG metadata** into dedicated searchable database columns

---

## **Feature 1: Sidecar .txt File Import**

### **Use Case:**

Image taggers (WD14, BLIP, DeepDanbooru, etc.) generate `.txt` files with AI-generated tags:

```
image_001.png          ← Original image
image_001.txt          ← AI-generated tags
```

**Example .txt content:**
```
1girl, solo, long hair, blonde hair, blue eyes, smile, looking at viewer, 
upper body, outdoors, day, sky, cloud, masterpiece, best quality, 
highres, absurdres
```

### **Database Schema:**

```sql
ALTER TABLE image ADD COLUMN generated_tags TEXT;
CREATE INDEX idx_image_generated_tags ON image USING gin(to_tsvector('english', generated_tags));
```

**Benefits:**
- Full-text search on AI-generated tags
- Separate from user-entered prompts
- Preserves original tagger output
- GIN index for fast keyword searches

### **Import Logic:**

```csharp
// In Metadata.ReadFromFileInternal()
var sidecarPath = Path.ChangeExtension(file, ".txt");
if (File.Exists(sidecarPath))
{
    fileParameters.GeneratedTags = await File.ReadAllTextAsync(sidecarPath);
}
```

### **Search Examples:**

```sql
-- Find all images with "blonde hair" in generated tags
SELECT * FROM image
WHERE to_tsvector('english', generated_tags) @@ to_tsquery('english', 'blonde & hair')
ORDER BY created_date DESC;

-- Find images with specific character traits
SELECT * FROM image
WHERE generated_tags ILIKE '%1girl%'
  AND generated_tags ILIKE '%blue eyes%'
  AND generated_tags ILIKE '%smile%';

-- Combine with prompt search
SELECT * FROM image
WHERE (prompt ILIKE '%portrait%' OR generated_tags ILIKE '%portrait%')
  AND nsfw = false;
```

---

## **Feature 2: Searchable Metadata Extraction**

### **Problem:**

Metadata is currently stored in these fields but not easily searchable:
- `OtherParameters` (TEXT) - Raw unparsed metadata
- `Parameters` (TEXT) - Full generation parameters string

### **Solution:**

Extract commonly searched metadata into dedicated columns with indexes.

### **New Columns:**

```sql
-- LoRA/LyCORIS embeddings
ALTER TABLE image ADD COLUMN loras JSONB;
CREATE INDEX idx_image_loras ON image USING gin(loras);

-- VAE model
ALTER TABLE image ADD COLUMN vae TEXT;
CREATE INDEX idx_image_vae ON image(vae);

-- Refiner model (for SDXL)
ALTER TABLE image ADD COLUMN refiner_model TEXT;
ALTER TABLE image ADD COLUMN refiner_switch DECIMAL(4, 2);
CREATE INDEX idx_image_refiner_model ON image(refiner_model);

-- Upscaler info
ALTER TABLE image ADD COLUMN upscaler TEXT;
ALTER TABLE image ADD COLUMN upscale_factor DECIMAL(3, 1);
CREATE INDEX idx_image_upscaler ON image(upscaler);

-- Hires fix
ALTER TABLE image ADD COLUMN hires_steps INT;
ALTER TABLE image ADD COLUMN hires_upscaler TEXT;
ALTER TABLE image ADD COLUMN hires_upscale DECIMAL(3, 1);
ALTER TABLE image ADD COLUMN denoising_strength DECIMAL(4, 2);

-- ControlNet (JSONB array for multiple ControlNets)
ALTER TABLE image ADD COLUMN controlnets JSONB;
CREATE INDEX idx_image_controlnets ON image USING gin(controlnets);

-- IP-Adapter
ALTER TABLE image ADD COLUMN ip_adapter TEXT;
ALTER TABLE image ADD COLUMN ip_adapter_strength DECIMAL(4, 2);

-- Wildcards used
ALTER TABLE image ADD COLUMN wildcards_used TEXT[];
CREATE INDEX idx_image_wildcards ON image USING gin(wildcards_used);

-- Generation time (for performance tracking)
ALTER TABLE image ADD COLUMN generation_time_seconds DECIMAL(6, 2);

-- Scheduler
ALTER TABLE image ADD COLUMN scheduler TEXT;
CREATE INDEX idx_image_scheduler ON image(scheduler);
```

### **Extraction Patterns:**

#### **LoRAs/LyCORIS:**

```
Input: "<lora:detail_enhancer_v2:0.8>, <lora:add_detail:0.6>"
Output: 
{
  "loras": [
    {"name": "detail_enhancer_v2", "strength": 0.8},
    {"name": "add_detail", "strength": 0.6}
  ]
}
```

#### **Hires Fix:**

```
Input: "Hires upscale: 2, Hires steps: 20, Hires upscaler: 4x-UltraSharp, Denoising strength: 0.4"
Output:
  hires_upscale = 2.0
  hires_steps = 20
  hires_upscaler = "4x-UltraSharp"
  denoising_strength = 0.4
```

#### **ControlNet:**

```
Input: "ControlNet 0: \"preprocessor: openpose_full, model: control_v11p_sd15_openpose_fp16, weight: 1.0\""
Output:
{
  "controlnets": [
    {
      "index": 0,
      "preprocessor": "openpose_full",
      "model": "control_v11p_sd15_openpose_fp16",
      "weight": 1.0
    }
  ]
}
```

#### **Wildcards:**

```
Input: "a __color__ __animal__ in a __location__"
Output:
  wildcards_used = ["color", "animal", "location"]
```

---

## **Implementation**

### **1. Update Database Schema (V4 Migration)**

```csharp
private async Task ApplyV4Async()
{
    var sql = @"
        -- Sidecar .txt file content (AI-generated tags)
        ALTER TABLE image ADD COLUMN IF NOT EXISTS generated_tags TEXT;
        CREATE INDEX IF NOT EXISTS idx_image_generated_tags 
            ON image USING gin(to_tsvector('english', generated_tags));
        
        -- LoRA/LyCORIS models with strengths
        ALTER TABLE image ADD COLUMN IF NOT EXISTS loras JSONB;
        CREATE INDEX IF NOT EXISTS idx_image_loras ON image USING gin(loras);
        
        -- VAE model
        ALTER TABLE image ADD COLUMN IF NOT EXISTS vae TEXT;
        CREATE INDEX IF NOT EXISTS idx_image_vae ON image(vae);
        
        -- Refiner (SDXL)
        ALTER TABLE image ADD COLUMN IF NOT EXISTS refiner_model TEXT;
        ALTER TABLE image ADD COLUMN IF NOT EXISTS refiner_switch DECIMAL(4, 2);
        CREATE INDEX IF NOT EXISTS idx_image_refiner_model ON image(refiner_model);
        
        -- Upscaler
        ALTER TABLE image ADD COLUMN IF NOT EXISTS upscaler TEXT;
        ALTER TABLE image ADD COLUMN IF NOT EXISTS upscale_factor DECIMAL(3, 1);
        CREATE INDEX IF NOT EXISTS idx_image_upscaler ON image(upscaler);
        
        -- Hires fix
        ALTER TABLE image ADD COLUMN IF NOT EXISTS hires_steps INT;
        ALTER TABLE image ADD COLUMN IF NOT EXISTS hires_upscaler TEXT;
        ALTER TABLE image ADD COLUMN IF NOT EXISTS hires_upscale DECIMAL(3, 1);
        ALTER TABLE image ADD COLUMN IF NOT EXISTS denoising_strength DECIMAL(4, 2);
        
        -- ControlNet (array of configurations)
        ALTER TABLE image ADD COLUMN IF NOT EXISTS controlnets JSONB;
        CREATE INDEX IF NOT EXISTS idx_image_controlnets ON image USING gin(controlnets);
        
        -- IP-Adapter
        ALTER TABLE image ADD COLUMN IF NOT EXISTS ip_adapter TEXT;
        ALTER TABLE image ADD COLUMN IF NOT EXISTS ip_adapter_strength DECIMAL(4, 2);
        
        -- Wildcards
        ALTER TABLE image ADD COLUMN IF NOT EXISTS wildcards_used TEXT[];
        CREATE INDEX IF NOT EXISTS idx_image_wildcards ON image USING gin(wildcards_used);
        
        -- Generation time
        ALTER TABLE image ADD COLUMN IF NOT EXISTS generation_time_seconds DECIMAL(6, 2);
        
        -- Scheduler
        ALTER TABLE image ADD COLUMN IF NOT EXISTS scheduler TEXT;
        CREATE INDEX IF NOT EXISTS idx_image_scheduler ON image(scheduler);
    ";

    await _connection.ExecuteAsync(sql);
}
```

### **2. Update ImageEntity Model**

```csharp
// Sidecar .txt file content (AI-generated tags from WD14, BLIP, etc.)
public string? GeneratedTags { get; set; }

// Extracted searchable metadata
public string? Loras { get; set; }              // JSONB string
public string? Vae { get; set; }
public string? RefinerModel { get; set; }
public decimal? RefinerSwitch { get; set; }
public string? Upscaler { get; set; }
public decimal? UpscaleFactor { get; set; }
public int? HiresSteps { get; set; }
public string? HiresUpscaler { get; set; }
public decimal? HiresUpscale { get; set; }
public decimal? DenoisingStrength { get; set; }
public string? Controlnets { get; set; }        // JSONB string
public string? IpAdapter { get; set; }
public decimal? IpAdapterStrength { get; set; }
public string[]? WildcardsUsed { get; set; }
public decimal? GenerationTimeSeconds { get; set; }
public string? Scheduler { get; set; }
```

### **3. Update FileParameters**

```csharp
// Add to FileParameters class
public string? GeneratedTags { get; set; }
public List<LoraInfo>? Loras { get; set; }
public string? Vae { get; set; }
public string? RefinerModel { get; set; }
public decimal? RefinerSwitch { get; set; }
public string? Upscaler { get; set; }
public decimal? UpscaleFactor { get; set; }
public int? HiresSteps { get; set; }
public string? HiresUpscaler { get; set; }
public decimal? HiresUpscale { get; set; }
public decimal? DenoisingStrength { get; set; }
public List<ControlNetInfo>? ControlNets { get; set; }
public string? IpAdapter { get; set; }
public decimal? IpAdapterStrength { get; set; }
public List<string>? WildcardsUsed { get; set; }
public decimal? GenerationTimeSeconds { get; set; }
public string? Scheduler { get; set; }

// Helper classes
public class LoraInfo
{
    public string Name { get; set; }
    public decimal Strength { get; set; }
}

public class ControlNetInfo
{
    public int Index { get; set; }
    public string Preprocessor { get; set; }
    public string Model { get; set; }
    public decimal Weight { get; set; }
    public decimal? GuidanceStart { get; set; }
    public decimal? GuidanceEnd { get; set; }
}
```

### **4. Update Metadata.cs Extraction**

```csharp
public static FileParameters? ReadFromFileInternal(string file)
{
    // ... existing code ...
    
    // NEW: Import sidecar .txt file (AI-generated tags)
    var sidecarPath = Path.ChangeExtension(file, ".txt");
    if (File.Exists(sidecarPath))
    {
        try
        {
            fileParameters.GeneratedTags = File.ReadAllText(sidecarPath);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to read sidecar file {sidecarPath}: {ex.Message}");
        }
    }
    
    // NEW: Extract searchable metadata from Parameters field
    if (!string.IsNullOrEmpty(fileParameters.Parameters))
    {
        ExtractSearchableMetadata(fileParameters);
    }
    
    return fileParameters;
}

private static void ExtractSearchableMetadata(FileParameters fp)
{
    var parameters = fp.Parameters;
    
    // Extract LoRAs: <lora:name:strength>
    var loraRegex = new Regex(@"<lora:([^:>]+):([0-9.]+)>", RegexOptions.IgnoreCase);
    var loraMatches = loraRegex.Matches(parameters);
    if (loraMatches.Count > 0)
    {
        fp.Loras = loraMatches.Select(m => new LoraInfo
        {
            Name = m.Groups[1].Value,
            Strength = decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
        }).ToList();
    }
    
    // Extract VAE
    var vaeMatch = Regex.Match(parameters, @"VAE:\s*([^,\n]+)", RegexOptions.IgnoreCase);
    if (vaeMatch.Success)
        fp.Vae = vaeMatch.Groups[1].Value.Trim();
    
    // Extract Refiner
    var refinerMatch = Regex.Match(parameters, @"Refiner:\s*([^,\n]+)", RegexOptions.IgnoreCase);
    if (refinerMatch.Success)
        fp.RefinerModel = refinerMatch.Groups[1].Value.Trim();
    
    var refinerSwitchMatch = Regex.Match(parameters, @"Refiner switch at:\s*([0-9.]+)", RegexOptions.IgnoreCase);
    if (refinerSwitchMatch.Success)
        fp.RefinerSwitch = decimal.Parse(refinerSwitchMatch.Groups[1].Value, CultureInfo.InvariantCulture);
    
    // Extract Upscaler
    var upscalerMatch = Regex.Match(parameters, @"Upscaler:\s*([^,\n]+)", RegexOptions.IgnoreCase);
    if (upscalerMatch.Success)
        fp.Upscaler = upscalerMatch.Groups[1].Value.Trim();
    
    var upscaleMatch = Regex.Match(parameters, @"Upscale by:\s*([0-9.]+)", RegexOptions.IgnoreCase);
    if (upscaleMatch.Success)
        fp.UpscaleFactor = decimal.Parse(upscaleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
    
    // Extract Hires fix
    var hiresStepsMatch = Regex.Match(parameters, @"Hires steps:\s*(\d+)", RegexOptions.IgnoreCase);
    if (hiresStepsMatch.Success)
        fp.HiresSteps = int.Parse(hiresStepsMatch.Groups[1].Value);
    
    var hiresUpscalerMatch = Regex.Match(parameters, @"Hires upscaler:\s*([^,\n]+)", RegexOptions.IgnoreCase);
    if (hiresUpscalerMatch.Success)
        fp.HiresUpscaler = hiresUpscalerMatch.Groups[1].Value.Trim();
    
    var hiresUpscaleMatch = Regex.Match(parameters, @"Hires upscale:\s*([0-9.]+)", RegexOptions.IgnoreCase);
    if (hiresUpscaleMatch.Success)
        fp.HiresUpscale = decimal.Parse(hiresUpscaleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
    
    var denoisingMatch = Regex.Match(parameters, @"Denoising strength:\s*([0-9.]+)", RegexOptions.IgnoreCase);
    if (denoisingMatch.Success)
        fp.DenoisingStrength = decimal.Parse(denoisingMatch.Groups[1].Value, CultureInfo.InvariantCulture);
    
    // Extract ControlNet
    var controlNetRegex = new Regex(@"ControlNet\s*(\d+):\s*""([^""]+)""", RegexOptions.IgnoreCase);
    var controlNetMatches = controlNetRegex.Matches(parameters);
    if (controlNetMatches.Count > 0)
    {
        fp.ControlNets = new List<ControlNetInfo>();
        foreach (Match match in controlNetMatches)
        {
            var index = int.Parse(match.Groups[1].Value);
            var config = match.Groups[2].Value;
            
            var cn = new ControlNetInfo { Index = index };
            
            var preprocessorMatch = Regex.Match(config, @"preprocessor:\s*([^,]+)");
            if (preprocessorMatch.Success)
                cn.Preprocessor = preprocessorMatch.Groups[1].Value.Trim();
            
            var modelMatch = Regex.Match(config, @"model:\s*([^,]+)");
            if (modelMatch.Success)
                cn.Model = modelMatch.Groups[1].Value.Trim();
            
            var weightMatch = Regex.Match(config, @"weight:\s*([0-9.]+)");
            if (weightMatch.Success)
                cn.Weight = decimal.Parse(weightMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            
            fp.ControlNets.Add(cn);
        }
    }
    
    // Extract Wildcards (detect __ patterns)
    var wildcardRegex = new Regex(@"__([a-zA-Z0-9_]+)__");
    var wildcardMatches = wildcardRegex.Matches(fp.Prompt ?? "");
    if (wildcardMatches.Count > 0)
    {
        fp.WildcardsUsed = wildcardMatches.Select(m => m.Groups[1].Value).Distinct().ToList();
    }
    
    // Extract Scheduler
    var schedulerMatch = Regex.Match(parameters, @"Schedule type:\s*([^,\n]+)", RegexOptions.IgnoreCase);
    if (schedulerMatch.Success)
        fp.Scheduler = schedulerMatch.Groups[1].Value.Trim();
}
```

---

## **Search Use Cases**

### **1. Find All Images Using Specific LoRA**

```sql
-- Find images using "detail_enhancer_v2" LoRA
SELECT 
    id, 
    path, 
    prompt,
    loras->0->>'name' as lora_name,
    loras->0->>'strength' as lora_strength
FROM image
WHERE loras::jsonb @> '[{"name": "detail_enhancer_v2"}]'::jsonb
ORDER BY created_date DESC;
```

### **2. Find Images with Hires Fix**

```sql
-- Find all images using hires fix with 4x-UltraSharp
SELECT * FROM image
WHERE hires_upscaler = '4x-UltraSharp'
  AND hires_upscale >= 2.0
ORDER BY created_date DESC;
```

### **3. Find Images with ControlNet**

```sql
-- Find images using OpenPose ControlNet
SELECT * FROM image
WHERE controlnets::jsonb @> '[{"model": "control_v11p_sd15_openpose_fp16"}]'::jsonb
ORDER BY created_date DESC;
```

### **4. Combine Generated Tags with Prompt Search**

```sql
-- Find blonde girls in either prompt or AI-generated tags
SELECT 
    id,
    path,
    COALESCE(prompt, '') as prompt,
    generated_tags
FROM image
WHERE 
    (to_tsvector('english', COALESCE(prompt, '')) @@ to_tsquery('english', 'blonde & girl'))
    OR
    (to_tsvector('english', COALESCE(generated_tags, '')) @@ to_tsquery('english', 'blonde & girl'))
ORDER BY created_date DESC
LIMIT 100;
```

### **5. Find Images by Specific VAE**

```sql
-- Find all images using vae-ft-mse-840000-ema-pruned
SELECT * FROM image
WHERE vae = 'vae-ft-mse-840000-ema-pruned'
ORDER BY created_date DESC;
```

### **6. Find Fastest Generation Times**

```sql
-- Find images generated in under 10 seconds
SELECT 
    id,
    path,
    generation_time_seconds,
    width,
    height,
    steps
FROM image
WHERE generation_time_seconds < 10.0
ORDER BY generation_time_seconds ASC
LIMIT 100;
```

---

## **UI Integration**

### **Search Filters Panel**

Add new filter options:

```
┌─ Advanced Filters ────────────────────┐
│                                        │
│ Generated Tags: [___________________] │
│                                        │
│ LoRA Name:     [___________________]  │
│ LoRA Strength: [____] to [____]       │
│                                        │
│ VAE:           [▼ All VAEs        ]   │
│ Upscaler:      [▼ All Upscalers   ]   │
│ Scheduler:     [▼ All Schedulers   ]  │
│                                        │
│ ☑ Has ControlNet                      │
│ ☑ Has Hires Fix                       │
│ ☑ Has IP-Adapter                      │
│ ☑ Uses Wildcards                      │
│                                        │
│ Hires Upscale: [____] to [____]       │
│ Gen Time (sec):[____] to [____]       │
│                                        │
└────────────────────────────────────────┘
```

### **Image Details Panel**

Show extracted metadata:

```
┌─ Image Details ───────────────────────┐
│                                        │
│ Generated Tags:                        │
│ 1girl, solo, long hair, blonde hair,  │
│ blue eyes, smile, looking at viewer,  │
│ masterpiece, best quality, highres    │
│                                        │
│ LoRAs:                                 │
│ • detail_enhancer_v2 (0.8)            │
│ • add_detail (0.6)                    │
│                                        │
│ VAE: vae-ft-mse-840000-ema-pruned     │
│ Upscaler: 4x-UltraSharp (2.0x)        │
│ Scheduler: Karras                      │
│                                        │
│ Hires Fix:                             │
│ • Steps: 20                            │
│ • Upscaler: 4x-UltraSharp             │
│ • Upscale: 2.0x                       │
│ • Denoising: 0.4                      │
│                                        │
│ Generation Time: 8.3 seconds           │
│                                        │
└────────────────────────────────────────┘
```

---

## **Benefits Summary**

### **Sidecar .txt Import:**
✅ **Searchable AI-generated tags** from WD14/BLIP/DeepDanbooru  
✅ **Full-text search** with GIN indexes  
✅ **Preserved tagger output** (no data loss)  
✅ **Complements user prompts** (search both)  

### **Extracted Metadata:**
✅ **LoRA/LyCORIS tracking** - Find images using specific LoRAs  
✅ **VAE/Upscaler filtering** - Quality control and workflow optimization  
✅ **Hires fix analysis** - Identify upscaled images  
✅ **ControlNet tracking** - Find images using specific control methods  
✅ **Wildcard detection** - Track dynamic prompt generation  
✅ **Performance tracking** - Find fastest/slowest generation times  
✅ **Better search UX** - Filter by technical parameters  

### **Database Impact:**
- **Storage**: ~500 bytes per image (minimal)
- **Indexes**: 10 new indexes for fast filtering
- **Search speed**: <10ms for filtered queries
- **Backwards compatible**: NULL for existing images

---

## **Migration Path**

### **Phase 1: Deploy Schema**
```bash
# V4 migration runs automatically
docker compose up -d
# New columns created with NULL defaults
```

### **Phase 2: Import New Images**
```csharp
// Automatically extracts metadata and imports .txt files
// for all newly scanned images
```

### **Phase 3: Backfill Existing Images** (Optional)
```csharp
// Re-scan existing images to extract metadata
await RebuildMetadataAsync();
```

---

## **Next Steps**

1. ✅ **Update schema** (V4 migration)
2. ⬜ **Update FileParameters** with new fields
3. ⬜ **Update Metadata.cs** extraction logic
4. ⬜ **Update ImageEntity** model
5. ⬜ **Add UI filters** for new metadata
6. ⬜ **Test with sample images**
7. ⬜ **Backfill existing images** (optional)
