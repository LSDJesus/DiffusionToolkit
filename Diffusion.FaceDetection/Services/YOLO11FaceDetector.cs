using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Diffusion.Common;
using Diffusion.Common.Models;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// YOLO11 face detector - simpler output format than RetinaFace
/// </summary>
public class YOLO11FaceDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _inputSize;
    private readonly float _confidenceThreshold;
    private readonly float _nmsThreshold;
    private bool _disposed;

    public YOLO11FaceDetector(string modelPath, int gpuDeviceId = 0, int inputSize = 640, 
        float confidenceThreshold = 0.5f, float nmsThreshold = 0.4f)
    {
        _inputSize = inputSize;
        _confidenceThreshold = confidenceThreshold;
        _nmsThreshold = nmsThreshold;

        var sessionOptions = new SessionOptions();
        
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(gpuDeviceId);
            Logger.Log($"YOLO11: Using CUDA GPU {gpuDeviceId}");
        }
        catch (Exception ex)
        {
            Logger.Log($"YOLO11: CUDA not available ({ex.Message}), falling back to CPU");
        }

        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        _session = new InferenceSession(modelPath, sessionOptions);
        Logger.Log($"YOLO11 face detector initialized: {modelPath}");
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

                // Resize to square input maintaining aspect ratio
                var scale = Math.Min((float)_inputSize / originalWidth, (float)_inputSize / originalHeight);
                var scaledWidth = (int)(originalWidth * scale);
                var scaledHeight = (int)(originalHeight * scale);

                using var resizedImage = image.Clone(ctx => ctx.Resize(scaledWidth, scaledHeight));

                // Create padded input (letterbox)
                var inputData = new float[1 * 3 * _inputSize * _inputSize];
                
                // Fill with gray padding (114/255)
                for (int i = 0; i < inputData.Length; i++)
                {
                    inputData[i] = 114f / 255f;
                }

                // Calculate padding offsets (center the image)
                int padX = (_inputSize - scaledWidth) / 2;
                int padY = (_inputSize - scaledHeight) / 2;

                // Copy image to input tensor (RGB order, normalized to 0-1)
                resizedImage.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < scaledHeight; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < scaledWidth; x++)
                        {
                            var pixel = row[x];
                            int targetX = x + padX;
                            int targetY = y + padY;
                            
                            inputData[0 * _inputSize * _inputSize + targetY * _inputSize + targetX] = pixel.R / 255f;
                            inputData[1 * _inputSize * _inputSize + targetY * _inputSize + targetX] = pixel.G / 255f;
                            inputData[2 * _inputSize * _inputSize + targetY * _inputSize + targetX] = pixel.B / 255f;
                        }
                    }
                });

                var inputTensor = new DenseTensor<float>(inputData, new[] { 1, 3, _inputSize, _inputSize });
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };

                // Run detection
                using var outputs = _session.Run(inputs);
                var output = outputs.First().AsEnumerable<float>().ToArray();

                // Parse YOLO11 output: [1, num_detections, 6] = [x, y, w, h, confidence, class]
                // But often comes as [1, 6, num_detections] - need to check shape
                var outputShape = outputs.First().AsTensor<float>().Dimensions.ToArray();
                
                Logger.Log($"YOLO11 output shape: [{string.Join(", ", outputShape)}]");

                var detections = ParseYOLOOutput(output, outputShape, scale, padX, padY, originalWidth, originalHeight);

                // Apply NMS
                results = ApplyNMS(detections, _nmsThreshold);

                Logger.Log($"YOLO11: Found {results.Count} faces after NMS (from {detections.Count} detections)");

                // Crop faces and calculate quality
                foreach (var face in results)
                {
                    try
                    {
                        face.FaceCrop = CropFace(image, face);
                        face.SharpnessScore = CalculateSharpness(image, face);
                        face.QualityScore = CalculateQuality(face);
                        face.DetectionModel = "yolo11";
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error processing face crop: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"YOLO11 detection error for {imagePath}: {ex.Message}");
            }

            return results;
        });
    }

    private List<FaceDetectionResult> ParseYOLOOutput(
        float[] output, 
        int[] shape,
        float scale, 
        int padX,
        int padY,
        int originalWidth, 
        int originalHeight)
    {
        var results = new List<FaceDetectionResult>();

        // YOLO11 output can be [1, num_det, 6] or [1, 6, num_det]
        int numDetections;
        bool transposed = false;

        if (shape.Length == 3)
        {
            if (shape[1] < shape[2])
            {
                // [1, 6, num_det] - transposed
                numDetections = shape[2];
                transposed = true;
            }
            else
            {
                // [1, num_det, 6] - normal
                numDetections = shape[1];
            }
        }
        else
        {
            Logger.Log($"YOLO11: Unexpected output shape, assuming [1, num_det, 6]");
            numDetections = output.Length / 6;
        }

        for (int i = 0; i < numDetections; i++)
        {
            float cx, cy, w, h, confidence, classId;

            if (transposed)
            {
                // [1, 6, num_det] format
                cx = output[0 * numDetections + i];
                cy = output[1 * numDetections + i];
                w = output[2 * numDetections + i];
                h = output[3 * numDetections + i];
                confidence = output[4 * numDetections + i];
                classId = output[5 * numDetections + i];
            }
            else
            {
                // [1, num_det, 6] format
                int idx = i * 6;
                cx = output[idx + 0];
                cy = output[idx + 1];
                w = output[idx + 2];
                h = output[idx + 3];
                confidence = output[idx + 4];
                classId = output[idx + 5];
            }

            if (confidence < _confidenceThreshold) continue;

            // Convert from normalized coordinates with padding
            // YOLO outputs are in input image space (0-640), need to remove padding and scale
            float x1 = (cx - w / 2 - padX) / scale;
            float y1 = (cy - h / 2 - padY) / scale;
            float x2 = (cx + w / 2 - padX) / scale;
            float y2 = (cy + h / 2 - padY) / scale;

            // Clamp to image bounds
            x1 = Math.Max(0, Math.Min(x1, originalWidth));
            y1 = Math.Max(0, Math.Min(y1, originalHeight));
            x2 = Math.Max(0, Math.Min(x2, originalWidth));
            y2 = Math.Max(0, Math.Min(y2, originalHeight));

            var width = x2 - x1;
            var height = y2 - y1;

            if (width > 10 && height > 10)
            {
                results.Add(new FaceDetectionResult
                {
                    X = (int)x1,
                    Y = (int)y1,
                    Width = (int)width,
                    Height = (int)height,
                    Confidence = confidence
                });
            }
        }

        return results;
    }

    private List<FaceDetectionResult> ApplyNMS(List<FaceDetectionResult> detections, float threshold)
    {
        var results = new List<FaceDetectionResult>();
        var sorted = detections.OrderByDescending(d => d.Confidence).ToList();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            results.Add(best);
            sorted.RemoveAt(0);

            sorted = sorted.Where(d => CalculateIoU(best, d) < threshold).ToList();
        }

        return results;
    }

    private float CalculateIoU(FaceDetectionResult a, FaceDetectionResult b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (x2 < x1 || y2 < y1) return 0f;

        var intersection = (x2 - x1) * (y2 - y1);
        var areaA = a.Width * a.Height;
        var areaB = b.Width * b.Height;
        var union = areaA + areaB - intersection;

        return union > 0 ? intersection / (float)union : 0f;
    }

    private byte[] CropFace(Image<Rgb24> image, FaceDetectionResult face)
    {
        var cropRect = new Rectangle(face.X, face.Y, face.Width, face.Height);
        using var cropped = image.Clone(ctx => ctx.Crop(cropRect));
        
        using var ms = new MemoryStream();
        cropped.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private float CalculateSharpness(Image<Rgb24> image, FaceDetectionResult face)
    {
        // Simplified sharpness using variance of grayscale values
        var cropRect = new Rectangle(face.X, face.Y, face.Width, face.Height);
        using var cropped = image.Clone(ctx => ctx.Crop(cropRect).Grayscale());
        
        var values = new List<byte>();
        cropped.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    values.Add(row[x].R);
                }
            }
        });

        if (values.Count == 0) return 0f;

        var mean = values.Average(v => (float)v);
        var variance = values.Average(v => Math.Pow(v - mean, 2));
        
        return (float)Math.Sqrt(variance) / 255f; // Normalize to 0-1
    }

    private float CalculateQuality(FaceDetectionResult face)
    {
        // Simple quality score: confidence × sharpness × size_factor
        var sizeFactor = Math.Min(1f, (face.Width * face.Height) / (100f * 100f));
        return face.Confidence * face.SharpnessScore * sizeFactor;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session?.Dispose();
        _disposed = true;
    }
}
