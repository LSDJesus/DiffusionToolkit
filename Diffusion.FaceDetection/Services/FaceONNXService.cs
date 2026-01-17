using Diffusion.Common;
using Diffusion.Common.Models;
using FaceONNX;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UMapx.Core;

// Alias to avoid conflict with FaceONNX.FaceDetectionResult
using DiffusionFaceResult = Diffusion.Common.Models.FaceDetectionResult;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// Configuration for the FaceONNX-based detection service
/// </summary>
public class FaceONNXServiceConfig
{
    public int GpuDeviceId { get; set; } = 0;
    public bool UseGpu { get; set; } = true;
    public float ConfidenceThreshold { get; set; } = 0.5f;
    public bool ExtractLandmarks { get; set; } = true;
    public bool GenerateEmbeddings { get; set; } = true;
    public bool ClassifyAgeGender { get; set; } = false;
    public int MinFaceSize { get; set; } = 20;
    public int CropPadding { get; set; } = 20; // Pixels to pad around face crop
}

/// <summary>
/// Face detection and recognition service using FaceONNX library.
/// Provides unified face detection, landmarks, embeddings, and optional age/gender classification.
/// </summary>
public class FaceONNXService : IDisposable
{
    private readonly FaceDetector _detector;
    private readonly Face68LandmarksExtractor _landmarksExtractor;
    private readonly FaceEmbedder _embedder;
    private readonly FaceONNXServiceConfig _config;
    private readonly ImageStyleClassifier _styleClassifier;
    private bool _disposed;

    public FaceONNXService(FaceONNXServiceConfig config)
    {
        _config = config;
        _styleClassifier = new ImageStyleClassifier();
        
        // Initialize FaceONNX components
        // Note: FaceONNX uses its own session options internally
        // GPU support would require FaceONNX.Gpu package
        _detector = new FaceDetector();
        _landmarksExtractor = new Face68LandmarksExtractor();
        _embedder = new FaceEmbedder();
        
        Logger.Log($"FaceONNXService initialized (GPU: {config.UseGpu}, DeviceId: {config.GpuDeviceId})");
    }

    /// <summary>
    /// Process a single image: detect faces and generate embeddings
    /// </summary>
    public async Task<ImageFaceResults> ProcessImageAsync(string imagePath)
    {
        return await Task.Run(() => ProcessImage(imagePath));
    }

    /// <summary>
    /// Process a single image synchronously
    /// </summary>
    public ImageFaceResults ProcessImage(string imagePath)
    {
        var results = new ImageFaceResults { ImagePath = imagePath };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var image = Image.Load<Rgb24>(imagePath);
            results.ImageWidth = image.Width;
            results.ImageHeight = image.Height;

            // Classify image style for embedding strategy
            var imageStyle = _styleClassifier.ClassifyImage(image);
            results.ImageStyle = imageStyle.ToString().ToLowerInvariant();

            // Convert to FaceONNX format (BGR float array)
            var imageArray = GetImageFloatArray(image);

            // Detect faces
            var detections = _detector.Forward(imageArray);

            var faces = new List<DiffusionFaceResult>();
            var faceIndex = 0;

            foreach (var detection in detections)
            {
                if (detection.Score < _config.ConfidenceThreshold)
                    continue;

                var box = detection.Box;
                if (box.Width < _config.MinFaceSize || box.Height < _config.MinFaceSize)
                    continue;

                var face = new DiffusionFaceResult
                {
                    X = Math.Max(0, box.X),
                    Y = Math.Max(0, box.Y),
                    Width = box.Width,
                    Height = box.Height,
                    Confidence = detection.Score,
                    DetectionModel = "faceonnx-ultraface",
                    StyleType = imageStyle.ToString().ToLowerInvariant()
                };

                // Extract landmarks if enabled
                float[][]? landmarks = null;
                float rotationAngle = 0;
                
                if (_config.ExtractLandmarks)
                {
                    try
                    {
                        var facePoints = _landmarksExtractor.Forward(imageArray, box);
                        rotationAngle = facePoints.RotationAngle;
                        
                        // Convert to our format (5-point: eyes, nose, mouth corners)
                        // FaceONNX gives 68 landmarks via All property, extract key points
                        var pts = facePoints.All;
                        if (pts != null && pts.Length >= 68)
                        {
                            landmarks = new float[][]
                            {
                                new float[] { pts[36].X, pts[36].Y }, // Left eye
                                new float[] { pts[45].X, pts[45].Y }, // Right eye
                                new float[] { pts[30].X, pts[30].Y }, // Nose tip
                                new float[] { pts[48].X, pts[48].Y }, // Left mouth
                                new float[] { pts[54].X, pts[54].Y }  // Right mouth
                            };
                        }
                        
                        face.Landmarks = landmarks;
                        face.PoseRoll = rotationAngle;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"FaceONNX landmarks extraction failed: {ex.Message}");
                    }
                }

                // Generate face crop and embeddings
                if (_config.GenerateEmbeddings)
                {
                    try
                    {
                        // Use proportional padding (30% of face size) for better context
                        var paddingX = (int)(box.Width * 0.3f);
                        var paddingY = (int)(box.Height * 0.3f);
                        
                        // Make crop square by using the larger dimension
                        var size = Math.Max(box.Width, box.Height) + 2 * Math.Max(paddingX, paddingY);
                        
                        // Center the face in the square crop
                        var centerX = box.X + box.Width / 2;
                        var centerY = box.Y + box.Height / 2;
                        
                        var cropX = Math.Max(0, centerX - size / 2);
                        var cropY = Math.Max(0, centerY - size / 2);
                        var cropW = Math.Min(image.Width - cropX, size);
                        var cropH = Math.Min(image.Height - cropY, size);

                        // Crop face
                        using var faceCrop = image.Clone(ctx => 
                            ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH)));
                        
                        // Save crop as JPEG bytes
                        using var ms = new MemoryStream();
                        faceCrop.SaveAsJpeg(ms);
                        face.FaceCrop = ms.ToArray();
                        face.CropWidth = faceCrop.Width;
                        face.CropHeight = faceCrop.Height;

                        // Align and generate embedding
                        var aligned = FaceProcessingExtensions.Align(imageArray, box, rotationAngle);
                        var embedding = _embedder.Forward(aligned);
                        
                        face.ArcFaceEmbedding = embedding; // FaceONNX uses ArcFace
                        
                        // Calculate quality scores
                        face.QualityScore = CalculateQualityScore(faceCrop, detection.Score);
                        face.SharpnessScore = CalculateSharpness(faceCrop);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"FaceONNX embedding generation failed: {ex.Message}");
                    }
                }

                faces.Add(face);
                faceIndex++;
            }

            results.Faces = faces;
        }
        catch (Exception ex)
        {
            results.ErrorMessage = ex.Message;
            Logger.Log($"FaceONNXService error for {imagePath}: {ex.Message}");
        }

        sw.Stop();
        results.ProcessingTimeMs = (float)sw.Elapsed.TotalMilliseconds;
        
        return results;
    }

    /// <summary>
    /// Process multiple images
    /// </summary>
    public async Task<List<ImageFaceResults>> ProcessImagesAsync(
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
    /// Calculate similarity between two face embeddings (cosine similarity)
    /// </summary>
    public static float CalculateSimilarity(float[] embedding1, float[] embedding2)
    {
        if (embedding1.Length != embedding2.Length)
            throw new ArgumentException("Embeddings must have the same dimension");

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < embedding1.Length; i++)
        {
            dot += embedding1[i] * embedding2[i];
            normA += embedding1[i] * embedding1[i];
            normB += embedding2[i] * embedding2[i];
        }
        
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    /// <summary>
    /// Convert ImageSharp image to FaceONNX float array format (CHW, BGR, 0-1)
    /// </summary>
    private static float[][,] GetImageFloatArray(Image<Rgb24> image)
    {
        var array = new[]
        {
            new float[image.Height, image.Width],
            new float[image.Height, image.Width],
            new float[image.Height, image.Width]
        };

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                {
                    // FaceONNX uses BGR order
                    array[2][y, x] = row[x].R / 255.0f;
                    array[1][y, x] = row[x].G / 255.0f;
                    array[0][y, x] = row[x].B / 255.0f;
                }
            }
        });

        return array;
    }

    /// <summary>
    /// Calculate a quality score for the face crop
    /// </summary>
    private static float CalculateQualityScore(Image<Rgb24> faceCrop, float confidence)
    {
        // Combine detection confidence with size-based quality
        var sizeScore = Math.Min(1f, Math.Min(faceCrop.Width, faceCrop.Height) / 150f);
        return (confidence * 0.6f + sizeScore * 0.4f);
    }

    /// <summary>
    /// Calculate sharpness using Laplacian variance
    /// </summary>
    private static float CalculateSharpness(Image<Rgb24> image)
    {
        float sum = 0;
        float sumSq = 0;
        int count = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 1; y < accessor.Height - 1; y++)
            {
                var prevRow = accessor.GetRowSpan(y - 1);
                var row = accessor.GetRowSpan(y);
                var nextRow = accessor.GetRowSpan(y + 1);

                for (var x = 1; x < accessor.Width - 1; x++)
                {
                    // Simple Laplacian (grayscale)
                    var center = (row[x].R + row[x].G + row[x].B) / 3f;
                    var neighbors = (
                        (prevRow[x].R + prevRow[x].G + prevRow[x].B) / 3f +
                        (nextRow[x].R + nextRow[x].G + nextRow[x].B) / 3f +
                        (row[x - 1].R + row[x - 1].G + row[x - 1].B) / 3f +
                        (row[x + 1].R + row[x + 1].G + row[x + 1].B) / 3f
                    ) / 4f;

                    var laplacian = Math.Abs(center - neighbors);
                    sum += laplacian;
                    sumSq += laplacian * laplacian;
                    count++;
                }
            }
        });

        if (count == 0) return 0;

        var mean = sum / count;
        var variance = (sumSq / count) - (mean * mean);
        
        // Normalize to 0-1 range
        return Math.Min(1f, variance / 100f);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _detector?.Dispose();
            _landmarksExtractor?.Dispose();
            _embedder?.Dispose();
            _disposed = true;
        }
    }
}
