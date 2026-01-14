using Diffusion.Common;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// CLIP-based face encoder for universal face embeddings.
/// Uses CLIP-ViT-H vision encoder to generate 1280D embeddings.
/// Works across all image styles (realistic, anime, 3D, etc.)
/// </summary>
public class ClipFaceEncoder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _inputSize;
    private readonly int _embeddingDim;
    private bool _disposed;

    // CLIP normalization constants
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    /// <summary>
    /// Initialize CLIP face encoder
    /// </summary>
    /// <param name="modelPath">Path to clip-vit-h vision encoder ONNX model</param>
    /// <param name="gpuDeviceId">GPU device ID</param>
    /// <param name="inputSize">Model input size (typically 224)</param>
    public ClipFaceEncoder(string modelPath, int gpuDeviceId = 0, int inputSize = 224)
    {
        _inputSize = inputSize;
        _embeddingDim = 1280; // CLIP-ViT-H

        var sessionOptions = new SessionOptions();
        
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(gpuDeviceId);
            Logger.Log($"CLIPFace: Using CUDA GPU {gpuDeviceId}");
        }
        catch (Exception ex)
        {
            Logger.Log($"CLIPFace: CUDA not available ({ex.Message}), falling back to CPU");
        }

        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        _session = new InferenceSession(modelPath, sessionOptions);
        Logger.Log($"CLIP face encoder initialized: {modelPath}");
    }

    /// <summary>
    /// Generate embedding from face crop bytes (JPEG)
    /// </summary>
    public async Task<float[]> EncodeAsync(byte[] faceCropBytes)
    {
        return await Task.Run(() =>
        {
            using var ms = new MemoryStream(faceCropBytes);
            using var image = Image.Load<Rgb24>(ms);
            return Encode(image);
        });
    }

    /// <summary>
    /// Generate embedding from face image
    /// </summary>
    public float[] Encode(Image<Rgb24> faceImage)
    {
        // Resize to model input size (center crop for faces)
        using var resized = faceImage.Clone(ctx => 
        {
            // First resize to fit, then center crop
            var scale = Math.Max((float)_inputSize / faceImage.Width, (float)_inputSize / faceImage.Height);
            var newWidth = (int)(faceImage.Width * scale);
            var newHeight = (int)(faceImage.Height * scale);
            
            ctx.Resize(newWidth, newHeight);
            
            // Center crop
            var cropX = (newWidth - _inputSize) / 2;
            var cropY = (newHeight - _inputSize) / 2;
            ctx.Crop(new Rectangle(cropX, cropY, _inputSize, _inputSize));
        });

        // Convert to tensor (CHW, CLIP normalized)
        var inputData = new float[1 * 3 * _inputSize * _inputSize];
        
        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < _inputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < _inputSize; x++)
                {
                    var pixel = row[x];
                    // RGB order, CLIP normalization
                    inputData[0 * _inputSize * _inputSize + y * _inputSize + x] = 
                        ((pixel.R / 255f) - Mean[0]) / Std[0];
                    inputData[1 * _inputSize * _inputSize + y * _inputSize + x] = 
                        ((pixel.G / 255f) - Mean[1]) / Std[1];
                    inputData[2 * _inputSize * _inputSize + y * _inputSize + x] = 
                        ((pixel.B / 255f) - Mean[2]) / Std[2];
                }
            }
        });

        var inputTensor = new DenseTensor<float>(inputData, new[] { 1, 3, _inputSize, _inputSize });
        
        // Get input name from model
        var inputName = _session.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
        };

        // Run inference
        using var results = _session.Run(inputs);
        var embedding = results.First().AsEnumerable<float>().ToArray();

        // L2 normalize
        return Normalize(embedding);
    }

    /// <summary>
    /// Generate embeddings for batch of faces
    /// </summary>
    public async Task<List<float[]>> EncodeBatchAsync(IEnumerable<byte[]> faceCropBytes)
    {
        var results = new List<float[]>();
        
        foreach (var cropBytes in faceCropBytes)
        {
            results.Add(await EncodeAsync(cropBytes));
        }
        
        return results;
    }

    /// <summary>
    /// Calculate cosine similarity between two embeddings
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Embeddings must have same dimension");
        
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private float[] Normalize(float[] embedding)
    {
        float norm = 0;
        for (int i = 0; i < embedding.Length; i++)
        {
            norm += embedding[i] * embedding[i];
        }
        norm = MathF.Sqrt(norm);
        
        if (norm > 0)
        {
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= norm;
            }
        }
        
        return embedding;
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
