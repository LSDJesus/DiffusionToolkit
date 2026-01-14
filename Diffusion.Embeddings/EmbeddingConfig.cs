using System;
using System.IO;

namespace Diffusion.Embeddings
{
    /// <summary>
    /// Configuration for embedding model paths and GPU device assignments.
    /// 
    /// Embedding types:
    /// - BGE-large-en-v1.5 (1024D) - Semantic text similarity for prompts, tags, captions
    /// - CLIP-ViT-H (1280D) - Visual image similarity
    /// 
    /// Note: CLIP-L/G text encoders removed - for ComfyUI integration,
    /// conditioning will be generated on-demand from prompts.
    /// </summary>
    public class EmbeddingConfig
    {
        // Model paths
        public string BgeModelPath { get; set; } = string.Empty;
        public string BgeVocabPath { get; set; } = string.Empty;
        public string ClipVisionModelPath { get; set; } = string.Empty;

        // GPU device assignments
        public int BgeGpuDevice { get; set; } = 0;
        public int ClipVisionGpuDevice { get; set; } = 0;

        // Batch sizes
        public int TextBatchSize { get; set; } = 64;
        public int ImageBatchSize { get; set; } = 32;

        /// <summary>
        /// Create default configuration pointing to models/onnx directory.
        /// </summary>
        public static EmbeddingConfig CreateDefault(string baseDirectory)
        {
            var modelsDir = Path.Combine(baseDirectory, "models", "onnx");

            return new EmbeddingConfig
            {
                // BGE-large-en-v1.5 (semantic text search)
                BgeModelPath = Path.Combine(modelsDir, "bge-large-en-v1.5", "model.onnx"),
                BgeVocabPath = Path.Combine(modelsDir, "bge-large-en-v1.5", "vocab.txt"),

                // CLIP-ViT-H (visual similarity)
                ClipVisionModelPath = Path.Combine(modelsDir, "clip-vit-h", "model.onnx"),

                // GPU assignments
                BgeGpuDevice = 0,
                ClipVisionGpuDevice = 0,

                // Batch sizes
                TextBatchSize = 64,
                ImageBatchSize = 32
            };
        }

        /// <summary>
        /// Validate that all model files exist.
        /// </summary>
        public void Validate()
        {
            ValidateFile(BgeModelPath, "BGE model");
            ValidateFile(BgeVocabPath, "BGE vocabulary");
            ValidateFile(ClipVisionModelPath, "CLIP Vision model");
        }

        private void ValidateFile(string path, string name)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException($"{name} path is not configured");
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"{name} not found: {path}");
            }
        }
    }
}
