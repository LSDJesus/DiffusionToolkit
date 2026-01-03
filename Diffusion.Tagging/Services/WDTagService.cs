using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Diffusion.Tagging.Models;

namespace Diffusion.Tagging.Services;

/// <summary>
/// Service for auto-tagging images using WD 1.4 tagger ONNX model
/// </summary>
public class WDTagService : IDisposable
{
    private readonly string _modelPath;
    private readonly string _tagsPath;
    private readonly WDImagePreprocessor _preprocessor;
    private InferenceSession? _session;
    private string[] _tags = Array.Empty<string>();
    private bool _isInitialized;

    public float Threshold { get; set; } = 0.5f;

    public WDTagService(string modelPath, string tagsPath, float threshold = 0.5f)
    {
        _modelPath = modelPath;
        _tagsPath = tagsPath;
        _preprocessor = new WDImagePreprocessor();
        Threshold = threshold;
    }

    /// <summary>
    /// Initialize the model and load tags
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        // Create session options with GPU support, fallback to CPU if CUDA fails
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        
        // Try GPU first, fallback to CPU if it fails
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(0);
            _session = new InferenceSession(_modelPath, sessionOptions);
        }
        catch
        {
            // CUDA not available, fallback to CPU-only
            sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            _session = new InferenceSession(_modelPath, sessionOptions);
        }

        await LoadTagsAsync();

        _isInitialized = true;
    }

    private async Task LoadTagsAsync()
    {
        if (!File.Exists(_tagsPath))
            throw new FileNotFoundException($"Tags file not found: {_tagsPath}");

        var lines = await File.ReadAllLinesAsync(_tagsPath);
        _tags = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                var parts = line.Split(',');
                var tagName = parts.Length > 1 ? parts[1].Trim('"') : parts[0].Trim('"');
                return tagName.Replace("_", " ");
            })
            .ToArray();
    }

    /// <summary>
    /// Tag a single image and return results above threshold
    /// </summary>
    public async Task<List<TagResult>> TagImageAsync(string imagePath)
    {
        if (!_isInitialized)
            await InitializeAsync();

        // Preprocess image
        var inputData = await _preprocessor.PreprocessImageAsync(imagePath);

        // Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_1:0", inputData.Input!)
        };

        using var results = _session!.Run(inputs);
        var predictions = results.First().AsTensor<float>().ToArray();

        // Verify array lengths match
        if (predictions.Length != _tags.Length)
        {
            throw new InvalidOperationException(
                $"Prediction count ({predictions.Length}) does not match tag count ({_tags.Length}). " +
                $"Model output shape mismatch!");
        }

        // WD models output sigmoid values directly (no additional sigmoid needed)
        var tagResults = new List<TagResult>();
        for (int i = 0; i < predictions.Length && i < _tags.Length; i++)
        {
            if (predictions[i] > Threshold)
            {
                tagResults.Add(new TagResult(_tags[i], predictions[i]));
            }
        }

        // Sort by confidence descending
        return tagResults.OrderByDescending(t => t.Confidence).ToList();
    }

    /// <summary>
    /// Get formatted tag string (comma-separated)
    /// </summary>
    public async Task<string> GetTagStringAsync(string imagePath, bool includeConfidence = false)
    {
        var tags = await TagImageAsync(imagePath);
        
        if (includeConfidence)
            return string.Join(", ", tags.Select(t => $"{t.Tag}:{t.Confidence:F2}"));
        else
            return string.Join(", ", tags.Select(t => t.Tag));
    }

    /// <summary>
    /// Release the ONNX model from memory to free VRAM
    /// </summary>
    public void ReleaseModel()
    {
        _session?.Dispose();
        _session = null;
        _isInitialized = false;
    }

    public void Dispose()
    {
        ReleaseModel();
    }
}
