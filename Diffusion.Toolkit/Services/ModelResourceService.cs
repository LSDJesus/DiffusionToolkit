using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Localization;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Scans model folders for resources (LoRAs, embeddings, checkpoints, etc.)
/// and enriches them with Civitai metadata
/// </summary>
public class ModelResourceService
{
    private readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors",
        ".ckpt",
        ".pt",
        ".pth",
        ".bin"
    };

    private string GetLocalizedText(string key)
    {
        return (string)JsonLocalizationProvider.Instance.GetLocalizedObject(key, null!, CultureInfo.InvariantCulture);
    }

    private Settings _settings => ServiceLocator.Settings!;
    private PostgreSQLDataStore _dataStore => ServiceLocator.DataStore!;

    /// <summary>
    /// Default model folders based on Brian's setup
    /// </summary>
    public static readonly List<(string Path, string ResourceType)> DefaultModelFolders = new()
    {
        (@"D:\AI\SD Models\loras", ResourceTypes.Lora),
        (@"D:\AI\SD Models\embeddings", ResourceTypes.Embedding),
        (@"D:\AI\SD Models\diffusion_models", ResourceTypes.DiffusionModel),
        (@"D:\AI\SD Models\unet", ResourceTypes.Unet),
        (@"D:\AI\SD Models\upscale_models", ResourceTypes.Upscaler),
    };

    /// <summary>
    /// Scan all configured model folders for resources
    /// </summary>
    public async Task ScanAllFoldersAsync(CancellationToken cancellationToken)
    {
        var folders = await _dataStore.GetModelFoldersAsync();
        
        if (!folders.Any())
        {
            Logger.Log("No model folders configured. Add folders in Settings > Model Library.");
            return;
        }

        var totalResources = 0;
        var newResources = 0;

        foreach (var folder in folders.Where(f => f.Enabled))
        {
            if (cancellationToken.IsCancellationRequested) break;

            var (total, added) = await ScanFolderAsync(folder, cancellationToken);
            totalResources += total;
            newResources += added;
        }

        Logger.Log($"Model scan complete: {totalResources} resources found, {newResources} new");
    }

    /// <summary>
    /// Scan a single folder for model resources
    /// </summary>
    public async Task<(int Total, int Added)> ScanFolderAsync(ModelFolder folder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder.Path))
        {
            Logger.Log($"Model folder not found: {folder.Path}");
            return (0, 0);
        }

        Logger.Log($"Scanning model folder: {folder.Path} (type: {folder.ResourceType})");

        var searchOption = folder.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = new List<string>();

        foreach (var ext in _supportedExtensions)
        {
            files.AddRange(Directory.GetFiles(folder.Path, $"*{ext}", searchOption));
        }

        var added = 0;

        foreach (var filePath in files)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var existing = await _dataStore.GetModelResourceByPathAsync(filePath);
                if (existing != null)
                {
                    // Update if file changed
                    var fileInfo = new FileInfo(filePath);
                    if (existing.FileSize != fileInfo.Length)
                    {
                        await UpdateResourceAsync(existing, fileInfo, folder.ResourceType);
                    }
                    continue;
                }

                var resource = await CreateResourceFromFileAsync(filePath, folder.ResourceType);
                if (resource != null)
                {
                    await _dataStore.InsertModelResourceAsync(resource);
                    added++;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error scanning model file {filePath}: {ex.Message}");
            }
        }

        // Update last scanned time
        await _dataStore.UpdateModelFolderScannedAsync(folder.Id);

        return (files.Count, added);
    }

    /// <summary>
    /// Create a ModelResource from a file on disk
    /// </summary>
    private async Task<ModelResource?> CreateResourceFromFileAsync(string filePath, string defaultResourceType)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) return null;

        var resource = new ModelResource
        {
            FilePath = filePath,
            FileName = Path.GetFileNameWithoutExtension(filePath),
            FileSize = fileInfo.Length,
            ResourceType = DetermineResourceType(filePath, defaultResourceType),
            ScannedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        // Try to compute hash for Civitai lookup (AutoV2 hash - first 10 chars of SHA256)
        try
        {
            resource.FileHash = await ComputeAutoV2HashAsync(filePath);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to compute hash for {filePath}: {ex.Message}");
        }

        // Try to extract local metadata from safetensors or sidecar JSON
        // This also populates Civitai fields if a sidecar JSON exists (avoiding API calls)
        try
        {
            var (localMetadata, hasCivitaiData) = await ExtractLocalMetadataAsync(filePath, resource);
            resource.LocalMetadata = localMetadata;
            
            // Try to infer base model from metadata (if not already set from Civitai sidecar)
            if (string.IsNullOrEmpty(resource.BaseModel) && !string.IsNullOrEmpty(resource.LocalMetadata))
            {
                resource.BaseModel = InferBaseModelFromMetadata(resource.LocalMetadata);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to extract metadata from {filePath}: {ex.Message}");
        }

        // Try to extract and cache preview image
        try
        {
            resource.HasPreviewImage = await ExtractAndCachePreviewAsync(filePath, resource.LocalMetadata);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to extract preview from {filePath}: {ex.Message}");
        }

        return resource;
    }

    /// <summary>
    /// Update an existing resource with new file info
    /// </summary>
    private async Task UpdateResourceAsync(ModelResource resource, FileInfo fileInfo, string defaultResourceType)
    {
        resource.FileSize = fileInfo.Length;
        resource.ScannedAt = DateTime.UtcNow;
        resource.Unavailable = false;

        // Recompute hash if size changed
        try
        {
            resource.FileHash = await ComputeAutoV2HashAsync(resource.FilePath);
        }
        catch { }

        await _dataStore.UpdateModelResourceAsync(resource);
    }

    /// <summary>
    /// Compute AutoV2 hash (Civitai format) - first 10 hex chars of SHA256
    /// </summary>
    private async Task<string> ComputeAutoV2HashAsync(string filePath)
    {
        // AutoV2 uses SHA256 of the entire file, taking first 10 hex characters
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var sha256 = SHA256.Create();
        
        var hashBytes = await sha256.ComputeHashAsync(stream);
        var fullHash = BitConverter.ToString(hashBytes).Replace("-", "").ToUpperInvariant();
        
        return fullHash.Substring(0, 10);
    }

    /// <summary>
    /// Extract metadata from safetensors header or companion JSON file.
    /// Also populates Civitai fields if found in sidecar JSON (to avoid API calls).
    /// </summary>
    private async Task<(string? LocalMetadata, bool HasCivitaiData)> ExtractLocalMetadataAsync(string filePath, ModelResource resource)
    {
        bool hasCivitaiData = false;
        
        // First, check for companion .json file (created by ComfyUI Manager / Civitai scraper)
        var jsonPath = filePath + ".json";
        if (File.Exists(jsonPath))
        {
            var jsonContent = await File.ReadAllTextAsync(jsonPath);
            
            // Try to parse Civitai fields from the sidecar JSON
            hasCivitaiData = TryParseCivitaiSidecar(jsonContent, resource);
            
            return (jsonContent, hasCivitaiData);
        }

        // For .safetensors, try to read the header metadata
        if (filePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            var metadata = await ExtractSafetensorsMetadataAsync(filePath);
            return (metadata, false); // Safetensors headers don't have full Civitai data
        }

        return (null, false);
    }
    
    /// <summary>
    /// Try to parse Civitai metadata from a sidecar JSON file.
    /// These are created by ComfyUI Manager or manual downloads from Civitai.
    /// </summary>
    private bool TryParseCivitaiSidecar(string jsonContent, ModelResource resource)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;
            
            // Check for Civitai-style fields (from model download or ComfyUI Manager)
            bool foundCivitaiData = false;
            
            // Model ID
            if (root.TryGetProperty("modelId", out var modelId) && modelId.ValueKind == JsonValueKind.Number)
            {
                resource.CivitaiId = modelId.GetInt32();
                foundCivitaiData = true;
            }
            
            // Version ID
            if (root.TryGetProperty("id", out var versionId) && versionId.ValueKind == JsonValueKind.Number)
            {
                resource.CivitaiVersionId = versionId.GetInt32();
                foundCivitaiData = true;
            }
            
            // Name
            if (root.TryGetProperty("model", out var model) && model.TryGetProperty("name", out var modelName))
            {
                resource.CivitaiName = modelName.GetString();
                foundCivitaiData = true;
            }
            else if (root.TryGetProperty("name", out var name))
            {
                resource.CivitaiName = name.GetString();
                foundCivitaiData = true;
            }
            
            // Description
            if (root.TryGetProperty("description", out var desc))
            {
                resource.CivitaiDescription = desc.GetString();
            }
            
            // Base model
            if (root.TryGetProperty("baseModel", out var baseModel))
            {
                resource.CivitaiBaseModel = baseModel.GetString();
                resource.BaseModel = NormalizeBaseModel(resource.CivitaiBaseModel);
            }
            
            // Trained words (trigger words)
            if (root.TryGetProperty("trainedWords", out var trainedWords) && trainedWords.ValueKind == JsonValueKind.Array)
            {
                resource.CivitaiTrainedWords = trainedWords.EnumerateArray()
                    .Where(w => w.ValueKind == JsonValueKind.String)
                    .Select(w => w.GetString()!)
                    .ToArray();
            }
            
            // NSFW flag
            if (root.TryGetProperty("model", out var modelObj) && modelObj.TryGetProperty("nsfw", out var nsfw))
            {
                resource.CivitaiNsfw = nsfw.GetBoolean();
            }
            
            if (foundCivitaiData)
            {
                // Store the full JSON as civitai_metadata and mark as fetched
                resource.CivitaiMetadata = jsonContent;
                resource.CivitaiFetchedAt = DateTime.UtcNow;
                Logger.Log($"Found Civitai sidecar for {resource.FileName}: {resource.CivitaiName}");
            }
            
            return foundCivitaiData;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to parse Civitai sidecar JSON: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Normalize Civitai base model names to our standard format
    /// </summary>
    private static string? NormalizeBaseModel(string? civitaiBaseModel)
    {
        if (string.IsNullOrEmpty(civitaiBaseModel)) return null;
        
        var lower = civitaiBaseModel.ToLowerInvariant();

        if (lower.Contains("pony")) return BaseModels.Pony;
        if (lower.Contains("illustrious")) return BaseModels.Illustrious;
        if (lower.Contains("flux")) return BaseModels.Flux;
        if (lower.Contains("sd 3") || lower.Contains("sd3")) return BaseModels.SD3;
        if (lower.Contains("sdxl") || lower.Contains("sd xl")) return BaseModels.SDXL;
        if (lower.Contains("sd 2") || lower.Contains("sd2")) return BaseModels.SD21;
        if (lower.Contains("sd 1") || lower.Contains("sd1") || lower.Contains("1.5")) return BaseModels.SD15;

        return civitaiBaseModel; // Keep original if unrecognized
    }

    /// <summary>
    /// Extract metadata from safetensors file header
    /// Safetensors format: 8-byte header length (little endian) + JSON header + tensor data
    /// </summary>
    private async Task<string?> ExtractSafetensorsMetadataAsync(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            // Read header length (8 bytes, little endian)
            var headerLengthBytes = reader.ReadBytes(8);
            var headerLength = BitConverter.ToInt64(headerLengthBytes, 0);

            // Sanity check - header shouldn't be larger than 10MB
            if (headerLength <= 0 || headerLength > 10 * 1024 * 1024)
            {
                return null;
            }

            // Read header JSON
            var headerBytes = reader.ReadBytes((int)headerLength);
            var headerJson = System.Text.Encoding.UTF8.GetString(headerBytes);

            // Parse and extract __metadata__ section
            using var doc = JsonDocument.Parse(headerJson);
            if (doc.RootElement.TryGetProperty("__metadata__", out var metadata))
            {
                return metadata.GetRawText();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract preview image from safetensors metadata or companion file and cache in SQLite
    /// </summary>
    private async Task<bool> ExtractAndCachePreviewAsync(string modelFilePath, string? localMetadata)
    {
        const int thumbnailSize = 256;
        
        // Check if we already have a cached thumbnail
        if (Thumbnails.ThumbnailCache.Instance.HasThumbnail(modelFilePath, thumbnailSize))
        {
            return true;
        }
        
        // First, check for companion preview file (higher priority - user may have custom previews)
        var previewPath = GetModelPreviewPath(modelFilePath);
        if (!string.IsNullOrEmpty(previewPath))
        {
            try
            {
                var imageBytes = await File.ReadAllBytesAsync(previewPath);
                if (imageBytes.Length > 0)
                {
                    // Resize and cache
                    var resizedBytes = ResizeImage(imageBytes, thumbnailSize);
                    if (resizedBytes != null)
                    {
                        Thumbnails.ThumbnailCache.Instance.AddThumbnailFromBytes(modelFilePath, thumbnailSize, resizedBytes);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load preview file {previewPath}: {ex.Message}");
            }
        }
        
        // Try to extract from safetensors metadata (ss_thumbnail or thumbnail fields)
        if (!string.IsNullOrEmpty(localMetadata))
        {
            try
            {
                using var doc = JsonDocument.Parse(localMetadata);
                var root = doc.RootElement;
                
                // Check common thumbnail field names
                string[] thumbnailFields = { "ss_thumbnail", "thumbnail", "preview", "ss_preview" };
                
                foreach (var field in thumbnailFields)
                {
                    if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var base64Data = value.GetString();
                        if (!string.IsNullOrEmpty(base64Data))
                        {
                            // Handle data URI format (data:image/png;base64,...)
                            if (base64Data.Contains(","))
                            {
                                base64Data = base64Data.Substring(base64Data.IndexOf(',') + 1);
                            }
                            
                            var imageBytes = Convert.FromBase64String(base64Data);
                            if (imageBytes.Length > 0)
                            {
                                var resizedBytes = ResizeImage(imageBytes, thumbnailSize);
                                if (resizedBytes != null)
                                {
                                    Thumbnails.ThumbnailCache.Instance.AddThumbnailFromBytes(modelFilePath, thumbnailSize, resizedBytes);
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to extract embedded preview from metadata: {ex.Message}");
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Resize an image to thumbnail size and return as JPEG bytes
    /// </summary>
    private byte[]? ResizeImage(byte[] imageBytes, int targetSize)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = ms;
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            
            // Calculate resize dimensions
            double scale;
            if (bitmap.PixelWidth > bitmap.PixelHeight)
            {
                scale = (double)targetSize / bitmap.PixelWidth;
            }
            else
            {
                scale = (double)targetSize / bitmap.PixelHeight;
            }
            
            var transform = new System.Windows.Media.ScaleTransform(scale, scale);
            var resized = new System.Windows.Media.Imaging.TransformedBitmap(bitmap, transform);
            
            // Encode as JPEG
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
            encoder.QualityLevel = 85;
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(resized));
            
            using var outputMs = new MemoryStream();
            encoder.Save(outputMs);
            return outputMs.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to resize image: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Determine resource type from file path and folder context
    /// </summary>
    private string DetermineResourceType(string filePath, string defaultType)
    {
        var lowerPath = filePath.ToLowerInvariant();

        // Check path components for hints
        if (lowerPath.Contains("lora") || lowerPath.Contains("lycoris"))
            return ResourceTypes.Lora;
        if (lowerPath.Contains("embedding") || lowerPath.Contains("textual"))
            return ResourceTypes.Embedding;
        if (lowerPath.Contains("vae"))
            return ResourceTypes.Vae;
        if (lowerPath.Contains("controlnet"))
            return ResourceTypes.ControlNet;
        if (lowerPath.Contains("upscale") || lowerPath.Contains("esrgan") || lowerPath.Contains("realesrgan"))
            return ResourceTypes.Upscaler;
        if (lowerPath.Contains("unet"))
            return ResourceTypes.Unet;
        if (lowerPath.Contains("clip"))
            return ResourceTypes.Clip;
        if (lowerPath.Contains("diffusion_model"))
            return ResourceTypes.DiffusionModel;
        if (lowerPath.Contains("checkpoint") || lowerPath.Contains("model"))
            return ResourceTypes.Checkpoint;

        return defaultType;
    }

    /// <summary>
    /// Try to infer base model from local metadata
    /// </summary>
    private string? InferBaseModelFromMetadata(string metadataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            // Check common metadata fields
            string[] modelFields = { "ss_base_model", "baseModel", "base_model", "ss_sd_model_name" };
            
            foreach (var field in modelFields)
            {
                if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var modelName = value.GetString()?.ToLowerInvariant() ?? "";
                    
                    if (modelName.Contains("pony")) return BaseModels.Pony;
                    if (modelName.Contains("illustrious")) return BaseModels.Illustrious;
                    if (modelName.Contains("sdxl") || modelName.Contains("sd_xl")) return BaseModels.SDXL;
                    if (modelName.Contains("flux")) return BaseModels.Flux;
                    if (modelName.Contains("sd3")) return BaseModels.SD3;
                    if (modelName.Contains("sd-v1") || modelName.Contains("sd1") || modelName.Contains("1.5")) return BaseModels.SD15;
                    if (modelName.Contains("sd-v2") || modelName.Contains("sd2") || modelName.Contains("2.1")) return BaseModels.SD21;
                }
            }

            // Check for resolution hints (SDXL vs SD1.5)
            if (root.TryGetProperty("ss_resolution", out var resolution))
            {
                var res = resolution.GetString() ?? "";
                if (res.Contains("1024")) return BaseModels.SDXL;
                if (res.Contains("512")) return BaseModels.SD15;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Find a model resource by name (for linking images to resources)
    /// </summary>
    public async Task<ModelResource?> FindResourceByNameAsync(string name, string resourceType)
    {
        return await _dataStore.FindModelResourceByNameAsync(name, resourceType);
    }

    /// <summary>
    /// Link an image to the resources it uses (extracted from prompt/workflow)
    /// </summary>
    public async Task LinkImageToResourcesAsync(int imageId, IEnumerable<(string Name, string Type, decimal? Strength)> resources)
    {
        foreach (var (name, type, strength) in resources)
        {
            // Try to find the resource on disk
            var resource = await FindResourceByNameAsync(name, type);

            await _dataStore.LinkImageToResourceAsync(imageId, resource?.Id, name, type, strength);
        }
    }

    /// <summary>
    /// Mark resources as unavailable if files no longer exist
    /// </summary>
    public async Task CheckResourceAvailabilityAsync(CancellationToken cancellationToken)
    {
        var resources = await _dataStore.GetAllModelResourcesAsync();
        var unavailableCount = 0;

        foreach (var resource in resources)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var exists = File.Exists(resource.FilePath);
            if (exists != !resource.Unavailable)
            {
                resource.Unavailable = !exists;
                await _dataStore.UpdateModelResourceAvailabilityAsync(resource.Id, !exists);
                if (!exists) unavailableCount++;
            }
        }

        if (unavailableCount > 0)
        {
            Logger.Log($"Marked {unavailableCount} model resources as unavailable");
        }
    }

    /// <summary>
    /// Get resources used by a specific image
    /// </summary>
    public async Task<List<ImageResource>> GetImageResourcesAsync(int imageId)
    {
        return await _dataStore.GetImageResourcesAsync(imageId);
    }

    /// <summary>
    /// Get images that use a specific resource
    /// </summary>
    public async Task<List<int>> GetImagesUsingResourceAsync(int resourceId)
    {
        return await _dataStore.GetImagesUsingResourceAsync(resourceId);
    }

    /// <summary>
    /// Find resources missing from disk but referenced in images
    /// </summary>
    public async Task<List<string>> GetMissingResourceNamesAsync()
    {
        return await _dataStore.GetMissingResourceNamesAsync();
    }
    
    /// <summary>
    /// Get the thumbnail/preview image path for a model resource.
    /// Checks for companion preview files in common naming conventions.
    /// </summary>
    public string? GetModelPreviewPath(string modelFilePath)
    {
        var directory = Path.GetDirectoryName(modelFilePath);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(modelFilePath);
        
        if (string.IsNullOrEmpty(directory)) return null;
        
        // Common preview file naming conventions used by various tools
        string[] previewPatterns = 
        {
            $"{fileNameWithoutExt}.preview.png",
            $"{fileNameWithoutExt}.preview.jpg",
            $"{fileNameWithoutExt}.preview.jpeg",
            $"{fileNameWithoutExt}.png",
            $"{fileNameWithoutExt}.jpg",
            $"{fileNameWithoutExt}.jpeg",
            $"{fileNameWithoutExt}.webp",
            $"{fileNameWithoutExt}.thumbnail.png",
            $"{fileNameWithoutExt}.thumb.png",
        };
        
        foreach (var pattern in previewPatterns)
        {
            var previewPath = Path.Combine(directory, pattern);
            if (File.Exists(previewPath))
            {
                return previewPath;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Get models by resource type for display
    /// </summary>
    public async Task<List<ModelResource>> GetModelsByTypeAsync(string resourceType, int limit = 100, int offset = 0)
    {
        return await _dataStore.GetModelResourcesByTypeAsync(resourceType, limit, offset);
    }
    
    /// <summary>
    /// Get all models for a folder path
    /// </summary>
    public async Task<List<ModelResource>> GetModelsByFolderAsync(string folderPath, int limit = 100, int offset = 0)
    {
        return await _dataStore.GetModelResourcesByFolderAsync(folderPath, limit, offset);
    }
    
    /// <summary>
    /// Search models by name
    /// </summary>
    public async Task<List<ModelResource>> SearchModelsAsync(string searchTerm, int limit = 100)
    {
        return await _dataStore.SearchModelResourcesAsync(searchTerm, limit);
    }
}
