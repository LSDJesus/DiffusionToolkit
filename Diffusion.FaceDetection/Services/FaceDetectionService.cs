using Diffusion.Common;
using Diffusion.Common.Models;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// Face detection backend options
/// </summary>
public enum FaceDetectionBackend
{
    /// <summary>YOLO11 + ArcFace + CLIP (custom models)</summary>
    YoloArcFace,
    
    /// <summary>FaceONNX library (UltraFace + ArcFace)</summary>
    FaceONNX
}

/// <summary>
/// Configuration for face detection service
/// </summary>
public class FaceDetectionConfig
{
    public FaceDetectionBackend Backend { get; set; } = FaceDetectionBackend.YoloArcFace;
    public string YoloModelPath { get; set; } = "";
    public string ArcFaceModelPath { get; set; } = "";
    public string ClipVisionModelPath { get; set; } = "";  // For universal face embeddings
    public int GpuDeviceId { get; set; } = 0;
    public int DetectionInputSize { get; set; } = 640;
    public int EmbeddingInputSize { get; set; } = 112;
    public int ClipInputSize { get; set; } = 224;
    public float ConfidenceThreshold { get; set; } = 0.5f;
    public float NmsThreshold { get; set; } = 0.4f;
    public float ClusterThreshold { get; set; } = 0.6f; // Similarity threshold for clustering
    public bool EnableClipEmbedding { get; set; } = true; // Generate CLIP face embeddings
    public bool EnableStyleClassification { get; set; } = true; // Classify image style
    
    /// <summary>
    /// Create default config from base directory
    /// </summary>
    public static FaceDetectionConfig CreateDefault(string baseDir)
    {
        return new FaceDetectionConfig
        {
            Backend = FaceDetectionBackend.YoloArcFace,
            YoloModelPath = Path.Combine(baseDir, "models", "onnx", "yolo", "yolov11l-face.onnx"),
            ArcFaceModelPath = Path.Combine(baseDir, "models", "onnx", "arcface", "w600k_r50.onnx"),
            ClipVisionModelPath = Path.Combine(baseDir, "models", "onnx", "clip-vit-h", "visual.onnx"),
            GpuDeviceId = 0,
            DetectionInputSize = 640,
            EmbeddingInputSize = 112,
            ClipInputSize = 224,
            ConfidenceThreshold = 0.5f,
            NmsThreshold = 0.4f,
            ClusterThreshold = 0.6f,
            EnableClipEmbedding = true,
            EnableStyleClassification = true
        };
    }
    
    /// <summary>
    /// Create config for FaceONNX backend (no external models needed)
    /// </summary>
    public static FaceDetectionConfig CreateFaceONNXConfig()
    {
        return new FaceDetectionConfig
        {
            Backend = FaceDetectionBackend.FaceONNX,
            GpuDeviceId = 0,
            ConfidenceThreshold = 0.5f,
            ClusterThreshold = 0.6f,
            EnableStyleClassification = true
        };
    }
}

/// <summary>
/// Unified face detection service combining detection + multi-model embeddings.
/// Supports style-aware processing for realistic, anime, 3D, and mixed content.
/// </summary>
public class FaceDetectionService : IDisposable
{
    private readonly YOLO11FaceDetector _detector;
    private readonly ArcFaceEncoder _arcFaceEncoder;
    private readonly ClipFaceEncoder? _clipEncoder;
    private readonly ImageStyleClassifier _styleClassifier;
    private readonly FaceDetectionConfig _config;
    private bool _disposed;

    public FaceDetectionService(FaceDetectionConfig config)
    {
        _config = config;
        _styleClassifier = new ImageStyleClassifier();
        
        _detector = new YOLO11FaceDetector(
            config.YoloModelPath,
            config.GpuDeviceId,
            config.DetectionInputSize,
            config.ConfidenceThreshold,
            config.NmsThreshold);
            
        _arcFaceEncoder = new ArcFaceEncoder(
            config.ArcFaceModelPath,
            config.GpuDeviceId,
            config.EmbeddingInputSize);
        
        // Initialize CLIP encoder if enabled and model exists
        if (config.EnableClipEmbedding && File.Exists(config.ClipVisionModelPath))
        {
            _clipEncoder = new ClipFaceEncoder(
                config.ClipVisionModelPath,
                config.GpuDeviceId,
                config.ClipInputSize);
            Logger.Log($"FaceDetectionService: CLIP face encoder enabled");
        }
        else if (config.EnableClipEmbedding)
        {
            Logger.Log($"FaceDetectionService: CLIP model not found at {config.ClipVisionModelPath}");
        }
            
        Logger.Log($"FaceDetectionService initialized on GPU {config.GpuDeviceId} (ArcFace + " +
            $"{(_clipEncoder != null ? "CLIP" : "no CLIP")})");
    }

    /// <summary>
    /// Create service from config
    /// </summary>
    public static FaceDetectionService FromConfig(FaceDetectionConfig config)
    {
        return new FaceDetectionService(config);
    }

    /// <summary>
    /// Process single image: detect faces and generate multi-model embeddings
    /// </summary>
    public async Task<ImageFaceResults> ProcessImageAsync(string imagePath)
    {
        var results = new ImageFaceResults { ImagePath = imagePath };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Get image dimensions
            var info = SixLabors.ImageSharp.Image.Identify(imagePath);
            results.ImageWidth = info.Width;
            results.ImageHeight = info.Height;
            
            // Classify image style
            ImageStyle imageStyle = ImageStyle.Mixed;
            if (_config.EnableStyleClassification)
            {
                imageStyle = _styleClassifier.Classify(imagePath);
                results.ImageStyle = imageStyle.ToString().ToLowerInvariant();
            }

            // Detect faces
            var faces = await _detector.DetectAsync(imagePath);
            
            // Generate embeddings for each face based on style
            foreach (var face in faces)
            {
                face.StyleType = imageStyle.ToString().ToLowerInvariant();
                
                if (face.FaceCrop != null && face.FaceCrop.Length > 0)
                {
                    // Generate ArcFace embedding (best for realistic faces)
                    // Always generate for backward compatibility
                    try
                    {
                        face.ArcFaceEmbedding = await _arcFaceEncoder.EncodeAsync(face.FaceCrop);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error generating ArcFace embedding: {ex.Message}");
                    }
                    
                    // Generate CLIP embedding (universal - works for all styles)
                    if (_clipEncoder != null)
                    {
                        try
                        {
                            face.ClipFaceEmbedding = await _clipEncoder.EncodeAsync(face.FaceCrop);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Error generating CLIP face embedding: {ex.Message}");
                        }
                    }
                }
            }

            results.Faces = faces;
        }
        catch (Exception ex)
        {
            results.ErrorMessage = ex.Message;
            Logger.Log($"FaceDetectionService error for {imagePath}: {ex.Message}");
        }

        sw.Stop();
        results.ProcessingTimeMs = (float)sw.Elapsed.TotalMilliseconds;
        
        return results;
    }

    /// <summary>
    /// Process batch of images
    /// </summary>
    public async Task<List<ImageFaceResults>> ProcessBatchAsync(
        IEnumerable<string> imagePaths,
        IProgress<(int current, int total, string path)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ImageFaceResults>();
        var pathList = imagePaths.ToList();
        var total = pathList.Count;
        var current = 0;

        foreach (var path in pathList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var result = await ProcessImageAsync(path);
            results.Add(result);
            
            current++;
            progress?.Report((current, total, path));
        }

        return results;
    }

    /// <summary>
    /// Calculate similarity between two faces using ArcFace embeddings
    /// </summary>
    public float CalculateSimilarity(float[] embedding1, float[] embedding2)
    {
        return ArcFaceEncoder.CosineSimilarity(embedding1, embedding2);
    }
    
    /// <summary>
    /// Calculate similarity between two faces using style-aware blending.
    /// Uses ArcFace for realistic, CLIP for anime/3D, weighted blend for mixed.
    /// </summary>
    public float CalculateSimilarityStyleAware(
        float[]? arcface1, float[]? arcface2,
        float[]? clip1, float[]? clip2,
        string style)
    {
        // Style-based weighting
        float arcWeight, clipWeight;
        switch (style.ToLowerInvariant())
        {
            case "realistic":
                arcWeight = 0.8f;
                clipWeight = 0.2f;
                break;
            case "anime":
            case "3d":
            case "threed":
                arcWeight = 0.2f;
                clipWeight = 0.8f;
                break;
            default: // mixed or unknown
                arcWeight = 0.5f;
                clipWeight = 0.5f;
                break;
        }
        
        float score = 0f;
        float totalWeight = 0f;
        
        if (arcface1 != null && arcface2 != null)
        {
            score += arcWeight * ArcFaceEncoder.CosineSimilarity(arcface1, arcface2);
            totalWeight += arcWeight;
        }
        
        if (clip1 != null && clip2 != null)
        {
            score += clipWeight * ClipFaceEncoder.CosineSimilarity(clip1, clip2);
            totalWeight += clipWeight;
        }
        
        return totalWeight > 0 ? score / totalWeight : 0f;
    }

    /// <summary>
    /// Find similar faces from a list
    /// </summary>
    public List<(int index, float similarity)> FindSimilarFaces(
        float[] queryEmbedding,
        IList<float[]> candidateEmbeddings,
        float threshold = 0.6f,
        int maxResults = 100)
    {
        var results = new List<(int index, float similarity)>();

        for (int i = 0; i < candidateEmbeddings.Count; i++)
        {
            var similarity = CalculateSimilarity(queryEmbedding, candidateEmbeddings[i]);
            if (similarity >= threshold)
            {
                results.Add((i, similarity));
            }
        }

        return results
            .OrderByDescending(r => r.similarity)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Cluster faces by similarity
    /// </summary>
    public List<List<int>> ClusterFaces(
        IList<float[]> embeddings,
        float threshold = 0.6f)
    {
        var clusters = new List<List<int>>();
        var assigned = new HashSet<int>();

        for (int i = 0; i < embeddings.Count; i++)
        {
            if (assigned.Contains(i)) continue;

            var cluster = new List<int> { i };
            assigned.Add(i);

            for (int j = i + 1; j < embeddings.Count; j++)
            {
                if (assigned.Contains(j)) continue;

                var similarity = CalculateSimilarity(embeddings[i], embeddings[j]);
                if (similarity >= threshold)
                {
                    cluster.Add(j);
                    assigned.Add(j);
                }
            }

            clusters.Add(cluster);
        }

        return clusters.OrderByDescending(c => c.Count).ToList();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _detector?.Dispose();
            _arcFaceEncoder?.Dispose();
            _clipEncoder?.Dispose();
            _disposed = true;
        }
    }
}
