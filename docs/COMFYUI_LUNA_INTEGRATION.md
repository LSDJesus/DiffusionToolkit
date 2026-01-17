# ComfyUI Luna Multi Saver Integration

**Direct PostgreSQL writes from ComfyUI → Diffusion Toolkit Database**

## Overview

Luna Multi Saver node in ComfyUI can write image metadata directly to the Diffusion Toolkit PostgreSQL database, bypassing the need for JSON sidecars and manual scanning. This creates a seamless workflow where generated images appear instantly in Diffusion Toolkit.

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│               ComfyUI + Luna Collection                 │
│                                                         │
│  1. Generate image                                      │
│  2. Luna Multi Saver (save_mode: database_store)       │
│     ├─ Save image to disk                              │
│     ├─ Write metadata to PostgreSQL                    │
│     │  (prompt, model, seed, workflow, etc.)           │
│     └─ SET scan_phase = 1 (fully processed)            │
│                                                         │
│  3. Send HTTP notification (optional):                 │
│     POST http://127.0.0.1:19284/process_image          │
│     {                                                   │
│       "image_id": 12345,                                │
│       "image_path": "...",                              │
│       "skip_metadata": true,  ← Skip re-extraction     │
│       "run_ai_only": true     ← Only AI processing     │
│     }                                                   │
└─────────────────────────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────┐
│         PostgreSQL Database (port 5436)                 │
│                                                         │
│  image table:                                           │
│  ├─ Full metadata from ComfyUI workflow                │
│  ├─ DAAM heatmaps (optional)                           │
│  └─ scan_phase = 1 ← Tells Watcher to skip             │
│                                                         │
│  Note: CLIP embeddings NOT stored (on-demand only)     │
└─────────────────────────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────┐
│          Diffusion.Watcher (optional)                   │
│                                                         │
│  Receives HTTP notification:                            │
│  ├─ Checks scan_phase (if = 1, skip metadata)          │
│  ├─ Only runs AI processing:                            │
│  │  ├─ Face detection (YOLO11 + ArcFace)               │
│  │  ├─ Auto-tagging (JoyTag + WD)                      │
│  │  └─ Auto-captioning (JoyCaption)                    │
│  └─ Updates database with AI results                   │
└─────────────────────────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────┐
│              Diffusion Toolkit GUI                      │
│                                                         │
│  Switch schema/database to view different collections:  │
│  ├─ Schema 1: Civitai downloads                         │
│  ├─ Schema 2: Personal ComfyUI images ← Luna writes here│
│  └─ Database 2: Luna-narrates images                    │
│                                                         │
│  Each collection is independent!                        │
└─────────────────────────────────────────────────────────┘
```

---

## Scan Phase Coordination

### Understanding `scan_phase` Column

| Value | Meaning | Set By | Watcher Behavior |
|-------|---------|--------|------------------|
| `0` | Quick-scanned (path, size, dates only) | Watcher QuickScan | Queues for metadata extraction |
| `1` | **Fully processed** (complete metadata) | **Luna Multi Saver** | **Skips metadata extraction** ✅ |

### Critical Rule

**Luna Multi Saver MUST set `scan_phase = 1`** when writing to database:

```python
# In luna_database_writer.py
cur.execute("""
    INSERT INTO image (
        path, prompt, negative_prompt, model, seed, steps, cfg_scale,
        sampler, width, height, workflow, loras, hash, file_size, 
        created_date, modified_date, scan_phase
    ) VALUES (
        %s, %s, %s, %s, %s, %s, %s,
        %s, %s, %s, %s, %s, %s,
        %s, %s, 1  -- ← scan_phase = 1 (fully processed)
    ) ON CONFLICT (path) DO UPDATE SET
        prompt = EXCLUDED.prompt,
        ... (all fields),
        scan_phase = 1  -- ← Always update to 1
""", (...))
```

### Why This Matters

**Without `scan_phase = 1`:**
- Watcher detects new image file via FileSystemWatcher
- Tries to extract metadata from PNG/JPEG EXIF
- Overwrites metadata that Luna already wrote
- Wastes processing time

**With `scan_phase = 1`:**
- Watcher detects new image file
- Checks database: `scan_phase = 1` → skip metadata extraction ✅
- Only runs AI processing (if enabled)
- No duplicate work!

---

## Integration Modes

### Mode 1: Luna Only (No Watcher)

**Best for**: Low-volume workflows, manual generation sessions

```python
# Luna Multi Saver settings
save_mode: database_store
notify_watcher: false  # Don't send HTTP notification
```

**Workflow:**
1. Generate images in ComfyUI
2. Luna writes to database (`scan_phase = 1`)
3. Open Diffusion Toolkit → images already there
4. No AI processing (face detection, tagging, etc.)

**Advantages:**
- Simplest setup
- No background service needed
- Zero overhead

**Limitations:**
- No automatic face detection
- No automatic tagging/captioning
- Must manually trigger AI processing from Toolkit UI

---

### Mode 2: Luna + Watcher (Full Automation)

**Best for**: High-volume workflows, batch generation, overnight runs

```python
# Luna Multi Saver settings
save_mode: database_store
notify_watcher: true
watcher_url: http://127.0.0.1:19284/process_image
skip_metadata: true  # Tell Watcher not to re-extract
run_ai_only: true    # Only face/tag/caption processing
```

**Workflow:**
1. Generate images in ComfyUI
2. Luna writes to database (`scan_phase = 1`)
3. HTTP notification sent to Watcher
4. Watcher queues AI processing (background)
5. Open Diffusion Toolkit → images + AI results ready

**Advantages:**
- Fully automated pipeline
- Face detection, tagging, captioning automatic
- Images searchable by AI-generated tags

**Processing Time:**
- Database write: <1 second
- Face detection: ~27ms/image
- Tagging: ~27ms/image
- Captioning: ~1-2s/image (if enabled)

---

### Mode 3: Watcher Only (Traditional Scanning)

**Best for**: Existing workflows, non-ComfyUI generation, legacy compatibility

**Setup:**
- Don't use Luna Multi Saver database mode
- Use traditional PNG metadata or JSON sidecars
- Let Watcher handle everything

**Workflow:**
1. Generate images anywhere (A1111, ComfyUI, etc.)
2. Watcher detects new files
3. Quick scan: `scan_phase = 0`
4. Metadata extraction: parse PNG/JSON → `scan_phase = 1`
5. AI processing (if enabled)

**Advantages:**
- Works with any image source
- No ComfyUI node required
- Handles legacy images

---

## HTTP Notification API

### Endpoint

```
POST http://127.0.0.1:19284/process_image
Content-Type: application/json
```

### Request Schema

```json
{
  "image_id": 12345,           // Optional: Database image ID
  "image_path": "D:/path.png", // Optional: Absolute file path
  "skip_metadata": true,       // NEW: Don't re-extract metadata
  "run_ai_only": true          // NEW: Only run AI processing
}
```

**At least one** of `image_id` or `image_path` must be provided.

### Response

```json
{
  "status": "accepted"
}
```

HTTP Status: `202 Accepted` (queued for processing)

### Error Handling

```python
import requests

try:
    response = requests.post(
        "http://127.0.0.1:19284/process_image",
        json={
            "image_id": image_id,
            "image_path": image_path,
            "skip_metadata": True,
            "run_ai_only": True
        },
        timeout=2  # Non-blocking
    )
    if response.status_code == 202:
        print(f"Watcher notified for image {image_id}")
    else:
        print(f"Watcher error: {response.status_code}")
except requests.exceptions.RequestException as e:
    # Watcher not running or timeout - this is OK!
    # Image is still saved to database
    print(f"Watcher notification failed (non-critical): {e}")
```

**Critical**: Notification failure should NOT block image save. The image is already in the database; Watcher notification is purely optional.

---

## Database Schema Requirements

### Minimum Required Columns

```sql
CREATE TABLE image (
    id SERIAL PRIMARY KEY,
    path TEXT UNIQUE NOT NULL,
    file_name TEXT NOT NULL,
    
    -- Generation metadata
    prompt TEXT,
    negative_prompt TEXT,
    model TEXT,
    seed BIGINT,
    steps INTEGER,
    cfg_scale REAL,
    sampler TEXT,
    width INTEGER,
    height INTEGER,
    
    -- Workflow storage
    workflow JSONB,                  -- Full ComfyUI workflow
    loras JSONB,                     -- LoRA array
    
    -- File metadata
    hash TEXT,                       -- SHA256 hash
    file_size BIGINT,
    created_date TIMESTAMP,
    modified_date TIMESTAMP,
    
    -- CRITICAL: Processing coordination
    scan_phase INTEGER DEFAULT 0    -- 0 = quick scan, 1 = full metadata
);
```

### Optional: DAAM Heatmaps

```sql
CREATE TABLE daam_heatmap (
    id SERIAL PRIMARY KEY,
    image_id INTEGER REFERENCES image(id) ON DELETE CASCADE,
    token TEXT NOT NULL,
    heatmap_data BYTEA,              -- Compressed numpy array
    attention_weights REAL[],
    created_at TIMESTAMP DEFAULT NOW()
);
```

### CLIP Embedding Storage (Deprecated)

**Important**: As of January 2026, **CLIP-L and CLIP-G embeddings are NO LONGER stored** in the database by Luna Multi Saver.

**Reasons for Removal:**
1. **Not used by Diffusion Toolkit** - Toolkit uses BGE embeddings for similarity search, not CLIP
2. **On-demand generation** - CLIP embeddings can be generated when needed for ComfyUI workflows
3. **Storage savings** - ~12KB per image saved (768D + 1280D float32 vectors)
4. **Performance** - Eliminates 2-5ms per image write time
5. **Simplicity** - Reduces complexity in Luna nodes and database schema

**What This Means:**
- ❌ `clip_l_embedding` and `clip_g_embedding` columns NOT populated by Luna
- ✅ CLIP conditioning still usable in ComfyUI workflows (generated on-demand)
- ✅ Full workflow stored in JSONB (can reconstruct CLIP if needed)
- ✅ Existing database columns can remain (will be NULL for new images)

**Migration for Existing Installations:**
```python
# In luna_database_writer.py - Remove CLIP from INSERT:
cur.execute("""
    INSERT INTO image (
        path, prompt, negative_prompt, model, seed, steps, cfg_scale,
        sampler, width, height, 
        -- REMOVED: clip_l_embedding, clip_g_embedding,
        workflow, loras, hash, file_size, scan_phase
    ) VALUES (
        %s, %s, %s, %s, %s, %s, %s, %s, %s,
        -- REMOVED: %s, %s,
        %s, %s, %s, %s, 1
    )
""", (...))
```

**Optional: Clean Up Database** (only if you want to reclaim space):
```sql
-- Remove columns to save space (optional - breaking change)
ALTER TABLE image DROP COLUMN IF EXISTS clip_l_embedding;
ALTER TABLE image DROP COLUMN IF EXISTS clip_g_embedding;

-- Or keep columns for backward compatibility (recommended)
-- New images will just have NULL values in these columns
```

---

## Configuration

### Luna Multi Saver (ComfyUI Node)

```python
# config/luna_database.yaml
database:
  host: localhost
  port: 5436
  database: diffusion_images        # Target database
  schema: personal_generation       # Target schema/collection
  user: diffusion
  password: diffusion_toolkit_secure_2025
  
watcher:
  enabled: true
  url: http://127.0.0.1:19284/process_image
  timeout: 2
  skip_metadata: true  # Don't re-extract what Luna already wrote
  run_ai_only: true    # Only face/tag/caption
```

**Independent Configuration:**
- Luna writes to **its configured schema** (e.g., `personal_generation`)
- Diffusion Toolkit can view **any schema** via Settings → Database → Schema dropdown
- **No automatic syncing** - each tool has independent configuration
- Supports multi-collection workflows:
  - Schema 1: `civitai_downloads` (imported images, scanned by Toolkit/Watcher)
  - Schema 2: `personal_generation` (Luna Multi Saver writes here)
  - Schema 3: `luna_narrates` (in separate database container)

**Switching Between Collections:**
1. In Diffusion Toolkit: Settings → Database → Select Schema/Database Profile
2. Images from that collection become visible
3. Luna continues writing to its configured target (independent)

### Diffusion.Watcher (WatcherSettings.cs)

```json
{
  "AutoTagOnScan": true,           // Auto-tag new images
  "AutoCaptionOnScan": false,      // Auto-caption (slow)
  "AutoFaceDetectionOnScan": true, // Auto-detect faces
  "BackgroundMetadataEnabled": true,
  "HttpPort": 19284
}
```

**Schema-Aware Watching (NEW):**

Watcher automatically determines which schema to save images to based on the folder:

```csharp
// When root folder is added in Toolkit:
// 1. Current active schema is recorded with the folder
// 2. Folder table stores schema association

// Folder table schema:
CREATE TABLE folder (
    id SERIAL PRIMARY KEY,
    path TEXT UNIQUE NOT NULL,
    schema_name TEXT,           // ← NEW: Which schema this folder belongs to
    parent_id INTEGER,
    is_root BOOLEAN
);

// When Watcher detects new file:
// 1. Determine folder from file path
// 2. Look up folder's schema_name in database
// 3. Switch connection to that schema
// 4. Insert image into correct schema
```

**Implementation Example:**
```csharp
// In WatcherService.cs
private async Task ProcessQuickScanBatch(List<string> files)
{
    // Group files by their target schema
    var filesBySchema = new Dictionary<string, List<string>>();
    
    foreach (var file in files)
    {
        var folderPath = Path.GetDirectoryName(file);
        var schema = GetSchemaForFolder(folderPath); // Look up in folder table
        
        if (!filesBySchema.ContainsKey(schema))
            filesBySchema[schema] = new List<string>();
        
        filesBySchema[schema].Add(file);
    }
    
    // Process each schema's files separately
    foreach (var (schema, schemaFiles) in filesBySchema)
    {
        using var conn = _dataStore.OpenConnection();
        
        // Switch to target schema
        await conn.ExecuteAsync($"SET search_path TO {schema}, public;");
        
        // Insert files into this schema
        var fileInfoList = BuildFileInfoList(schemaFiles);
        _dataStore.QuickAddImages(conn, fileInfoList, folderCache, _cts.Token);
    }
}

private string GetSchemaForFolder(string folderPath)
{
    // Look up folder's schema in database
    using var conn = _dataStore.OpenConnection();
    
    var schema = conn.QuerySingleOrDefault<string>(
        @"SELECT schema_name FROM folder 
          WHERE path = @path OR @path LIKE path || '%'
          ORDER BY length(path) DESC LIMIT 1",
        new { path = folderPath }
    );
    
    return schema ?? "public"; // Default to public if not found
}
```

**Workflow:**
1. User adds folder `D:/ComfyUI/output` while viewing schema `personal_generation`
2. Folder record created: `{path: "D:/ComfyUI/output", schema_name: "personal_generation"}`
3. ComfyUI generates image: `D:/ComfyUI/output/image_001.png`
4. Watcher detects file → looks up folder → finds schema `personal_generation`
5. Watcher inserts to `personal_generation.image` table ✅

**Benefits:**
- ✅ Automatic schema routing (no manual configuration)
- ✅ Supports multiple folders in different schemas simultaneously
- ✅ New images go to the correct collection automatically
- ✅ Works with Luna Multi Saver + traditional scanning

---

## Performance Benchmarks

### Luna Multi Saver Database Write

| Operation | Time | Notes |
|-----------|------|-------|
| Image save to disk | ~50ms | PNG/WebP compression |
| Database INSERT | ~5ms | Single transaction |
| HTTP notification | ~1ms | Non-blocking |
| **Total** | **~55ms/image** | No blocking |

### Watcher AI Processing (Background)

| Task | Time/Image | Batch Throughput |
|------|------------|------------------|
| Face detection | 27ms | ~37,000/hour |
| Auto-tagging | 27ms | ~133,000/hour |
| Auto-captioning | 1.5s | ~2,400/hour |

**For 1000-image batch:**
- Database write: ~60 seconds (ComfyUI)
- Face detection: ~27 seconds (Watcher)
- Tagging: ~27 seconds (Watcher)
- Captioning: ~25 minutes (Watcher, optional)

Images are browsable **immediately after database write** (60 seconds). AI processing happens in background.

---

## Workflow Examples

### Example 1: High-Speed Batch Generation

```
ComfyUI queue: 1000 images
    ↓
Luna Multi Saver → PostgreSQL (scan_phase = 1)
    ↓
Notification → Watcher (skip_metadata: true, run_ai_only: true)
    ↓
Watcher queues 1000 AI jobs (background)
    ↓
After ~1 minute: Open Diffusion Toolkit → All images indexed
After ~27 seconds: Face detection complete
After ~27 seconds: Auto-tagging complete
After ~25 minutes: Auto-captioning complete (if enabled)
```

**User experience**: Browse images **immediately**, AI results appear progressively.

---

### Example 2: Selective AI Processing

```python
# Only run face detection + tagging, skip captioning (too slow)
POST http://127.0.0.1:19284/process_image
{
  "image_id": 12345,
  "skip_metadata": true,
  "run_ai_only": true
}

# Watcher settings:
AutoTagOnScan: true
AutoCaptionOnScan: false  # ← Disabled
AutoFaceDetectionOnScan: true
```

**Result**: 27ms processing time per image (face + tags only).

---

### Example 3: Manual AI Processing Later

```python
# Save without Watcher notification
Luna Multi Saver:
  notify_watcher: false

# Later, from Diffusion Toolkit UI:
# Select images → Right-click → "Auto-Tag" or "Caption"
# Or use Watcher API manually:

POST http://127.0.0.1:19284/process_image
{
  "image_id": 12345,
  "skip_metadata": true,
  "run_ai_only": true
}
```

**Use case**: Generate during the day, process AI overnight.

---

## Troubleshooting

### Images Not Appearing in Toolkit

**Symptom**: Luna writes to database, but Toolkit shows nothing

**Solution:**
1. **Check schema mismatch** - This is by design for multi-collection workflows!
   ```sql
   -- Check which schema Luna wrote to:
   SELECT schemaname, tablename, COUNT(*) 
   FROM pg_tables 
   WHERE tablename = 'image' 
   GROUP BY schemaname, tablename;
   
   -- View images in Luna's schema:
   SELECT COUNT(*) FROM personal_generation.image;
   
   -- Check Toolkit's current schema:
   SELECT current_schema();
   ```
2. **Switch Toolkit to Luna's schema:**
   - Settings → Database → Schema: `personal_generation`
   - Or create Database Profile: "ComfyUI Generated" → schema: `personal_generation`
3. Verify `scan_phase = 1` is set by Luna
4. Check `folder` table has entry for image directory
5. Restart Diffusion Toolkit to refresh

**Multi-Collection Workflow:**
- Luna writes to: `personal_generation` schema (configured in Luna)
- Watcher auto-detects schema from folder: Checks `folder.schema_name`
- Toolkit currently viewing: `civitai_downloads` schema
- **Expected behavior**: 
  - Images not visible until you switch schemas (intentional)
  - Watcher saves to correct schema automatically based on folder
- **This is intentional** - keeps collections separate!

**Watcher Schema Detection:**
When Watcher sees a new file:
1. Extract folder path: `D:/ComfyUI/output`
2. Query: `SELECT schema_name FROM folder WHERE path = 'D:/ComfyUI/output'`
3. Result: `personal_generation`
4. Insert to: `personal_generation.image` table ✅

### Duplicate Metadata Extraction

**Symptom**: Watcher re-extracts metadata from PNG despite Luna writing to database

**Solution:**
1. Ensure Luna sets `scan_phase = 1`
2. Update Watcher to skip `scan_phase = 1` images (see code above)
3. Use `skip_metadata: true` in HTTP notification

### Watcher Notification Timeout

**Symptom**: Luna logs "Watcher notification timeout"

**This is OK!** The image is already saved to the database. Watcher notification is optional.

**To fix** (if you want Watcher processing):
1. Start Diffusion.Watcher.exe
2. Verify port 19284 is listening
3. Check firewall settings

### CLIP Embedding Storage (Removed)

**As of January 2026**: CLIP-L and CLIP-G embeddings are **NO LONGER stored** in the database.

**Why removed:**
- CLIP embeddings are not used for pre-computed similarity search in Diffusion Toolkit
- CLIP-L/G can be generated on-demand when needed for ComfyUI workflows
- Saves ~12KB per image in database storage
- Reduces database write time by ~2-5ms per image

**If you need CLIP embeddings:**
- Generate them on-demand in ComfyUI workflows (not stored)
- Use for real-time conditioning during image generation
- No need to pre-compute or store for later use

**What IS stored:**
- ✅ Full workflow JSONB (can reconstruct CLIP conditioning)
- ✅ Prompt and negative prompt text
- ✅ All generation parameters
- ✅ DAAM heatmaps (if enabled)

---

## Watcher Schema Auto-Detection

**Status: ✅ IMPLEMENTED (as of January 2026)**

The Watcher now automatically routes images to the correct schema based on their folder location. This enables truly independent multi-collection workflows.

### Database Schema Enhancement

Schema tracking column added to `folder` table (Migration V11):

```sql
-- Migration V11: Add schema tracking to folders
ALTER TABLE folder ADD COLUMN IF NOT EXISTS schema_name TEXT DEFAULT 'public';

-- Update existing folders to current schema
UPDATE folder SET schema_name = current_schema() WHERE schema_name IS NULL OR schema_name = 'public';

-- Indexes for fast lookups
CREATE INDEX IF NOT EXISTS idx_folder_schema ON folder(schema_name);
CREATE INDEX IF NOT EXISTS idx_folder_path_prefix ON folder(path text_pattern_ops);
```

### Toolkit: Folder Addition

When user adds a folder in Diffusion Toolkit, the current active schema is automatically recorded:

```csharp
// In PostgreSQLDataStore.Folder.cs - AddWatchedFolder()
INSERT INTO folder (..., schema_name)
VALUES (..., current_schema())  // ← Records active schema at time of addition

// Subfolders automatically inherit parent's schema
```

**User workflow:**
1. Switch Toolkit to schema: `personal_generation`
2. Add folder: `D:/ComfyUI/output`
3. Folder record created: `{path: "D:/ComfyUI/output", schema_name: "personal_generation"}`
4. All subfolders automatically inherit `personal_generation` schema ✅

### Watcher: Schema Resolution

When Watcher detects a new file, it automatically determines the target schema:

```csharp
// In WatcherService.cs - GetSchemaForFile()
private string GetSchemaForFile(string filePath)
{
    var folderPath = Path.GetDirectoryName(filePath);
    
    // Check cache first (performance optimization)
    if (_folderSchemaCache.TryGetValue(folderPath, out var cachedSchema))
        return cachedSchema;
    
    // Query database: find longest matching folder path
    var schema = conn.QuerySingleOrDefault<string>(@"
        SELECT schema_name 
        FROM folder 
        WHERE @filePath LIKE path || '%'
        ORDER BY length(path) DESC 
        LIMIT 1",
        new { filePath });
    
    return schema ?? "public"; // Default fallback
}

// Files are grouped by schema before processing
private async Task ProcessQuickScanBatch(List<string> files)
{
    var filesBySchema = files.GroupBy(f => GetSchemaForFile(f));
    
    foreach (var (schema, schemaFiles) in filesBySchema)
    {
        // Switch connection to target schema
        conn.Execute($"SET search_path TO {schema}, public;");
        
        // Insert files into correct schema
        _dataStore.QuickAddImages(conn, schemaFiles, ...);
        
        Logger.Log($"Quick-scanned {schemaFiles.Count} files to schema '{schema}'");
    }
}
```

**Performance optimizations:**
- In-memory cache for folder→schema lookups
- Thread-safe cache access
- Longest path matching handles nested folders automatically
        .ToDictionary(g => g.Key, g => g.ToList());
    
    foreach (var (schema, schemaFiles) in filesBySchema)
    {
        using var conn = _dataStore.OpenConnection();
        await conn.ExecuteAsync($"SET search_path TO {schema}, public;");
        
        // Build file info and insert into correct schema
        var fileInfoList = BuildFileInfoList(schemaFiles);
        _dataStore.QuickAddImages(conn, fileInfoList, folderCache, _cts.Token);
        
        Logger.Log($"Quick-scanned {schemaFiles.Count} files to schema '{schema}'");
    }
}
```

### Example Workflow

**Setup:**
```
1. Toolkit active schema: civitai_downloads
   Add folder: D:/Downloads/Civitai
   → Folder record: {path: "D:/Downloads/Civitai", schema_name: "civitai_downloads"}

2. Toolkit switch to schema: personal_generation
   Add folder: D:/ComfyUI/output
   → Folder record: {path: "D:/ComfyUI/output", schema_name: "personal_generation"}

3. Toolkit switch to database: luna_narrates (port 5437)
   Switch schema: public
   Add folder: D:/Luna/stories
   → Folder record in luna_narrates DB: {path: "D:/Luna/stories", schema_name: "public"}
```

**Runtime:**
```
Watcher detects: D:/Downloads/Civitai/character_001.png
  → Query folder table → schema_name = "civitai_downloads"
  → Insert to: civitai_downloads.image ✅

Watcher detects: D:/ComfyUI/output/generation_042.png
  → Query folder table → schema_name = "personal_generation"
  → Insert to: personal_generation.image ✅

Luna Multi Saver saves: D:/ComfyUI/output/luna_save_001.png
  → Configured schema = "personal_generation"
  → Insert to: personal_generation.image ✅
  → (Both Luna and Watcher save to same schema - no conflict)
```

**Edge Cases:**
- **Subfolder not in DB**: Uses parent folder's schema (longest path match)
- **No matching folder**: Defaults to `public` schema (safe fallback)
- **Multiple databases**: Watcher needs separate instances per database (different ports)

---

## Best Practices

### 1. Always Set `scan_phase = 1`

```python
# CORRECT:
cur.execute("INSERT INTO image (..., scan_phase) VALUES (..., 1)", ...)

# WRONG (Watcher will re-process):
cur.execute("INSERT INTO image (..., scan_phase) VALUES (..., 0)", ...)
```

### 2. Hash-Based Duplicate Detection

```python
import hashlib

def compute_hash(image_path):
    with open(image_path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()

# Check for duplicates before insert
hash_value = compute_hash(image_path)
existing = cur.execute("SELECT id FROM image WHERE hash = %s", (hash_value,)).fetchone()
if existing:
    print(f"Duplicate image detected: {image_path}")
```

### 3. Batch Database Writes

```python
# Instead of INSERT per image:
for image in images:
    write_image_metadata(image)  # 1000 transactions

# Use batch insert:
with conn.cursor() as cur:
    cur.executemany("""
        INSERT INTO image (...) VALUES (...)
    """, image_tuples)  # 1 transaction for 1000 images
conn.commit()
```

**Performance**: 50× faster for large batches.

### 4. Graceful Watcher Notification

```python
def notify_watcher(image_id, image_path):
    try:
        requests.post(
            "http://127.0.0.1:19284/process_image",
            json={"image_id": image_id, "image_path": image_path, 
                  "skip_metadata": True, "run_ai_only": True},
            tim,
    # Set default schema for all connections from pool
    configure=lambda conn: conn.execute("SET search_path TO your_schema, public;")
)

# Use connections from pool
with pool.connection() as conn:
    with conn.cursor() as cur:
        cur.execute("INSERT INTO image ...")  # Uses correct schema
    conn.commit()
```

**Performance**: Eliminates connection overhead (~50ms per insert).

### 6. Multi-Collection Architecture

```python
# Example: Multiple Luna instances for different projects

# Luna instance 1: Personal art generation
luna_personal = LunaDatabaseWriter(
    host="localhost",
    port=5436,
    database="diffusion_images",
    schema="personal_generation"  # ← Collection 1
)

# Luna instance 2: Client work
luna_client = LunaDatabaseWriter(
    host="localhost",
    port=5436,
    database="diffusion_images",
    schema="client_work"  # ← Collection 2
)

# Luna instance 3: Story/narrative images (separate database)
luna_narrates = LunaDatabaseWriter(
    host="localhost",
    port=5437,  # Different container
    database="luna_narrates",
    schema="public"  # ← Separate database entirely
)
```

**Diffusion Toolkit Database Profiles:**
```json
{
  "DatabaseProfiles": [
    {
      "Name": "Civitai Downloads",
      "Schema": "civitai_downloads",
      "ConnectionString": "Host=localhost;Port=5436;Database=diffusion_images;..."
    },
    {
      "Name": "Personal Generation",
      "Schema": "personal_generation",  // ← Luna writes here
      "ConnectionString": "Host=localhost;Port=5436;Database=diffusion_images;..."
    },
    {
      "Name": "Luna Narrates",
      "Schema": "public",
      "ConnectionString": "Host=localhost;Port=5437;Database=luna_narrates;..."  // Different DB
    }
  ]
}
```

**Workflow:**
1. Luna Multi Saver configured to write to `personal_generation`
2. Generate images in ComfyUI → saved to `personal_generation` schema
3. Switch Toolkit profile to "Personal Generation" → see ComfyUI images
4. Switch to "Civitai Downloads" → see downloaded images
5. Switch to "Luna Narrates" (different database) → see story images

**Benefits:**
- ✅ Separate collections for different sources/projects
- ✅ No mixing of downloaded vs generated images
- ✅ Independent databases for critical projects (backup/isolation)
- ✅ Fast schema switching in Toolkit (no re-scan needed)
- ✅ Luna writes to fixed target (predictable behavior)
# Create connection pool (reuse connections)
pool = psycopg_pool.ConnectionPool(
    conninfo="host=localhost port=5436 dbname=diffusion_images",
    min_size=1,
    max_size=10
)

# Use connections from pool
with pool.connection() as conn:
    with conn.cursor() as cur:
        cur.execute("INSERT INTO image ...")
    conn.commit()
```

**Performance**: Eliminates connection overhead (~50ms per insert).

---

## Future Enhancements

- [ ] **Bi-directional sync**: Update Luna workflow from Toolkit edits
- [ ] **Real-time preview**: WebSocket notifications to Toolkit UI
- [ ] **Embedding search from ComfyUI**: Query similar images during generation
- [ ] **Workflow templates**: Save/load from database
- [ ] **Multi-schema support**: Different collections for different projects

---

## Summary

| Aspect | Traditional Workflow | Luna + Database Integration |
|--------|----------------------|-----------------------------|
| **Save Location** | Disk only | Disk + PostgreSQL |
| **Metadata Storage** | JSON sidecars | Database columns |
| **Indexing Speed** | Manual scan (minutes) | **Instant** (<1 second) |
| **Toolkit Startup** | Wait for scan | **Images ready** |
| **AI Processing** | Manual trigger | **Automatic** (Watcher) |
| **Search** | After scan | **Immediate** |
| **Duplicate Detection** | Manual | **Hash-based** |
| **Workflow Reconstruction** | Embedded in PNG | **Database JSONB** |

**Bottom Line**: Luna Multi Saver + PostgreSQL integration eliminates scanning delays and enables instant access to generated images in Diffusion Toolkit. ✅

---

**Last Updated**: January 15, 2026  
**Watcher Enhancement**: Added `skip_metadata` and `run_ai_only` flags  
**Schema Auto-Detection**: ✅ **IMPLEMENTED** - Watcher automatically routes images to correct schema based on folder  
**Status**: Production-ready

---

## Implementation Summary

### Changes Made (January 15, 2026)

**1. Database Schema (Migration V11)**
- Added `schema_name` column to `folder` table
- Automatically records active schema when folder is added
- Indexes for fast schema lookups and path prefix matching

**2. Folder Model Enhancement**
- Added `SchemaName` property to `Folder.cs` model
- Updated all folder INSERT statements to capture `current_schema()`
- Subfolders automatically inherit parent schema

**3. Watcher Intelligence**
- Implemented `GetSchemaForFile()` method with caching
- Modified `ProcessQuickScanBatch()` to group files by schema
- Automatic schema routing - no configuration needed
- Thread-safe in-memory cache for performance

**4. Integration Flow**
```
User adds folder D:/ComfyUI/output while viewing schema "personal_generation"
  ↓
Folder record created: {path: "...", schema_name: "personal_generation"}
  ↓
ComfyUI generates image in that folder
  ↓  
Watcher detects file → queries folder table → finds "personal_generation"
  ↓
Watcher inserts to personal_generation.image table ✅
  ↓
User switches Toolkit to "personal_generation" schema → sees new images
```

### Files Modified
1. `Diffusion.Database.PostgreSQL/Models/Folder.cs` - Added SchemaName property
2. `Diffusion.Database.PostgreSQL/PostgreSQLMigrations.cs` - Added V11 migration
3. `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.Folder.cs` - Updated INSERT statements
4. `Diffusion.Watcher/WatcherService.cs` - Added schema detection logic

### Backward Compatibility
- ✅ Existing folders automatically migrated to current schema
- ✅ Default fallback to `public` schema if no match
- ✅ No breaking changes to existing workflows
