# Textual Embedding Model Type Detection

## How to Identify Which Model Was Used

### **Priority Order (Most Reliable → Least Reliable)**

1. ✅ **CivitAI .metadata.json file** (`baseModel` field)
2. ✅ **Safetensors metadata header** (`sd_version`, `base_model`)
3. ✅ **Directory structure** (folder names)
4. ✅ **Tensor structure analysis** (key names, dimensions)
5. ⚠️ **Filename patterns** (less reliable but useful)

---

## **Method 1: CivitAI Metadata File** ⭐ Most Reliable

Every embedding downloaded from CivitAI includes a `.metadata.json` file:

```bash
DV_Alina_Vicious.safetensors
DV_Alina_Vicious.metadata.json  ← This file!
```

**Content Example:**
```json
{
  "modelId": 123456,
  "modelName": "DV_Alina_Vicious",
  "baseModel": "SDXL 1.0",           ← KEY FIELD
  "trainedWords": ["DV_Alina_Vicious"],
  "description": "Character embedding...",
  "creator": {
    "username": "DeviantVixens"
  }
}
```

### **Common `baseModel` Values:**
- `"SD 1.5"` → SD 1.5
- `"SDXL 1.0"` → SDXL
- `"Pony"` or `"Pony XL"` → Pony Diffusion XL
- `"Illustrious XL"` → Illustrious
- `"SD 3"` or `"SD 3.5"` → Stable Diffusion 3.x
- `"FLUX.1"` → FLUX

**Code to Read:**
```csharp
var metadataPath = embeddingPath.Replace(".safetensors", ".metadata.json");
if (File.Exists(metadataPath))
{
    var json = await File.ReadAllTextAsync(metadataPath);
    var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    var baseModel = metadata["baseModel"].GetString();
    // baseModel = "SDXL 1.0"
}
```

---

## **Method 2: Safetensors Header Metadata** ⭐ Built-in

Safetensors files have a JSON header that may contain metadata:

### **File Structure:**
```
[8 bytes: header length]
[N bytes: JSON header]
[remaining: tensor data]
```

### **Header Example:**
```json
{
  "__metadata__": {
    "sd_version": "sdxl",
    "base_model": "SDXL 1.0",
    "format": "pt"
  },
  "clip_l": {
    "dtype": "F32",
    "shape": [3, 768],
    "data_offsets": [0, 9216]
  },
  "clip_g": {
    "dtype": "F32",
    "shape": [3, 1280],
    "data_offsets": [9216, 24576]
  }
}
```

**Code to Read:**
```csharp
// Read first 8 bytes (header length)
var headerLength = BitConverter.ToInt64(fileBytes, 0);

// Read JSON header
var headerBytes = new byte[headerLength];
Array.Copy(fileBytes, 8, headerBytes, 0, headerLength);
var headerJson = Encoding.UTF8.GetString(headerBytes);

var header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(headerJson);

// Check metadata
if (header.ContainsKey("__metadata__"))
{
    var metadata = header["__metadata__"];
    var sdVersion = metadata.GetProperty("sd_version").GetString();
    // sdVersion = "sdxl", "sd15", "pony", etc.
}
```

---

## **Method 3: Tensor Structure Analysis** ⭐ Fallback

Different models have different tensor structures:

### **SD 1.5**
```json
{
  "string_to_param.*": {
    "shape": [N, 768],
    "dtype": "F32"
  }
}
```
- **Single tensor** with key like `"string_to_param.*"`
- **768 dimensions** (CLIP-L only)
- **No separate CLIP-L/G keys**

### **SDXL / Pony / Illustrious**
```json
{
  "clip_l": {
    "shape": [N, 768],
    "dtype": "F32"
  },
  "clip_g": {
    "shape": [N, 1280],
    "dtype": "F32"
  }
}
```
- **Two tensors**: `clip_l` and `clip_g`
- **CLIP-L**: 768 dimensions
- **CLIP-G**: 1280 dimensions
- **Note**: Cannot distinguish SDXL from Pony or Illustrious by structure alone!

### **SD 3.x**
```json
{
  "clip_l": {"shape": [N, 768]},
  "clip_g": {"shape": [N, 4096]},    ← Larger!
  "t5": {"shape": [N, 4096]}          ← T5 encoder
}
```
- **Three tensors**: `clip_l`, `clip_g`, `t5`
- **CLIP-G**: 4096 dimensions (larger than SDXL)
- **T5**: Additional text encoder

### **Detection Logic:**
```csharp
var hasClipL = header.Keys.Any(k => k.Contains("clip_l"));
var hasClipG = header.Keys.Any(k => k.Contains("clip_g"));
var hasT5 = header.Keys.Any(k => k.Contains("t5"));
var hasStringToParam = header.Keys.Any(k => k.Contains("string_to_param"));

if (hasStringToParam && !hasClipL && !hasClipG)
    return "SD1.5";

if (hasClipL && hasClipG && hasT5)
    return "SD3";

if (hasClipL && hasClipG && !hasT5)
    return "SDXL";  // Could also be Pony or Illustrious
```

---

## **Method 4: Directory Structure** ⚠️ Your Organization

Based on your folder organization:

```
D:\AI\SD Models\embeddings\
├── SDXL\           → SDXL embeddings
├── Pony\           → Pony XL embeddings
├── Illustrious\    → Illustrious embeddings
├── SD1.5\          → SD 1.5 embeddings
├── Characters\     → Mixed (check subfolder or filename)
└── Egirls\         → Mixed (check subfolder or filename)
```

**Detection:**
```csharp
var path = embedding.FilePath.ToLowerInvariant();

if (path.Contains(@"\pony\"))
    return "Pony";
if (path.Contains(@"\illustrious\"))
    return "Illustrious";
if (path.Contains(@"\sd1.5\") || path.Contains(@"\sd15\"))
    return "SD1.5";
if (path.Contains(@"\sdxl\"))
    return "SDXL";
```

---

## **Method 5: Filename Patterns** ⚠️ Least Reliable

Some creators use consistent naming:

### **Common Patterns:**
- `*_PDXL*` → **Pony** (Pony Diffusion XL)
- `*_SDXL*` → **SDXL**
- `BadPonyHD` → **Pony** (name indicates Pony)
- `ILXL*` → **Illustrious XL**
- `zPDXL*` → **Pony**
- `Stable_Yogis_PDXL_*` → **Pony**
- `Stable_Yogis_Illustrious_*` → **Illustrious**

**Your Examples:**
```
Stable_Yogis_PDXL_Negatives-neg.safetensors  → Pony
Stable_Yogis_Illustrious_Negatives-neg.safetensors  → Illustrious
SDXL1.0_DeepNegative_xl_v1.safetensors  → SDXL
ILXLneg.safetensors  → Illustrious
BadPonyHD.safetensors  → Pony
```

---

## **Pony vs Illustrious vs SDXL: The Challenge**

All three use **identical tensor structure**:
- CLIP-L: 768D
- CLIP-G: 1280D

**They differ in training data, not architecture!**

### **How to Distinguish:**

1. **Check metadata** (most reliable)
2. **Check directory** (if organized)
3. **Check filename** (creator conventions)
4. **Test in-app** (if uncertain, try loading in both SDXL and Pony checkpoints)

### **Your Collection:**

From your directory structure:
```
/Pony/
  BadPonyHD.safetensors              → Definitely Pony
  zPDXL3.safetensors                 → Pony (PDXL = Pony Diffusion XL)
  Stable_Yogis_PDXL_*                → Pony

/Illustrious/
  ILXLneg.safetensors                → Illustrious (ILXL = Illustrious XL)
  lazyhand.safetensors               → Illustrious (in Illustrious folder)
  Smooth_Quality_illus_plus.safetensors  → Illustrious

/SDXL/
  SDXL1.0_DeepNegative_xl_v1.safetensors  → SDXL
  Stable_Yogis_Animetoon_Negatives-neg.safetensors  → SDXL
```

---

## **Detection Algorithm (Priority Order)**

```csharp
public static async Task<string> DetectModelTypeAsync(string embeddingPath)
{
    // 1. Check .metadata.json (CivitAI)
    var metadataPath = embeddingPath.Replace(".safetensors", ".metadata.json");
    if (File.Exists(metadataPath))
    {
        var json = await File.ReadAllTextAsync(metadataPath);
        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        
        if (metadata?.ContainsKey("baseModel") == true)
        {
            var baseModel = metadata["baseModel"].GetString();
            return NormalizeModelType(baseModel);
            // Returns: "SDXL", "Pony", "Illustrious", "SD1.5", etc.
        }
    }
    
    // 2. Read safetensors header
    var fileBytes = await File.ReadAllBytesAsync(embeddingPath);
    var headerLength = BitConverter.ToInt64(fileBytes, 0);
    var headerBytes = new byte[headerLength];
    Array.Copy(fileBytes, 8, headerBytes, 0, headerLength);
    var headerJson = Encoding.UTF8.GetString(headerBytes);
    var header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(headerJson);
    
    // 2a. Check safetensors metadata
    if (header?.ContainsKey("__metadata__") == true)
    {
        var metadata = header["__metadata__"];
        
        if (metadata.TryGetProperty("sd_version", out var sdVersion))
        {
            return NormalizeModelType(sdVersion.GetString());
        }
        
        if (metadata.TryGetProperty("base_model", out var baseModel))
        {
            return NormalizeModelType(baseModel.GetString());
        }
    }
    
    // 2b. Analyze tensor structure
    var hasClipL = header.Keys.Any(k => k.Contains("clip_l"));
    var hasClipG = header.Keys.Any(k => k.Contains("clip_g"));
    var hasT5 = header.Keys.Any(k => k.Contains("t5"));
    var hasStringToParam = header.Keys.Any(k => k.Contains("string_to_param"));
    
    if (hasStringToParam && !hasClipL && !hasClipG)
        return "SD1.5";
    
    if (hasClipL && hasClipG && hasT5)
        return "SD3";
    
    // 3. Check directory structure
    var directory = Path.GetDirectoryName(embeddingPath)?.ToLowerInvariant() ?? "";
    
    if (directory.Contains(@"\pony\"))
        return "Pony";
    if (directory.Contains(@"\illustrious\"))
        return "Illustrious";
    if (directory.Contains(@"\sd1.5\") || directory.Contains(@"\sd15\"))
        return "SD1.5";
    if (directory.Contains(@"\sdxl\"))
        return "SDXL";
    
    // 4. Check filename patterns
    var fileName = Path.GetFileNameWithoutExtension(embeddingPath).ToLowerInvariant();
    
    if (fileName.Contains("pdxl") || fileName.Contains("pony"))
        return "Pony";
    if (fileName.Contains("ilxl") || fileName.Contains("illustrious"))
        return "Illustrious";
    if (fileName.Contains("sd15") || fileName.Contains("sd1_5"))
        return "SD1.5";
    
    // 5. Default to SDXL (most common for CLIP-L+G structure)
    if (hasClipL && hasClipG)
        return "SDXL";
    
    return "Unknown";
}
```

---

## **Your Collection Statistics**

Based on your directory structure:

| Model Type | Count | Location |
|-----------|-------|----------|
| **SDXL** | ~50 | `/SDXL/` folder |
| **Pony** | ~40 | `/Pony/` folder |
| **Illustrious** | ~50 | `/Illustrious/` folder |
| **SD 1.5** | ~10 | `/SD1.5/` folder |
| **Characters (Mixed)** | ~650 | `/Characters/`, `/Egirls/` - Need individual detection |

**Total**: ~800 embeddings

### **Character Embeddings Detection:**

Your character embeddings (DV_*, Tower13_*, etc.) are likely **SDXL** unless:
- Filename contains PDXL → Pony
- Metadata says otherwise
- In specific subfolder

---

## **Confidence Levels**

| Detection Method | Confidence | Notes |
|-----------------|-----------|-------|
| CivitAI metadata `baseModel` | 99% | Most reliable, direct from creator |
| Safetensors `__metadata__` | 95% | Built into file |
| Tensor structure (SD1.5 vs SDXL) | 90% | Clear structural differences |
| Directory structure | 80% | If you organized correctly |
| Tensor structure (SDXL vs Pony vs Illustrious) | **20%** | ⚠️ Identical structure! |
| Filename patterns | 60% | Creator-dependent |

**Bottom line**: For SDXL/Pony/Illustrious distinction, **always check metadata first!**

---

## **Testing Unknown Embeddings**

If detection fails, test manually:

1. Load embedding in SDXL checkpoint → Works well? Likely SDXL/Pony/Illustrious
2. Load embedding in SD 1.5 checkpoint → Works well? Likely SD 1.5
3. Compare results between Pony and SDXL checkpoints → Better in one? That's the target model!

**ComfyUI Test:**
- Load embedding with SDXL base → Generate image
- Load same embedding with Pony base → Generate image
- Compare quality → The better one indicates correct model type
