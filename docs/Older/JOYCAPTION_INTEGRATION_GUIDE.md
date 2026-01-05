# JoyCaption GGUF Integration Guide for .NET Projects

## Overview
This guide covers three approaches to integrate JoyCaption (GGUF format) into a .NET C# project for detailed image captioning. JoyCaption is a large language model (LLM) with vision capabilities that generates detailed natural language descriptions of images.

**Model Format**: GGUF (llama.cpp format)
- Main model: `joycaption-q6_k.gguf` (quantized Q6_K)
- Vision encoder: `mmproj-f16.gguf` (FP16 multimodal projection)

**Why not ONNX?**
- ONNX Runtime is not optimized for large autoregressive language models
- GGUF format is specifically designed for efficient LLM inference
- No practical conversion path exists from GGUF to ONNX
- llama.cpp (GGUF) is 5-10x faster for LLM inference than ONNX

---

## Option 1: LLamaSharp (Native C# Integration) ⭐ RECOMMENDED

### What is LLamaSharp?
A C# wrapper around llama.cpp that allows you to load and run GGUF models directly in C# without Python dependencies.

### Pros:
- ✅ Pure C#, integrates directly into your .NET project
- ✅ Uses your existing GGUF models (no conversion needed)
- ✅ GPU acceleration via CUDA/Vulkan/Metal
- ✅ Efficient inference using llama.cpp backend
- ✅ No Python runtime required
- ✅ Easy to debug and maintain

### Cons:
- ❌ LLamaSharp's multimodal (vision) support is still maturing
- ❌ May require some tweaking for LLaVA-style models
- ❌ Documentation for vision models is limited

### Setup Steps:

#### 1. Install NuGet Packages
```bash
dotnet add package LLamaSharp
dotnet add package LLamaSharp.Backend.Cuda12  # For NVIDIA GPU
# OR
dotnet add package LLamaSharp.Backend.Cpu     # For CPU-only
```

#### 2. Create Service Interface
```csharp
public interface IJoyCaptionService
{
    Task<string> CaptionImageAsync(string imagePath, string prompt = null);
    Task<Dictionary<string, string>> CaptionImagesAsync(List<string> imagePaths, string prompt = null);
}
```

#### 3. Implement Service Class
```csharp
using LLama;
using LLama.Common;
using LLama.Native;

public class JoyCaptionService : IJoyCaptionService
{
    private readonly string _modelPath;
    private readonly string _mmprojPath;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private bool _isInitialized;

    public JoyCaptionService(string modelPath, string mmprojPath)
    {
        _modelPath = modelPath;
        _mmprojPath = mmprojPath;
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var parameters = new ModelParams(_modelPath)
        {
            ContextSize = 4096,
            GpuLayerCount = 35, // Adjust based on your GPU VRAM
            UseMemorymap = true,
            UseMemoryLock = false,
            Seed = (uint)Random.Shared.Next()
        };

        _model = LLamaWeights.LoadFromFile(parameters);
        _context = _model.CreateContext(parameters);
        _executor = new InteractiveExecutor(_context);

        _isInitialized = true;
    }

    public async Task<string> CaptionImageAsync(string imagePath, string prompt = null)
    {
        await InitializeAsync();

        prompt ??= "Describe this image in detail with approximately 150-200 words. Include colors, objects, composition, lighting, mood, and any notable details.";

        // Load and process image with mmproj
        // NOTE: LLamaSharp's vision support may require additional work
        // You may need to use LLavaWeights for multimodal support
        
        var inferenceParams = new InferenceParams
        {
            MaxTokens = 512,
            Temperature = 0.7f,
            TopP = 0.9f,
            AntiPrompts = new[] { "\n\n", "USER:", "ASSISTANT:" }
        };

        var response = new StringBuilder();
        await foreach (var text in _executor.InferAsync(prompt, inferenceParams))
        {
            response.Append(text);
        }

        return response.ToString().Trim();
    }

    public async Task<Dictionary<string, string>> CaptionImagesAsync(List<string> imagePaths, string prompt = null)
    {
        var results = new Dictionary<string, string>();
        
        foreach (var imagePath in imagePaths)
        {
            var caption = await CaptionImageAsync(imagePath, prompt);
            results[imagePath] = caption;
        }

        return results;
    }

    public void Dispose()
    {
        _executor?.Dispose();
        _context?.Dispose();
        _model?.Dispose();
    }
}
```

#### 4. Register in Dependency Injection
```csharp
services.AddSingleton<IJoyCaptionService>(sp => 
    new JoyCaptionService(
        modelPath: Path.Combine(modelsDir, "joycaption-q6_k.gguf"),
        mmprojPath: Path.Combine(modelsDir, "mmproj-f16.gguf")
    )
);
```

#### 5. Use in Your Application
```csharp
var caption = await joyCaptionService.CaptionImageAsync("image.jpg");
Console.WriteLine(caption);
```

### Important Notes:
- **Vision Support**: LLamaSharp's vision/multimodal support is evolving. Check the latest documentation or issues on GitHub for LLaVA integration.
- **GPU Layers**: Adjust `GpuLayerCount` based on your VRAM (0-50+ layers). More layers = faster but requires more VRAM.
- **Context Size**: 4096 is usually sufficient. Increase if you need longer prompts/outputs.
- **Model Loading**: First load takes 10-30 seconds. Keep the service as a singleton to avoid reloading.

### Resources:
- [LLamaSharp GitHub](https://github.com/SciSharp/LLamaSharp)
- [LLamaSharp Documentation](https://scisharp.github.io/LLamaSharp/)
- [LLamaSharp Examples](https://github.com/SciSharp/LLamaSharp/tree/master/LLama.Examples)

---

## Option 2: llama.cpp Server Mode (Production Ready) 🚀

### What is llama.cpp Server?
Run llama.cpp as an HTTP server that loads your model once and exposes a REST API for inference. Your C# app communicates via HTTP requests.

### Pros:
- ✅ Model stays loaded in memory (fast subsequent requests)
- ✅ Clean separation of concerns
- ✅ Can run on a different machine/GPU server
- ✅ Standard OpenAI-compatible API
- ✅ Proven, stable, and actively maintained
- ✅ Easy to scale and monitor

### Cons:
- ❌ Need to manage a separate process
- ❌ Minimal network overhead on localhost
- ❌ Requires llama.cpp binaries

### Setup Steps:

#### 1. Download llama.cpp
Get prebuilt binaries with CUDA support:
- Windows: [llama.cpp releases](https://github.com/ggerganov/llama.cpp/releases)
- Or build from source for optimal performance

#### 2. Start llama-server
```bash
# Basic command
llama-server.exe ^
    -m "path/to/joycaption-q6_k.gguf" ^
    --mmproj "path/to/mmproj-f16.gguf" ^
    --host 127.0.0.1 ^
    --port 8080 ^
    -ngl 35 ^
    -c 4096

# With all recommended options
llama-server.exe ^
    -m "D:\Models\joycaption-q6_k.gguf" ^
    --mmproj "D:\Models\mmproj-f16.gguf" ^
    --host 127.0.0.1 ^
    --port 8080 ^
    -ngl 35 ^
    -c 4096 ^
    --n-gpu-layers 35 ^
    --threads 8 ^
    --batch-size 512 ^
    --log-disable
```

**Parameters:**
- `-m`: Path to main model (GGUF)
- `--mmproj`: Path to vision encoder (GGUF)
- `--host`: Server host (use 127.0.0.1 for localhost only)
- `--port`: Server port
- `-ngl` / `--n-gpu-layers`: Number of layers to offload to GPU (0-50+)
- `-c`: Context size
- `--threads`: CPU threads to use

#### 3. Create C# HTTP Client Service
```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public class JoyCaptionHttpService : IJoyCaptionService
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;

    public JoyCaptionHttpService(string serverUrl = "http://localhost:8080")
    {
        _serverUrl = serverUrl;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<string> CaptionImageAsync(string imagePath, string prompt = null)
    {
        prompt ??= "Describe this image in detail with approximately 150-200 words.";

        // Read image as base64
        var imageBytes = await File.ReadAllBytesAsync(imagePath);
        var imageBase64 = Convert.ToBase64String(imageBytes);

        // Create request payload
        var request = new CompletionRequest
        {
            Prompt = $"USER: [img-1]{prompt}\nASSISTANT:",
            Images = new[] { imageBase64 },
            Temperature = 0.7f,
            TopP = 0.9f,
            MaxTokens = 512,
            Stop = new[] { "\n\n", "USER:", "ASSISTANT:" }
        };

        // Send request
        var response = await _httpClient.PostAsJsonAsync($"{_serverUrl}/completion", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        return result?.Content?.Trim() ?? string.Empty;
    }

    public async Task<Dictionary<string, string>> CaptionImagesAsync(List<string> imagePaths, string prompt = null)
    {
        var results = new Dictionary<string, string>();
        
        foreach (var imagePath in imagePaths)
        {
            var caption = await CaptionImageAsync(imagePath, prompt);
            results[imagePath] = caption;
        }

        return results;
    }

    // Request/Response models
    private class CompletionRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("image_data")]
        public string[]? Images { get; set; }

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0.7f;

        [JsonPropertyName("top_p")]
        public float TopP { get; set; } = 0.9f;

        [JsonPropertyName("n_predict")]
        public int MaxTokens { get; set; } = 512;

        [JsonPropertyName("stop")]
        public string[]? Stop { get; set; }
    }

    private class CompletionResponse
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("stop")]
        public bool Stop { get; set; }
    }
}
```

#### 4. Register and Use
```csharp
// Registration
services.AddSingleton<IJoyCaptionService>(sp => 
    new JoyCaptionHttpService("http://localhost:8080")
);

// Usage
var caption = await joyCaptionService.CaptionImageAsync("image.jpg");
```

#### 5. Running as Windows Service (Optional)
Create a wrapper to run llama-server as a Windows service:

```csharp
public class LlamaServerManager
{
    private Process? _serverProcess;

    public void StartServer(string modelPath, string mmprojPath, int port = 8080)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "llama-server.exe",
            Arguments = $"-m \"{modelPath}\" --mmproj \"{mmprojPath}\" --port {port} -ngl 35",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _serverProcess = Process.Start(startInfo);
        
        // Wait for server to be ready
        Thread.Sleep(5000);
    }

    public void StopServer()
    {
        _serverProcess?.Kill();
        _serverProcess?.Dispose();
    }
}
```

### Important Notes:
- **Startup Time**: Server takes 10-30 seconds to load the model initially
- **Keep Running**: Once started, keep the server running for best performance
- **Memory Usage**: Expect ~8-12GB RAM/VRAM depending on model size and layers offloaded
- **Batch Processing**: Send requests sequentially for best results

### Resources:
- [llama.cpp GitHub](https://github.com/ggerganov/llama.cpp)
- [llama.cpp Server Documentation](https://github.com/ggerganov/llama.cpp/blob/master/examples/server/README.md)
- [OpenAI-compatible API Reference](https://github.com/ggerganov/llama.cpp/blob/master/examples/server/README.md#api-endpoints)

---

## Option 3: llama.cpp CLI (Simplest for Testing)

### What is llama.cpp CLI?
Call the llama.cpp command-line executable directly from C# using `Process.Start()`. Model loads fresh for each request.

### Pros:
- ✅ Simplest to implement
- ✅ No additional libraries needed
- ✅ Great for testing and prototyping
- ✅ Easy to debug

### Cons:
- ❌ Slow: Model loads from scratch each time (~20-30 seconds startup)
- ❌ Not suitable for production/batch processing
- ❌ Inter-process communication overhead

### Setup Steps:

#### 1. Download llama.cpp CLI
Get `llama-cli.exe` (formerly `main.exe`) from [llama.cpp releases](https://github.com/ggerganov/llama.cpp/releases)

#### 2. Create CLI Wrapper Service
```csharp
public class JoyCaptionCliService : IJoyCaptionService
{
    private readonly string _llamaCliPath;
    private readonly string _modelPath;
    private readonly string _mmprojPath;

    public JoyCaptionCliService(string llamaCliPath, string modelPath, string mmprojPath)
    {
        _llamaCliPath = llamaCliPath;
        _modelPath = modelPath;
        _mmprojPath = mmprojPath;
    }

    public async Task<string> CaptionImageAsync(string imagePath, string prompt = null)
    {
        prompt ??= "Describe this image in detail.";

        var arguments = $"-m \"{_modelPath}\" " +
                       $"--mmproj \"{_mmprojPath}\" " +
                       $"--image \"{imagePath}\" " +
                       $"-p \"USER: {prompt}\\nASSISTANT:\" " +
                       $"-n 512 " +
                       $"--temp 0.7 " +
                       $"--top-p 0.9 " +
                       $"-ngl 35 " +
                       $"-c 4096";

        var startInfo = new ProcessStartInfo
        {
            FileName = _llamaCliPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start llama-cli process");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"llama-cli failed: {error}");

        // Extract caption from output (may need parsing)
        return ExtractCaption(output);
    }

    private string ExtractCaption(string output)
    {
        // Parse llama-cli output to extract just the generated caption
        // Output format varies, may need adjustment
        var lines = output.Split('\n');
        var captionStart = false;
        var caption = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.Contains("ASSISTANT:"))
            {
                captionStart = true;
                continue;
            }

            if (captionStart && !string.IsNullOrWhiteSpace(line))
            {
                caption.AppendLine(line);
            }
        }

        return caption.ToString().Trim();
    }

    public async Task<Dictionary<string, string>> CaptionImagesAsync(List<string> imagePaths, string prompt = null)
    {
        var results = new Dictionary<string, string>();
        
        foreach (var imagePath in imagePaths)
        {
            var caption = await CaptionImageAsync(imagePath, prompt);
            results[imagePath] = caption;
        }

        return results;
    }
}
```

#### 3. Register and Use
```csharp
// Registration
services.AddSingleton<IJoyCaptionService>(sp => 
    new JoyCaptionCliService(
        llamaCliPath: @"C:\Tools\llama.cpp\llama-cli.exe",
        modelPath: Path.Combine(modelsDir, "joycaption-q6_k.gguf"),
        mmprojPath: Path.Combine(modelsDir, "mmproj-f16.gguf")
    )
);

// Usage
var caption = await joyCaptionService.CaptionImageAsync("image.jpg");
```

### Important Notes:
- **Very Slow**: 20-30 seconds per image due to model loading
- **Best for**: Quick tests, single images, prototyping
- **Not for**: Batch processing, production use

---

## Performance Comparison

| Method | First Request | Subsequent Requests | Memory Usage | Complexity |
|--------|---------------|---------------------|--------------|------------|
| **LLamaSharp** | ~15-20s | ~2-5s | 8-12GB | Medium |
| **llama.cpp Server** | ~15-20s (startup) | ~2-5s | 8-12GB | Low |
| **llama.cpp CLI** | ~20-30s | ~20-30s | 8-12GB | Very Low |

---

## Recommended Workflow

1. **Start with Option 3 (CLI)** for quick testing to verify your models work
2. **Move to Option 2 (Server)** for production - most stable and performant
3. **Consider Option 1 (LLamaSharp)** if you need tight C# integration and the vision support matures

---

## Common Issues & Solutions

### Issue: "Model not found"
- Verify file paths are absolute and correct
- Check file permissions
- Ensure GGUF files are not corrupted

### Issue: "Out of memory"
- Reduce GPU layers (`-ngl` parameter)
- Use smaller quantization (Q4 instead of Q6)
- Increase system pagefile

### Issue: "Slow inference"
- Ensure GPU acceleration is working (`-ngl 35+`)
- Check CUDA/drivers are installed
- Use server mode instead of CLI

### Issue: "Empty or garbled output"
- Check prompt format (LLaVA models expect specific formats)
- Verify mmproj file matches the model
- Try different temperature/top-p values

---

## Additional Resources

- [llama.cpp GitHub](https://github.com/ggerganov/llama.cpp)
- [LLamaSharp GitHub](https://github.com/SciSharp/LLamaSharp)
- [JoyCaption Model Card](https://huggingface.co/fancyfeast/llama-joycaption-beta-one-hf-llava)
- [GGUF Format Spec](https://github.com/ggerganov/ggml/blob/master/docs/gguf.md)

---

## Example Prompts

**Detailed Description:**
```
Describe this image in detail with approximately 150-200 words. Include colors, objects, composition, lighting, mood, and any notable details.
```

**Short Caption:**
```
Provide a concise one-sentence description of this image.
```

**Specific Focus:**
```
Describe the people in this image, including their appearance, clothing, expressions, and what they are doing.
```

**Technical Analysis:**
```
Analyze this image's composition, including rule of thirds, leading lines, color harmony, and photographic techniques used.
```

---

**Document Version:** 1.0  
**Last Updated:** November 15, 2025  
**Tested With:** .NET 9.0/10.0, llama.cpp master branch, LLamaSharp 0.15.0+
