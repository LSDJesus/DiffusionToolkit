# ONNX Runtime GPU Setup

The embedding system uses ONNX Runtime GPU for 2-3x faster inference compared to CPU.

## Requirements

1. **NVIDIA GPU** with CUDA Compute Capability 6.0+ (Pascal or newer)
   - RTX 5090, RTX 3080 Ti, or similar
   
2. **CUDA 12.x Runtime**
   - Download from: https://developer.nvidia.com/cuda-downloads
   - Toolkit includes cuDNN 9.x
   
3. **ONNX Runtime GPU Provider DLLs**
   - Automatically downloaded with NuGet package
   - Location: `runtimes/win-x64/native/`

## Setup Steps

### 1. Install CUDA Toolkit 12.x

```powershell
# Check current NVIDIA driver
nvidia-smi

# Install CUDA Toolkit (includes cuDNN 9.x)
# Download installer from NVIDIA website
# Or use Chocolatey:
choco install cuda --version=12.6.0
```

### 2. Verify Installation

```powershell
# Check CUDA version
nvcc --version

# Verify GPU detection
nvidia-smi
```

### 3. Copy CUDA DLLs (if needed)

The ONNX Runtime GPU package includes required DLLs in:
```
Diffusion.Embeddings/bin/Release/net10.0/runtimes/win-x64/native/
```

Required files:
- `onnxruntime_providers_cuda.dll`
- `onnxruntime_providers_shared.dll`

These are automatically copied during build, but if you see LoadLibrary errors:

```powershell
# Copy DLLs to application directory
Copy-Item "runtimes/win-x64/native/*.dll" -Destination "./" -Force
```

### 4. Test GPU Acceleration

```powershell
cd TestEmbeddings
dotnet run --configuration Release
```

Expected output:
```
✓ Service initialized with GPU acceleration

=== Test 1: Text Embeddings ===
✓ Generated in ~200ms
  BGE:    1024D
  CLIP-L: 768D
  CLIP-G: 1280D
```

### 5. Monitor GPU Utilization

```powershell
# Real-time GPU monitoring
nvidia-smi -l 1

# Expected during embedding generation:
# GPU 0 (RTX 5090): 30-50% utilization (BGE + CLIP Vision)
# GPU 1 (RTX 3080 Ti): 30-50% utilization (CLIP-L + CLIP-G)
```

## Architecture

### Dual-GPU Load Balancing

- **GPU 0 (Primary)**: BGE-large-en-v1.5 (text) + CLIP-ViT-H (image)
- **GPU 1 (Secondary)**: CLIP-L (text) + CLIP-G (text)

### Parallel Execution

`EmbeddingService` uses `Task.WhenAll()` to run all encoders simultaneously:
```csharp
var tasks = new[]
{
    _bgeEncoder.EncodeAsync(prompt),          // GPU 0
    _clipLEncoder.EncodeAsync(prompt),        // GPU 1
    _clipGEncoder.EncodeAsync(prompt),        // GPU 1
    _clipVisionEncoder.EncodeAsync(imagePath) // GPU 0
};

await Task.WhenAll(tasks);
```

## Performance

### Expected Throughput

- **Single image**: ~200-500ms (all 5 embeddings)
- **Batch (32 images)**: ~5-10 seconds
- **200K images**: ~1.4 hours (vs 4.4 hours with Python)

### Optimization Tips

1. **Batch Processing**: Use `GenerateBatchEmbeddingsAsync()` with 32-64 images
2. **Text Deduplication**: Reuse embeddings for identical prompts/negatives
3. **GPU Memory**: Each model loads ~1-4GB VRAM
   - Total: ~6GB on GPU 0, ~2GB on GPU 1

## Troubleshooting

### Error: "LoadLibrary failed with error 126"

**Cause**: Missing CUDA runtime or cuDNN DLLs

**Solution**:
1. Install CUDA Toolkit 12.x
2. Verify `cuda12.dll` in `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x\bin\`
3. Add to PATH if not present:
   ```powershell
   $env:PATH += ";C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6\bin"
   ```

### Error: "CUDA_PATH environment variable is not set"

```powershell
[System.Environment]::SetEnvironmentVariable("CUDA_PATH", "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6", "Machine")
```

### Error: "cudaErrorInvalidResourceHandle"

This error occurs during ONNX Runtime GPU initialization on certain GPU architectures:

**Root Cause**: CUDA compute capability compatibility issue
- ONNX Runtime 1.20.1 + CUDA 12.9 has issues with older compute capabilities
- **Confirmed Issue**: RTX 3080 Ti (compute 8.6, Ampere) fails with this error
- **Confirmed Working**: RTX 5090 (compute 12.0, Blackwell) works perfectly

**Check Your GPU Compute Capability**:
```powershell
nvidia-smi --query-gpu=name,compute_cap --format=csv
```

**Solutions**:
1. **Use a compatible GPU** (compute 9.0+ recommended for CUDA 12.x)
2. **Run all models on one GPU** (if you have a compatible GPU):
   ```csharp
   var config = new EmbeddingConfig
   {
       BgeGpuDevice = 0,        // Use GPU 0 for all
       ClipLGpuDevice = 0,
       ClipGGpuDevice = 0,
       ClipVisionGpuDevice = 0
   };
   ```
3. **Try older CUDA version**: Install CUDA 11.8 + ONNX Runtime 1.19.x (may have better Ampere support)
4. **Use CPU provider as fallback**: Set `sessionOptions.AppendExecutionProvider_CPU()` only

### Out of Memory (OOM)

Reduce batch size or use single GPU:
```csharp
var config = new EmbeddingConfig
{
    // ... paths ...
    BgeGpuDevice = 0,
    ClipLGpuDevice = 0,  // Same GPU
    ClipGGpuDevice = 0,  // Same GPU
    ClipVisionGpuDevice = 0,
    TextBatchSize = 32,   // Reduced from 64
    ImageBatchSize = 16   // Reduced from 32
};
```

### Low GPU Utilization

Check batch sizes - too small batches don't saturate GPU:
```csharp
config.TextBatchSize = 128;   // Increase
config.ImageBatchSize = 64;   // Increase
```

## CPU Fallback

If GPU setup fails, encoders will automatically fall back to CPU:
- Performance: ~3-5x slower than GPU
- No CUDA installation required
- Suitable for testing but not production at scale

## Next Steps

After successful GPU setup:
1. Run TestEmbeddings to verify all encoders work
2. Integrate EmbeddingService into main application
3. Build bulk import pipeline for 200K images
4. Monitor GPU utilization during production run
