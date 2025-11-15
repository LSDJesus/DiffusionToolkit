using System.Text.Json;

namespace Diffusion.Embeddings;

/// <summary>
/// Export embeddings to ComfyUI-compatible format for direct workflow integration
/// </summary>
public class ComfyUIExporter
{
    /// <summary>
    /// Export image embeddings as ComfyUI conditioning data
    /// </summary>
    public static async Task ExportToComfyUIAsync(
        int imageId,
        float[]? clipLEmbedding,
        float[]? clipGEmbedding,
        float[]? imageEmbedding,
        string outputPath,
        int width = 1024,
        int height = 1024)
    {
        var conditioning = new ComfyUIConditioning
        {
            ImageId = imageId,
            ClipL = clipLEmbedding,
            ClipG = clipGEmbedding,
            ImageEmbedding = imageEmbedding,
            Width = width,
            Height = height,
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(conditioning, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });

        await File.WriteAllTextAsync(outputPath, json);
    }

    /// <summary>
    /// Export batch of embeddings for workflow automation
    /// </summary>
    public static async Task ExportBatchToComfyUIAsync(
        IEnumerable<ImageEmbeddingSet> embeddings,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        foreach (var embedding in embeddings)
        {
            var filename = $"image_{embedding.ImageId}_embeddings.json";
            var outputPath = Path.Combine(outputDirectory, filename);
            
            await ExportToComfyUIAsync(
                embedding.ImageId,
                embedding.ClipL,
                embedding.ClipG,
                embedding.ImageEmbedding,
                outputPath,
                embedding.Width,
                embedding.Height
            );
        }
    }

    /// <summary>
    /// Create ComfyUI workflow JSON with embedded conditioning from database
    /// </summary>
    public static string GenerateWorkflowJson(
        float[] clipLEmbedding,
        float[] clipGEmbedding,
        string prompt,
        string negativePrompt = "",
        int width = 1024,
        int height = 1024,
        int steps = 25,
        float cfgScale = 7.5f,
        long seed = -1)
    {
        var workflow = new
        {
            prompt = new Dictionary<string, object>
            {
                ["1"] = new // CLIP-L Text Encoder
                {
                    inputs = new
                    {
                        text_l = prompt,
                        clip = new object[] { "4", 0 },
                        embedding_override = clipLEmbedding
                    },
                    class_type = "CLIPTextEncode",
                    _meta = new { title = "CLIP Text Encode (L)" }
                },
                ["2"] = new // CLIP-G Text Encoder
                {
                    inputs = new
                    {
                        text_g = prompt,
                        clip = new object[] { "4", 0 },
                        embedding_override = clipGEmbedding
                    },
                    class_type = "CLIPTextEncode",
                    _meta = new { title = "CLIP Text Encode (G)" }
                },
                ["3"] = new // KSampler
                {
                    inputs = new
                    {
                        seed = seed,
                        steps = steps,
                        cfg = cfgScale,
                        sampler_name = "euler_ancestral",
                        scheduler = "normal",
                        denoise = 1.0,
                        model = new object[] { "4", 0 },
                        positive = new object[] { "1", 0 },
                        negative = new object[] { "2", 0 },
                        latent_image = new object[] { "5", 0 }
                    },
                    class_type = "KSampler"
                },
                ["4"] = new // Load Checkpoint
                {
                    inputs = new
                    {
                        ckpt_name = "sdxl_base.safetensors"
                    },
                    class_type = "CheckpointLoaderSimple",
                    _meta = new { title = "Load Checkpoint" }
                },
                ["5"] = new // Empty Latent
                {
                    inputs = new
                    {
                        width = width,
                        height = height,
                        batch_size = 1
                    },
                    class_type = "EmptyLatentImage"
                }
            },
            metadata = new
            {
                source = "DiffusionToolkit",
                exported_at = DateTime.UtcNow.ToString("o"),
                embedding_models = new
                {
                    clip_l = "clip-L_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32",
                    clip_g = "clip-G_noMERGE_Universal_CLIP_FLUX_illustrious_Base-fp32"
                }
            }
        };

        return JsonSerializer.Serialize(workflow, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
    }
}

/// <summary>
/// ComfyUI conditioning data structure
/// </summary>
public class ComfyUIConditioning
{
    public int ImageId { get; set; }
    public float[]? ClipL { get; set; }         // 768D - SDXL text_l
    public float[]? ClipG { get; set; }         // 1280D - SDXL text_g
    public float[]? ImageEmbedding { get; set; } // 1024D - Image similarity
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Complete embedding set for an image
/// </summary>
public class ImageEmbeddingSet
{
    public int ImageId { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public float[]? ClipL { get; set; }
    public float[]? ClipG { get; set; }
    public float[]? ImageEmbedding { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
