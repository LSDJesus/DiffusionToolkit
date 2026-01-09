# 🌈 Luna LSD (Luna Semantic Detailing) - Complete Specification v3.0

> **Paradigm Shift:** From monolithic generation to compositional refinement through introspective semantic awareness.

**LSD Branding** - LSDJesus 🌈

**Latest Update:** January 7th 2026 - Bidirectional DAAM, Optimization System, Analyzer Config

---

## The Revolution

Traditional image generation treats prompts as monolithic instructions. Want different eyes? Re-generate everything. Want to iterate on clothing? Lose the perfect face you just got.

**Luna LSD changes the game:**
1. **Draft with semantic placeholders** - "eyes, hair, dress, hands"
2. **DAAM extracts spatial understanding** - Model tells us where it placed each concept
3. **Enhance regions independently** - Replace "eyes" with "heterochromia red and blue eyes"
4. **Refine with spatial awareness** - Only affected regions change
5. **Iterate infinitely** - ComfyUI caching = free re-rolls per region

**Result:** Compositional slot machine. Each semantic region is an independent lever.

---

## Core Insight: Introspective Semantic Awareness

### The Problem

Tiled upscaling fails at high denoise (>0.4) because every tile receives the **global prompt**. A tile containing only a dragon's toe tries to generate an entire dragon—this is the "Tiled Hallucination" problem.

**Traditional solutions:**
- Keep denoise at 0.3 (limits detail generation)
- Use SAM3/YOLO to detect regions post-hoc (external model, inference overhead, detection errors)

**LSD solution:** Use DAAM (Diffusion Attentive Attribution Maps) to extract the model's own cross-attention maps during generation. The model *tells us* where it placed each concept. We then build spatially-aware conditioning so each region only "hears" relevant tokens.

### Why This Works

During diffusion, cross-attention layers map prompt tokens to spatial locations. DAAM extracts these maps as heatmaps—one per token. This is not detection; it's **introspection**.

```
SAM3 Approach:  Generate → Detect (external model) → Refine
LSD Approach:   Generate → Introspect (same model) → Refine
```

Benefits:
- ✅ Zero additional models (DAAM hooks existing UNet)
- ✅ Zero detection errors (model's own understanding)
- ✅ Zero extra VRAM (attention maps already computed)
- ✅ Compositional iteration (edit regions independently)

### Why Booru Tagging is Perfect for LSD

Booru-style prompting is unintentionally ideal for semantic detailing:

```
1girl, blue eyes, silver hair, black dress, forest background
```

Each **comma-delimited tag** is already a discrete semantic unit. No natural language parsing, no sub-word tokenization gymnastics:

- `"blue eyes"` → ONE tag → ONE DAAM heatmap → ONE semantic mask
- `"forest background"` → ONE tag → ONE DAAM heatmap → ONE semantic mask

Contrast with natural language prompts:

```
"a beautiful young woman with strikingly vibrant blue eyes..."
```

CLIP tokenizes `"strikingly"` as `["strik", "##ingly"]`. Which heatmaps belong together? How do you reconstitute the semantic boundary?

**With booru tags, you don't have to.** Each tag is pre-segmented. The LSD system simply:

```python
def parse_booru_tokens(prompt: str) -> List[str]:
    return [tag.strip() for tag in prompt.split(",") if tag.strip()]

# "1girl, blue eyes, forest background"
# → ["1girl", "blue eyes", "forest background"]
# Each maps directly to its DAAM heatmap. Done.
```

This simplification eliminates an entire class of edge cases and makes LSD reliable out of the box.

---

## Architecture Overview

### Phase 0: DAAM Integration v3.0 (Foundation + Bidirectional + Optimization)

DAAM integrated into Luna Collection from comfyui-daam project (MIT License).

#### Node Architecture

**Production Workflow (No DAAM):**
- **Luna KSampler (VRAM Optimized)** - Pure `inference_mode()` wrapper, ~3.5GB VRAM
- Use for final renders, batch generation, production workflows

**Analysis Workflow (DAAM Enabled):**
- **Luna DAAM KSampler** - Dedicated DAAM sampler with optimization config
- **Luna DAAM Config** - Performance presets (quality, balanced, fast, turbo, bidirectional)
- Use for LSD workflows, prompt analysis, iterative refinement

#### Modified Core Nodes

1. **Luna Config Gateway** - LUNA_PIPE now contains 17 elements (was 14):
   - Index 14: `pos_tokens` (for positive DAAM)
   - Index 15: `neg_tokens` (for negative DAAM)
   - Index 16: `pos_prompt` (processed, wildcards resolved)
   - Index 17: `neg_prompt` (processed)

2. **Luna Pipe Expander** - Outputs 17 elements (was 14):
   - `pos_tokens`, `neg_tokens` (DAAM token sequences)
   - `pos_prompt`, `neg_prompt` (processed prompts)

3. **Luna KSampler** - STRIPPED DAAM, pure VRAM optimization:
   - Returns: `(LATENT,)` only
   - Always uses `inference_mode()` for 60-70% memory reduction
   - For DAAM, use Luna DAAM KSampler instead

4. **Luna DAAM KSampler** - NEW dedicated DAAM sampler:
   - Optional `daam_config` input for optimization presets
   - Returns: `(LATENT, DAAM_HEATMAPS)`
   - Configurable skip_early, stride, weighting

5. **Luna DAAM Analyzer** - Enhanced visualization:
   - Two outputs: `raw_overlays`, `processed_overlays`
   - Both show heatmaps overlaid on images for context
   - Processed overlays show topological modifications
   - Optional `analyzer_config` input for parameter tuning

#### Bidirectional DAAM System

**DAAM_HEATMAPS Format (Combined Positive + Negative):**
```python
{
    "positive": pos_heatmaps,    # Islands (uplift)
    "negative": neg_heatmaps,    # Trenches (deepen)
    "pos_tokens": pos_tokens,
    "neg_tokens": neg_tokens,
    "config": {...}              # Optional optimization config
}
```

**Why Bidirectional?**
- **Positive heatmaps** = Where to ENHANCE ("blue eyes" → uplift that region)
- **Negative heatmaps** = Where to SUPPRESS ("blurry" → deepen suppression)
- **LSD Token Editor** can now map both positive and negative attention spatially

**Ocean-First Topological Processing:**
- **Islands (Positive):** Uplift peaks, create beach ramps, extend sea level
- **Trenches (Negative):** Deepen focused negation, preserve surface areas
- **Atolls:** Preserve small features (fabric patterns, textures)

#### DAAM Optimization System

**Luna DAAM Config Node** - Performance tuning with 6 presets:

| Preset | Skip Early | Stride | Speed | Use Case |
|--------|------------|--------|-------|----------|
| `quality` | 0% | 1 | Baseline | Best accuracy |
| `balanced` | 30% | 1 | ~30% faster | Default, good tradeoff |
| `fast` | 40% | 2 | ~60% faster | Quick iterations |
| `turbo` | 50% | 3 | ~70% faster | Maximum speed |
| `bidirectional` | 30% | 1 | ~30% faster | Pos+Neg capture |
| `bidirectional_fast` | 40% | 2 | ~60% faster | Fast pos+neg |

**Optimization Parameters:**
- `skip_early_ratio` (0.0-0.8) - Skip first N% of noisy timesteps
- `timestep_stride` (1-5) - Capture every Nth step (2 = 50% reduction)
- `late_weight_bias` (1.0-5.0) - Weight late steps more heavily
- `gpu_resident` (bool) - Keep heatmaps on GPU (faster, +1GB VRAM)

**Why This Works:**
- Early timesteps (t=1.0→0.8) are noisy, low signal
- Late timesteps (t=0.2→0.0) have high detail, best semantic maps
- Adjacent timesteps have similar attention patterns
- Skipping early + stride = 60% reduction with minimal quality loss

#### Analyzer Config System

**Luna DAAM Analyzer Config Node** - Topological parameter tuning:

| Preset | Mode | Uplift | Padding | Use Case |
|--------|------|--------|---------|----------|
| `balanced` | topological | 1.5× | 20% | Default |
| `tight` | topological | 2.0× | 10% | Aggressive detection |
| `loose` | topological | 1.2× | 35% | Conservative |
| `aggressive` | topological | 2.5× | 15% | Maximum uplift |
| `advanced` | advanced | - | - | Otsu+Adaptive |
| `legacy` | legacy | - | - | Simple threshold |

**Tunable Parameters:**
- `uplift_factor` (1.0-3.0) - Island peak stretch multiplier
- `padding_ratio` (0.0-0.5) - Sea level extension as % of island size
- `sea_level_percentile` (5-20) - Ocean detection threshold
- `high_tide_percentile` (80-95) - Peak detection threshold
- `atoll_dilation` (0-10) - Small island smoothing
- `colormap` - jet, viridis, plasma, hot, cool

**Live Tuning Workflow:**
```
Luna DAAM Analyzer Config → analyzer_config
                                ↓
Luna DAAM Analyzer
├─→ raw_overlays       (see what DAAM captured)
└─→ processed_overlays (see topological modifications)
```

Change config parameters → Re-queue → See instant visual feedback!

**Memory Behavior:**
- DAAM disabled (Luna KSampler): `torch.inference_mode()` → ~3.5GB VRAM
- DAAM enabled (Luna DAAM KSampler): Gradient tracking → ~6-7GB VRAM (+2-4GB for heatmaps)
- With optimizations (fast preset): Same VRAM, 60% faster collection

### Phase 1: LSD Bridge (Spatial Conditioning)

**Purpose:** Convert DAAM heatmaps to composite area conditioning with automatic weighting.

**Input:**
- DAAM heatmaps from draft KSampler
- Original CLIP encoding
- Booru-tagged prompt
- Target resolution

**Output:**
- Composite conditioning (spatially-aware, auto-weighted)
- Debug masks (visualization)

**Core Logic:**

```python
# 1. Parse booru tags
tags = ["eyes", "hair", "dress", "background"]

# 2. Extract DAAM heatmaps (64×64 latent space)
heatmap_dict = {
    "eyes": tensor[64, 64],
    "hair": tensor[64, 64],
    # ...
}

# 3. OVERLAP MERGING (NEW): Detect and merge redundant tags
# If "blue eyes", "eyes", "detailed eyes" have 90%+ spatial overlap:
merged_groups = merge_overlapping_tags(heatmap_dict, threshold=0.9)
# → [(["blue eyes", "eyes", "detailed eyes"], union_mask), ...]

# 4. Calculate attention density per group
density = total_heat / masked_area
# eyes: high heat + small area = high density = weight 1.0
# background: low heat + large area = low density = weight 0.3

# 5. Generate pixel masks (64×64 → 4K with Gaussian feathering)
eye_mask = upscale_and_feather(union_mask)

# 6. Build layered composite conditioning
composite = [
    global_prompt @ strength=0.4,                           # Layer 0: ambient
    "background" @ background_mask, weight=0.3,             # Layer 1: diffuse
    "blue eyes, eyes, detailed eyes" @ eye_mask, weight=1.0 # Layer 2: focused (merged!)
]
```

**Overlap Detection (NEW - Implemented):**

Spatial overlap calculated via Intersection over Smaller Area:

```python
def compute_mask_overlap(mask1, mask2, method="smaller"):
    intersection = (mask1 * mask2).sum()
    smaller_area = min(mask1.sum(), mask2.sum())
    return intersection / smaller_area  # 0.0-1.0
```

**Merging Strategy:**

| Tags | Overlap | Action |
|------|---------|--------|
| "blue eyes" + "eyes" | 95% | ✅ Merge → "blue eyes, eyes" |
| "eyes" + "hair" | 5% | ❌ Keep separate |
| "detailed eyes" + "eyes" | 92% | ✅ Merge with existing "eyes" group |
| "dress" + "1girl" | 60% | ❌ Keep separate (below 90% threshold) |

**Benefits:**
- ✅ Reduces redundant layers (3 eye tags → 1 merged layer)
- ✅ Improves coherence (single conditioning for semantically-related tags)
- ✅ Cleaner composite (fewer overlapping area masks)
- ✅ No manual tuning (automatic 90% threshold)

**Automatic Weighting:**

Attention density (heat ÷ area) becomes conditioning strength:

| Tag | Heat | Area | Density | Auto-Weight |
|-----|------|------|---------|-------------|
| `eyes` | High | Small | **High** | 1.0 |
| `face` | High | Medium | Medium | 0.75 |
| `dress` | Medium | Large | Low | 0.5 |
| `background` | Low | Large | **Low** | 0.3 |

Focused concepts (eyes, jewelry) get high weights. Diffuse concepts (background, fog) get scaffold-level weights. **No manual tuning required.**

**Memory Efficiency:**

Area conditioning is lazy-evaluated during sampling:

- **NOT stored:** 4096×4096×20 mask values = ~320MB per image
- **Actually stored:** 20 conditioning tensors + 20 area boxes = ~1.2MB
- **At sample time:** Only compute masks for current tile = negligible

### Phase 2: LSD Token Editor (Compositional Enhancement)

**Purpose:** Replace/enhance specific tags while preserving their spatial masks.

**The Revolutionary Pattern:**

```
Step 1: DRAFT (Simple Semantic References)
Prompt: "eyes, hair, dress, hands"
  ↓
KSampler + DAAM enabled → Draft Image + Spatial Masks

Step 2: ENHANCE (Region-Specific Detail)
LSD Token Editor:
  {eyes:blue eyes with gold flecks}
  {hair:gradient cyan and magenta hair}
  {dress:{clothing} {materials}}  ← Wildcards!
  {hands:cat paws with retractable claws}
  ↓
Enhanced Conditioning (Modified text + Original spatial masks)

Step 3: REFINE (High-Denoise Regional Update)
KSampler (0.7 denoise) → Final Image
```

**Key Innovation:** Original DAAM masks + New semantic conditioning

```python
# Original tag: "eyes"
# Original mask: [spatial heatmap from draft]
# 
# Replacement: "blue eyes with gold flecks"
# New conditioning: encode("blue eyes with gold flecks")
# 
# Result: Same spatial location, enhanced semantic detail
```

**Wildcard Integration:**

```yaml
# wildcards/eye_colors.yaml
eye_colors:
  - blue eyes with gold flecks
  - heterochromia, red and blue eyes
  - purple glowing eyes
```

**Token Editor Input:**
```
{eyes:{eye_colors}}
{hair:{hairstyles}}
```

**Result:**
- Eyes keep ORIGINAL spatial location from draft
- Wildcard randomly selects eye detail
- Can re-run JUST the eye region without touching hair!

---

## The Anatomy-Aware Semantic Editor

### The "Spatial Envelope" Problem

Traditional inpainting struggles with structural changes:
- **Tight mask** (just the object): `jeans → dress` fails because dress needs torso space
- **Loose mask** (too much): Person's identity drifts, face changes

**LSD Token Editor solves this with Semantic Hierarchy Awareness.**

### Smart Swapping Modes

**Mode A: Tight Swap (Default)**
- Use for: Eye color changes, material swaps (`leather → silk`)
- Logic: Uses original DAAM mask only
- Example: `{eyes:glowing red eyes}` → Only eye region refined

**Mode B: Structural Swap (Auto-Detected)**
- Use for: Clothing changes, pose adjustments
- Logic: **Automatic Parent Anchoring**
  1. Detect that `jeans` is child of `1girl` (semantic hierarchy)
  2. Create workspace: `Union(jeans_mask, 1girl_mask) + 15-20% padding`
  3. Re-prompt with context: `(1girl, wearing pink dress)`
  4. Refine expanded region at 0.7 denoise

**Why This Works:**
- `1girl` mask provides anatomical anchor (hips, torso)
- `jeans` mask provides starting point
- Padding gives model "empty space" to grow the dress
- Combined context prevents identity drift

### The Mathematical Perfect Mask

Because LSD has DAAM spatial maps, it knows:
- ✅ Where person ends (1girl mask boundary)
- ✅ Where clothing sits (jeans mask)
- ✅ Where background begins (low attention)

**Result:** Auto-calculate the perfect mask size for any swap!

### Semantic Hierarchy System

Built-in parent-child relationships:

```python
# Clothing → Person
"jeans" → ["1girl", "1boy", "person", "legs"]
"dress" → ["1girl", "woman", "person"]

# Facial → Face → Person
"eyes" → ["face", "head", "1girl"]
"earrings" → ["ears", "face", "head", "1girl"]

# Body Parts → Person
"hands" → ["arms", "1girl", "person"]
```

**Auto-Detection Example:**

```
Draft: "1girl, jeans, hands"
Token Editor: {jeans:flowing red ballgown}

System detects:
1. "jeans" has parents → structural swap needed
2. Find parent "1girl" in prompt
3. Expand mask: Union(jeans_mask, 1girl_mask) + 128px padding
4. Refine with: "(1girl, wearing flowing red ballgown)"
5. Result: Perfect dress transition without face drift!
```

## Advanced Mask Generation

The original LSD used fixed 0.4 threshold for mask generation. This works but isn't data-driven.

### The Problem with Fixed Thresholds

Different heatmaps have different intensity distributions:
- **Eyes:** Bright, concentrated spot → needs low threshold
- **Background:** Diffuse, low-intensity glow → needs high threshold
- Fixed 0.4 fails both cases

### Solution: Otsu + Adaptive + Gradient Detection

**Four-stage self-tuning pipeline:**

$$\text{Otsu Threshold} \rightarrow \text{Adaptive Masking} \rightarrow \text{Gradient Detection} \rightarrow \text{Soft Edges}$$

#### Stage 1: Otsu's Method (Data-Driven Threshold)

Find the threshold that maximizes between-class variance—no tuning needed:

$$\sigma^2_B = w_0 w_1 (\mu_0 - \mu_1)^2$$

Works with ANY intensity distribution. Focused spots get low thresholds, diffuse regions get high thresholds automatically.

#### Stage 2: Adaptive Thresholding (Spatial Context)

Compare each pixel to neighborhood mean, not global threshold:

$$\text{Threshold}_{local} = \text{mean}_{neighborhood} - c$$

Handles varying intensity gradients within the same heatmap.

#### Stage 3: Gradient Detection (Natural Boundaries)

Find where intensity changes sharply—semantic boundaries:

$$\nabla I = \left|\frac{\partial I}{\partial x}\right| + \left|\frac{\partial I}{\partial y}\right|$$

Apply softer blending only at boundaries, keep interior crisp.

#### Stage 4: Density-Based Auto-Tuning

**Attention density** determines feature scale:
VRAM Optimized)

**Category:** `Luna/Workflow`

**Purpose:** Production sampler with pure VRAM optimization (no DAAM overhead)

**Inputs:**
- Standard KSampler inputs (seed, steps, cfg, sampler_name, scheduler, denoise)
- Optional `luna_pipe` for pipe-based workflows

**Outputs:**
- `LATENT` - Sampled latent (no DAAM_HEATMAPS)

**Behavior:**
- Always uses `torch.inference_mode()` for 60-70% memory reduction
- ~3.5GB VRAM (vs ~10GB stock KSampler)
- No DAAM collection - use Luna DAAM KSampler for analysis workflows

**Use Cases:**
- Production renders
- Batch generation
- Final upscaling
- Any workflow not requiring DAAM

### 3b. Luna DAAM KSampler 🔥

**Category:** `Luna/Workflow`

**Purpose:** Dedicated DAAM sampler for attention heatmap collection

**Inputs:**
- Standard KSampler inputs
- Optional `luna_pipe`
- `pos_tokens`, `neg_tokens` (TOKENS) - From Pipe Expander
- `daam_config` (DAAM_CONFIG, optional) - From Luna DAAM Config node

**Outputs:**
- `LATENT` - Sampled latent
- `DAAM_HEATMAPS` - Combined positive/negative heatmaps dict

**Behavior:**
- Collects attention heatmaps based on config settings
- No `inference_mode()` (DAAM needs forward hooks)
- ~6-7GB VRAM (gradient tracking + heatmap storage)

**Config Integration:**
If no config provided, uses `balanced` preset:
- Skip first 30% of timesteps
- Capture every timestep
- Late-step weighting enabled
- Collect positive only

**Model Support:**
- SDXL/SD1.5: Cross-attention tracking (CrossAttentionPatcher)
- Flux: Double-stream block attention (FluxAttentionPatcher)
- SD3: Joint block attention (SD3AttentionPatcher)) | Data-driven per heatmap |
| Spatial awareness | None (global) | Neighborhood context |
| Edge handling | Gaussian blur | Gradient-aware smoothstep |
| Tuning | Manual | Automatic (density-based) |
| Quality | Good | **Excellent** |

---

## The Compositional Workflow

### The Cached Execution Magic

ComfyUI's execution model reuses unchanged node outputs. This turns into a **feature** for LSD workflows:

```
RUN 1 (Full execution):
Draft KSampler (DAAM ON) → 20 seconds
  ↓ (cached!)
Token Editor → instant
  {eyes:{eye_colors}} → Roll: "blue eyes"
  ↓
Refine KSampler → 15 seconds

RUNS 2-∞ (Wildcard iterations):
Draft KSampler → CACHED! (0 seconds, same image/masks)
  ↓
Token Editor → instant
  {eyes:{eye_colors}} → Roll: "heterochromia eyes"
  ↓ (new conditioning only)
Refine KSampler → 15 seconds
  ↓
New variant! (only eyes changed)
```

**Performance:**
- Traditional workflow: 35 seconds × 10 variants = 350 seconds
- LSD workflow: 35s + (9 × 15s) = 170 seconds
- **51% faster!**

### The Compositional Gacha

Each semantic region is an independent slot:

```
Draft: "eyes, hair, dress"
  ↓
{eyes:{eye_colors}} → Roll 1: Blue eyes
                      Roll 2: Red eyes  
                      Roll 3: Heterochromia
                      Roll 4: Purple glowing
                      ↓ Keep purple!

{hair:{hairstyles}} → Roll 1: Long straight
                      Roll 2: Curly bob
                      Roll 3: Gradient waves
                      ↓ Keep gradient!

{dress:{clothing}}  → Roll 1: Red dress
                      Roll 2: Black coat
                      ↓ Keep black!

Final: Purple eyes + Gradient hair + Black coat
Iterations: 9 total (4+3+2)
Time: 35s + 8×15s = 155s
Traditional: 9×35s = 315s (51% savings!)
```

**Benefits:**
- ✅ Iterate on specific features without losing good results elsewhere
- ✅ Compositional iteration (mix and match)
- ✅ Wildcard integration per region
- ✅ Computational caching = near-instant regional re-rolls

---

## Node Reference
Config

**Category:** `Luna/Analysis`

**Purpose:** Configure DAAM optimization parameters for Luna DAAM KSampler

**Inputs:**
- `preset` - quality, balanced, fast, turbo, bidirectional, bidirectional_fast
- `use_custom` - Override preset with custom settings
- Custom parameters (if use_custom=True):
  - `skip_early_ratio`, `timestep_stride`, `late_weight_bias`
  - `gpu_resident`, `collect_positive`, `collect_negative`

**Outputs:**
- `D7AM_CONFIG` - Configuration for Luna DAAM KSampler

**Presets:**
- **quality:** Full capture, no optimizations, best accuracy
- **balanced:** 30% skip early, good tradeoff (default)
- **fast:** 40% skip + stride 2 = ~60% faster
- **turbo:** 50% skip + stride 3 = ~70% faster
- **bidirectional:** Balanced + capture both pos/neg
- **bidirectional_fast:** Fast + capture both pos/neg

### 5. Luna DAAM Analyzer Config

**Category:** `Luna/Analysis`

**Purpose:** Configure topological processing parameters for DAAM Analyzer

**Inputs:**
- `preset` - balanced, tight, loose, aggressive, advanced, legacy
- `use_custom` - Override preset with custom settings
- Cu8tom parameters (if use_custom=True):
  - Topological: `uplift_factor`, `padding_ratio`, `sea_level_percentile`, `high_tide_percentile`, `atoll_dilation`
  - Advanced: `c_offset`, `gradient_threshold_scale`
  - Legacy: `threshold`
  - Visualization: `colormap`, `normalize_per_word`

**Outputs:**
- `DAAM_ANALYZER_CONFIG` - Configuration for Luna DAAM Analyzer

**Processing Modes:**
- **topological** - Ocean-first island detection with uplift/beach/extension
  ├─ Ocean-first: Identifies low-attention "ocean", identifies islands
  ├─ Island expansion: Controlled padding lets model grow details
  ├─ Best for: Production LSD workflows, general image upscaling
  └─ Use when: You want semantic-aware region detection
- **advanced** - Otsu + Adaptive thresholding
  ├─ Threshold-first: Global Otsu + local adaptive thresholding
  ├─ Manual control: Tune offset, gradient sensitivity, softening
  ├─ Best for: Understanding attention, non-LSD use cases
  └─ Use when: You need maximum control or unusual content
- **legacy** - Simple threshold-based (0.4 cutoff)

### 6. Luna DAAM Analyzer 🔥

**Category:** `Luna/Analysis`

**Purpose:** Visualize attention heatmaps with topological processing comparison

**Inputs:**
- `clip` - CLIP model
- `daam_heatmaps` - Combined DAAM_HEATMAPS from Luna DAAM KSampler
- `attentions` - Comma-separated words/phrases to visualize
- `heatmap_mode` - positive, negative, or both
- `caption`, `alpha` - Overlay settings
- `images` - Generated images to overlay on
- `analyzer_config` (optional) - Processing parameters from Luna DAAM Analyzer Config
- Legacy inputs: `heatmaps`, `tokens` (for backward compatibility)

**Outputs:**
- `raw_overlays` - Raw DAAM heatmaps overlaid on images
- `processed_overlays` - Topologically processed heatmaps overlaid on images

**Attention Phrase Grouping:**
Commas split visualizations, multi-word phrases aggregate tokens:
```
attentions: "blue eyes, red hair, forest"
→ 3 separate overlays
  1. "blue eyes" (aggregates both tokens)
  2. "red hair" (aggregates both tokens)
  3. "forest" (single token)
```

**Processing Behavior:**
- Raw overlays show what DAAM captured directly
- Processed overlays show topological modifications
  - With metadata caption: bbox coordinates, padding distance
  - Visual: Same image context, different mask boundaries
- Compare side-by-side to tune analyzer_config parameters
**Changes:**
- Now outputs 15 elements (was 14)
- New output: `tokens` (15th element)

**Backward Compatibility:**
- Handles both 14-element (old) and 15-element (new) pipes
- Returns `None` for tokens when using old format

### 3. Luna KSampler (All 3 Variants - Enhanced)

**New Inputs:**
- `enable_daam` (BOOLEAN) - Toggle DAAM heatmap collection
- `pos_tokens` (TOKENS, optional) - From pipe or manual connection

**New Outputs:**
- `pos_heatmaps` (HEATMAP) - Positive attention heatmaps

**Behavior:**
- DAAM disabled: Uses `torch.inference_mode()` for memory optimization
- DAAM enabled: Collects attention maps (disables inference_mode)

**Model Support:**
- SDXL/SD1.5: Cross-attention tracking
- Flux: Double-stream block attention
- SD3: Joint block attention

### 4. Luna DAAM Analyzer 🔥

**Category:** `Luna/Analysis`

**Purpose:** Visualize attention heatmaps (debugging/testing)

**Inputs:**
- `clip`, `tokens`, `heatmaps`, `images`
- `attentions` - Comma-separated words to visualize
- `caption`, `alpha` - Overlay settings

**Outputs:**
- `IMAGE` - Heatmap overlay images (one per attention word per batch image)

### 5. Luna LSD Bridge 🌈

**Category:** `Luna/LSD`

**Purpose:** Convert DAAM heatmaps to composite area conditioning

**Inputs:**
- `clip`, `tokens`, `heatmaps`, `base_conditioning`
- `prompt` - Original booru prompt
- `target_width`, `target_height` - Target resolution
- `threshold`, `global_strength`, `min_weight`, `max_weight` - Conditioning parameters

**Outputs:**
- `composite_conditioning` - Spatially-aware, auto-weighted conditioning
- `debug_masks` - Colorized visualization of semantic regions

**Usage:**
Connect output to standard KSampler for testing spatial conditioning.

### 6. Luna LSD Token Editor ✏️

**Category:** `Luna/LSD`

**Purpose:** Interactive token replacement for compositional refinement

**Inputs:**
- `clip`, `tokens`, `heatmaps`, `base_conditioning`
- `original_prompt` - Draft prompt (booru tags)
- `token_replacements` - Enhancement syntax: `{tag:replacement}`
- Target resolution and conditioning parameters

**Outputs:**
- `enhanced_conditioning` - Modified conditioning with original spatial masks
- `modified_prompt` - Full prompt with replacements applied
- `debug_masks` - Visualization

**Syntax:**
```
{eyes:blue eyes with gold flecks}
{hair:gradient cyan and magenta hair with pink highlights}
{dress:{clothing} {materials}}  ← Wildcards supported!
```

**Key Feature:** Original tag's spatial mask + New tag's semantic conditioning

---

## Workflow Examples

### Basic LSD Workflow (Testing)

```
Luna Config Gateway → LUNA_PIPE
  ↓
Luna Pipe Expander → CLIP + TOKENS + POSITIVE
  ↓
Luna KSampler (enable_daam=True) → pos_heatmaps
  ↓
Luna LSD Bridge
  ← CLIP + TOKENS + heatmaps + POSITIVE + prompt
  → composite_conditioning + debug_masks
  ↓
Standard KSampler (denoise 0.7!)
  ← composite_conditioning
  → High-detail without hallucination! 🎯
```

### Compositional Wildcard Workflow

```
DRAFT PASS:
Config Gateway → "eyes, hair, dress, hands"
  ↓
KSampler (DAAM ON) → Draft image + heatmaps
  ↓ (CACHED for subsequent runs!)

ENHANCEMENT LOOP:
Pipe Expander → TOKENS + CLIP
  ↓
LSD Token Editor:
  {eyes:{eye_colors}}      ← Roll different eyes
  {hair:{hairstyles}}      ← Roll different hair
  {dress:{clothing}}       ← Roll different dress
  → enhanced_conditioning
  ↓
Refine KSampler (0.7 denoise)
  → Final variant

Change one line in Token Editor → Re-queue
  → New variant (15 seconds, not 35!)
```

### Tiled Upscaling (Future: LSD Refine Node)

```
Draft (1024px):
Config Gateway → "1girl, blue eyes, forest background"
  ↓
KSampler (DAAM ON) → heatmaps
  ↓
LSD Bridge → composite_conditioning

Refinement (4096px):
Upscale → 4K latent
  ↓
LSD Refine (tiled, 0.7 denoise)
  ← composite_conditioning
  → Each tile only "hears" its semantic tokens
  → "eyes" tile doesn't hallucinate extra faces
  → "background" tile doesn't hallucinate dragons
```

---

## Technical Details

### DAAM Heatmap Collection Process

1. KSampler detects model type (SDXL/Flux/SD3)
2. Instantiates appropriate patcher (CrossAttentionPatcher/FluxAttentionPatcher/SD3AttentionPatcher)
3. Patches model's attention layers to capture cross-attention probabilities
4. Runs sampling (without inference_mode when DAAM enabled)
5. Collects heatmaps across all denoising timesteps
6. Unpatches model and returns heatmaps

### Spatial Mask Generation

```python
def generate_pixel_mask(heatmap, target_width, target_height, threshold):
    # 1. Normalize 64×64 heatmap to 0-1
    normalized = (heatmap - min) / (max - min)
    
    # 2. Upscale to target resolution (bilinear)
    pixel_mask = F.interpolate(normalized, (target_height, target_width))
    
    # 3. Gaussian feathering (prevents seam artifacts)
    sigma = target_width / 128  # ~32px at 4K
    pixel_mask = gaussian_blur(pixel_mask, sigma)
    
    # 4. Soft threshold (sigmoid for smooth transition)
    pixel_mask = sigmoid((pixel_mask - threshold) * 10)
    
    return pixel_mask  # [target_height, target_width], values 0-1
```

### Attention Density Calculation

```python
def calculate_attention_density(heatmap, threshold):
    total_heat = heatmap.sum()
    area = (heatmap > threshold).sum()
    density = total_heat / (area + 1e-8)
    return density

def density_to_weight(density, min_weight=0.3, max_weight=1.0):
    log_density = log1p(density)
    normalized = (log_density - log_min) / (log_max - log_min)
    weight = min_weight + normalized * (max_weight - min_weight)
    return weight
```

High heat + small area = high density = high weight (eyes, jewelry)  
Low heat + large area = low density = low weight (background, fog)

### Token Shift Problem (SOLVED)

**The Problem:**

When replacing tokens, token indices change:

```
Draft:  "1girl, blue eyes, hat"
Tokens: [1girl=ID1, blue=ID2, eyes=ID3, hat=ID4]
DAAM:   {ID2: heatmap_eyes, ID3: heatmap_eyes_part2}

Refine: "1girl, glowing red mechanical eyes, hat"
Tokens: [1girl=ID1, glowing=ID2, red=ID3, mechanical=ID4, eyes=ID5, hat=ID6]
❌ ID2 now maps to "glowing" not "blue"!
```

If we stored heatmaps by token ID, the mapping would break.

**The Solution (Already Implemented):**

We map **TEXT LABELS → SPATIAL MASKS**, not token IDs:

```python
# During DRAFT (DAAM collection):
# PromptAnalyzer.calc_word_indices("blue eyes") → [ID2, ID3]
# Aggregate both token heatmaps → single spatial map
heatmap_dict = {
    "blue eyes": aggregated_spatial_mask  # Keyed by TEXT!
}

# During REFINE (Token Editor):
# Original tag "blue eyes" → retrieve spatial mask
# New text "glowing red mechanical eyes" → encode to conditioning
# Apply new conditioning to OLD spatial mask
composite = ConditioningSetArea().append(
    conditioning=encode("glowing red mechanical eyes"),
    mask=heatmap_dict["blue eyes"]  # Same spatial location!
)
```

**Why This Works:**

1. **Text-based keys** - Heatmaps indexed by semantic tags ("blue eyes"), not token IDs
2. **Phrase aggregation** - Multi-token phrases collapsed to single spatial map during DAAM
3. **Spatial persistence** - Token Editor retrieves mask by original tag text
4. **Token-agnostic** - New conditioning can have any number of tokens, mask stays fixed

**Result:** Token count can change arbitrarily during refinement without breaking spatial mappings. ✅

### Composite Conditioning Structure

```
┌─────────────────────────────────────────────────────────────────────┐
│                    COMPOSITE_CONDITIONING                           │
├─────────────────────────────────────────────────────────────────────┤
│  Layer 0 (Bottom): Global Prompt                                    │
│    - Strength: 0.4                                                  │
│    - Coverage: Full canvas                                          │
│    - Purpose: Color temperature, style coherence, "vibe"            │
├─────────────────────────────────────────────────────────────────────┤
│  Layer 1: "background, forest"                                      │
│    - Strength: 0.35 (auto: low density)                             │
│    - Mask: [4096×4096 values 0-1]                                   │
├─────────────────────────────────────────────────────────────────────┤
│  Layer 2: "1girl, dress"                                            │
│    - Strength: 0.55 (auto: medium density)                          │
│    - Mask: [4096×4096 values 0-1]                                   │
├─────────────────────────────────────────────────────────────────────┤
│  Layer 3: "face"                                                    │
│    - Strength: 0.75 (auto: high density)                            │
│    - Mask: [4096×4096 values 0-1]                                   │
├─────────────────────────────────────────────────────────────────────┤
│  Layer 4 (Top): "eyes, blue eyes"                                   │
│    - Strength: 1.0 (auto: highest density)                          │
│    - Mask: [4096×4096 values 0-1]                                   │
└─────────────────────────────────────────────────────────────────────┘

When sampler processes a tile at eye coordinates:
  → Tile intersects Layers 0, 2, 3, 4
  → Sampler blends: global + dress + face + eyes
  → Model understands spatial hierarchy
  → High denoise generates detail without hallucination
```

---

## Future Roadmap

### Phase 3: LSD Target (Surgical Region Refinement)

**Purpose:** Crop and refine specific regions at native resolution

**Features:**
- Island detection (4 disconnected "hands" → 4 separate crops)
- Square crop normalization with padding (To enable batching)
- Masked conditioning application
- Upscale small crop to 1k native model resolution (Bicubic or Lanczos)
- Native resolution refinement
- Downscale to original crop size (Supersampling)
---

## Changelog

### v3.0 (January 2026) - Bidirectional DAAM + Optimization System

**Major Changes:**

1. **Node Split for Clarity**
   - **Luna KSampler** → Pure VRAM optimization (no DAAM)
   - **Luna DAAM KSampler** → NEW dedicated DAAM sampler
   - **Reason:** Clean separation of production vs analysis workflows

2. **Bidirectional DAAM**
   - Capture both positive and negative attention heatmaps
   - `DAAM_HEATMAPS` format: `{positive, negative, pos_tokens, neg_tokens, config}`
   - Ocean-first processing: Islands (positive) + Trenches (negative)
   - **Reason:** Enable spatial suppression in addition to enhancement

3. **DAAM Optimization System**
   - **Luna DAAM Config** node with 6 presets
   - Skip early timesteps (first 30-50% are noisy)
   - Timestep stride (capture every Nth step)
   - Late-step weighting (final steps have better signal)
   - **Result:** 60% faster DAAM collection with minimal quality loss

4. **Analyzer Enhancement**
   - Two outputs: `raw_overlays`, `processed_overlays`
   - Both show heatmaps ON images for context
   - Processed overlays show topological modifications
   - **Reason:** Visual debugging of parameter effects

5. **Analyzer Config System**
   - **Luna DAAM Analyzer Config** node with 6 presets
   - Expose topological parameters: uplift, padding, percentiles
   - Live parameter tuning with instant visual feedback
   - **Reason:** Research and per-workflow optimization

6. **Pipe Format Extension**
   - LUNA_PIPE: 14 → 17 elements
   - Added: `pos_tokens`, `neg_tokens`, `pos_prompt`, `neg_prompt`
   - Pipe Expander updated to match
   - **Reason:** Support bidirectional DAAM and processed prompts

**Technical Improvements:**

- **CLIP Parentheses Clarification:** Weights only, not grouping operators
- **Comma Behavior:** Soft attention boundaries, not hard separators
- **Phrase Aggregation:** Multi-word phrases aggregate token heatmaps
- **Overlap Detection & Merging:** ✅ IMPLEMENTED - Automatically merges tags with 90%+ spatial overlap into single layers (e.g., "blue eyes" + "eyes" + "detailed eyes" → one merged layer)

**Backward Compatibility:**

- Pipe Expander handles both 14-element (old) and 17-element (new) pipes
- DAAM Analyzer accepts legacy `heatmaps`/`tokens` inputs
- All config parameters have sensible defaults

### v2.4 (Previous) - Advanced Mask Generation

**Deprecated Implementation (moved to legacy mode):**

Old Luna KSampler behavior:
- Had `enable_daam` toggle
- Returned `(LATENT, DAAM_HEATMAPS)` always
- **Issue:** Unclear when DAAM overhead was present
- **Solution:** Split into two dedicated nodes

Old DAAM format:
- Single `HEATMAP` output for positive only
- Separate `tokens` input
- **Issue:** No bidirectional support
- **Solution:** Combined `DAAM_HEATMAPS` dict

Old Analyzer output:
- Single `IMAGE` output with overlays
- No comparison capability
- **Issue:** Couldn't see processing effects
- **Solution:** Two outputs (raw + processed)

Fixed topological parameters:
- `uplift_factor=1.5`, `padding_ratio=0.2` hardcoded
- **Issue:** No tuning for different use cases
- **Solution:** Config node with presets + custom mode

---

- Paste back with feathering

### Phase 4: LSD Refine (Smart Tiled Upscaling)

**Purpose:** High-denoise tiled refinement with semantic awareness

**Features:**
- Full/Tiled toggle
- Smart tile calculation (dynamic sizing, 8px snapping)
- Chess batch logic with overlap blending
- Composite conditioning integration

**Smart Tile Calculation:**
```python
# Given: 3840×2160 image, max_dimension=1024, overlap=64
# Result: 4×3 grid with 1008×768 tiles (efficient, not forced to 1024!)

def calculate_optimal_tiles(width, height, max_dim, overlap):
    cols = ceil(width / max_dim)
    rows = ceil(height / max_dim)
    
    tile_width = (width + overlap * (cols - 1)) / cols
    tile_height = (height + overlap * (rows - 1)) / rows
    
    # Snap to 8px for VAE encoding
    tile_width = ((tile_width + 7) // 8) * 8
    tile_height = ((tile_height + 7) // 8) * 8
    
    return tile_width, tile_height, cols, rows
```

---

## Troubleshooting

**Q: Heatmaps are None/empty**
- Ensure `pos_tokens` is connected from Pipe Expander
- Verify `enable_daam=True` on Luna KSampler
- Check that TOKENS output is connected properly

**Q: Out of memory errors with DAAM enabled**
- DAAM requires ~2-4GB extra VRAM due to gradient tracking
- Reduce image resolution or batch size
- Use DAAM only for draft pass, disable for refinement

**Q: No overlay appears for my word in Token Editor**
- Word may not exist in original draft prompt
- Original tag must match exactly (e.g., "eyes" not "eye")
- Check debug masks output to verify spatial coverage

**Q: Compositional edits affecting wrong regions**
- Verify original tag names match between draft and Token Editor
- Check debug masks to ensure masks are correctly mapped
- Increase threshold if masks are too broad

**Q: Cache not working (draft re-executes every time)**
- Ensure DAAM toggle stays unchanged between runs
- Don't modify any inputs to Draft KSampler
- Only modify Token Editor replacements

---

## License Attribution

DAAM integration uses code from [comfyui-daam](https://github.com/nisaruj/comfyui-daam) under the MIT License.

Full attribution in `DAAM_LICENSE.txt`.

---

## Summary

Luna LSD transforms image generation from monolithic to compositional:

**Traditional:** All-or-nothing. Re-generate entire image for any change.

**LSD:** Layer-based. Draft layout → Enhance regions independently → Iterate infinitely.

**Key Innovations:**
1. **Introspective Semantic Awareness** - Model's own attention as spatial truth
2. **Automatic Weighting** - Attention density = conditioning strength
3. **Booru Tag Optimization** - Pre-segmented semantic atoms
4. **Compositional Iteration** - Independent region refinement
5. **Computational Caching** - ComfyUI's execution model as feature
6. **Wildcard Integration** - Per-region randomization

**Result:** Photoshop-like layer control for diffusion models. Each semantic region is an editable, re-rollable layer.

---

*Luna Collection v2.4 - Compositional Semantic Detailing* 🌈✨

**LSD: Because your dragon's toe shouldn't hallucinate a whole dragon.** 🐉
