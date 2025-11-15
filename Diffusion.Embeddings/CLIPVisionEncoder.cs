using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Diffusion.Embeddings
{
    /// <summary>
    /// ONNX GPU encoder for CLIP-ViT-H vision model (1280D image embeddings).
    /// Processes images and generates normalized embedding vectors for visual similarity search.
    /// </summary>
    public class CLIPVisionEncoder : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly int _imageSize = 224;
        private readonly int _embeddingDim = 1280; // LAION CLIP-ViT-H-14 outputs 1280D
        private readonly string _modelName = "CLIP-ViT-H";

        // ImageNet normalization values (standard for CLIP)
        private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
        private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

        public CLIPVisionEncoder(string modelPath, int deviceId = 0)
        {
            if (!System.IO.File.Exists(modelPath))
            {
                throw new System.IO.FileNotFoundException($"ONNX model not found: {modelPath}");
            }

            var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_PARALLEL,
                InterOpNumThreads = 2,
                IntraOpNumThreads = 4
            };

            try
            {
                // Try CUDA first
                sessionOptions.AppendExecutionProvider_CUDA(deviceId);
            }
            catch
            {
                // Fallback to CPU if CUDA unavailable
                Console.WriteLine($"Warning: CUDA provider not available for {_modelName}, using CPU");
            }

            _session = new InferenceSession(modelPath, sessionOptions);
        }

        /// <summary>
        /// Encode a single image to a 1280D embedding vector.
        /// </summary>
        public async Task<float[]> EncodeAsync(string imagePath)
        {
            var results = await EncodeBatchAsync(new[] { imagePath });
            return results[0];
        }

        /// <summary>
        /// Encode a batch of images to 1280D embedding vectors.
        /// Recommended batch size: 16-32 images for optimal GPU utilization.
        /// </summary>
        public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> imagePaths)
        {
            var imageList = imagePaths.ToList();
            if (imageList.Count == 0) return Array.Empty<float[]>();

            return await Task.Run(() =>
            {
                // Load and preprocess images
                var pixelTensors = new List<float[,,]>();
                foreach (var imagePath in imageList)
                {
                    var pixels = PreprocessImage(imagePath);
                    pixelTensors.Add(pixels);
                }

                int batchSize = pixelTensors.Count;

                // Create input tensor [batch_size, channels, height, width]
                var inputData = new List<float>();
                for (int b = 0; b < batchSize; b++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        for (int h = 0; h < _imageSize; h++)
                        {
                            for (int w = 0; w < _imageSize; w++)
                            {
                                inputData.Add(pixelTensors[b][c, h, w]);
                            }
                        }
                    }
                }

                var inputTensor = new DenseTensor<float>(
                    inputData.ToArray(),
                    new[] { batchSize, 3, _imageSize, _imageSize }
                );

                // Create input
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("pixel_values", inputTensor)
                };

                // Run inference
                using var results = _session.Run(inputs);
                
                // Extract pooler_output (first output)
                var outputTensor = results.First().AsEnumerable<float>().ToArray();

                // Split into individual embeddings and normalize
                var embeddings = new float[batchSize][];
                for (int i = 0; i < batchSize; i++)
                {
                    var embedding = new float[_embeddingDim];
                    Array.Copy(outputTensor, i * _embeddingDim, embedding, 0, _embeddingDim);
                    embeddings[i] = Normalize(embedding);
                }

                return embeddings;
            });
        }

        /// <summary>
        /// Preprocess an image: resize to 224x224, convert to RGB, normalize with ImageNet stats.
        /// </summary>
        private float[,,] PreprocessImage(string imagePath)
        {
            using var image = Image.Load<Rgb24>(imagePath);
            
            // Resize to 224x224
            image.Mutate(x => x.Resize(_imageSize, _imageSize));

            var pixels = new float[3, _imageSize, _imageSize];

            // Convert to normalized float tensor
            for (int y = 0; y < _imageSize; y++)
            {
                for (int x = 0; x < _imageSize; x++)
                {
                    var pixel = image[x, y];
                    
                    // Normalize with ImageNet stats
                    pixels[0, y, x] = (pixel.R / 255.0f - Mean[0]) / Std[0];
                    pixels[1, y, x] = (pixel.G / 255.0f - Mean[1]) / Std[1];
                    pixels[2, y, x] = (pixel.B / 255.0f - Mean[2]) / Std[2];
                }
            }

            return pixels;
        }

        /// <summary>
        /// L2 normalize embedding vector to unit length for cosine similarity.
        /// </summary>
        private float[] Normalize(float[] vector)
        {
            double sumSquares = 0;
            foreach (var val in vector)
            {
                sumSquares += val * val;
            }

            var norm = (float)Math.Sqrt(sumSquares);
            if (norm < 1e-12f) return vector; // Avoid division by zero

            var normalized = new float[vector.Length];
            for (int i = 0; i < vector.Length; i++)
            {
                normalized[i] = vector[i] / norm;
            }

            return normalized;
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}
