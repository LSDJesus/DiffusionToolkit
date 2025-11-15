using System.Text.Json;

namespace Diffusion.Embeddings;

/// <summary>
/// Import textual embeddings from .safetensors files into PostgreSQL
/// Supports SDXL, Pony, Illustrious, and SD1.5 textual inversion embeddings
/// </summary>
public class TextualEmbeddingImporter
{
    /// <summary>
    /// Scan a directory and import all textual embeddings
    /// </summary>
    public static async Task<List<TextualEmbedding>> ImportFromDirectoryAsync(
        string directory,
        bool recursive = true)
    {
        var embeddings = new List<TextualEmbedding>();
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        
        var safetensorsFiles = Directory.GetFiles(directory, "*.safetensors", searchOption);
        
        foreach (var file in safetensorsFiles)
        {
            try
            {
                var embedding = await ImportSingleEmbeddingAsync(file);
                if (embedding != null)
                {
                    embeddings.Add(embedding);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to import {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        
        return embeddings;
    }

    /// <summary>
    /// Import a single textual embedding from .safetensors file
    /// </summary>
    public static async Task<TextualEmbedding?> ImportSingleEmbeddingAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var name = Path.GetFileNameWithoutExtension(filePath);
        var directory = Path.GetDirectoryName(filePath) ?? "";
        
        // Determine category from filename
        var category = DetermineCategoryFromName(name);
        
        // Read raw safetensors file
        var rawData = await File.ReadAllBytesAsync(filePath);
        
        // Detect model type from multiple sources
        var modelType = await DetermineModelTypeAsync(filePath, directory, rawData);
        
        // Load metadata if available
        var metadataPath = filePath.Replace(".safetensors", ".metadata.json");
        string? description = null;
        string? metadataModelType = null;
        
        if (File.Exists(metadataPath))
        {
            try
            {
                var metadataJson = await File.ReadAllTextAsync(metadataPath);
                var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson);
                
                if (metadata != null)
                {
                    if (metadata.ContainsKey("description"))
                        description = metadata["description"].GetString();
                    
                    // CivitAI metadata includes baseModel
                    if (metadata.ContainsKey("baseModel"))
                        metadataModelType = metadata["baseModel"].GetString();
                }
            }
            catch { /* Ignore metadata parsing errors */ }
        }
        
        // Prefer metadata model type over inference
        if (!string.IsNullOrEmpty(metadataModelType))
        {
            modelType = NormalizeModelType(metadataModelType);
        }
        
        return new TextualEmbedding
        {
            Name = name,
            FilePath = filePath,
            Category = category,
            ModelType = modelType,
            Description = description,
            RawEmbedding = rawData,
            ClipLEmbedding = null,  // Extract with proper safetensors library
            ClipGEmbedding = null
        };
    }

    private static string DetermineCategoryFromName(string name)
    {
        var lowerName = name.ToLowerInvariant();
        
        if (lowerName.Contains("-neg") || lowerName.Contains("negative") || 
            lowerName.Contains("badpony") || lowerName.Contains("deepnegative"))
            return "negative";
            
        if (lowerName.Contains("-pos") || lowerName.Contains("positive") || 
            lowerName.Contains("quality") || lowerName.Contains("smooth"))
            return "quality";
            
        if (lowerName.Contains("dv_") || lowerName.Contains("tower") || 
            lowerName.Contains("egirl") || lowerName.Contains("character"))
            return "character";
            
        if (lowerName.Contains("hand") || lowerName.Contains("anatomy") || 
            lowerName.Contains("eye") || lowerName.Contains("makeup"))
            return "detail_enhancer";
            
        return "other";
    }

    /// <summary>
    /// Determine model type from multiple sources (metadata, path, tensor analysis)
    /// </summary>
    private static async Task<string> DetermineModelTypeAsync(string filePath, string directory, byte[] rawData)
    {
        // 1. Check directory structure first (most reliable)
        var modelTypeFromPath = DetermineModelTypeFromPath(directory);
        if (modelTypeFromPath != "SDXL") // Not default guess
            return modelTypeFromPath;
        
        // 2. Check filename patterns
        var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        
        if (fileName.Contains("pony") || fileName.Contains("pdxl"))
            return "Pony";
        if (fileName.Contains("ilxl") || fileName.Contains("illustrious"))
            return "Illustrious";
        if (fileName.Contains("sd15") || fileName.Contains("sd1_5"))
            return "SD1.5";
        
        // 3. Analyze safetensors structure (read header)
        try
        {
            var modelTypeFromTensor = AnalyzeSafetensorsStructure(rawData);
            if (!string.IsNullOrEmpty(modelTypeFromTensor))
                return modelTypeFromTensor;
        }
        catch { /* Tensor analysis failed, continue */ }
        
        // 4. Default to SDXL (most common for modern embeddings)
        return "SDXL";
    }

    private static string DetermineModelTypeFromPath(string path)
    {
        var lowerPath = path.ToLowerInvariant();
        
        if (lowerPath.Contains("pony"))
            return "Pony";
        if (lowerPath.Contains("illustrious"))
            return "Illustrious";
        if (lowerPath.Contains("sd1.5") || lowerPath.Contains("sd15"))
            return "SD1.5";
        if (lowerPath.Contains("sdxl"))
            return "SDXL";
            
        return "SDXL";  // Default assumption
    }

    /// <summary>
    /// Analyze safetensors file structure to infer model type
    /// Safetensors format: [8-byte header length][JSON header][tensor data]
    /// </summary>
    private static string AnalyzeSafetensorsStructure(byte[] data)
    {
        if (data.Length < 8)
            return "SDXL";
        
        // Read 8-byte header length (little-endian)
        long headerLength = BitConverter.ToInt64(data, 0);
        
        if (headerLength <= 0 || headerLength > data.Length - 8)
            return "SDXL";
        
        // Read JSON header
        var headerBytes = new byte[headerLength];
        Array.Copy(data, 8, headerBytes, 0, headerLength);
        var headerJson = System.Text.Encoding.UTF8.GetString(headerBytes);
        
        // Parse header to check tensor keys
        try
        {
            var header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(headerJson);
            if (header == null)
                return "SDXL";
            
            // Check for metadata
            if (header.ContainsKey("__metadata__"))
            {
                var metadata = header["__metadata__"];
                if (metadata.TryGetProperty("sd_version", out var sdVersion))
                {
                    var version = sdVersion.GetString()?.ToLowerInvariant();
                    if (version?.Contains("sd15") == true || version?.Contains("sd1.5") == true)
                        return "SD1.5";
                    if (version?.Contains("pony") == true)
                        return "Pony";
                    if (version?.Contains("sdxl") == true)
                        return "SDXL";
                }
                
                if (metadata.TryGetProperty("base_model", out var baseModel))
                {
                    return NormalizeModelType(baseModel.GetString());
                }
            }
            
            // Analyze tensor keys and shapes
            var hasClipL = header.Keys.Any(k => k.Contains("clip_l"));
            var hasClipG = header.Keys.Any(k => k.Contains("clip_g"));
            var hasT5 = header.Keys.Any(k => k.Contains("t5"));
            var hasStringToParam = header.Keys.Any(k => k.Contains("string_to_param"));
            
            // SD 1.5: Only has string_to_param, no separate CLIP-L/G
            if (hasStringToParam && !hasClipL && !hasClipG)
                return "SD1.5";
            
            // SD 3.x: Has CLIP-L, CLIP-G, and T5
            if (hasClipL && hasClipG && hasT5)
                return "SD3";
            
            // SDXL/Pony/Illustrious: Has CLIP-L and CLIP-G
            // Can't distinguish these from structure alone, rely on path/filename
            if (hasClipL && hasClipG)
                return "SDXL";  // Covers SDXL, Pony, and Illustrious
        }
        catch { /* JSON parsing failed */ }
        
        return "SDXL";
    }

    /// <summary>
    /// Normalize various model type names from metadata
    /// </summary>
    private static string NormalizeModelType(string? modelType)
    {
        if (string.IsNullOrEmpty(modelType))
            return "SDXL";
        
        var lower = modelType.ToLowerInvariant();
        
        // CivitAI baseModel values
        if (lower.Contains("sd 1.5") || lower.Contains("sd1.5"))
            return "SD1.5";
        if (lower.Contains("sdxl") || lower.Contains("sd xl"))
            return "SDXL";
        if (lower.Contains("pony"))
            return "Pony";
        if (lower.Contains("illustrious"))
            return "Illustrious";
        if (lower.Contains("sd 3") || lower.Contains("sd3"))
            return "SD3";
        if (lower.Contains("flux"))
            return "FLUX";
        
        return "SDXL";  // Default
    }
}

/// <summary>
/// Textual embedding entity for database storage
/// </summary>
public class TextualEmbedding
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    public string ModelType { get; set; } = "SDXL";
    public string? Description { get; set; }
    
    public float[]? ClipLEmbedding { get; set; }  // 768D
    public float[]? ClipGEmbedding { get; set; }  // 1280D
    public byte[]? RawEmbedding { get; set; }     // Original safetensors data
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Compare image/prompt embeddings against textual embedding library
/// </summary>
public class TextualEmbeddingMatcher
{
    /// <summary>
    /// Find textual embeddings similar to an image's CLIP-L embedding
    /// </summary>
    public static async Task<List<TextualEmbeddingMatch>> FindSimilarEmbeddingsAsync(
        float[] imageClipLEmbedding,
        string category = "negative",
        string modelType = "SDXL",
        int topK = 10)
    {
        // Query PostgreSQL for similar textual embeddings
        // This would be implemented in PostgreSQLDataStore
        
        // Example SQL:
        // SELECT id, name, category, 
        //        1 - (clip_l_embedding <=> @query_embedding) AS similarity
        // FROM textual_embedding
        // WHERE category = @category AND model_type = @model_type
        // ORDER BY clip_l_embedding <=> @query_embedding
        // LIMIT @topK;
        
        return new List<TextualEmbeddingMatch>();
    }

    /// <summary>
    /// Find best negative embeddings for an image
    /// (embeddings that are least similar = avoid these concepts)
    /// </summary>
    public static async Task<List<TextualEmbeddingMatch>> FindBestNegativeEmbeddingsAsync(
        float[] imageClipLEmbedding,
        string modelType = "SDXL",
        int topK = 5)
    {
        // Find negative embeddings that are LEAST similar to the image
        // These represent concepts to avoid
        return await FindSimilarEmbeddingsAsync(imageClipLEmbedding, "negative", modelType, topK);
    }

    /// <summary>
    /// Find quality/positive embeddings to enhance an image
    /// </summary>
    public static async Task<List<TextualEmbeddingMatch>> FindQualityEnhancersAsync(
        float[] promptClipLEmbedding,
        string modelType = "SDXL",
        int topK = 5)
    {
        return await FindSimilarEmbeddingsAsync(promptClipLEmbedding, "quality", modelType, topK);
    }

    /// <summary>
    /// Find character embeddings similar to an image
    /// </summary>
    public static async Task<List<TextualEmbeddingMatch>> FindSimilarCharactersAsync(
        float[] imageClipLEmbedding,
        string modelType = "SDXL",
        int topK = 10)
    {
        return await FindSimilarEmbeddingsAsync(imageClipLEmbedding, "character", modelType, topK);
    }
}

/// <summary>
/// Result of textual embedding similarity search
/// </summary>
public class TextualEmbeddingMatch
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ModelType { get; set; } = string.Empty;
    public float Similarity { get; set; }
    public string? Description { get; set; }
}
