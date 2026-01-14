using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Diffusion.Embeddings
{
    /// <summary>
    /// Result containing embedding vectors for an image.
    /// 
    /// Stored embeddings:
    /// - BGE-large-en-v1.5 (1024D) - Semantic text similarity for prompts
    /// - CLIP-ViT-H (1280D) - Visual image similarity
    /// 
    /// Note: CLIP-L/G text embeddings removed - for ComfyUI integration,
    /// conditioning will be generated on-demand from prompts.
    /// </summary>
    public class EmbeddingResult
    {
        public float[]? BgeEmbedding { get; set; }      // 1024D semantic text search
        public float[]? ImageEmbedding { get; set; }    // 1280D visual similarity (CLIP-ViT-H)
        
        // Legacy properties - always null, kept for compatibility
        [Obsolete("CLIP-L text embeddings no longer generated")]
        public float[]? ClipLEmbedding { get; set; }
        
        [Obsolete("CLIP-G text embeddings no longer generated")]
        public float[]? ClipGEmbedding { get; set; }
    }

    /// <summary>
    /// High-level service orchestrating embedding encoders.
    /// Generates BGE text and CLIP-ViT-H image embeddings.
    /// 
    /// Embedding types:
    /// - BGE-large-en-v1.5 (1024D) - Semantic text similarity for prompts, tags, captions
    /// - CLIP-ViT-H (1280D) - Visual image similarity
    /// 
    /// Note: CLIP-L/G text encoders removed - their embeddings are effectively just
    /// tokenized prompts that need transformer inference anyway. For ComfyUI integration,
    /// conditioning will be generated on-demand.
    /// </summary>
    public class EmbeddingService : IDisposable
    {
        private readonly BGETextEncoder _bgeEncoder;
        private readonly CLIPVisionEncoder _clipVisionEncoder;
        private bool _disposed = false;

        public EmbeddingService(
            string bgeModelPath,
            string bgeVocabPath,
            string clipVisionModelPath,
            int bgeGpuDevice = 0,
            int clipVisionGpuDevice = 0)
        {
            _bgeEncoder = new BGETextEncoder(bgeModelPath, bgeVocabPath, bgeGpuDevice);
            _clipVisionEncoder = new CLIPVisionEncoder(clipVisionModelPath, clipVisionGpuDevice);
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
                config.ClipVisionModelPath,
                config.BgeGpuDevice,
                config.ClipVisionGpuDevice
            );
        }

        /// <summary>
        /// Generate all embeddings for a single image with its prompt.
        /// Uses parallel execution for BGE text and CLIP-ViT-H vision.
        /// </summary>
        public async Task<EmbeddingResult> GenerateAllEmbeddingsAsync(
            string prompt,
            string imagePath)
        {
            // Run both encoders in parallel
            var bgeTask = _bgeEncoder.EncodeAsync(prompt);
            var visionTask = _clipVisionEncoder.EncodeAsync(imagePath);

            await Task.WhenAll(bgeTask, visionTask);

            return new EmbeddingResult
            {
                BgeEmbedding = await bgeTask,
                ImageEmbedding = await visionTask
            };
        }

        /// <summary>
        /// Generate BGE text embedding only (for prompts/captions/tags similarity search).
        /// </summary>
        public async Task<float[]> GenerateTextEmbeddingAsync(string text)
        {
            return await _bgeEncoder.EncodeAsync(text);
        }

        /// <summary>
        /// Generate CLIP-ViT-H image embedding only (for visual similarity search).
        /// </summary>
        public async Task<float[]> GenerateImageEmbeddingAsync(string imagePath)
        {
            return await _clipVisionEncoder.EncodeAsync(imagePath);
        }

        /// <summary>
        /// Batch process multiple images for maximum GPU efficiency.
        /// </summary>
        public async Task<EmbeddingResult[]> GenerateBatchEmbeddingsAsync(
            IEnumerable<(string prompt, string imagePath)> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0) return Array.Empty<EmbeddingResult>();

            // Extract unique texts for deduplication
            var uniquePrompts = itemList.Select(x => x.prompt).Distinct().ToList();
            var imagePaths = itemList.Select(x => x.imagePath).ToList();

            // Process in parallel
            var textTask = _bgeEncoder.EncodeBatchAsync(uniquePrompts);
            var imageTask = _clipVisionEncoder.EncodeBatchAsync(imagePaths);

            await Task.WhenAll(textTask, imageTask);

            var bgeEmbeddings = await textTask;
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
                _clipVisionEncoder?.Dispose();
                _disposed = true;
            }
        }
    }
}
