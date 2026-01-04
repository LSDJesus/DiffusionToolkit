using System.Diagnostics;
using Diffusion.Common;
using Diffusion.FaceDetection.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// RetinaFace detector using ONNX Runtime for GPU-accelerated face detection
/// Based on InsightFace implementation
/// </summary>
public class RetinaFaceDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _inputSize;
    private readonly float _confidenceThreshold;
    private readonly float _nmsThreshold;
    private bool _disposed;

    /// <summary>
    /// Initialize RetinaFace detector
    /// </summary>
    /// <param name="modelPath">Path to retinaface.onnx model</param>
    /// <param name="gpuDeviceId">GPU device ID (0, 1, etc.)</param>
    /// <param name="inputSize">Model input size (typically 640)</param>
    /// <param name="confidenceThreshold">Minimum confidence to keep detection</param>
    /// <param name="nmsThreshold">Non-maximum suppression threshold</param>
    public RetinaFaceDetector(
        string modelPath,
        int gpuDeviceId = 0,
        int inputSize = 640,
        float confidenceThreshold = 0.5f,
        float nmsThreshold = 0.4f)
    {
        _inputSize = inputSize;
        _confidenceThreshold = confidenceThreshold;
        _nmsThreshold = nmsThreshold;

        var sessionOptions = new SessionOptions();
        
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(gpuDeviceId);
            Logger.Log($"RetinaFace: Using CUDA GPU {gpuDeviceId}");
        }
        catch (Exception ex)
        {
            Logger.Log($"RetinaFace: CUDA not available ({ex.Message}), falling back to CPU");
        }

        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        _session = new InferenceSession(modelPath, sessionOptions);
        Logger.Log($"RetinaFace detector initialized: {modelPath}");
    }

    /// <summary>
    /// Detect faces in an image
    /// </summary>
    public async Task<List<FaceDetectionResult>> DetectAsync(string imagePath)
    {
        return await Task.Run(() =>
        {
            var results = new List<FaceDetectionResult>();
            
            try
            {
                using var image = Image.Load<Rgb24>(imagePath);
                var originalWidth = image.Width;
                var originalHeight = image.Height;

                // Calculate scale to fit input size while maintaining aspect ratio
                var scale = Math.Min((float)_inputSize / originalWidth, (float)_inputSize / originalHeight);
                var scaledWidth = (int)(originalWidth * scale);
                var scaledHeight = (int)(originalHeight * scale);

                // Resize image
                using var resizedImage = image.Clone(ctx => ctx.Resize(scaledWidth, scaledHeight));

                // Create padded input
                var inputData = new float[1 * 3 * _inputSize * _inputSize];
                
                // Fill with padding color (usually mean values)
                for (int i = 0; i < inputData.Length; i++)
                {
                    inputData[i] = 0f;
                }

                // Copy resized image to input tensor (CHW format, normalized)
                for (int y = 0; y < scaledHeight; y++)
                {
                    var row = resizedImage.GetPixelRowSpan(y);
                    for (int x = 0; x < scaledWidth; x++)
                    {
                        var pixel = row[x];
                        // BGR order (InsightFace convention), subtract mean
                        inputData[0 * _inputSize * _inputSize + y * _inputSize + x] = (pixel.B - 127.5f) / 128f;
                        inputData[1 * _inputSize * _inputSize + y * _inputSize + x] = (pixel.G - 127.5f) / 128f;
                        inputData[2 * _inputSize * _inputSize + y * _inputSize + x] = (pixel.R - 127.5f) / 128f;
                    }
                }

                var inputTensor = new DenseTensor<float>(inputData, new[] { 1, 3, _inputSize, _inputSize });
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input.1", inputTensor)
                };

                // Run detection
                using var outputs = _session.Run(inputs);
                var outputList = outputs.ToList();

                // Parse outputs (RetinaFace outputs: scores, bboxes, landmarks)
                var detections = ParseOutputs(outputList, scale, originalWidth, originalHeight);

                // Apply NMS
                results = ApplyNMS(detections, _nmsThreshold);

                // Crop faces and calculate quality scores
                foreach (var face in results)
                {
                    try
                    {
                        face.FaceCrop = CropFace(image, face);
                        face.SharpnessScore = CalculateSharpness(image, face);
                        face.QualityScore = CalculateQuality(face);
                        face.DetectionModel = "retinaface";
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error processing face crop: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"RetinaFace detection error for {imagePath}: {ex.Message}");
            }

            return results;
        });
    }

    private List<FaceDetectionResult> ParseOutputs(
        List<DisposableNamedOnnxValue> outputs, 
        float scale, 
        int originalWidth, 
        int originalHeight)
    {
        var results = new List<FaceDetectionResult>();

        // Get output tensors (order may vary by model version)
        float[] scores = null!;
        float[] bboxes = null!;
        float[] landmarks = null!;

        foreach (var output in outputs)
        {
            var data = output.AsEnumerable<float>().ToArray();
            var name = output.Name.ToLower();
            
            if (name.Contains("score") || name.Contains("conf"))
                scores = data;
            else if (name.Contains("bbox") || name.Contains("box"))
                bboxes = data;
            else if (name.Contains("landmark") || name.Contains("kps"))
                landmarks = data;
        }

        if (scores == null || bboxes == null) return results;

        // Parse detections
        int numDetections = scores.Length;
        for (int i = 0; i < numDetections; i++)
        {
            if (scores[i] < _confidenceThreshold) continue;

            // Get bbox (x1, y1, x2, y2)
            int bboxIdx = i * 4;
            float x1 = bboxes[bboxIdx] / scale;
            float y1 = bboxes[bboxIdx + 1] / scale;
            float x2 = bboxes[bboxIdx + 2] / scale;
            float y2 = bboxes[bboxIdx + 3] / scale;

            // Clamp to image bounds
            x1 = Math.Max(0, Math.Min(x1, originalWidth));
            y1 = Math.Max(0, Math.Min(y1, originalHeight));
            x2 = Math.Max(0, Math.Min(x2, originalWidth));
            y2 = Math.Max(0, Math.Min(y2, originalHeight));

            var face = new FaceDetectionResult
            {
                X = (int)x1,
                Y = (int)y1,
                Width = (int)(x2 - x1),
                Height = (int)(y2 - y1),
                Confidence = scores[i]
            };

            // Parse landmarks if available
            if (landmarks != null)
            {
                int landmarkIdx = i * 10; // 5 points × 2 (x, y)
                if (landmarkIdx + 10 <= landmarks.Length)
                {
                    face.Landmarks = new float[5][];
                    for (int j = 0; j < 5; j++)
                    {
                        face.Landmarks[j] = new float[]
                        {
                            landmarks[landmarkIdx + j * 2] / scale,
                            landmarks[landmarkIdx + j * 2 + 1] / scale
                        };
                    }

                    // Estimate head pose from landmarks
                    EstimateHeadPose(face);
                }
            }

            if (face.Width > 10 && face.Height > 10)
            {
                results.Add(face);
            }
        }

        return results;
    }

    private void EstimateHeadPose(FaceDetectionResult face)
    {
        if (face.Landmarks == null || face.Landmarks.Length < 5) return;

        // Simple head pose estimation from eye/nose positions
        var leftEye = face.Landmarks[0];
        var rightEye = face.Landmarks[1];
        var nose = face.Landmarks[2];

        // Yaw: horizontal eye difference vs nose position
        var eyeCenterX = (leftEye[0] + rightEye[0]) / 2;
        var faceWidth = face.Width;
        face.PoseYaw = (nose[0] - eyeCenterX) / (faceWidth / 2) * 45f; // Rough approximation

        // Pitch: vertical eye-to-nose ratio
        var eyeCenterY = (leftEye[1] + rightEye[1]) / 2;
        var eyeNoseDistance = nose[1] - eyeCenterY;
        var expectedDistance = face.Height * 0.35f;
        face.PosePitch = (eyeNoseDistance - expectedDistance) / expectedDistance * 30f;

        // Roll: eye line angle
        var eyeDeltaY = rightEye[1] - leftEye[1];
        var eyeDeltaX = rightEye[0] - leftEye[0];
        face.PoseRoll = (float)(Math.Atan2(eyeDeltaY, eyeDeltaX) * 180 / Math.PI);
    }

    private List<FaceDetectionResult> ApplyNMS(List<FaceDetectionResult> detections, float threshold)
    {
        if (detections.Count == 0) return detections;

        // Sort by confidence descending
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
        var keep = new List<FaceDetectionResult>();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            keep.Add(best);
            sorted.RemoveAt(0);

            sorted = sorted.Where(d => CalculateIoU(best, d) < threshold).ToList();
        }

        return keep;
    }

    private float CalculateIoU(FaceDetectionResult a, FaceDetectionResult b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        int intersectionArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        int aArea = a.Width * a.Height;
        int bArea = b.Width * b.Height;
        int unionArea = aArea + bArea - intersectionArea;

        return unionArea > 0 ? (float)intersectionArea / unionArea : 0;
    }

    private byte[] CropFace(Image<Rgb24> image, FaceDetectionResult face)
    {
        // Expand bounding box by 30% for better context
        int padding = (int)(Math.Max(face.Width, face.Height) * 0.3f);
        int x = Math.Max(0, face.X - padding);
        int y = Math.Max(0, face.Y - padding);
        int width = Math.Min(image.Width - x, face.Width + padding * 2);
        int height = Math.Min(image.Height - y, face.Height + padding * 2);

        using var cropped = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
        
        // Resize to standard size for storage (256x256)
        cropped.Mutate(ctx => ctx.Resize(256, 256));
        
        face.CropWidth = 256;
        face.CropHeight = 256;

        using var ms = new MemoryStream();
        cropped.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 90 });
        return ms.ToArray();
    }

    private float CalculateSharpness(Image<Rgb24> image, FaceDetectionResult face)
    {
        // Calculate Laplacian variance as sharpness metric
        try
        {
            int x = Math.Max(0, face.X);
            int y = Math.Max(0, face.Y);
            int width = Math.Min(image.Width - x, face.Width);
            int height = Math.Min(image.Height - y, face.Height);

            if (width < 10 || height < 10) return 0;

            using var cropped = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, width, height)));

            // Simple variance calculation on grayscale
            double sum = 0, sumSq = 0;
            int count = 0;

            for (int py = 0; py < cropped.Height; py++)
            {
                var row = cropped.GetPixelRowSpan(py);
                for (int px = 0; px < cropped.Width; px++)
                {
                    var gray = (row[px].R + row[px].G + row[px].B) / 3.0;
                    sum += gray;
                    sumSq += gray * gray;
                    count++;
                }
            }

            var mean = sum / count;
            var variance = (sumSq / count) - (mean * mean);
            return (float)(variance / 1000.0); // Normalize to 0-10 range roughly
        }
        catch
        {
            return 0;
        }
    }

    private float CalculateQuality(FaceDetectionResult face)
    {
        // Composite quality score
        var score = 0f;
        
        // Confidence contributes 40%
        score += face.Confidence * 4f;
        
        // Sharpness contributes 30%
        score += Math.Min(face.SharpnessScore, 3f);
        
        // Size contributes 20% (prefer larger faces)
        var sizeScore = Math.Min(face.Width * face.Height / 10000f, 2f);
        score += sizeScore;
        
        // Pose contributes 10% (prefer frontal)
        var poseScore = 1f - (Math.Abs(face.PoseYaw) + Math.Abs(face.PosePitch)) / 90f;
        score += Math.Max(0, poseScore);

        return Math.Min(10f, score);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _disposed = true;
        }
    }
}
