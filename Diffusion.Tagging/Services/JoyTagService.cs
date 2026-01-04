using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Diffusion.Tagging.Models;

namespace Diffusion.Tagging.Services;

/// <summary>
/// Service for auto-tagging images using JoyTag ONNX model
/// </summary>
public class JoyTagService : IDisposable
{
    private readonly string _modelPath;
    private readonly string _tagsPath;
    private readonly int _deviceId;
    private readonly JoyTagImagePreprocessor _preprocessor;
    private InferenceSession? _session;
    private string[] _tags = Array.Empty<string>();
    private bool _isInitialized;

    public float Threshold { get; set; } = 0.5f;

    public JoyTagService(string modelPath, string tagsPath, float threshold = 0.5f, int deviceId = 0)
    {
        _modelPath = modelPath;
        _tagsPath = tagsPath;
        _deviceId = deviceId;
        _preprocessor = new JoyTagImagePreprocessor();
        Threshold = threshold;
    }

    /// <summary>
    /// Initialize the model and load tags
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        // Create session options with GPU
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        
        // Try GPU first, fallback to CPU if it fails
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(_deviceId);
            _session = new InferenceSession(_modelPath, sessionOptions);
        }
        catch
        {
            sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            _session = new InferenceSession(_modelPath, sessionOptions);
        }

        // Load tags from CSV
        await LoadTagsAsync();
        _isInitialized = true;
    }

    private async Task LoadTagsAsync()
    {
        if (!File.Exists(_tagsPath))
            throw new FileNotFoundException($"Tags file not found: {_tagsPath}");

        var lines = await File.ReadAllLinesAsync(_tagsPath);
        _tags = lines
            .Where(line => !string.IsNullOrWhiteSpace(line)) // Remove empty lines
            .Select(line =>
            {
                var parts = line.Split(',');
                // Handle different CSV formats - take the first column (tag ID is column 0, tag name is column 0 if no ID)
                var tagName = parts.Length > 1 ? parts[1].Trim('"') : parts[0].Trim('"');
                return tagName.Replace("_", " "); // Replace underscores with spaces for readability
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

        // Apply sigmoid and filter by threshold
        var tagResults = new List<TagResult>();
        for (int i = 0; i < predictions.Length && i < _tags.Length; i++)
        {
            var confidence = Sigmoid(predictions[i]);
            if (confidence > Threshold)
            {
                tagResults.Add(new TagResult(_tags[i], confidence));
            }
        }

        // Sort by confidence descending
        return tagResults.OrderByDescending(t => t.Confidence).ToList();
    }

    /// <summary>
    /// Tag multiple images in batch
    /// </summary>
    public async Task<Dictionary<string, List<TagResult>>> TagImagesAsync(
        IEnumerable<string> imagePaths, 
        IProgress<(int current, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
            await InitializeAsync();

        var results = new Dictionary<string, List<TagResult>>();
        var imagePathsList = imagePaths.ToList();
        var total = imagePathsList.Count;
        var current = 0;

        foreach (var imagePath in imagePathsList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var tags = await TagImageAsync(imagePath);
                results[imagePath] = tags;
            }
            catch (Exception ex)
            {
                // Log error but continue with other images
                Console.WriteLine($"Error tagging {imagePath}: {ex.Message}");
                results[imagePath] = new List<TagResult>();
            }

            current++;
            progress?.Report((current, total));
        }

        return results;
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

    private static float Sigmoid(float x)
    {
        return 1.0f / (1.0f + MathF.Exp(-x));
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
