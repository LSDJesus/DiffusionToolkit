# Metadata Parsing Logic Reference

**For Diffusion Toolkit Implementation**

Technical reference for parsing generation metadata from diverse image sources. Includes regex patterns, JSON paths, algorithm specifications, and error handling strategies.

---

## 1. LoRA Tag Extraction

### Regex Pattern
```regex
<lora:([^:>]+):([0-9.]+)(?::([0-9.]+))?>
```

### Breakdown
```
<lora:          # Literal opening tag
([^:>]+)        # Group 1: LoRA name (anything except : and >)
:([0-9.]+)      # Group 2: Model strength (number with optional decimal)
(?::([0-9.]+))? # Group 3 (optional): Clip strength (number with optional decimal)
>               # Literal closing >
```

### Extraction Algorithm
```python
import re

def extract_loras(prompt: str):
    loras = []
    pattern = r'<lora:([^:>]+):([0-9.]+)(?::([0-9.]+))?>'
    
    for match in re.finditer(pattern, prompt):
        name = match.group(1)
        model_strength = float(match.group(2))
        # If clip_strength not specified, use model_strength for both
        clip_strength = float(match.group(3)) if match.group(3) else model_strength
        
        loras.append({
            "name": name,
            "model_strength": model_strength,
            "clip_strength": clip_strength
        })
    
    # Remove LoRA tags from prompt
    cleaned_prompt = re.sub(pattern, '', prompt).strip()
    # Clean up multiple spaces
    cleaned_prompt = re.sub(r'\s+', ' ', cleaned_prompt)
    
    return cleaned_prompt, loras
```

### Examples
```
INPUT: "<lora:detail:0.8> beautiful cat"
OUTPUT: name="detail", model=0.8, clip=0.8, prompt="beautiful cat"

INPUT: "<lora:style:0.6:0.7> with style"
OUTPUT: name="style", model=0.6, clip=0.7, prompt="with style"

INPUT: "cat <lora:a:1> and <lora:b:0.5> dog"
OUTPUT: loras=[{name:"a", model:1, clip:1}, {name:"b", model:0.5, clip:0.5}]
        prompt="cat and dog"
```

---

## 2. Embedding Tag Extraction

### Regex Patterns
```regex
# Format 1: embedding:name
embedding:([a-zA-Z0-9_\-\.]+)

# Format 2: (embedding:name)
\(embedding:([a-zA-Z0-9_\-\.]+)\)

# Format 3: Textual Inversion reference
([a-zA-Z0-9_\-]+)(?:\.safetensors|\.pt|\.bin)?(?=[\s,\)\]])
```

### Extraction Algorithm
```python
def extract_embeddings(prompt: str):
    embeddings = []
    
    # Primary: explicit embedding: prefix
    pattern1 = r'embedding:([a-zA-Z0-9_\-\.]+)'
    for match in re.finditer(pattern1, prompt):
        embeddings.append(match.group(1))
    
    # Secondary: bracketed format
    pattern2 = r'\(embedding:([a-zA-Z0-9_\-\.]+)\)'
    for match in re.finditer(pattern2, prompt):
        embeddings.append(match.group(1))
    
    # Remove embedding tags from prompt
    cleaned = re.sub(r'embedding:[a-zA-Z0-9_\-\.]+', '', prompt)
    cleaned = re.sub(r'\(embedding:[a-zA-Z0-9_\-\.]+\)', '', cleaned)
    cleaned = re.sub(r'\s+', ' ', cleaned).strip()
    
    return cleaned, list(set(embeddings))  # Remove duplicates
```

---

## 3. Attention Weight Extraction & Clamping

### Regex Patterns
```regex
# Bracketed weights: (word:1.5) [word:1.8] {word:1.6}
([\(\[\{])([^:\)\]\}]+):(\d+\.?\d*)([\)\]\}])

# Unbracketed weights: word:2.0 (followed by space, comma, or end)
(?<![\(\[\{])(\b[^:,\(\[\{]+):(\d+\.?\d*)(?=[\s,\)\]\}]|$)
```

### Clamping Algorithm
```python
def clamp_attention_weights(prompt: str, max_weight: float = 1.4) -> str:
    """
    Normalize attention weights to prevent over-conditioning.
    Preserves bracket types and word content.
    """
    
    # Pattern 1: Bracketed weights (text:weight) with any bracket type
    bracketed_pattern = r'([\(\[\{])([^:\)\]\}]+):(\d+\.?\d*)([\)\]\}])'
    
    def clamp_bracketed(match):
        open_bracket = match.group(1)
        text = match.group(2)
        weight = float(match.group(3))
        close_bracket = match.group(4)
        
        if weight > max_weight:
            weight = max_weight
        
        return f"{open_bracket}{text}:{weight}{close_bracket}"
    
    # Pattern 2: Unbracketed weights
    unbracketed_pattern = r'(?<![\(\[\{])(\b[^:,\(\[\{]+):(\d+\.?\d*)(?=[\s,\)\]\}]|$)'
    
    def clamp_unbracketed(match):
        text = match.group(1)
        weight = float(match.group(2))
        
        if weight > max_weight:
            weight = max_weight
        
        return f"{text}:{weight}"
    
    result = re.sub(bracketed_pattern, clamp_bracketed, prompt)
    result = re.sub(unbracketed_pattern, clamp_unbracketed, result)
    
    return result
```

### Examples
```
INPUT: "((masterpiece:1.5), (best quality:2.0))"
CLAMPED: "((masterpiece:1.4), (best quality:1.4))"

INPUT: "[highly detailed:1.8] portrait"
CLAMPED: "[highly detailed:1.4] portrait"

INPUT: "blue eyes:2.5, red hair:1.6"
CLAMPED: "blue eyes:1.4, red hair:1.4"
```

---

## 4. ComfyUI Workflow Format

### JSON Structure (PNG Metadata)
```json
{
  "prompt": "{\"0\": {...}, \"1\": {...}, ...}",  // Executed graph
  "workflow": "{\"0\": {...}, \"1\": {...}, ...}"  // Full UI state
}
```

### Extraction Algorithm
```python
import json

def extract_comfyui_metadata(png_metadata: dict) -> dict:
    """Parse ComfyUI workflow from PNG metadata"""
    
    result = {
        "positive_prompt": "",
        "negative_prompt": "",
        "loras": [],
        "embeddings": [],
        "extra": {}
    }
    
    # Parse prompt (executed graph)
    if 'prompt' in png_metadata:
        try:
            prompt_data = json.loads(png_metadata['prompt'])
            # CLIPTextEncode nodes typically at indices 6, 7 or 3, 4
            # Keys: "inputs" → "text"
            for node_id, node in prompt_data.items():
                if isinstance(node, dict) and node.get('class_type') == 'CLIPTextEncode':
                    text = node.get('inputs', {}).get('text', '')
                    if text:
                        # Determine if positive or negative based on node ID
                        if int(node_id) % 2 == 0:
                            result["positive_prompt"] += text + " "
                        else:
                            result["negative_prompt"] += text + " "
        except json.JSONDecodeError:
            pass
    
    # Parse workflow for metadata (CFG, seed, sampler, etc)
    if 'workflow' in png_metadata:
        try:
            workflow_data = json.loads(png_metadata['workflow'])
            for node_id, node in workflow_data.items():
                if isinstance(node, dict):
                    class_type = node.get('class_type', '')
                    
                    # KSampler: seed, steps, cfg, sampler_name, scheduler
                    if 'Sampler' in class_type or 'KSample' in class_type:
                        inputs = node.get('inputs', {})
                        result["extra"]["seed"] = inputs.get('seed')
                        result["extra"]["steps"] = inputs.get('steps')
                        result["extra"]["cfg"] = inputs.get('cfg')
                        result["extra"]["sampler"] = inputs.get('sampler_name')
                        result["extra"]["scheduler"] = inputs.get('scheduler')
                    
                    # CheckpointLoader: model name
                    if 'CheckpointLoader' in class_type:
                        result["extra"]["model"] = node.get('inputs', {}).get('ckpt_name')
        except json.JSONDecodeError:
            pass
    
    # Extract LoRAs and embeddings from prompts
    result["positive_prompt"], loras_pos = extract_loras(result["positive_prompt"])
    result["negative_prompt"], loras_neg = extract_loras(result["negative_prompt"])
    result["loras"].extend(loras_pos + loras_neg)
    
    return result
```

### Key Node Types
```
CheckpointLoader      → class_type: "CheckpointLoader"
                         inputs.ckpt_name: Model filename

CLIPTextEncode        → class_type: "CLIPTextEncode"
                         inputs.text: Prompt text
                         inputs.clip: CLIP reference

KSampler              → class_type: "KSampler"
                         inputs.seed: Random seed
                         inputs.steps: Denoising steps
                         inputs.cfg: Classifier-free guidance
                         inputs.sampler_name: e.g., "dpmpp_2m"
                         inputs.scheduler: e.g., "normal"

VAEDecode            → class_type: "VAEDecode"
                       outputs decoded image

LoRA nodes           → class_type: "LoraLoader"
                       inputs.lora_name: LoRA file
                       inputs.strength_model: Model weight
                       inputs.strength_clip: CLIP weight
```

---

## 5. A1111/Forge Parameter Format

### Structure
```
[positive prompt text - may be multi-line]
Negative prompt: [negative prompt text - may be multi-line]
[Key]: [Value], [Key]: [Value], ...
```

### Regex Patterns
```regex
# Separator between positive and negative
^Negative prompt:\s*(.*)$

# Key-value pairs in steps line
([A-Za-z\s]+):\s*([^,]+)(?:,|$)

# LoRA references in hashes line
"([^"]+):\s*([a-f0-9]+)"
```

### Extraction Algorithm
```python
def extract_a1111_metadata(parameters_text: str) -> dict:
    """Parse A1111/Forge format parameters"""
    
    result = {
        "positive_prompt": "",
        "negative_prompt": "",
        "loras": [],
        "extra": {}
    }
    
    if not parameters_text:
        return result
    
    lines = parameters_text.split('\n')
    positive_lines = []
    negative_lines = []
    params_lines = []
    
    in_negative = False
    
    for line in lines:
        if line.startswith("Negative prompt:"):
            in_negative = True
            # Extract negative text after "Negative prompt:"
            neg_text = line.replace("Negative prompt:", "").strip()
            if neg_text:
                negative_lines.append(neg_text)
        elif in_negative and line and not any(c.isdigit() for c in line.split(':')[0]):
            # Still in negative prompt section (no key:value pattern)
            negative_lines.append(line)
        elif ':' in line and not line.startswith(' '):
            # This is a parameter line (Key: Value format)
            in_negative = False
            params_lines.append(line)
        elif not in_negative and line:
            # Positive prompt section
            positive_lines.append(line)
    
    result["positive_prompt"] = '\n'.join(positive_lines).strip()
    result["negative_prompt"] = '\n'.join(negative_lines).strip()
    
    # Parse parameters
    params_text = '\n'.join(params_lines)
    
    # Key-value pattern
    kv_pattern = r'([A-Za-z\s]+?):\s*([^,]+?)(?:,|$)'
    for match in re.finditer(kv_pattern, params_text):
        key = match.group(1).strip().lower()
        value = match.group(2).strip()
        
        # Map to standard keys
        if key == "steps":
            result["extra"]["steps"] = int(value)
        elif key in ["sampler", "sampler name"]:
            result["extra"]["sampler"] = value
        elif key in ["cfg", "cfg scale"]:
            result["extra"]["cfg"] = float(value)
        elif key == "seed":
            result["extra"]["seed"] = int(value)
        elif key == "size" or key == "dimensions":
            # Parse "1024x768" format
            parts = value.split('x')
            if len(parts) == 2:
                result["extra"]["width"] = int(parts[0])
                result["extra"]["height"] = int(parts[1])
        elif key == "model":
            result["extra"]["model"] = value
        elif key in ["scheduler", "schedule"]:
            result["extra"]["scheduler"] = value
    
    # Extract LoRA info from hashes line if present
    lora_hashes_pattern = r'"([^"]+):\s*([a-f0-9]+)"'
    for match in re.finditer(lora_hashes_pattern, params_text):
        lora_name = match.group(1)
        # Note: only name and hash, no weights in hashes line
        result["loras"].append({
            "name": lora_name,
            "model_strength": 1.0,  # Default assumption
            "clip_strength": 1.0
        })
    
    return result
```

### Example Parsing
```
INPUT TEXT:
---
masterpiece, best quality, (blue eyes:1.2)
Negative prompt: blurry, low quality
Steps: 25, Sampler: DPM++ 2M, CFG scale: 7.5, Seed: 12345, Size: 1024x768, Model: model_v1.0
Lora hashes: "style_lora: abc123def456"
---

OUTPUT:
{
  "positive_prompt": "masterpiece, best quality, (blue eyes:1.2)",
  "negative_prompt": "blurry, low quality",
  "loras": [{"name": "style_lora", "model_strength": 1.0, "clip_strength": 1.0}],
  "extra": {
    "steps": 25,
    "sampler": "DPM++ 2M",
    "cfg": 7.5,
    "seed": 12345,
    "width": 1024,
    "height": 768,
    "model": "model_v1.0"
  }
}
```

---

## 6. CivitAI Workflow Format

### JSON Structure
```json
{
  "workflow": {...},  // Full ComfyUI workflow (parse as ComfyUI format)
  "extraMetadata": {
    "prompt": "...",
    "negativePrompt": "...",
    "cfgScale": 7.5,
    "steps": 25,
    "seed": 12345,
    "sampler": "...",
    "scheduler": "..."
  }
}
```

### Extraction Algorithm
```python
def extract_civitai_metadata(data: dict) -> dict:
    """Parse CivitAI workflow format"""
    
    result = {
        "positive_prompt": "",
        "negative_prompt": "",
        "loras": [],
        "extra": {}
    }
    
    # First, try extraMetadata (simplified)
    if "extraMetadata" in data and isinstance(data["extraMetadata"], dict):
        extra = data["extraMetadata"]
        result["positive_prompt"] = extra.get("prompt", "")
        result["negative_prompt"] = extra.get("negativePrompt", "")
        result["extra"]["steps"] = extra.get("steps")
        result["extra"]["cfg"] = extra.get("cfgScale")
        result["extra"]["seed"] = extra.get("seed")
        result["extra"]["sampler"] = extra.get("sampler")
        result["extra"]["scheduler"] = extra.get("scheduler")
    
    # Then parse full workflow if available (for complete data)
    if "workflow" in data:
        workflow_result = extract_comfyui_metadata({
            "workflow": json.dumps(data["workflow"]) if isinstance(data["workflow"], dict) else data["workflow"]
        })
        # Merge workflow data (don't override extraMetadata)
        if not result["positive_prompt"] and workflow_result["positive_prompt"]:
            result["positive_prompt"] = workflow_result["positive_prompt"]
        if not result["negative_prompt"] and workflow_result["negative_prompt"]:
            result["negative_prompt"] = workflow_result["negative_prompt"]
        result["loras"].extend(workflow_result["loras"])
        result["extra"].update(workflow_result["extra"])
    
    return result
```

---

## 7. EXIF Metadata Format

### EXIF Tag Specifications

```
IFD 0 (Main Image):
  ─ Tag 0x010F (Make): Camera manufacturer
  ─ Tag 0x0110 (Model): Camera model
  ─ Tag 0x011C (RawDataByteOrder): Byte order
  ─ Tag 0x0131 (Software): Software used
  ─ Tag 0x8825 (GPSInfo): GPS data

IFD EXIF:
  ─ Tag 0x9000 (ExifVersion): EXIF version
  ─ Tag 0xA000 (ColorSpace): Color space info
  ─ Tag 0x927C (MakerNote): Manufacturer notes
  ─ Tag 0x9286 (UserComment): ← PRIMARY SOURCE FOR PROMPTS

XMP Namespace:
  ─ http://purl.org/dc/elements/1.1/
  ─ http://ns.adobe.com/xap/1.0/
  ─ Custom: dc:description, subject, title
```

### UserComment Charset Decoding

**Structure**: 8-byte charset marker + data

```python
def decode_exif_user_comment(comment: bytes) -> str:
    """
    Decode EXIF UserComment with charset handling.
    
    Format: [8-byte charset identifier][data]
    
    Charset markers:
    - b'ASCII\x00\x00\x00' (8 bytes) → ASCII encoding
    - b'JIS\x00\x00\x00\x00\x00' (8 bytes) → JIS encoding
    - b'UNICODE\x00' (8 bytes) → UTF-16 (varies by tool)
    - b'\x00\x00\x00\x00\x00\x00\x00\x00' (8 null bytes) → Undefined (try UTF-8)
    """
    
    if isinstance(comment, str):
        return comment
    
    if not isinstance(comment, bytes) or len(comment) <= 8:
        return ""
    
    charset_marker = comment[:8]
    data = comment[8:]
    
    # Try to decode based on charset marker
    try:
        if charset_marker.startswith(b'ASCII'):
            return data.decode('ascii')
        
        elif charset_marker.startswith(b'JIS'):
            return data.decode('shift-jis')
        
        elif charset_marker.startswith(b'UNICODE'):
            # CivitAI and others often use UTF-16BE despite spec
            # Detect by checking if first byte is null
            if len(data) > 1 and data[0] == 0:
                return data.decode('utf-16-be')
            else:
                return data.decode('utf-16-le')
        
        elif charset_marker == b'\x00\x00\x00\x00\x00\x00\x00\x00':
            # Undefined - try UTF-8 first, then fallback
            try:
                return data.decode('utf-8')
            except:
                return data.decode('latin-1')
        
        else:
            # Unknown charset, try UTF-8
            try:
                return data.decode('utf-8')
            except:
                return data.decode('latin-1')
    
    except Exception as e:
        # Last resort: latin-1 never fails
        return data.decode('latin-1', errors='replace')
```

### XMP Extraction
```python
import re

def extract_xmp_metadata(xmp_raw: str) -> dict:
    """Extract metadata from XMP namespace"""
    
    result = {
        "positive_prompt": "",
        "negative_prompt": "",
        "extra": {}
    }
    
    # Common XMP paths for prompts
    patterns = {
        "positive_prompt": [
            r'<dc:description[^>]*>([^<]+)</dc:description>',
            r'<rdf:Description[^>]*about[^>]*>[^<]*<dc:description>([^<]+)</dc:description>',
            r'<prompt[^>]*>([^<]+)</prompt>',
            r'<ai:prompt[^>]*>([^<]+)</ai:prompt>'
        ],
        "negative_prompt": [
            r'<negative[^>]*>([^<]+)</negative>',
            r'<negativePrompt[^>]*>([^<]+)</negativePrompt>',
            r'<ai:negativePrompt[^>]*>([^<]+)</ai:negativePrompt>'
        ]
    }
    
    for key, pattern_list in patterns.items():
        for pattern in pattern_list:
            matches = re.findall(pattern, xmp_raw, re.IGNORECASE | re.DOTALL)
            if matches:
                result[key] = ' '.join(matches)
                break
    
    return result
```

---

## 8. Generic Fallback Strategy

### Priority Chain
```
1. Check "parameters" field (A1111 format)
2. Check "prompt" field (ComfyUI text export)
3. Check "description" field
4. Check "title" field
5. Check any text field containing common keywords:
   - "prompt", "steps", "seed", "sampler"
6. Return empty if nothing found
```

### Implementation
```python
def extract_generic_metadata(metadata_dict: dict) -> dict:
    """Fallback: attempt to extract from any available field"""
    
    result = {
        "positive_prompt": "",
        "negative_prompt": "",
        "loras": [],
        "extra": {}
    }
    
    # Priority order of fields to check
    text_fields = [
        "parameters", "prompt", "description", "title",
        "comment", "comments", "keywords", "subject",
        "software", "history", "metadata"
    ]
    
    for field in text_fields:
        if field in metadata_dict:
            value = metadata_dict[field]
            if isinstance(value, str) and value.strip():
                # Check if this looks like A1111 format (has "Negative prompt:")
                if "Negative prompt:" in value:
                    return extract_a1111_metadata(value)
                
                # Check if it looks like parameters
                if ':' in value and len(value) > 50:
                    result["positive_prompt"] = value
                    result["positive_prompt"], loras = extract_loras(value)
                    result["loras"] = loras
                    return result
                
                # Just return as prompt
                if value.strip():
                    result["positive_prompt"] = value
                    result["positive_prompt"], loras = extract_loras(value)
                    result["loras"] = loras
                    return result
    
    return result
```

---

## 9. Error Handling & Validation

### JSON Parsing Safety
```python
def safe_json_parse(json_string: str, default=None):
    """Safely parse JSON with fallback"""
    try:
        return json.loads(json_string)
    except json.JSONDecodeError as e:
        print(f"JSON parse error: {e}")
        return default if default is not None else {}
    except Exception as e:
        print(f"Unexpected error: {e}")
        return default if default is not None else {}
```

### Prompt Cleaning
```python
def sanitize_prompt(prompt: str) -> str:
    """Clean and normalize prompt text"""
    if not isinstance(prompt, str):
        return ""
    
    # Remove null bytes
    prompt = prompt.replace('\x00', '')
    
    # Remove control characters except newline/tab
    prompt = ''.join(char for char in prompt if ord(char) >= 32 or char in '\n\t')
    
    # Normalize whitespace
    prompt = re.sub(r'\s+', ' ', prompt).strip()
    
    return prompt
```

### Validation Checks
```python
def validate_metadata(metadata: dict) -> bool:
    """Validate extracted metadata structure"""
    
    required_keys = ["positive_prompt", "negative_prompt"]
    
    # At least one prompt should exist
    if not any(metadata.get(key, "").strip() for key in required_keys):
        return False
    
    # Reject if prompts are suspiciously short (likely noise)
    total_text = " ".join([
        metadata.get("positive_prompt", ""),
        metadata.get("negative_prompt", "")
    ])
    
    if len(total_text.strip()) < 5:
        return False
    
    return True
```

---

## 10. Format Detection Algorithm

### Decision Tree
```python
def detect_and_parse_metadata(image_metadata: dict, image_data=None) -> dict:
    """
    Detect format and parse metadata.
    Returns: {metadata_dict, source_format}
    """
    
    source = "unknown"
    result = {
        "positive_prompt": "",
        "negative_prompt": "",
        "loras": [],
        "embeddings": [],
        "extra": {}
    }
    
    # 1. ComfyUI Workflow (highest priority)
    if 'prompt' in image_metadata and 'workflow' in image_metadata:
        try:
            result = extract_comfyui_metadata(image_metadata)
            source = "comfyui_workflow"
            return result, source
        except Exception as e:
            print(f"ComfyUI parse failed: {e}")
    
    # 2. CivitAI Format
    if 'extraMetadata' in image_metadata:
        try:
            result = extract_civitai_metadata(image_metadata)
            source = "civitai_workflow"
            if validate_metadata(result):
                return result, source
        except Exception as e:
            print(f"CivitAI parse failed: {e}")
    
    # 3. A1111/Forge Parameters (very common)
    if 'parameters' in image_metadata:
        try:
            result = extract_a1111_metadata(image_metadata['parameters'])
            source = "a1111_parameters"
            if validate_metadata(result):
                return result, source
        except Exception as e:
            print(f"A1111 parse failed: {e}")
    
    # 4. EXIF Metadata
    try:
        exif_result = extract_exif_metadata(image_data)
        if exif_result and validate_metadata(exif_result):
            result = exif_result
            source = "exif_metadata"
            return result, source
    except Exception as e:
        print(f"EXIF parse failed: {e}")
    
    # 5. Generic Fallback
    try:
        result = extract_generic_metadata(image_metadata)
        source = "generic_fallback"
        if validate_metadata(result):
            return result, source
    except Exception as e:
        print(f"Generic parse failed: {e}")
    
    # No valid metadata found
    return result, "no_metadata"
```

---

## Summary: Implementation Checklist

- [ ] **LoRA extraction**: Regex `<lora:name:weight[:clip]>`
- [ ] **Embedding extraction**: Patterns for `embedding:name` and variations
- [ ] **Weight clamping**: Normalize bracketed + unbracketed weights
- [ ] **ComfyUI parsing**: JSON workflow traversal
- [ ] **A1111 parsing**: Regex multi-line key-value extraction
- [ ] **CivitAI parsing**: JSON + workflow fallback
- [ ] **EXIF parsing**: Charset-aware UserComment decoding + XMP
- [ ] **Generic fallback**: Field priority chain
- [ ] **Format detection**: Decision tree with validation
- [ ] **Error handling**: Safe JSON, sanitization, validation
- [ ] **Test coverage**: All formats + edge cases

---

**For Questions**: Cross-reference with actual Luna implementation in:
- `luna_batch_prompt_extractor_node.py` - Full source code
- Metadata stored in database: `image.metadata`, `image.extra`
