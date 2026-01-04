using Diffusion.Tagging.Services;
using Diffusion.Captioning.Services;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("JoyTag & JoyCaption Test Harness");
        Console.WriteLine("=================================\n");

        // Test options
        Console.WriteLine("Test options:");
        Console.WriteLine("1. Single image (JoyTag + JoyCaption)");
        Console.WriteLine("2. Batch JoyTag (105 images)");
        Console.WriteLine("3. Batch WD 1.4 Tagger (105 images)");
        Console.WriteLine("4. Batch WDv3 Tagger (105 images)");
        Console.WriteLine("5. Batch WDv3 Large Tagger (105 images)");
        Console.WriteLine("6. Compare all taggers (10 images)");
        Console.WriteLine("7. Batch JoyCaption (10 images)");
        Console.WriteLine("8. Compare JoyCaption prompts (5 images × 8 prompts)");
        Console.WriteLine("9. VRAM usage test (measure memory per tagger)");
        Console.WriteLine("10. Compare JoyTag vs WDv3 Large (20 images)");
        Console.Write("\nChoose option (1-10): ");
        var choice = Console.ReadLine();

        switch (choice)
        {
            case "1":
                await TestSingleImage();
                break;
            case "2":
                await TestBatchTagging();
                break;
            case "3":
                await TestBatchWDTag();
                break;
            case "4":
                await TestBatchWDV3Tag();
                break;
            case "5":
                await TestBatchWDV3LargeTag();
                break;
            case "6":
                await TestBatchAllTaggers();
                break;
            case "7":
                await TestBatchCaptioning();
                break;
            case "8":
                await TestCompareJoyCaptionPrompts();
                break;
            case "9":
                await TestVRAMUsage();
                break;
            case "10":
                await TestJoyTagVsWDV3Large();
                break;
            default:
                Console.WriteLine("Invalid option!");
                break;
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    static async Task TestSingleImage()
    {
        Console.Write("\nEnter image path (or press Enter for first test image): ");
        var imagePath = Console.ReadLine()?.Trim('"') ?? "";

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
            imagePath = Directory.GetFiles(testFolder, "*.png").FirstOrDefault() ?? "";
        }

        if (!File.Exists(imagePath))
        {
            Console.WriteLine("Invalid image path!");
            return;
        }

        Console.WriteLine($"\nTesting with: {Path.GetFileName(imagePath)}");

        // Initialize JoyTag
        Console.WriteLine("\n--- Initializing JoyTag ---");
        var joyTagService = new JoyTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtTags.csv");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await joyTagService.InitializeAsync();
        Console.WriteLine($"JoyTag initialized in {sw.ElapsedMilliseconds}ms");

        // Test tagging
        Console.WriteLine("\n--- JoyTag Results ---");
        sw.Restart();
        var tags = await joyTagService.TagImageAsync(imagePath);
        Console.WriteLine($"Tagging took {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Found {tags.Count} tags (threshold {joyTagService.Threshold:P0})\n");

        Console.WriteLine("Top 20 tags:");
        foreach (var tag in tags.Take(20))
        {
            Console.WriteLine($"  {tag.Tag,-40} {tag.Confidence:P1}");
        }

        var tagString = await joyTagService.GetTagStringAsync(imagePath);
        Console.WriteLine($"\nComma-separated:\n{tagString}");

        joyTagService.Dispose();

        // Initialize JoyCaption
        Console.WriteLine("\n--- Initializing JoyCaption ---");
        var joyCaptionService = new JoyCaptionService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-hf-llava.i1-Q4_K_S.gguf",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-llava-mmproj-model-f16.gguf");

        sw.Restart();
        try
        {
            await joyCaptionService.InitializeAsync(gpuLayers: 35, contextSize: 4096);
            Console.WriteLine($"JoyCaption initialized in {sw.ElapsedMilliseconds}ms");

            // Test captioning with detailed prompt
            Console.WriteLine("\n--- JoyCaption Results (Detailed) ---");
            sw.Restart();
            var caption = await joyCaptionService.CaptionImageAsync(imagePath, JoyCaptionService.DEFAULT_DETAILED_PROMPT);
            Console.WriteLine($"Captioning took {caption.GenerationTimeMs:F0}ms");
            Console.WriteLine($"Tokens: {caption.TokenCount}");
            Console.WriteLine($"\nCaption:\n{caption.Caption}");

            // Test short prompt
            Console.WriteLine("\n--- JoyCaption Results (Short) ---");
            var shortCaption = await joyCaptionService.CaptionImageAsync(imagePath, JoyCaptionService.DEFAULT_SHORT_PROMPT);
            Console.WriteLine($"Captioning took {shortCaption.GenerationTimeMs:F0}ms");
            Console.WriteLine($"Tokens: {shortCaption.TokenCount}");
            Console.WriteLine($"\nCaption:\n{shortCaption.Caption}");

            joyCaptionService.UnloadModels();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JoyCaption Error: {ex.Message}");
            Console.WriteLine("(Model files may not be available)");
        }
    }

    static async Task TestBatchTagging()
    {
        var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
        var images = Directory.GetFiles(testFolder, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"\nFound {images.Count} images");
        Console.Write("Confidence threshold (0.0-1.0, default 0.2): ");
        var thresholdInput = Console.ReadLine();
        float threshold = 0.2f;
        if (!string.IsNullOrWhiteSpace(thresholdInput) && float.TryParse(thresholdInput, out var t))
            threshold = Math.Clamp(t, 0f, 1f);

        Console.Write("Save tags to text files? (y/n, default n): ");
        var saveToFile = Console.ReadLine()?.ToLower() == "y";

        Console.Write("Include confidence scores? (y/n, default n): ");
        var includeConfidence = Console.ReadLine()?.ToLower() == "y";

        Console.WriteLine("\n--- Initializing JoyTag ---");
        
        var joyTagService = new JoyTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtTags.csv");
        joyTagService.Threshold = threshold;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await joyTagService.InitializeAsync();
        Console.WriteLine($"JoyTag initialized in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Using threshold: {threshold:F2}, Save to file: {saveToFile}, Include confidence: {includeConfidence}");

        Console.WriteLine($"\n--- Processing {images.Count} Images ---");
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var times = new List<long>();
        var totalTags = 0;
        var savedFiles = 0;

        for (int i = 0; i < images.Count; i++)
        {
            var imagePath = images[i];
            Console.Write($"\r[{i + 1}/{images.Count}] {Path.GetFileName(imagePath),-60} ");

            try
            {
                var tagSw = System.Diagnostics.Stopwatch.StartNew();
                var tags = await joyTagService.TagImageAsync(imagePath);
                tagSw.Stop();
                times.Add(tagSw.ElapsedMilliseconds);
                totalTags += tags.Count;
                Console.Write($"✓ {tagSw.ElapsedMilliseconds}ms ({tags.Count} tags)");

                // Save to file if requested
                if (saveToFile)
                {
                    var tagString = await joyTagService.GetTagStringAsync(imagePath, includeConfidence);
                    var txtPath = Path.ChangeExtension(imagePath, ".tags.txt");
                    await File.WriteAllTextAsync(txtPath, tagString);
                    savedFiles++;
                }
            }
            catch (Exception ex)
            {
                Console.Write($"✗ ({ex.Message})");
            }
        }

        totalSw.Stop();
        joyTagService.Dispose();

        Console.WriteLine($"\n\n--- JoyTag Batch Results ---");
        Console.WriteLine($"Total time: {totalSw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Processed: {times.Count}/{images.Count} images");
        Console.WriteLine($"Average: {times.Average():F0}ms per image");
        Console.WriteLine($"Min: {times.Min()}ms | Max: {times.Max()}ms");
        Console.WriteLine($"Throughput: {times.Count / totalSw.Elapsed.TotalSeconds:F1} images/sec");
        Console.WriteLine($"Total tags: {totalTags} (avg {totalTags / (double)times.Count:F1} per image)");
        Console.WriteLine($"Threshold: {threshold:F2}");
        if (saveToFile)
            Console.WriteLine($"Saved: {savedFiles} .tags.txt files in {testFolder}");
    }

    static async Task TestBatchCaptioning()
    {
        var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
        var images = Directory.GetFiles(testFolder, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .Take(10) // Only test 10 images - captioning is slow
            .ToList();

        Console.WriteLine($"\nTesting with {images.Count} images (captioning is slow)");
        Console.WriteLine("\n--- Initializing JoyCaption ---");
        
        var joyCaptionService = new JoyCaptionService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-hf-llava.i1-Q4_K_S.gguf",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-llava-mmproj-model-f16.gguf");

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await joyCaptionService.InitializeAsync(gpuLayers: 35, contextSize: 4096);
            Console.WriteLine($"JoyCaption initialized in {sw.ElapsedMilliseconds}ms");

            Console.WriteLine($"\n--- Processing {images.Count} Images ---");
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            var times = new List<double>();
            var tokens = new List<int>();

            for (int i = 0; i < images.Count; i++)
            {
                var imagePath = images[i];
                Console.Write($"\n[{i + 1}/{images.Count}] {Path.GetFileName(imagePath)}... ");

                try
                {
                    var caption = await joyCaptionService.CaptionImageAsync(imagePath, JoyCaptionService.DEFAULT_SHORT_PROMPT);
                    times.Add(caption.GenerationTimeMs);
                    tokens.Add(caption.TokenCount);
                    Console.WriteLine($"✓ {caption.GenerationTimeMs:F0}ms ({caption.TokenCount} tokens)");
                    Console.WriteLine($"    \"{caption.Caption.Substring(0, Math.Min(80, caption.Caption.Length))}...\"");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ ({ex.Message})");
                }
            }

            totalSw.Stop();
            joyCaptionService.UnloadModels();

            Console.WriteLine($"\n--- JoyCaption Batch Results ---");
            Console.WriteLine($"Total time: {totalSw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"Processed: {times.Count}/{images.Count} images");
            Console.WriteLine($"Average: {times.Average():F0}ms per image");
            Console.WriteLine($"Min: {times.Min():F0}ms | Max: {times.Max():F0}ms");
            Console.WriteLine($"Throughput: {times.Count / totalSw.Elapsed.TotalSeconds:F2} images/sec");
            Console.WriteLine($"Avg tokens: {tokens.Average():F1}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JoyCaption Error: {ex.Message}");
            Console.WriteLine("(Model files may not be available)");
        }
    }

    static async Task TestBatchBoth()
    {
        var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
        var images = Directory.GetFiles(testFolder, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .Take(5) // Small sample for combined test
            .ToList();

        Console.WriteLine($"\nTesting with {images.Count} images");
        
        // Initialize both services
        Console.WriteLine("\n--- Initializing Services ---");
        var joyTagService = new JoyTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtTags.csv");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await joyTagService.InitializeAsync();
        Console.WriteLine($"JoyTag initialized in {sw.ElapsedMilliseconds}ms");

        var joyCaptionService = new JoyCaptionService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-hf-llava.i1-Q4_K_S.gguf",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-llava-mmproj-model-f16.gguf");
        
        try
        {
            sw.Restart();
            await joyCaptionService.InitializeAsync(gpuLayers: 35, contextSize: 4096);
            Console.WriteLine($"JoyCaption initialized in {sw.ElapsedMilliseconds}ms");

            Console.WriteLine($"\n--- Processing {images.Count} Images ---");
            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < images.Count; i++)
            {
                var imagePath = images[i];
                Console.WriteLine($"\n[{i + 1}/{images.Count}] {Path.GetFileName(imagePath)}");

                try
                {
                    // Tag
                    var tagSw = System.Diagnostics.Stopwatch.StartNew();
                    var tags = await joyTagService.TagImageAsync(imagePath);
                    Console.WriteLine($"  Tags: {tagSw.ElapsedMilliseconds}ms ({tags.Count} tags)");
                    Console.WriteLine($"    {string.Join(", ", tags.Take(10).Select(t => t.Tag))}...");

                    // Caption
                    var caption = await joyCaptionService.CaptionImageAsync(imagePath, JoyCaptionService.DEFAULT_SHORT_PROMPT);
                    Console.WriteLine($"  Caption: {caption.GenerationTimeMs:F0}ms ({caption.TokenCount} tokens)");
                    Console.WriteLine($"    \"{caption.Caption.Substring(0, Math.Min(100, caption.Caption.Length))}...\"");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Error: {ex.Message}");
                }
            }

            totalSw.Stop();
            Console.WriteLine($"\n--- Combined Test Complete ---");
            Console.WriteLine($"Total time: {totalSw.Elapsed.TotalSeconds:F1}s ({totalSw.Elapsed.TotalSeconds / images.Count:F1}s per image)");

            joyCaptionService.UnloadModels();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JoyCaption Error: {ex.Message}");
            Console.WriteLine("(Continuing with JoyTag only)");
        }

        joyTagService.Dispose();
    }

    static async Task TestBatchWDTag()
    {
        await TestBatchWDTagger(
            "WD 1.4 Tagger",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wd_tag\wdModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wd_tag\wdTags.csv",
            (modelPath, tagsPath) => new WDTagService(modelPath, tagsPath));
    }

    static async Task TestBatchWDV3Tag()
    {
        await TestBatchWDTagger(
            "WD v3 Tagger",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_tag\wdV3Model.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_tag\wdV3Tags.csv",
            (modelPath, tagsPath) => new WDV3TagService(modelPath, tagsPath));
    }

    static async Task TestBatchWDV3LargeTag()
    {
        await TestBatchWDTagger(
            "WD v3 Large Tagger",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeTags.csv",
            (modelPath, tagsPath) => new WDV3LargeTagService(modelPath, tagsPath));
    }

    static async Task TestBatchWDTagger(string name, string modelPath, string tagsPath, Func<string, string, WDTagService> createService)
    {
        var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
        var images = Directory.GetFiles(testFolder, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"\nFound {images.Count} images");
        Console.Write("Confidence threshold (0.0-1.0, default 0.35): ");
        var thresholdInput = Console.ReadLine();
        float threshold = 0.35f;
        if (!string.IsNullOrWhiteSpace(thresholdInput) && float.TryParse(thresholdInput, out var t))
            threshold = Math.Clamp(t, 0f, 1f);

        Console.Write("Save tags to text files? (y/n, default n): ");
        var saveToFile = Console.ReadLine()?.ToLower() == "y";

        Console.Write("Include confidence scores? (y/n, default n): ");
        var includeConfidence = Console.ReadLine()?.ToLower() == "y";

        Console.WriteLine($"\n--- Initializing {name} ---");
        
        var service = createService(modelPath, tagsPath);
        service.Threshold = threshold;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.InitializeAsync();
        Console.WriteLine($"{name} initialized in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Using threshold: {threshold:F2}, Save to file: {saveToFile}, Include confidence: {includeConfidence}");

        Console.WriteLine($"\n--- Processing {images.Count} Images ---");
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var times = new List<long>();
        var totalTags = 0;
        var savedFiles = 0;

        for (int i = 0; i < images.Count; i++)
        {
            var imagePath = images[i];
            Console.Write($"\r[{i + 1}/{images.Count}] {Path.GetFileName(imagePath),-50}");

            try
            {
                sw.Restart();
                var tags = await service.TagImageAsync(imagePath);
                times.Add(sw.ElapsedMilliseconds);
                totalTags += tags.Count;

                Console.Write($" √ {sw.ElapsedMilliseconds}ms ({tags.Count} tags)");

                if (saveToFile)
                {
                    var tagString = await service.GetTagStringAsync(imagePath, includeConfidence);
                    var txtPath = Path.ChangeExtension(imagePath, ".tags.txt");
                    await File.WriteAllTextAsync(txtPath, tagString);
                    savedFiles++;
                }

                if (i == 0)
                {
                    // Show sample output for first image
                    var sampleTags = string.Join(", ", tags.Take(10).Select(t => $"{t.Tag}:{t.Confidence:F2}"));
                    Console.WriteLine($"\n    Sample: {sampleTags}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" ✗ ({ex.Message})");
            }
        }

        Console.WriteLine();
        totalSw.Stop();
        service.Dispose();

        Console.WriteLine($"\n--- {name} Batch Results ---");
        Console.WriteLine($"Total time: {totalSw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Processed: {times.Count}/{images.Count} images");
        Console.WriteLine($"Average: {times.Average():F0}ms per image");
        Console.WriteLine($"Min: {times.Min():F0}ms | Max: {times.Max():F0}ms");
        Console.WriteLine($"Throughput: {times.Count / totalSw.Elapsed.TotalSeconds:F1} images/sec");
        Console.WriteLine($"Total tags: {totalTags} (avg {(float)totalTags / times.Count:F1} per image)");
        Console.WriteLine($"Threshold: {threshold:F2}");
        if (saveToFile)
            Console.WriteLine($"Saved: {savedFiles} .tags.txt files in {testFolder}");
    }

    static async Task TestBatchAllTaggers()
    {
        var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
        var images = Directory.GetFiles(testFolder, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        Console.WriteLine($"\nComparing all taggers on {images.Count} images\n");

        // Initialize all services
        var joyTag = new JoyTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtTags.csv");

        var wdTag = new WDTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wd_tag\wdModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wd_tag\wdTags.csv");

        var wdv3Tag = new WDV3TagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_tag\wdV3Model.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_tag\wdV3Tags.csv");

        var wdv3Large = new WDV3LargeTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeTags.csv");

        Console.WriteLine("Initializing all taggers...");
        await Task.WhenAll(
            joyTag.InitializeAsync(),
            wdTag.InitializeAsync(),
            wdv3Tag.InitializeAsync(),
            wdv3Large.InitializeAsync()
        );
        Console.WriteLine("All taggers ready!\n");

        var results = new List<object>();

        foreach (var imagePath in images)
        {
            Console.WriteLine($"\n{Path.GetFileName(imagePath)}:");
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var joyTags = await joyTag.TagImageAsync(imagePath);
            var joyTime = sw.ElapsedMilliseconds;

            sw.Restart();
            var wdTags = await wdTag.TagImageAsync(imagePath);
            var wdTime = sw.ElapsedMilliseconds;

            sw.Restart();
            var wdv3Tags = await wdv3Tag.TagImageAsync(imagePath);
            var wdv3Time = sw.ElapsedMilliseconds;

            sw.Restart();
            var wdv3LargeTags = await wdv3Large.TagImageAsync(imagePath);
            var wdv3LargeTime = sw.ElapsedMilliseconds;

            Console.WriteLine($"  JoyTag:        {joyTime,4}ms ({joyTags.Count,3} tags) - {string.Join(", ", joyTags.Take(5).Select(t => t.Tag))}...");
            Console.WriteLine($"  WD 1.4:        {wdTime,4}ms ({wdTags.Count,3} tags) - {string.Join(", ", wdTags.Take(5).Select(t => t.Tag))}...");
            Console.WriteLine($"  WD v3:         {wdv3Time,4}ms ({wdv3Tags.Count,3} tags) - {string.Join(", ", wdv3Tags.Take(5).Select(t => t.Tag))}...");
            Console.WriteLine($"  WD v3 Large:   {wdv3LargeTime,4}ms ({wdv3LargeTags.Count,3} tags) - {string.Join(", ", wdv3LargeTags.Take(5).Select(t => t.Tag))}...");

            results.Add(new
            {
                ImageName = Path.GetFileName(imagePath),
                ImagePath = imagePath,
                Taggers = new
                {
                    JoyTag = new
                    {
                        TimeMs = joyTime,
                        TagCount = joyTags.Count,
                        Tags = joyTags.Select(t => new { t.Tag, Confidence = t.Confidence }).ToList()
                    },
                    WD14 = new
                    {
                        TimeMs = wdTime,
                        TagCount = wdTags.Count,
                        Tags = wdTags.Select(t => new { t.Tag, Confidence = t.Confidence }).ToList()
                    },
                    WDv3 = new
                    {
                        TimeMs = wdv3Time,
                        TagCount = wdv3Tags.Count,
                        Tags = wdv3Tags.Select(t => new { t.Tag, Confidence = t.Confidence }).ToList()
                    },
                    WDv3Large = new
                    {
                        TimeMs = wdv3LargeTime,
                        TagCount = wdv3LargeTags.Count,
                        Tags = wdv3LargeTags.Select(t => new { t.Tag, Confidence = t.Confidence }).ToList()
                    }
                }
            });
        }

        // Save results to JSON
        var outputPath = Path.Combine(testFolder, "tagger_comparison.json");
        var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"\n\nResults saved to: {outputPath}");

        joyTag.Dispose();
        wdTag.Dispose();
        wdv3Tag.Dispose();
        wdv3Large.Dispose();
    }

    static async Task TestCompareJoyCaptionPrompts()
    {
        var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
        var images = Directory.GetFiles(testFolder, "*.png").Take(5).ToList();

        Console.WriteLine($"\nTesting JoyCaption with 8 different prompts on {images.Count} images");
        Console.WriteLine("Results will be saved to joycaption_prompt_comparison.json\n");

        // Define all prompt presets
        var prompts = new Dictionary<string, string>
        {
            { "Detailed", JoyCaptionService.PROMPT_DETAILED },
            { "Short", JoyCaptionService.PROMPT_SHORT },
            { "Technical", JoyCaptionService.PROMPT_TECHNICAL },
            { "Precise Colors", JoyCaptionService.PROMPT_PRECISE_COLORS },
            { "Character Focus", JoyCaptionService.PROMPT_CHARACTER_FOCUS },
            { "Scene Focus", JoyCaptionService.PROMPT_SCENE_FOCUS },
            { "Artistic Style", JoyCaptionService.PROMPT_ARTISTIC_STYLE },
            { "Booru Tags", JoyCaptionService.PROMPT_BOORU_TAGS }
        };

        Console.WriteLine("--- Initializing JoyCaption ---");
        var joyCaptionService = new JoyCaptionService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-hf-llava.i1-Q4_K_S.gguf",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\Joycaption\llama-joycaption-beta-one-llava-mmproj-model-f16.gguf");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await joyCaptionService.InitializeAsync(gpuLayers: 35, contextSize: 4096);
        Console.WriteLine($"JoyCaption initialized in {sw.ElapsedMilliseconds}ms\n");

        var results = new List<object>();
        var imageIndex = 0;

        foreach (var imagePath in images)
        {
            imageIndex++;
            var imageName = Path.GetFileName(imagePath);
            Console.WriteLine($"[{imageIndex}/{images.Count}] Processing: {imageName}");

            var imageResults = new Dictionary<string, object>
            {
                { "image", imageName },
                { "path", imagePath },
                { "prompts", new Dictionary<string, object>() }
            };

            // Create image embedding ONCE per image (this is the expensive encoding operation)
            using var imageEmbed = joyCaptionService.CreateImageEmbedding(imagePath);
            Console.WriteLine($"  Image encoded successfully");

            var promptIndex = 0;
            foreach (var (promptName, promptText) in prompts)
            {
                promptIndex++;
                Console.Write($"  {promptName,-20}");
                sw.Restart();

                try
                {
                    // Reuse the same embedding for all 8 prompts - no re-encoding!
                    var caption = await joyCaptionService.CaptionWithEmbeddingAsync(
                        imageEmbed, 
                        promptText);

                    Console.WriteLine($" {caption.GenerationTimeMs,5:F0}ms ({caption.TokenCount,3} tokens)");

                    ((Dictionary<string, object>)imageResults["prompts"])[promptName] = new
                    {
                        caption = caption.Caption,
                        tokens = caption.TokenCount,
                        time_ms = caption.GenerationTimeMs,
                        prompt_used = promptText
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" ERROR: {ex.Message}");
                    ((Dictionary<string, object>)imageResults["prompts"])[promptName] = new
                    {
                        error = ex.Message,
                        prompt_used = promptText
                    };
                }
            }

            results.Add(imageResults);
            Console.WriteLine();
        }

        joyCaptionService.Dispose();

        // Save results to JSON
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var outputPath = Path.Combine(testFolder, "joycaption_prompt_comparison.json");
        var json = JsonSerializer.Serialize(new
        {
            test_date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            images_tested = images.Count,
            prompts_tested = prompts.Count,
            total_captions = images.Count * prompts.Count,
            results = results
        }, jsonOptions);

        await File.WriteAllTextAsync(outputPath, json);

        Console.WriteLine($"\n✓ Results saved to: {outputPath}");
        Console.WriteLine($"  Total captions generated: {images.Count * prompts.Count}");
    }

    static async Task TestVRAMUsage()
    {
        var testImage = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images\2025-09-24-002737_IlustRealdollij_FINAL.png";
        
        Console.WriteLine("\nVRAM Usage Test");
        Console.WriteLine("================\n");
        Console.WriteLine("Testing each tagger individually to measure GPU memory usage.");
        Console.WriteLine("This uses nvidia-smi to query GPU memory before/after loading each model.\n");

        var baselineMemory = GetGPUMemoryUsageMB();
        Console.WriteLine($"Baseline GPU memory (before loading any models): {baselineMemory} MB\n");

        // Test JoyTag
        Console.WriteLine("Testing JoyTag...");
        var joyTag = new JoyTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtTags.csv");
        await joyTag.InitializeAsync();
        var joyTagMemory = GetGPUMemoryUsageMB() - baselineMemory;
        await joyTag.TagImageAsync(testImage); // Run inference to ensure everything is loaded
        var joyTagInferenceMemory = GetGPUMemoryUsageMB() - baselineMemory;
        Console.WriteLine($"  Model loaded: +{joyTagMemory} MB");
        Console.WriteLine($"  After inference: +{joyTagInferenceMemory} MB");
        joyTag.Dispose();
        await Task.Delay(1000); // Give time for cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(1000);

        // Test WD 1.4
        Console.WriteLine("\nTesting WD 1.4 Tagger...");
        var wdTag = new WDTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wd_tag\wdModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wd_tag\wdTags.csv");
        await wdTag.InitializeAsync();
        var wdTagMemory = GetGPUMemoryUsageMB() - baselineMemory;
        await wdTag.TagImageAsync(testImage);
        var wdTagInferenceMemory = GetGPUMemoryUsageMB() - baselineMemory;
        Console.WriteLine($"  Model loaded: +{wdTagMemory} MB");
        Console.WriteLine($"  After inference: +{wdTagInferenceMemory} MB");
        wdTag.Dispose();
        await Task.Delay(1000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(1000);

        // Test WD v3
        Console.WriteLine("\nTesting WD v3 Tagger...");
        var wdv3Tag = new WDV3TagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_tag\wdV3Model.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_tag\wdV3Tags.csv");
        await wdv3Tag.InitializeAsync();
        var wdv3TagMemory = GetGPUMemoryUsageMB() - baselineMemory;
        await wdv3Tag.TagImageAsync(testImage);
        var wdv3TagInferenceMemory = GetGPUMemoryUsageMB() - baselineMemory;
        Console.WriteLine($"  Model loaded: +{wdv3TagMemory} MB");
        Console.WriteLine($"  After inference: +{wdv3TagInferenceMemory} MB");
        wdv3Tag.Dispose();
        await Task.Delay(1000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(1000);

        // Test WD v3 Large
        Console.WriteLine("\nTesting WD v3 Large Tagger...");
        var wdv3Large = new WDV3LargeTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeTags.csv");
        await wdv3Large.InitializeAsync();
        var wdv3LargeMemory = GetGPUMemoryUsageMB() - baselineMemory;
        await wdv3Large.TagImageAsync(testImage);
        var wdv3LargeInferenceMemory = GetGPUMemoryUsageMB() - baselineMemory;
        Console.WriteLine($"  Model loaded: +{wdv3LargeMemory} MB");
        Console.WriteLine($"  After inference: +{wdv3LargeInferenceMemory} MB");
        wdv3Large.Dispose();

        Console.WriteLine("\n\nSummary:");
        Console.WriteLine("========");
        Console.WriteLine($"JoyTag:         {joyTagInferenceMemory} MB VRAM");
        Console.WriteLine($"WD 1.4:         {wdTagInferenceMemory} MB VRAM");
        Console.WriteLine($"WD v3:          {wdv3TagInferenceMemory} MB VRAM");
        Console.WriteLine($"WD v3 Large:    {wdv3LargeInferenceMemory} MB VRAM");
        Console.WriteLine($"\nTotal if all loaded: ~{joyTagInferenceMemory + wdTagInferenceMemory + wdv3TagInferenceMemory + wdv3LargeInferenceMemory} MB");
        Console.WriteLine($"With 32GB VRAM, theoretical max concurrent taggers: ~{32000 / ((joyTagInferenceMemory + wdTagInferenceMemory + wdv3TagInferenceMemory + wdv3LargeInferenceMemory) / 4)}");
    }

    static int GetGPUMemoryUsageMB()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.used --format=csv,noheader,nounits -i 0",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (int.TryParse(output.Trim(), out int memoryMB))
                {
                    return memoryMB;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not query GPU memory via nvidia-smi: {ex.Message}");
        }
        return 0;
    }

    static async Task TestJoyTagVsWDV3Large()
    {
        var testFolder = @"d:\AI\Github_Desktop\DiffusionToolkit\_test_images";
        var images = Directory.GetFiles(testFolder, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();

        Console.WriteLine($"\nComparing JoyTag vs WDv3 Large on {images.Count} images");
        Console.WriteLine("This will help determine if both taggers are needed or if one is sufficient.\n");

        // Initialize both services
        var joyTag = new JoyTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\joytag\jtTags.csv");

        var wdv3Large = new WDV3LargeTagService(
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeModel.onnx",
            @"d:\AI\Github_Desktop\DiffusionToolkit\models\onnx\wdv3_large_tag\wdV3LargeTags.csv");

        Console.WriteLine("Initializing taggers...");
        await Task.WhenAll(
            joyTag.InitializeAsync(),
            wdv3Large.InitializeAsync()
        );
        Console.WriteLine("Ready!\n");

        var results = new List<object>();

        foreach (var imagePath in images)
        {
            var fileName = Path.GetFileName(imagePath);
            Console.Write($"Processing {fileName}... ");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var joyTags = await joyTag.TagImageAsync(imagePath);
            var joyTime = sw.ElapsedMilliseconds;

            sw.Restart();
            var wdv3LargeTags = await wdv3Large.TagImageAsync(imagePath);
            var wdv3Time = sw.ElapsedMilliseconds;

            // Create comma-delimited tag strings
            var joyTagsString = string.Join(", ", joyTags.Select(t => t.Tag));
            var wdv3LargeTagsString = string.Join(", ", wdv3LargeTags.Select(t => t.Tag));

            // Find common tags
            var joyTagSet = new HashSet<string>(joyTags.Select(t => t.Tag));
            var wdv3TagSet = new HashSet<string>(wdv3LargeTags.Select(t => t.Tag));
            var commonTags = joyTagSet.Intersect(wdv3TagSet).ToList();
            var joyOnlyTags = joyTagSet.Except(wdv3TagSet).ToList();
            var wdv3OnlyTags = wdv3TagSet.Except(joyTagSet).ToList();

            Console.WriteLine($"JoyTag: {joyTags.Count} tags ({joyTime}ms), WDv3Large: {wdv3LargeTags.Count} tags ({wdv3Time}ms), Common: {commonTags.Count}");

            results.Add(new
            {
                ImageName = fileName,
                ImagePath = imagePath,
                JoyTag = new
                {
                    TimeMs = joyTime,
                    TagCount = joyTags.Count,
                    TagsCommaSeparated = joyTagsString,
                    Tags = joyTags.Select(t => new { t.Tag, Confidence = Math.Round(t.Confidence, 4) }).ToList()
                },
                WDv3Large = new
                {
                    TimeMs = wdv3Time,
                    TagCount = wdv3LargeTags.Count,
                    TagsCommaSeparated = wdv3LargeTagsString,
                    Tags = wdv3LargeTags.Select(t => new { t.Tag, Confidence = Math.Round(t.Confidence, 4) }).ToList()
                },
                Comparison = new
                {
                    CommonTagCount = commonTags.Count,
                    CommonTags = string.Join(", ", commonTags),
                    JoyTagOnlyCount = joyOnlyTags.Count,
                    JoyTagOnly = string.Join(", ", joyOnlyTags),
                    WDv3LargeOnlyCount = wdv3OnlyTags.Count,
                    WDv3LargeOnly = string.Join(", ", wdv3OnlyTags),
                    OverlapPercentage = Math.Round((double)commonTags.Count / Math.Max(joyTagSet.Count, wdv3TagSet.Count) * 100, 2)
                }
            });
        }

        // Calculate summary statistics
        var avgJoyTagCount = results.Average(r => (double)((dynamic)r).JoyTag.TagCount);
        var avgWdv3Count = results.Average(r => (double)((dynamic)r).WDv3Large.TagCount);
        var avgCommonCount = results.Average(r => (double)((dynamic)r).Comparison.CommonTagCount);
        var avgOverlap = results.Average(r => (double)((dynamic)r).Comparison.OverlapPercentage);

        // Save results to JSON
        var outputPath = Path.Combine(testFolder, "joytag_vs_wdv3large_comparison.json");
        var json = JsonSerializer.Serialize(new
        {
            test_date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            images_tested = images.Count,
            summary = new
            {
                avg_joytag_tags = Math.Round(avgJoyTagCount, 1),
                avg_wdv3large_tags = Math.Round(avgWdv3Count, 1),
                avg_common_tags = Math.Round(avgCommonCount, 1),
                avg_overlap_percentage = Math.Round(avgOverlap, 2),
                recommendation = avgOverlap > 70 
                    ? "High overlap - consider using only one tagger" 
                    : "Low overlap - both taggers provide unique value, consider combining"
            },
            results = results
        }, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(outputPath, json);

        Console.WriteLine($"\n\nResults saved to: {outputPath}");
        Console.WriteLine("\nSummary:");
        Console.WriteLine($"  Average JoyTag tags:        {avgJoyTagCount:F1}");
        Console.WriteLine($"  Average WDv3 Large tags:    {avgWdv3Count:F1}");
        Console.WriteLine($"  Average common tags:        {avgCommonCount:F1}");
        Console.WriteLine($"  Average overlap:            {avgOverlap:F1}%");
        Console.WriteLine($"\n  {(avgOverlap > 70 ? "✓ High overlap - one tagger may be sufficient" : "⚠ Low overlap - both taggers add unique value")}");

        joyTag.Dispose();
        wdv3Large.Dispose();
    }
}


