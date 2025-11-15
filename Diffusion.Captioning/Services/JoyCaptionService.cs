using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Diffusion.Captioning.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Diffusion.Captioning.Services;

/// <summary>
/// Service for generating detailed image captions using JoyCaption (LLaVA-style model)
/// via LLamaSharp with GGUF format
/// </summary>
public class JoyCaptionService : IDisposable
{
    private readonly string _modelPath;
    private readonly string _mmProjPath;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private bool _isInitialized;
    private readonly object _lock = new();

    // Default prompts
    public const string DEFAULT_DETAILED_PROMPT = "Describe this image in detail with approximately 150-200 words. Include colors, objects, composition, lighting, mood, and any notable details.";
    public const string DEFAULT_SHORT_PROMPT = "Provide a concise one-sentence description of this image.";
    public const string DEFAULT_TECHNICAL_PROMPT = "Analyze this image's composition, including rule of thirds, leading lines, color harmony, and photographic techniques used.";

    public JoyCaptionService(string modelPath, string mmProjPath)
    {
        _modelPath = modelPath;
        _mmProjPath = mmProjPath;
    }

    /// <summary>
    /// Initialize the model and load into memory/VRAM
    /// </summary>
    public async Task InitializeAsync(int gpuLayers = 35, int contextSize = 4096)
    {
        if (_isInitialized) return;

        lock (_lock)
        {
            if (_isInitialized) return;

            var parameters = new ModelParams(_modelPath)
            {
                ContextSize = (uint)contextSize,
                GpuLayerCount = gpuLayers,
                Encoding = System.Text.Encoding.UTF8,
                UseMemorymap = true,
                UseMemoryLock = false,
            };

            _model = LLamaWeights.LoadFromFile(parameters);
            _context = _model.CreateContext(parameters);
            _executor = new InteractiveExecutor(_context);

            _isInitialized = true;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Generate a caption for an image
    /// </summary>
    public async Task<CaptionResult> CaptionImageAsync(string imagePath, string? prompt = null, CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
            await InitializeAsync();

        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image not found: {imagePath}");

        prompt ??= DEFAULT_DETAILED_PROMPT;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Convert image to base64 for LLaVA-style input
            var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
            var base64Image = Convert.ToBase64String(imageBytes);

            // LLaVA-style prompt format: <image>\n{prompt}
            var fullPrompt = $"<image>\n{prompt}";

            var inferenceParams = new InferenceParams
            {
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.7f
                },
                MaxTokens = 300
            };

            var response = new System.Text.StringBuilder();
            var tokenCount = 0;

            await foreach (var text in _executor!.InferAsync(fullPrompt, inferenceParams, cancellationToken))
            {
                response.Append(text);
                tokenCount++;
            }

            sw.Stop();

            return new CaptionResult(
                caption: response.ToString().Trim(),
                promptUsed: prompt,
                tokenCount: tokenCount,
                generationTimeMs: sw.Elapsed.TotalMilliseconds
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            throw new Exception($"Failed to caption image: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Caption multiple images in batch
    /// </summary>
    public async Task<Dictionary<string, CaptionResult>> CaptionImagesAsync(
        IEnumerable<string> imagePaths,
        string? prompt = null,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
            await InitializeAsync();

        var results = new Dictionary<string, CaptionResult>();
        var imagePathsList = imagePaths.ToList();
        var total = imagePathsList.Count;
        var current = 0;

        foreach (var imagePath in imagePathsList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var caption = await CaptionImageAsync(imagePath, prompt, cancellationToken);
                results[imagePath] = caption;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error captioning {imagePath}: {ex.Message}");
                results[imagePath] = new CaptionResult
                {
                    Caption = $"[Error: {ex.Message}]",
                    PromptUsed = prompt ?? DEFAULT_DETAILED_PROMPT,
                    GenerationTimeMs = 0
                };
            }

            current++;
            progress?.Report((current, total));
        }

        return results;
    }

    /// <summary>
    /// Check if the model is loaded and ready
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Unload models from memory/VRAM
    /// </summary>
    public void UnloadModels()
    {
        lock (_lock)
        {
            _executor = null;
            _context?.Dispose();
            _context = null;

            _model?.Dispose();
            _model = null;

            _isInitialized = false;

            // Force garbage collection to free VRAM
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    public void Dispose()
    {
        UnloadModels();
    }
}
