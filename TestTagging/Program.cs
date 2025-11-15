using Diffusion.Tagging.Services;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("JoyTag Test Program");
        Console.WriteLine("==================\n");

        // Paths
        var modelPath = @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtModel.onnx";
        var tagsPath = @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtTags.csv";

        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"ERROR: Model not found at {modelPath}");
            return;
        }

        if (!File.Exists(tagsPath))
        {
            Console.WriteLine($"ERROR: Tags CSV not found at {tagsPath}");
            return;
        }

        // Ask for image path
        Console.Write("Enter path to test image: ");
        var imagePath = Console.ReadLine()?.Trim('"') ?? "";

        if (!File.Exists(imagePath))
        {
            Console.WriteLine($"ERROR: Image not found at {imagePath}");
            return;
        }

        Console.WriteLine($"\nInitializing JoyTag...");
        var joyTagService = new JoyTagService(modelPath, tagsPath);
        joyTagService.Threshold = 0.2f;

        try
        {
            await joyTagService.InitializeAsync();
            Console.WriteLine("Model loaded successfully!\n");

            Console.WriteLine($"Tagging image: {Path.GetFileName(imagePath)}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            var tags = await joyTagService.TagImageAsync(imagePath);
            
            sw.Stop();

            Console.WriteLine($"\nProcessing time: {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"Found {tags.Count} tags above threshold {joyTagService.Threshold:P0}:\n");

            foreach (var tag in tags.Take(20)) // Show top 20
            {
                Console.WriteLine($"  {tag.Tag,-40} {tag.Confidence:P1}");
            }

            if (tags.Count > 20)
            {
                Console.WriteLine($"\n... and {tags.Count - 20} more tags");
            }

            // Get comma-separated string
            Console.WriteLine("\n\nComma-separated tags:");
            var tagString = await joyTagService.GetTagStringAsync(imagePath);
            Console.WriteLine(tagString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            joyTagService.Dispose();
        }

        Console.WriteLine("\n\nPress any key to exit...");
        Console.ReadKey();
    }
}
