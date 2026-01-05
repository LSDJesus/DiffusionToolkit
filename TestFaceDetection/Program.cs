using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Diffusion.FaceDetection.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace TestFaceDetection;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Face Detection Pipeline Test ===\n");

        // Test integrated Face Detection Service
        Console.WriteLine("Testing Integrated Face Detection Service...");
        await TestFaceDetectionService();

        Console.WriteLine("\n=== All tests completed ===");
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    static async Task TestYOLO11Detection()
    {
        // Deprecated - use integrated service
    }

    static async Task TestArcFaceEmbedding()
    {
        // Deprecated - use integrated service
    }

    static async Task TestFaceDetectionService()
    {
        try
        {
            // Check model paths
            var modelsDir = Path.Combine("models");
            var config = FaceDetectionConfig.CreateDefault(modelsDir);
            
            if (!File.Exists(config.YoloModelPath) || !File.Exists(config.ArcFaceModelPath))
            {
                Console.WriteLine("  ❌ Missing models. Need both:");
                Console.WriteLine($"     - {config.YoloModelPath}");
                Console.WriteLine($"     - {config.ArcFaceModelPath}");
                return;
            }

            // Initialize service
            using var service = FaceDetectionService.FromConfig(config);
            Console.WriteLine("  ✓ Service initialized");

            // Test with sample image
            var testImagePath = "test_face.jpg";
            if (File.Exists(testImagePath))
            {
                var result = await service.ProcessImageAsync(testImagePath);
                
                Console.WriteLine($"  ✓ Processed image:");
                Console.WriteLine($"     - Image: {result.ImagePath}");
                Console.WriteLine($"     - Dimensions: {result.ImageWidth}x{result.ImageHeight}");
                Console.WriteLine($"     - Faces detected: {result.Faces.Count}");
                Console.WriteLine($"     - Processing time: {result.ProcessingTimeMs:F2}ms");
                
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    Console.WriteLine($"     - Error: {result.ErrorMessage}");
                }
                
                if (result.Faces.Any())
                {
                    Console.WriteLine("     - Face details:");
                    foreach (var face in result.Faces.Take(3))
                    {
                        Console.WriteLine($"       • Confidence={face.Confidence:F3}, " +
                                        $"Quality={face.QualityScore:F3}, " +
                                        $"Crop={face.FaceCrop?.Length ?? 0} bytes, " +
                                        $"Embedding={face.ArcFaceEmbedding?.Length ?? 0}D");
                    }
                }
            }
            else
            {
                Console.WriteLine($"  ⚠ No test image found ({testImagePath})");
                Console.WriteLine("     Create a test image with faces to test the pipeline");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"     Inner: {ex.InnerException.Message}");
            }
        }
    }
}
