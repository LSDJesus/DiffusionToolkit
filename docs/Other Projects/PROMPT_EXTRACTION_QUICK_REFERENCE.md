# Luna Batch Prompt Extractor - Quick Reference

## Metadata Format Auto-Detection

The extractor automatically detects and processes **5+ metadata formats** from a single image directory. No configuration needed—it intelligently handles mixed sources!

```
┌─────────────────────────────────────────────────────────────┐
│                    Image Metadata Source                    │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ComfyUI PNG Metadata      ← Native workflow JSON          │
│  ↓                                                          │
│  A1111/Forge Parameters    ← Plain text in PNG             │
│  ↓                                                          │
│  CivitAI Workflow          ← Extra metadata field          │
│  ↓                                                          │
│  EXIF Data (JPEG/WebP)     ← EXIF UserComment + XMP       │
│  ↓                                                          │
│  Generic Metadata          ← Any text metadata field       │
│  ↓                                                          │
│  Fallback: No metadata     → Skip image                    │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Format Priority (Detection Order)

1. **ComfyUI Workflow** (most reliable)
   - Native format from Luna/ComfyUI
   - Stores full workflow + prompt
   - Location: PNG metadata `prompt` field

2. **CivitAI Format** (specialized)
   - ComfyUI workflows shared on CivitAI
   - Contains `extraMetadata` with simplified prompt structure
   - Preserves all conditioning data

3. **A1111/Forge Parameters** (very common)
   - Standard format from Stable Diffusion WebUI
   - Human-readable text format
   - Used by: InvokeAI, Nai, Comfy exports

4. **EXIF Metadata** (diverse tools)
   - JPEG, WebP, TIFF support
   - EXIF UserComment + XMP
   - Used by: Automatic1111, many mobile apps

5. **Generic Fallback** (catch-all)
   - Any text metadata field
   - Attempts basic parsing
   - Last resort option



```
╔══════════════════════════════════════════════════════════╗
║  Luna Batch Prompt Extractor                            ║
╚══════════════════════════════════════════════════════════╝

INPUT PARAMETERS:
─────────────────────────────────────────────────────────

[Required]
• image_directory    : Path to folder with images
• output_file        : Filename for JSON output (e.g., prompts_metadata.json)
• save_to_input_dir  : Save to ComfyUI input folder (True/False)
• output_directory   : Custom output path (if save_to_input_dir=False)
• overwrite          : Overwrite existing JSON file (True/False)
• include_path       : Include full image paths in JSON (True/False)

[Processing Options] ← NEW
• clean_bad_syntax        : Remove malformed punctuation (True/False)
• extract_loras_to_key    : Move <lora:...> to 'loras' key (True/False)
• extract_embeddings_to_key : Move embedding:... to 'embeddings' key (True/False)
• lookup_embedding_metadata : Query database for embedding metadata (True/False)

OUTPUT:
─────────────────────────────────────────────────────────
• status             : Success/error message
• images_scanned     : Number of images processed
• images_extracted   : Number with valid metadata
```

## Metadata Formats by Source

### 1. ComfyUI Workflow Format ⭐
```json
{
  "metadata": {
    "prompt": "{\"0\": {\"inputs\": {...}, \"class_type\": \"CheckpointLoader\"}, ...}",
    "workflow": "{\"0\": {...}, \"1\": {...}}"
  }
}
```
✓ **Reliability**: ★★★★★ (most reliable, all data included)  
✓ **Extraction**: Full workflow JSON parsing → complete conditioning preserved  
✓ **Source**: Native Luna/ComfyUI generations  
✓ **Details Captured**: Model, seed, sampler, CFG, steps, LoRAs, embeddings, workflow

### 2. A1111/Forge Parameter Format
```
masterpiece, best quality, blue eyes
Negative prompt: blurry, low quality, deformed
Steps: 25, Sampler: DPM++ 2M, CFG scale: 7.5, Seed: 12345, Size: 1024x1024
Lora hashes: "style_lora: abc123"
```
✓ **Reliability**: ★★★★☆ (very common, may miss complex nesting)  
✓ **Extraction**: Regex parsing → prompts + sampler parameters  
✓ **Source**: Stable Diffusion WebUI, InvokeAI, many Comfy exports  
✓ **Details Captured**: Prompts, sampler, CFG, seed, dimensions, partial LoRA info

### 3. CivitAI Workflow Format
```json
{
  "workflow": {...},
  "extraMetadata": {
    "prompt": "...",
    "negativePrompt": "...",
    "cfgScale": 7.5,
    "steps": 25
  }
}
```
✓ **Reliability**: ★★★★☆ (good for shared workflows)  
✓ **Extraction**: JSON parsing → both simplified metadata + full workflow  
✓ **Source**: CivitAI shared model workflows  
✓ **Details Captured**: Simplified prompts + original workflow for full detail

### 4. EXIF Format (JPEG/WebP/TIFF)
```
EXIF UserComment: "a beautiful landscape with sunset"
XMP Parameters: structured metadata
```
✓ **Reliability**: ★★★☆☆ (format varies widely by tool)  
✓ **Extraction**: EXIF parser with charset detection (ASCII, UNICODE, UTF-16BE/LE)  
✓ **Source**: Mobile apps, specialty tools, some JPEG workflows  
✓ **Details Captured**: Prompts, some parameters (varies by source)

### 5. Generic/Fallback Format
```
Any text field: Image Description, Comments, Title, etc.
```
✓ **Reliability**: ★★☆☆☆ (unpredictable)  
✓ **Extraction**: Basic text parsing (best effort)  
✓ **Source**: Catchall for unknown formats  
✓ **Details Captured**: Text only, minimal structure

---

## Bad Syntax Examples

```
INPUT  → OUTPUT
────────────────────────────────────────────────
text,, → text,
a, , b → a, b
,,,,   → (removed)
text,  → text
,text  → text
```

## LoRA Extraction Examples

```
INPUT PROMPT:
"a beautiful cat <lora:detail:0.8> with blue eyes <lora:style:0.6:0.7>"

AFTER EXTRACTION (extract_loras_to_key=True):
Prompt: "a beautiful cat with blue eyes"
Loras:  [
  {name: "detail", model_strength: 0.8, clip_strength: 0.8},
  {name: "style", model_strength: 0.6, clip_strength: 0.7}
]

LoRA Weight Formats:
  <lora:name:weight>                    → Both model & clip = weight
  <lora:name:model:clip>                → Separate model and CLIP weights
```

## Attention Weight Clamping

The extractor can automatically normalize attention weights to prevent over-conditioning:

```
INPUT  → CLAMPED (max_weight=1.4)
─────────────────────────────────────
(word:2.0)    → (word:1.4)
[blue:1.8]    → [blue:1.4]
{red:1.5}     → {red:1.4}
hair:2.5      → hair:1.4
(text:1.0)    → (text:1.0)  ← No change if within limit

Format Preservation:
  Bracket types maintained: ( ) [ ] { }
  Word content preserved
  Internal weights normalized
```



## Embedding Extraction Examples

```
INPUT PROMPT:
"a cat embedding:anime_style with embedding:comic_effect"

AFTER EXTRACTION:
Prompt: "a cat with"
Embeddings: ["anime_style", "comic_effect"]
Tags: ["anime", "2d art", "cel shading", "comic style"]  ← From database!
```

## Complete Workflow

```
1. Extract Metadata
   └─ Luna Batch Prompt Extractor
      ├─ Reads 50 images
      ├─ Cleans bad syntax
      ├─ Extracts LoRAs & embeddings
      ├─ Looks up metadata in database
      └─ Saves JSON

2. Load Prompts
   └─ Luna Batch Prompt Loader
      ├─ Reads prompts_metadata.json
      ├─ Navigate with integer slider
      └─ Outputs: prompt, negative, loras, seed, width, height

3. Use in Workflow
   ├─ Positive → CLIP Text Encode
   ├─ Negative → CLIP Text Encode
   ├─ LoRAs → Apply LoRA Stack
   └─ Seed/Width/Height → KSampler
```

## Processing Modes

### Mode 1: Full Processing (Recommended)
```python
clean_bad_syntax=True              ✓ Remove malformed punctuation
extract_loras_to_key=True          ✓ Move LoRAs to key
extract_embeddings_to_key=True     ✓ Move embeddings to key
lookup_embedding_metadata=True     ✓ Enrich with trigger words
```

### Mode 2: Conservative
```python
clean_bad_syntax=False             - Don't modify prompts
extract_loras_to_key=False         - Keep <lora:...> in text
extract_embeddings_to_key=False    - Keep embedding:... in text
lookup_embedding_metadata=False    - No database queries
```

### Mode 3: Selective
```python
clean_bad_syntax=True              ✓ Only clean bad syntax
extract_loras_to_key=False         - Keep LoRAs in text
extract_embeddings_to_key=False    - Keep embeddings in text
lookup_embedding_metadata=False    - No database queries
```

## JSON Output Fields

```json
{
  "image_name": "generation_001.png",
  "image_path": "/full/path/to/generation_001.png",
  "width": 1024,
  "height": 1024,
  
  "positive_prompt": "...",  ← Cleaned (bad syntax fixed, LoRAs/embeddings removed if extraction enabled)
  "negative_prompt": "...",  ← Cleaned
  
  "loras": [                 ← Extracted from <lora:...> tags (if extract_loras_to_key=True)
    {"name": "style_lora", "model_strength": 0.8, "clip_strength": 0.8}
  ],
  
  "embeddings": [            ← Extracted embedding names (if extract_embeddings_to_key=True)
    "anime_style", "comic_effect"
  ],
  
  "embedding_tags": [        ← Trigger words from database lookup (if lookup_embedding_metadata=True)
    "anime", "2d art", "cel shading", "comic style"
  ],
  
  "embedding_metadata": {    ← Full metadata from database (if lookup_embedding_metadata=True)
    "anime_style": {
      "title": "Anime Style",
      "trigger_phrase": "animestyle",
      "base_model": "SDXL 1.0",
      "description": "...",
      "usage_hint": "..."
    }
  },
  
  "extra": {
    "seed": 12345,
    "steps": 25,
    "cfg": 7.5,
    "sampler": "dpmpp_2m",
    "scheduler": "normal",
    "metadata_source": "comfyui_workflow"  ← Which format was detected
  }
}
```

## Troubleshooting Checklist

- [ ] Images are in a valid format (.png, .jpg, .webp, .tiff)
- [ ] **Images contain metadata** (not just raw pixels)
  - ComfyUI: PNG must have "prompt" + "workflow" metadata
  - A1111: PNG must have "parameters" text field
  - EXIF: JPEG/WebP must have EXIF UserComment or XMP tags
- [ ] LoRAs use `<lora:name:weight>` or `<lora:name:model:clip>` format
- [ ] Embeddings use `embedding:name` format
- [ ] For database lookup: Luna Civitai Batch Scraper was run
- [ ] Database file exists: `{ComfyUI}/user/default/ComfyUI-Luna-Collection/metadata.db`
- [ ] All required inputs are filled in
- [ ] Output directory exists and is writable
- [ ] Check console output for metadata source detection (`metadata_source: ...`)

## Performance

| Operation | Time |
|-----------|------|
| 10 images | ~50-100ms |
| 50 images | ~300-500ms |
| 100 images | ~1-2 seconds |
| Database lookup | ~5-10ms per embedding |

## Requirements

- ComfyUI (with Luna Collection installed)
- PIL/Pillow (for image reading)
- SQLite3 (for embedding metadata lookups, optional)
- Luna Civitai Batch Scraper (for embedding metadata, optional)

## Common Settings

### Archive Processing
```
clean_bad_syntax: True
extract_loras_to_key: True
extract_embeddings_to_key: True
lookup_embedding_metadata: True
include_path: True
overwrite: False  ← Don't overwrite existing results
```

### Quick Validation
```
clean_bad_syntax: False
extract_loras_to_key: False
extract_embeddings_to_key: False
lookup_embedding_metadata: False
include_path: False  ← Minimal output
```

### Research/Analysis
```
clean_bad_syntax: True
extract_loras_to_key: True
extract_embeddings_to_key: True
lookup_embedding_metadata: True
include_path: True  ← Full info for analysis
```

## Tips & Tricks

1. **Mixed Format Directories**: Single directory can contain ComfyUI PNGs + A1111 PNGs + EXIF JPEGs!
   - Extractor auto-detects each format
   - All output to same JSON
   - No setup needed

2. **Batch Multiple Folders**: Run extractor on different folders, then load in BatchPromptLoader
   - Each folder → separate JSON
   - Or merge JSON files manually for combined list

3. **Incremental Processing**: Set `overwrite=False` to skip previously processed batches
   - Only new images extracted
   - Previous results preserved

4. **Prompt Augmentation**: Database lookup automatically enriches prompts with trigger words
   - Original prompt + trigger words → more semantic data
   - Better for search/analysis later

5. **LoRA Analysis**: View `loras` key to find most-used models in batch
   - Identify signature techniques
   - Track style evolution

6. **Style Classification**: Use `embedding_tags` to categorize images by style
   - Filter by metadata → tag-based collections
   - Build semantic libraries

7. **Attention Weight Safety**: Enable `clean_bad_syntax` to normalize over-conditioned prompts
   - Prevents weight values >1.4
   - Maintains bracket structure
   - Improves consistency

---

For detailed documentation, see: `PROMPT_PROCESSING_STUDIO.md`
