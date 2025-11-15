using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;

namespace Diffusion.Embeddings;

/// <summary>
/// BGE-large-en-v1.5 text encoder using ONNX Runtime GPU.
/// Produces 1024-dimensional embeddings for semantic text search.
/// </summary>
public class BGETextEncoder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly SimpleTokenizer _tokenizer;
    private readonly int _maxLength = 512;
    private readonly int _embeddingDim = 1024;
    private bool _disposed;

    /// <summary>
    /// Initialize BGE encoder with ONNX model.
    /// </summary>
    /// <param name="modelPath">Path to model.onnx file</param>
    /// <param name="vocabPath">Path to vocab.txt file</param>
    /// <param name="deviceId">CUDA device ID (0 = RTX 5090, 1 = RTX 3080 Ti)</param>
    public BGETextEncoder(string modelPath, string vocabPath, int deviceId = 0)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"BGE ONNX model not found: {modelPath}");
        
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Vocabulary file not found: {vocabPath}");

        // Configure ONNX Runtime for GPU
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            InterOpNumThreads = 2,
            IntraOpNumThreads = 4
        };

        // Use CUDA execution provider
        sessionOptions.AppendExecutionProvider_CUDA(deviceId);
        sessionOptions.AppendExecutionProvider_CPU(); // Fallback

        _session = new InferenceSession(modelPath, sessionOptions);
        _tokenizer = new SimpleTokenizer(vocabPath);

        Console.WriteLine($"✓ BGE-large-en-v1.5 loaded on GPU {deviceId} (1024D embeddings)");
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
    /// More efficient than encoding individually.
    /// </summary>
    /// <param name="texts">Texts to encode (up to 64 recommended)</param>
    public async Task<List<float[]>> EncodeBatchAsync(IEnumerable<string> texts)
    {
        return await Task.Run(() =>
        {
            var textList = texts.ToList();
            if (textList.Count == 0)
                return new List<float[]>();

            // Tokenize all texts
            var tokenizedBatch = new List<(int[] inputIds, int[] attentionMask)>();
            int maxSeqLen = 0;

            foreach (var text in textList)
            {
                var (inputIds, attentionMask) = _tokenizer.Encode(text, _maxLength);
                tokenizedBatch.Add((inputIds, attentionMask));
                maxSeqLen = Math.Max(maxSeqLen, inputIds.Length);
            }

            // Convert to tensors
            var batchSize = textList.Count;
            var inputIdsList = new List<long>();
            var attentionMaskList = new List<long>();

            for (int i = 0; i < batchSize; i++)
            {
                var (inputIds, attentionMask) = tokenizedBatch[i];
                for (int j = 0; j < maxSeqLen; j++)
                {
                    if (j < inputIds.Length)
                    {
                        inputIdsList.Add(inputIds[j]);
                        attentionMaskList.Add(attentionMask[j]);
                    }
                    else
                    {
                        inputIdsList.Add(0); // Padding
                        attentionMaskList.Add(0);
                    }
                }
            }

            // Create ONNX tensors
            var inputIdsTensor = new DenseTensor<long>(inputIdsList.ToArray(), new[] { batchSize, maxSeqLen });
            var attentionMaskTensor = new DenseTensor<long>(attentionMaskList.ToArray(), new[] { batchSize, maxSeqLen });

            // Run inference
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
            };

            using var results = _session.Run(inputs);
            
            // Get output tensor (usually first output is pooler_output or last_hidden_state)
            var outputTensor = results.First().AsTensor<float>();
            
            // Copy tensor data to CPU memory
            var outputData = outputTensor.ToArray();
            
            // Extract embeddings
            var embeddings = new List<float[]>();
            for (int i = 0; i < batchSize; i++)
            {
                var embedding = new float[_embeddingDim];
                Array.Copy(outputData, i * _embeddingDim, embedding, 0, _embeddingDim);

                // Normalize embedding (BGE uses cosine similarity)
                Normalize(embedding);
                embeddings.Add(embedding);
            }

            return embeddings;
        });
    }

    /// <summary>
    /// Normalize vector to unit length for cosine similarity.
    /// </summary>
    private void Normalize(float[] vector)
    {
        double sum = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        double norm = Math.Sqrt(sum);
        if (norm > 1e-12) // Avoid division by zero
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

/// <summary>
/// Simple BERT tokenizer for BGE models.
/// Loads vocabulary from vocab.txt file.
/// </summary>
internal class SimpleTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _clsTokenId = 101;
    private readonly int _sepTokenId = 102;
    private readonly int _padTokenId = 0;
    private readonly int _unkTokenId = 100;

    public SimpleTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>();
        var lines = File.ReadAllLines(vocabPath);
        for (int i = 0; i < lines.Length; i++)
        {
            _vocab[lines[i].Trim()] = i;
        }
    }

    public (int[] inputIds, int[] attentionMask) Encode(string text, int maxLength)
    {
        // Simple tokenization (split on whitespace and punctuation)
        var tokens = TokenizeText(text);
        
        // Convert to IDs
        var inputIds = new List<int> { _clsTokenId };
        foreach (var token in tokens)
        {
            if (inputIds.Count >= maxLength - 1)
                break;
            
            var tokenId = _vocab.ContainsKey(token) ? _vocab[token] : _unkTokenId;
            inputIds.Add(tokenId);
        }
        inputIds.Add(_sepTokenId);

        // Create attention mask (1 for real tokens, 0 for padding)
        var attentionMask = Enumerable.Repeat(1, inputIds.Count).ToArray();

        return (inputIds.ToArray(), attentionMask);
    }

    private List<string> TokenizeText(string text)
    {
        // Basic tokenization: lowercase and split
        text = text.ToLowerInvariant();
        var tokens = new List<string>();
        
        var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            // WordPiece tokenization would go here
            // For now, use simple word-level tokens
            tokens.Add(word);
        }

        return tokens;
    }
}
