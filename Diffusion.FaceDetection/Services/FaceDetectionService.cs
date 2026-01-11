using Diffusion.Common;
using Diffusion.Common.Models;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// Configuration for face detection service
/// </summary>
public class FaceDetectionConfig
{
    public string YoloModelPath { get; set; } = "";
    public string ArcFaceModelPath { get; set; } = "";
    public int GpuDeviceId { get; set; } = 0;
    public int DetectionInputSize { get; set; } = 640;
    public int EmbeddingInputSize { get; set; } = 112;
    public float ConfidenceThreshold { get; set; } = 0.5f;
    public float NmsThreshold { get; set; } = 0.4f;
    public float ClusterThreshold { get; set; } = 0.6f; // Similarity threshold for clustering
    
    /// <summary>
    /// Create default config from base directory
    /// </summary>
    public static FaceDetectionConfig CreateDefault(string baseDir)
    {
        return new FaceDetectionConfig
        {
            YoloModelPath = Path.Combine(baseDir, "models", "onnx", "yolo", "yolov12m-face.onnx"),
            ArcFaceModelPath = Path.Combine(baseDir, "models", "onnx", "arcface", "w600k_r50.onnx"),
            GpuDeviceId = 0,
            DetectionInputSize = 640,
            EmbeddingInputSize = 112,
            ConfidenceThreshold = 0.5f,
            NmsThreshold = 0.4f,
            ClusterThreshold = 0.6f
        };
    }
}

/// <summary>
/// Unified face detection service combining detection + embedding
/// </summary>
public class FaceDetectionService : IDisposable
{
    private readonly YOLO11FaceDetector _detector;
    private readonly ArcFaceEncoder _encoder;
    private readonly FaceDetectionConfig _config;
    private bool _disposed;

    public FaceDetectionService(FaceDetectionConfig config)
    {
        _config = config;
        
        _detector = new YOLO11FaceDetector(
            config.YoloModelPath,
            config.GpuDeviceId,
            config.DetectionInputSize,
            config.ConfidenceThreshold,
            config.NmsThreshold);
            
        _encoder = new ArcFaceEncoder(
            config.ArcFaceModelPath,
            config.GpuDeviceId,
            config.EmbeddingInputSize);
            
        Logger.Log($"FaceDetectionService initialized on GPU {config.GpuDeviceId}");
    }

    /// <summary>
    /// Create service from config
    /// </summary>
    public static FaceDetectionService FromConfig(FaceDetectionConfig config)
    {
        return new FaceDetectionService(config);
    }

    /// <summary>
    /// Process single image: detect faces and generate embeddings
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

            // Detect faces
            var faces = await _detector.DetectAsync(imagePath);
            
            // Generate embeddings for each face
            foreach (var face in faces)
            {
                if (face.FaceCrop != null && face.FaceCrop.Length > 0)
                {
                    try
                    {
                        face.ArcFaceEmbedding = await _encoder.EncodeAsync(face.FaceCrop);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error generating face embedding: {ex.Message}");
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
    /// Calculate similarity between two faces
    /// </summary>
    public float CalculateSimilarity(float[] embedding1, float[] embedding2)
    {
        return ArcFaceEncoder.CosineSimilarity(embedding1, embedding2);
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
            _encoder?.Dispose();
            _disposed = true;
        }
    }
}
