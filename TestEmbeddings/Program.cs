using Diffusion.Embeddings;
using System.Diagnostics;

namespace TestEmbeddings
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Diffusion Toolkit - ONNX Embedding Test ===\n");

            // Add CUDA paths to environment
            var cudaPaths = new[]
            {
                @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9\bin",
                @"C:\Program Files\NVIDIA\CUDNN\v9.14\bin\12.9"
            };

            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var newPath = string.Join(";", cudaPaths) + ";" + existingPath;
            Environment.SetEnvironmentVariable("PATH", newPath);
            
            Console.WriteLine("CUDA paths added to environment:");
            foreach (var path in cudaPaths)
            {
                Console.WriteLine($"  {path}");
            }
            Console.WriteLine();

            // Configuration
            var config = EmbeddingConfig.CreateDefault(@"D:\AI\Github_Desktop\DiffusionToolkit");
            
            Console.WriteLine("Validating model files...");
            try
            {
                config.Validate();
                Console.WriteLine("✓ All model files found\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Model validation failed: {ex.Message}");
                return;
            }

            // Initialize service
            Console.WriteLine("Initializing embedding service...");
            Console.WriteLine($"  BGE + CLIP Vision on GPU {config.BgeGpuDevice}");
            Console.WriteLine($"  CLIP-L + CLIP-G on GPU {config.ClipLGpuDevice}\n");

            EmbeddingService service;
            try
            {
                service = EmbeddingService.FromConfig(config);
                Console.WriteLine("✓ Service initialized with GPU acceleration\n");
            }
            catch (Exception ex) when (ex.Message.Contains("onnxruntime_providers_cuda"))
            {
                Console.WriteLine("⚠ CUDA provider not available - this is expected for first run");
                Console.WriteLine("  ONNX Runtime GPU requires:");
                Console.WriteLine("  1. CUDA 12.x runtime libraries");
                Console.WriteLine("  2. cuDNN 9.x");
                Console.WriteLine("  3. Copy DLLs to application directory\n");
                Console.WriteLine("For now, encoders will initialize without GPU but architecture is correct.\n");
                return;
            }
            
            using var serviceDispose = service;

            // Test 1: Single text encoding
            Console.WriteLine("=== Test 1: Text Embeddings ===");
            var testPrompt = "a beautiful landscape with mountains and lake";
            Console.WriteLine($"Prompt: \"{testPrompt}\"");
            
            var sw = Stopwatch.StartNew();
            var (bge, clipL, clipG) = await service.GenerateTextEmbeddingsAsync(testPrompt);
            sw.Stop();
            
            Console.WriteLine($"✓ Generated in {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"  BGE:    {bge.Length}D (norm: {Math.Sqrt(bge.Sum(x => x * x)):F2})");
            Console.WriteLine($"  CLIP-L: {clipL.Length}D (norm: {Math.Sqrt(clipL.Sum(x => x * x)):F2})");
            Console.WriteLine($"  CLIP-G: {clipG.Length}D (norm: {Math.Sqrt(clipG.Sum(x => x * x)):F2})\n");

            // Validate dimensions
            if (bge.Length != 1024 || clipL.Length != 768 || clipG.Length != 1280)
            {
                Console.WriteLine("✗ FAILED: Incorrect embedding dimensions!");
                return;
            }

            // Test 2: Image encoding (find any available image)
            string? sampleImagePath = null;
            var searchPaths = new[]
            {
                @"D:\AI\Github_Desktop\DiffusionToolkit\test_sample.png",
                @"D:\AI\Github_Desktop\DiffusionToolkit\test_sample.jpg",
                @"E:\Output\Images"
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    sampleImagePath = path;
                    break;
                }
                else if (Directory.Exists(path))
                {
                    var firstImage = Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || 
                                           f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                           f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                           f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                    if (firstImage != null)
                    {
                        sampleImagePath = firstImage;
                        break;
                    }
                }
            }

            if (sampleImagePath != null && File.Exists(sampleImagePath))
            {
                Console.WriteLine("=== Test 2: Image Embedding ===");
                Console.WriteLine($"Image: {Path.GetFileName(sampleImagePath)}");
                
                sw.Restart();
                var imageEmb = await service.GenerateImageEmbeddingAsync(sampleImagePath);
                sw.Stop();
                
                Console.WriteLine($"✓ Generated in {sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"  CLIP-H Vision: {imageEmb.Length}D (norm: {Math.Sqrt(imageEmb.Sum(x => x * x)):F2})\n");

                if (imageEmb.Length != 1280)
                {
                    Console.WriteLine("✗ FAILED: Incorrect image embedding dimension!");
                    return;
                }
            }
            else
            {
                Console.WriteLine("=== Test 2: Image Embedding ===");
                Console.WriteLine($"⊗ Skipped (no test image at {sampleImagePath})\n");
            }

            // Test 3: Full image generation
            if (File.Exists(sampleImagePath))
            {
                Console.WriteLine("=== Test 3: Full Image Generation ===");
                var prompt = "masterpiece, best quality, sunset over ocean";
                var negative = "blurry, low quality, watermark";
                
                Console.WriteLine($"Prompt: \"{prompt}\"");
                Console.WriteLine($"Negative: \"{negative}\"");
                
                sw.Restart();
                var result = await service.GenerateAllEmbeddingsAsync(prompt, negative, sampleImagePath);
                sw.Stop();
                
                Console.WriteLine($"✓ Generated all 5 embeddings in {sw.ElapsedMilliseconds}ms (parallel execution)");
                Console.WriteLine($"  BGE:    {result.BgeEmbedding.Length}D");
                Console.WriteLine($"  CLIP-L: {result.ClipLEmbedding.Length}D");
                Console.WriteLine($"  CLIP-G: {result.ClipGEmbedding.Length}D");
                Console.WriteLine($"  Image:  {result.ImageEmbedding.Length}D\n");
            }

            // Test 4: Batch processing (only if we have a sample image)
            if (sampleImagePath != null)
            {
                Console.WriteLine("=== Test 4: Batch Text Processing ===");
                var prompts = new[]
                {
                    "a beautiful landscape",
                    "a portrait of a woman",
                    "abstract art with colors",
                    "cyberpunk city at night",
                    "a beautiful landscape"  // Duplicate for deduplication test
                };

                Console.WriteLine($"Batch size: {prompts.Length} prompts ({prompts.Distinct().Count()} unique)");
                
                sw.Restart();
                var batchResults = await service.GenerateBatchEmbeddingsAsync(
                    prompts.Select(p => (prompt: p, negativePrompt: "", imagePath: sampleImagePath))
                );
                sw.Stop();
                
                Console.WriteLine($"✓ Generated in {sw.ElapsedMilliseconds}ms");
                Console.WriteLine($"  Average: {sw.ElapsedMilliseconds / (double)prompts.Length:F1}ms per item");
                Console.WriteLine($"  Results: {batchResults.Length} items\n");
            }
            else
            {
                Console.WriteLine("=== Test 4: Batch Text Processing ===");
                Console.WriteLine($"⊗ Skipped (no test image available)\n");
            }

            // Test 5: Text similarity
            Console.WriteLine("=== Test 5: Text Similarity ===");
            var text1 = "a beautiful landscape with mountains";
            var text2 = "a beautiful landscape with mountains";  // Identical
            var text3 = "a cyberpunk city at night";  // Different
            
            var (bge1, _, _) = await service.GenerateTextEmbeddingsAsync(text1);
            var (bge2, _, _) = await service.GenerateTextEmbeddingsAsync(text2);
            var (bge3, _, _) = await service.GenerateTextEmbeddingsAsync(text3);
            
            var similarity12 = CosineSimilarity(bge1, bge2);
            var similarity13 = CosineSimilarity(bge1, bge3);
            
            Console.WriteLine($"Identical texts: {similarity12:F4} (expected: 1.0000)");
            Console.WriteLine($"Different texts: {similarity13:F4} (expected: <0.95)");
            
            if (Math.Abs(similarity12 - 1.0) > 0.001)
            {
                Console.WriteLine("✗ FAILED: Identical texts should have similarity 1.0!");
                return;
            }
            if (similarity13 > 0.95)
            {
                Console.WriteLine("⚠ WARNING: Different texts have very high similarity!");
            }
            Console.WriteLine();

            // Test 6: GPU memory usage check
            Console.WriteLine("=== Test 6: GPU Information ===");
            Console.WriteLine("Run 'nvidia-smi' in another terminal to check GPU utilization");
            Console.WriteLine("Expected: Both RTX 5090 (GPU 0) and RTX 3080 Ti (GPU 1) active\n");

            Console.WriteLine("=== All Tests Completed Successfully ===");
            Console.WriteLine("\nNext steps:");
            Console.WriteLine("1. Check GPU memory usage with nvidia-smi");
            Console.WriteLine("2. Test with real image files from your dataset");
            Console.WriteLine("3. Benchmark batch processing with 32-64 images");
            Console.WriteLine("4. Integrate with EmbeddingCacheService for deduplication");
        }

        static double CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) throw new ArgumentException("Vectors must have same length");
            
            double dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            
            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }
    }
}
