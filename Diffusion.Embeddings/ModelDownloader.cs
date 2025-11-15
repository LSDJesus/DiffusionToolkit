using System.IO.Compression;
using System.Net.Http;

namespace Diffusion.Embeddings;

/// <summary>
/// Downloads and manages ONNX model files for embeddings
/// </summary>
public static class ModelDownloader
{
    private const string BGE_LARGE_URL = "https://huggingface.co/BAAI/bge-large-en-v1.5/resolve/main/onnx/model.onnx";
    private const string BGE_VOCAB_URL = "https://huggingface.co/BAAI/bge-large-en-v1.5/resolve/main/vocab.txt";
    private const string CLIP_VIT_URL = "https://huggingface.co/openai/clip-vit-base-patch32/resolve/main/onnx/model.onnx";

    /// <summary>
    /// Download all required models to the specified directory
    /// </summary>
    public static async Task DownloadModelsAsync(string modelsDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modelsDirectory);

        var textModelPath = Path.Combine(modelsDirectory, "bge-large-en-v1.5.onnx");
        var vocabPath = Path.Combine(modelsDirectory, "vocab.txt");
        var imageModelPath = Path.Combine(modelsDirectory, "clip-vit-b32.onnx");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(30);

        // Download BGE-large-en-v1.5 text model
        if (!File.Exists(textModelPath))
        {
            progress?.Report("Downloading BGE-large-en-v1.5 text model (~1.3GB)...");
            await DownloadFileAsync(client, BGE_LARGE_URL, textModelPath, progress, cancellationToken);
            progress?.Report("✓ BGE-large-en-v1.5 downloaded");
        }
        else
        {
            progress?.Report("✓ BGE-large-en-v1.5 already exists");
        }

        // Download vocabulary file
        if (!File.Exists(vocabPath))
        {
            progress?.Report("Downloading BERT vocabulary...");
            await DownloadFileAsync(client, BGE_VOCAB_URL, vocabPath, progress, cancellationToken);
            progress?.Report("✓ Vocabulary downloaded");
        }
        else
        {
            progress?.Report("✓ Vocabulary already exists");
        }

        // Download CLIP ViT-B/32 image model
        if (!File.Exists(imageModelPath))
        {
            progress?.Report("Downloading CLIP ViT-B/32 image model (~350MB)...");
            await DownloadFileAsync(client, CLIP_VIT_URL, imageModelPath, progress, cancellationToken);
            progress?.Report("✓ CLIP ViT-B/32 downloaded");
        }
        else
        {
            progress?.Report("✓ CLIP ViT-B/32 already exists");
        }

        progress?.Report("\n✅ All models ready!");
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        string url,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalBytesRead = 0;
            int bytesRead;
            var lastReportedPercent = -1;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;

                if (totalBytes > 0)
                {
                    var percent = (int)((totalBytesRead * 100) / totalBytes);
                    if (percent != lastReportedPercent && percent % 10 == 0)
                    {
                        progress?.Report($"  Progress: {percent}% ({totalBytesRead / 1024 / 1024} MB / {totalBytes / 1024 / 1024} MB)");
                        lastReportedPercent = percent;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"❌ Error downloading {Path.GetFileName(destinationPath)}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Get the default models directory path
    /// </summary>
    public static string GetDefaultModelsDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "DiffusionToolkit", "Models");
    }

    /// <summary>
    /// Check if all required models are downloaded
    /// </summary>
    public static bool AreModelsDownloaded(string modelsDirectory)
    {
        var textModelPath = Path.Combine(modelsDirectory, "bge-large-en-v1.5.onnx");
        var vocabPath = Path.Combine(modelsDirectory, "vocab.txt");
        var imageModelPath = Path.Combine(modelsDirectory, "clip-vit-b32.onnx");

        return File.Exists(textModelPath) && File.Exists(vocabPath) && File.Exists(imageModelPath);
    }

    /// <summary>
    /// Get configuration with model paths
    /// </summary>
    public static EmbeddingConfig GetDefaultConfig(string? modelsDirectory = null)
    {
        modelsDirectory ??= GetDefaultModelsDirectory();
        return EmbeddingConfig.CreateDefault(modelsDirectory);
    }
}
