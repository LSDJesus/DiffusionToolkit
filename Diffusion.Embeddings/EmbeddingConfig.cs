using System;
using System.IO;

namespace Diffusion.Embeddings
{
    /// <summary>
    /// Configuration for embedding model paths and GPU device assignments.
    /// </summary>
    public class EmbeddingConfig
    {
        // Model paths
        public string BgeModelPath { get; set; } = string.Empty;
        public string BgeVocabPath { get; set; } = string.Empty;
        public string ClipLModelPath { get; set; } = string.Empty;
        public string ClipLVocabPath { get; set; } = string.Empty;
        public string ClipLMergesPath { get; set; } = string.Empty;
        public string ClipGModelPath { get; set; } = string.Empty;
        public string ClipGVocabPath { get; set; } = string.Empty;
        public string ClipGMergesPath { get; set; } = string.Empty;
        public string ClipVisionModelPath { get; set; } = string.Empty;

        // GPU device assignments
        public int BgeGpuDevice { get; set; } = 0;
        public int ClipLGpuDevice { get; set; } = 1;
        public int ClipGGpuDevice { get; set; } = 1;
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

                // CLIP-L (SDXL text_l encoder)
                ClipLModelPath = Path.Combine(modelsDir, "clip-l", "model.onnx"),
                ClipLVocabPath = Path.Combine(modelsDir, "clip-l", "vocab.json"),
                ClipLMergesPath = Path.Combine(modelsDir, "clip-l", "merges.txt"),

                // CLIP-G (SDXL text_g encoder)
                ClipGModelPath = Path.Combine(modelsDir, "clip-g", "model.onnx"),
                ClipGVocabPath = Path.Combine(modelsDir, "clip-g", "vocab.json"),
                ClipGMergesPath = Path.Combine(modelsDir, "clip-g", "merges.txt"),

                // CLIP-ViT-H (visual similarity)
                ClipVisionModelPath = Path.Combine(modelsDir, "clip-vit-h", "model.onnx"),

                // GPU assignments - all on GPU 0 (RTX 5090, compute 12.0)
                BgeGpuDevice = 0,        // RTX 5090
                ClipVisionGpuDevice = 0, // RTX 5090
                ClipLGpuDevice = 0,      // RTX 5090 (GPU 1 incompatible - compute 8.6)
                ClipGGpuDevice = 0,      // RTX 5090 (GPU 1 incompatible - compute 8.6)

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
            ValidateFile(ClipLModelPath, "CLIP-L model");
            // Note: CLIP vocab files might not exist (tokenizer built-in)
            ValidateFile(ClipGModelPath, "CLIP-G model");
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
