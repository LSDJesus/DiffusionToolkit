using Diffusion.Common;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// ArcFace encoder for generating 512D face embeddings
/// Compatible with InsightFace models
/// </summary>
public class ArcFaceEncoder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _inputSize;
    private readonly int _embeddingDim;
    private bool _disposed;

    /// <summary>
    /// Initialize ArcFace encoder
    /// </summary>
    /// <param name="modelPath">Path to arcface.onnx model</param>
    /// <param name="gpuDeviceId">GPU device ID</param>
    /// <param name="inputSize">Model input size (typically 112)</param>
    public ArcFaceEncoder(string modelPath, int gpuDeviceId = 0, int inputSize = 112)
    {
        _inputSize = inputSize;
        _embeddingDim = 512; // ArcFace standard

        var sessionOptions = new SessionOptions();
        
        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(gpuDeviceId);
            Logger.Log($"ArcFace: Using CUDA GPU {gpuDeviceId}");
        }
        catch (Exception ex)
        {
            Logger.Log($"ArcFace: CUDA not available ({ex.Message}), falling back to CPU");
        }

        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        _session = new InferenceSession(modelPath, sessionOptions);
        Logger.Log($"ArcFace encoder initialized: {modelPath}");
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
        // Resize to model input size
        using var resized = faceImage.Clone(ctx => ctx.Resize(_inputSize, _inputSize));

        // Convert to tensor (CHW, normalized)
        var inputData = new float[1 * 3 * _inputSize * _inputSize];
        
        for (int y = 0; y < _inputSize; y++)
        {
            var row = resized.GetPixelRowSpan(y);
            for (int x = 0; x < _inputSize; x++)
            {
                var pixel = row[x];
                // RGB order, normalize to [-1, 1]
                inputData[0 * _inputSize * _inputSize + y * _inputSize + x] = (pixel.R - 127.5f) / 127.5f;
                inputData[1 * _inputSize * _inputSize + y * _inputSize + x] = (pixel.G - 127.5f) / 127.5f;
                inputData[2 * _inputSize * _inputSize + y * _inputSize + x] = (pixel.B - 127.5f) / 127.5f;
            }
        }

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
