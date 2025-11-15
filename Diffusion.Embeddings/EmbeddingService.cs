using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Diffusion.Embeddings
{
    /// <summary>
    /// Result containing all 5 embedding vectors for an image.
    /// </summary>
    public class EmbeddingResult
    {
        public required float[] BgeEmbedding { get; set; }      // 1024D semantic text search
        public required float[] ClipLEmbedding { get; set; }    // 768D SDXL text_l
        public required float[] ClipGEmbedding { get; set; }    // 1280D SDXL text_g
        public float[]? ClipHEmbedding { get; set; }            // 1280D CLIP-H text (optional)
        public required float[] ImageEmbedding { get; set; }    // 1280D visual similarity
    }

    /// <summary>
    /// High-level service orchestrating all embedding encoders with dual-GPU parallelism.
    /// Generates all 5 embeddings (4 text + 1 image) for an AI-generated image.
    /// </summary>
    public class EmbeddingService : IDisposable
    {
        private readonly BGETextEncoder _bgeEncoder;
        private readonly CLIPTextEncoder _clipLEncoder;
        private readonly CLIPTextEncoder _clipGEncoder;
        private readonly CLIPVisionEncoder _clipVisionEncoder;
        private bool _disposed = false;

        public EmbeddingService(
            string bgeModelPath,
            string bgeVocabPath,
            string clipLModelPath,
            string clipLVocabPath,
            string clipLMergesPath,
            string clipGModelPath,
            string clipGVocabPath,
            string clipGMergesPath,
            string clipVisionModelPath,
            int bgeGpuDevice = 0,
            int clipLGpuDevice = 1,
            int clipGGpuDevice = 1,
            int clipVisionGpuDevice = 0)
        {
            // GPU 0 (RTX 5090): BGE + CLIP Vision
            _bgeEncoder = new BGETextEncoder(bgeModelPath, bgeVocabPath, bgeGpuDevice);
            _clipVisionEncoder = new CLIPVisionEncoder(clipVisionModelPath, clipVisionGpuDevice);

            // GPU 1 (RTX 3080 Ti): CLIP-L + CLIP-G
            _clipLEncoder = new CLIPTextEncoder(clipLModelPath, clipLVocabPath, clipLMergesPath, 768, "CLIP-L", clipLGpuDevice);
            _clipGEncoder = new CLIPTextEncoder(clipGModelPath, clipGVocabPath, clipGMergesPath, 1280, "CLIP-G", clipGGpuDevice);
        }

        /// <summary>
        /// Create from configuration object.
        /// </summary>
        public static EmbeddingService FromConfig(EmbeddingConfig config)
        {
            config.Validate();

            return new EmbeddingService(
                config.BgeModelPath,
                config.BgeVocabPath,
                config.ClipLModelPath,
                config.ClipLVocabPath,
                config.ClipLMergesPath,
                config.ClipGModelPath,
                config.ClipGVocabPath,
                config.ClipGMergesPath,
                config.ClipVisionModelPath,
                config.BgeGpuDevice,
                config.ClipLGpuDevice,
                config.ClipGGpuDevice,
                config.ClipVisionGpuDevice
            );
        }

        /// <summary>
        /// Generate all embeddings for a single image with its prompt and negative prompt.
        /// Uses parallel execution across both GPUs for maximum throughput.
        /// </summary>
        public async Task<EmbeddingResult> GenerateAllEmbeddingsAsync(
            string prompt,
            string negativePrompt,
            string imagePath)
        {
            // Run all encoders in parallel (dual-GPU utilization)
            var tasks = new[]
            {
                _bgeEncoder.EncodeAsync(prompt),
                _clipLEncoder.EncodeAsync(prompt),
                _clipGEncoder.EncodeAsync(prompt),
                _clipVisionEncoder.EncodeAsync(imagePath)
            };

            await Task.WhenAll(tasks);

            return new EmbeddingResult
            {
                BgeEmbedding = await tasks[0],
                ClipLEmbedding = await tasks[1],
                ClipGEmbedding = await tasks[2],
                ImageEmbedding = await tasks[3]
            };
        }

        /// <summary>
        /// Generate text embeddings only (for prompts/negative prompts).
        /// Returns BGE, CLIP-L, and CLIP-G embeddings in parallel.
        /// </summary>
        public async Task<(float[] bge, float[] clipL, float[] clipG)> GenerateTextEmbeddingsAsync(string text)
        {
            var tasks = new[]
            {
                _bgeEncoder.EncodeAsync(text),
                _clipLEncoder.EncodeAsync(text),
                _clipGEncoder.EncodeAsync(text)
            };

            await Task.WhenAll(tasks);

            return (await tasks[0], await tasks[1], await tasks[2]);
        }

        /// <summary>
        /// Generate image embedding only (for visual similarity search).
        /// </summary>
        public async Task<float[]> GenerateImageEmbeddingAsync(string imagePath)
        {
            return await _clipVisionEncoder.EncodeAsync(imagePath);
        }

        /// <summary>
        /// Batch process multiple images for maximum GPU efficiency.
        /// Recommended batch size: 32-64 images depending on GPU memory.
        /// </summary>
        public async Task<EmbeddingResult[]> GenerateBatchEmbeddingsAsync(
            IEnumerable<(string prompt, string negativePrompt, string imagePath)> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0) return Array.Empty<EmbeddingResult>();

            // Extract unique texts for deduplication
            var uniquePrompts = itemList.Select(x => x.prompt).Distinct().ToList();
            var imagePaths = itemList.Select(x => x.imagePath).ToList();

            // Process in parallel across both GPUs
            var textTask = Task.WhenAll(
                _bgeEncoder.EncodeBatchAsync(uniquePrompts),
                _clipLEncoder.EncodeBatchAsync(uniquePrompts),
                _clipGEncoder.EncodeBatchAsync(uniquePrompts)
            );
            var imageTask = _clipVisionEncoder.EncodeBatchAsync(imagePaths);

            await Task.WhenAll(textTask, imageTask);

            var textResults = await textTask;
            var bgeEmbeddings = textResults[0];
            var clipLEmbeddings = textResults[1];
            var clipGEmbeddings = textResults[2];
            var imageEmbeddings = await imageTask;

            // Build lookup for text embeddings
            var textLookup = new Dictionary<string, int>();
            for (int i = 0; i < uniquePrompts.Count; i++)
            {
                textLookup[uniquePrompts[i]] = i;
            }

            // Combine results
            var results = new EmbeddingResult[itemList.Count];
            for (int i = 0; i < itemList.Count; i++)
            {
                var textIdx = textLookup[itemList[i].prompt];
                results[i] = new EmbeddingResult
                {
                    BgeEmbedding = bgeEmbeddings[textIdx],
                    ClipLEmbedding = clipLEmbeddings[textIdx],
                    ClipGEmbedding = clipGEmbeddings[textIdx],
                    ImageEmbedding = imageEmbeddings[i]
                };
            }

            return results;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _bgeEncoder?.Dispose();
                _clipLEncoder?.Dispose();
                _clipGEncoder?.Dispose();
                _clipVisionEncoder?.Dispose();
                _disposed = true;
            }
        }
    }
}
