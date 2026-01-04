using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Batched;
using Diffusion.Captioning.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Diffusion.Captioning.Services;

/// <summary>
/// Service for generating detailed image captions using JoyCaption (LLaVA-style model)
/// via LLamaSharp with GGUF format using BatchedExecutor for efficient embedding reuse
/// </summary>
public class JoyCaptionService : IDisposable, ICaptionService
{
    private readonly string _modelPath;
    private readonly string _mmProjPath;
    private readonly int _deviceId;
    private LLamaWeights? _model;
    private LLavaWeights? _clipModel;
    private LLamaContext? _context;
    private BatchedExecutor? _executor;
    private bool _isInitialized;
    private readonly object _lock = new();

    // Preset prompts for different use cases
    public const string PROMPT_DETAILED = "Write a detailed 150-200 word description of this image. Include: colors, objects, people, composition, lighting, and mood.";
    public const string PROMPT_SHORT = "Write a single sentence describing this image.";
    public const string PROMPT_TECHNICAL = "Analyze the composition: rule of thirds, leading lines, color harmony, depth of field, and lighting techniques.";
    public const string PROMPT_PRECISE_COLORS = "Describe this image using specific color names (turquoise not blue, auburn not red, ivory not white). Include all visual details.";
    public const string PROMPT_CHARACTER_FOCUS = "Describe the character(s): appearance, facial features, clothing, accessories, pose, and expression.";
    public const string PROMPT_SCENE_FOCUS = "Describe the environment: location type, time of day, weather/atmosphere, and background elements.";
    public const string PROMPT_ARTISTIC_STYLE = "Identify the artistic style and medium (3D render, digital painting, photograph, traditional art). Describe the visual aesthetic.";
    public const string PROMPT_BOORU_TAGS = "List booru-style tags: character features, clothing items, objects, setting, and mood tags.";
    
    // Legacy aliases for backwards compatibility
    public const string DEFAULT_DETAILED_PROMPT = PROMPT_DETAILED;
    public const string DEFAULT_SHORT_PROMPT = PROMPT_SHORT;
    public const string DEFAULT_TECHNICAL_PROMPT = PROMPT_TECHNICAL;

    public JoyCaptionService(string modelPath, string mmProjPath, int deviceId = 0)
    {
        _modelPath = modelPath;
        _mmProjPath = mmProjPath;
        _deviceId = deviceId;
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
                MainGpu = _deviceId,  // Assign to specific GPU device
                Encoding = System.Text.Encoding.UTF8,
                UseMemorymap = true,
                UseMemoryLock = false,
            };

            _model = LLamaWeights.LoadFromFile(parameters);
            
            // Load the mmproj (clip model) for multimodal support
            _clipModel = LLavaWeights.LoadFromFile(_mmProjPath);
            
            // Create BatchedExecutor for efficient multi-prompt processing
            _executor = new BatchedExecutor(_model, parameters);
            
            // Get context from executor
            _context = _executor.Context;

            _isInitialized = true;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// BatchedExecutor doesn't need state reset - each conversation is independent
    /// This method is kept for API compatibility but does nothing
    /// </summary>
    private void ResetExecutorState()
    {
        // BatchedExecutor handles state isolation per conversation automatically
        // No action needed
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

        // Create image embedding once (this is the expensive encoding operation)
        using var imageEmbed = _clipModel!.CreateImageEmbeddings(_context!, imagePath);
        
        return await CaptionWithEmbeddingAsync(imageEmbed, prompt, cancellationToken);
    }

    /// <summary>
    /// Generate a caption using a pre-created image embedding
    /// Most efficient when captioning the same image with multiple prompts
    /// Uses BatchedExecutor with independent conversations for true embedding reuse
    /// </summary>
    /// <param name="imageEmbed">Pre-created image embedding handle</param>
    /// <param name="prompt">Caption prompt (uses default if null)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<CaptionResult> CaptionWithEmbeddingAsync(SafeLlavaImageEmbedHandle imageEmbed, string? prompt = null, CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
            await InitializeAsync();

        prompt ??= DEFAULT_DETAILED_PROMPT;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        Conversation? conversation = null;
        try
        {
            // Create a new independent conversation for this caption
            // Each conversation has its own KV cache, preventing cross-contamination
            conversation = _executor!.Create();

            // Add the pre-created image embedding to the conversation
            // This reuses the embedding without re-encoding
            conversation.Prompt(imageEmbed);

            // Process ALL image patches before adding text prompt
            // The image has 729 patches that need to be fully processed
            while (_executor.BatchedTokenCount > 0)
            {
                await _executor.Infer(cancellationToken);
            }

            // Format as proper chat conversation as JoyCaption expects
            // System prompt helps prevent conversational artifacts
            var chatPrompt = $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\nYou are a helpful image captioner.<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n<image>\n{prompt}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n";
            var tokens = _context!.Tokenize(chatPrompt);
            
            // Add all text tokens at once
            conversation.Prompt(tokens);

            var samplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.7f
            };

            var decoder = new StreamingTokenDecoder(_context);
            var tokenCount = 0;
            
            // Adjust max tokens based on prompt type
            var maxTokens = prompt!.Contains("single sentence") || prompt.Contains("one sentence") ? 75 :
                           prompt.Contains("150-200 word") ? 250 : 450;

            // Get EOS token for natural stopping
            var eosToken = _model!.Tokens.EOS;

            // Generate tokens one at a time
            while (tokenCount < maxTokens)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Only call Infer if this conversation needs it
                if (conversation.RequiresInference)
                {
                    await _executor.Infer(cancellationToken);
                }

                // Sample next token from this conversation using GetSampleIndex()
                var nextToken = samplingPipeline.Sample(_context.NativeHandle, conversation.GetSampleIndex());
                
                // Check for EOS - stop generation naturally
                if (nextToken == eosToken)
                    break;
                    
                decoder.Add(nextToken);
                
                // Prompt the conversation with this token to continue
                conversation.Prompt(nextToken);
                tokenCount++;
            }

            sw.Stop();

            var caption = decoder.Read();
            
            // Parse response to remove chat template artifacts
            // Stop at first occurrence of end-of-turn token
            var endMarkers = new[] { "<|eot_id|>", "<|end_header_id|>", "<|end_of_text|>", "<image>" };
            foreach (var marker in endMarkers)
            {
                var markerIndex = caption.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex >= 0)
                {
                    caption = caption.Substring(0, markerIndex);
                    break;
                }
            }
            
            return new CaptionResult(
                caption: caption.Trim(),
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
        finally
        {
            // Dispose the conversation to clean up KV cache
            conversation?.Dispose();
        }
    }

    /// <summary>
    /// Create a reusable image embedding from a file
    /// Use this when captioning the same image with multiple prompts
    /// Remember to dispose the handle when done
    /// </summary>
    public SafeLlavaImageEmbedHandle CreateImageEmbedding(string imagePath)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Service must be initialized before creating embeddings");

        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image not found: {imagePath}");

        return _clipModel!.CreateImageEmbeddings(_context!, imagePath);
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

            _clipModel?.Dispose();
            _clipModel = null;

            _model?.Dispose();
            _model = null;

            _isInitialized = false;

            // Force garbage collection to free VRAM
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    /// <summary>
    /// Release models from VRAM (alias for UnloadModels for consistency)
    /// </summary>
    public void ReleaseModel()
    {
        UnloadModels();
    }

    public void Dispose()
    {
        UnloadModels();
    }
}
