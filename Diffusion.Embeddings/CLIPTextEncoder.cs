using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;

namespace Diffusion.Embeddings;

/// <summary>
/// CLIP text encoder (CLIP-L or CLIP-G) using ONNX Runtime GPU.
/// Used for SDXL text conditioning in ComfyUI workflows.
/// </summary>
public class CLIPTextEncoder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly CLIPTokenizer _tokenizer;
    private readonly int _maxLength = 77;  // CLIP uses 77 tokens
    private readonly int _embeddingDim;
    private readonly string _modelName;
    private bool _disposed;

    /// <summary>
    /// Initialize CLIP text encoder with ONNX model.
    /// </summary>
    /// <param name="modelPath">Path to model.onnx file</param>
    /// <param name="vocabPath">Path to vocab.json file (CLIP BPE vocab)</param>
    /// <param name="mergesPath">Path to merges.txt file (CLIP BPE merge rules)</param>
    /// <param name="embeddingDim">Embedding dimension (768 for CLIP-L, 1280 for CLIP-G)</param>
    /// <param name="modelName">Model name for logging (e.g., "CLIP-L", "CLIP-G")</param>
    /// <param name="deviceId">CUDA device ID</param>
    public CLIPTextEncoder(string modelPath, string vocabPath, string mergesPath, int embeddingDim, string modelName, int deviceId = 1)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"{modelName} ONNX model not found: {modelPath}");
        
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Vocabulary file not found: {vocabPath}");
        
        if (!File.Exists(mergesPath))
            throw new FileNotFoundException($"Merges file not found: {mergesPath}");

        _embeddingDim = embeddingDim;
        _modelName = modelName;

        // Use memory-efficient CUDA session options
        var sessionOptions = OnnxSessionHelper.CreateCudaSessionOptions(deviceId, memoryEfficientMode: true);

        _session = new InferenceSession(modelPath, sessionOptions);
        _tokenizer = new CLIPTokenizer(vocabPath, mergesPath);

        Console.WriteLine($"✓ {_modelName} loaded on GPU {deviceId} ({_embeddingDim}D embeddings)");
    }

    /// <summary>
    /// Encode single text to embedding vector.
    /// </summary>
    public async Task<float[]> EncodeAsync(string text)
    {
        var batch = await EncodeBatchAsync(new[] { text });
        return batch[0];
    }

    /// <summary>
    /// Encode batch of texts to embedding vectors.
    /// </summary>
    public async Task<List<float[]>> EncodeBatchAsync(IEnumerable<string> texts)
    {
        return await Task.Run(() =>
        {
            var textList = texts.ToList();
            if (textList.Count == 0)
                return new List<float[]>();

            // Tokenize all texts (CLIP uses fixed 77 tokens)
            var batchSize = textList.Count;
            var inputIdsList = new List<long>();

            foreach (var text in textList)
            {
                var inputIds = _tokenizer.Encode(text, _maxLength);
                
                // CLIP requires exactly 77 tokens (already padded by tokenizer)
                foreach (var id in inputIds)
                {
                    inputIdsList.Add(id);
                }
            }

            // Create ONNX tensor
            var inputIdsTensor = new DenseTensor<long>(inputIdsList.ToArray(), new[] { batchSize, _maxLength });

            // Run inference
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor)
            };

            using var results = _session.Run(inputs);
            
            // Get last_hidden_state output (preferred for CLIP text embeddings)
            var output = results.FirstOrDefault(r => r.Name == "last_hidden_state");
            if (output == null)
            {
                throw new InvalidOperationException("CLIP model does not have 'last_hidden_state' output");
            }
            
            var outputTensor = output.AsTensor<float>();
            
            // Copy tensor data to CPU memory
            var outputData = outputTensor.ToArray();

            // Extract embeddings (shape is [batch_size, embedding_dim])
            var embeddings = new List<float[]>();
            for (int i = 0; i < batchSize; i++)
            {
                var embedding = new float[_embeddingDim];
                Array.Copy(outputData, i * _embeddingDim, embedding, 0, _embeddingDim);

                // Normalize embedding
                Normalize(embedding);
                embeddings.Add(embedding);
            }

            return embeddings;
        });
    }

    private void Normalize(float[] vector)
    {
        double sum = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        double norm = Math.Sqrt(sum);
        if (norm > 1e-12)
        {
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }
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
