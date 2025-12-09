using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Civitai;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Enriches model resources with metadata from Civitai API
/// </summary>
public class CivitaiEnrichmentService
{
    private readonly CivitaiClient _client;
    private readonly HttpClient _httpClient;
    private PostgreSQLDataStore _dataStore => ServiceLocator.DataStore!;
    
    // Rate limiting - Civitai has request limits
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private readonly TimeSpan _minRequestInterval = TimeSpan.FromMilliseconds(500);
    
    // Thumbnail settings (matching Luna node format)
    private const int MaxThumbnailSize = 256;
    private const int ThumbnailQuality = 85;

    public CivitaiEnrichmentService()
    {
        _client = new CivitaiClient();
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Enrich resources that haven't been fetched from Civitai yet
    /// </summary>
    public async Task EnrichPendingResourcesAsync(CancellationToken cancellationToken, IProgress<(int Current, int Total)>? progress = null)
    {
        var resources = await _dataStore.GetResourcesNeedingCivitaiAsync(500);
        
        if (!resources.Any())
        {
            Logger.Log("No resources need Civitai enrichment");
            return;
        }

        Logger.Log($"Enriching {resources.Count} resources from Civitai...");

        var current = 0;
        var success = 0;
        var notFound = 0;

        foreach (var resource in resources)
        {
            if (cancellationToken.IsCancellationRequested) break;

            current++;
            progress?.Report((current, resources.Count));

            try
            {
                var enriched = await EnrichResourceAsync(resource, cancellationToken);
                if (enriched) success++;
                else notFound++;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error enriching {resource.FileName}: {ex.Message}");
            }
        }

        Logger.Log($"Civitai enrichment complete: {success} found, {notFound} not on Civitai");
    }

    /// <summary>
    /// Enrich a single resource with Civitai metadata
    /// </summary>
    public async Task<bool> EnrichResourceAsync(ModelResource resource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(resource.FileHash))
        {
            // Mark as fetched so we don't retry
            resource.CivitaiFetchedAt = DateTime.UtcNow;
            await _dataStore.UpdateResourceCivitaiAsync(resource);
            return false;
        }

        // Rate limiting
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < _minRequestInterval)
            {
                await Task.Delay(_minRequestInterval - elapsed, cancellationToken);
            }
            _lastRequest = DateTime.UtcNow;

            // Query Civitai by hash
            var modelVersion = await _client.GetModelVersionsByHashAsync(resource.FileHash, cancellationToken);

            if (modelVersion == null)
            {
                // Not found on Civitai - mark as fetched so we don't retry
                resource.CivitaiFetchedAt = DateTime.UtcNow;
                await _dataStore.UpdateResourceCivitaiAsync(resource);
                return false;
            }

            // Populate Civitai fields from API response
            // If a field is missing/null, explicitly set it to empty to prevent re-querying
            resource.CivitaiId = modelVersion.Model?.Name != null ? modelVersion.ModelId : null;
            resource.CivitaiVersionId = modelVersion.Id;
            resource.CivitaiName = modelVersion.Model?.Name ?? modelVersion.Name ?? ""; // Empty string if no name
            resource.CivitaiDescription = CleanDescription(modelVersion.Description) ?? ""; // Empty string if no description
            resource.CivitaiTrainedWords = modelVersion.TrainedWords?.Any() == true ? modelVersion.TrainedWords.ToArray() : Array.Empty<string>();
            resource.CivitaiBaseModel = modelVersion.BaseModel ?? ""; // Empty string if no base model
            resource.CivitaiNsfw = modelVersion.Model?.Nsfw ?? false;
            
            // Extended fields (matching Luna node modelspec.* format)
            resource.CivitaiAuthor = modelVersion.Model?.Creator?.Username ?? ""; // Empty string if no author
            resource.CivitaiTags = modelVersion.Model?.Tags?.Any() == true ? modelVersion.Model.Tags.ToArray() : Array.Empty<string>();
            resource.CivitaiDefaultWeight = modelVersion.Weight;
            resource.CivitaiDefaultClipWeight = modelVersion.ClipWeight;
            resource.CivitaiPublishedAt = modelVersion.PublishedAt ?? modelVersion.CreatedAt;
            
            // Get cover image URL (first image from version)
            var coverImage = modelVersion.Images?.FirstOrDefault();
            if (coverImage != null)
            {
                resource.CivitaiCoverImageUrl = coverImage.Url;
                
                // Download and resize thumbnail
                try
                {
                    resource.CivitaiThumbnail = await DownloadAndResizeThumbnailAsync(coverImage.Url, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to download thumbnail for {resource.FileName}: {ex.Message}");
                    // Still mark as processed even if thumbnail failed
                    resource.CivitaiCoverImageUrl = null;
                    resource.CivitaiThumbnail = null;
                }
            }
            else
            {
                // No cover image found - explicitly mark as empty
                resource.CivitaiCoverImageUrl = "";
                resource.CivitaiThumbnail = null;
            }
            
            // Store full response as JSON for future use
            resource.CivitaiMetadata = System.Text.Json.JsonSerializer.Serialize(modelVersion);
            
            // Mark as fetched - this is the key: even if fields are empty, we've checked Civitai
            resource.CivitaiFetchedAt = DateTime.UtcNow;

            // Update base model if we got it from Civitai
            if (!string.IsNullOrEmpty(modelVersion.BaseModel))
            {
                resource.BaseModel = NormalizeBaseModel(modelVersion.BaseModel);
            }

            await _dataStore.UpdateResourceCivitaiAsync(resource);

            Logger.Log($"Enriched: {resource.FileName} → {resource.CivitaiName} ({resource.BaseModel})");
            return true;
        }
        catch (CivitaiRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Not on Civitai - mark as fetched so we don't retry
            resource.CivitaiFetchedAt = DateTime.UtcNow;
            await _dataStore.UpdateResourceCivitaiAsync(resource);
            return false;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// Clean HTML and markdown from Civitai description
    /// </summary>
    private static string? CleanDescription(string? description)
    {
        if (string.IsNullOrEmpty(description)) return null;

        // Remove HTML tags
        var cleaned = System.Text.RegularExpressions.Regex.Replace(description, "<[^>]+>", " ");
        
        // Decode HTML entities (e.g., &nbsp; -> space, &quot; -> ")
        cleaned = System.Net.WebUtility.HtmlDecode(cleaned);
        
        // Remove markdown formatting
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[*_]{1,2}(.+?)[*_]{1,2}", "$1"); // Bold/italic
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^#+\s", "", System.Text.RegularExpressions.RegexOptions.Multiline); // Headers
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\[(.+?)\]\(.+?\)", "$1"); // Links
        
        // Normalize whitespace
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n\s*\n", "\n"); // Remove blank lines
        
        return cleaned.Trim();
    }

    /// <summary>
    /// Normalize Civitai base model names to our standard format
    /// </summary>
    private static string NormalizeBaseModel(string civitaiBaseModel)
    {
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
    /// Get trigger words for a resource (from Civitai trained_words)
    /// </summary>
    public async Task<string[]> GetTriggerWordsAsync(int resourceId)
    {
        await using var connection = await _dataStore.OpenConnectionAsync();
        var sql = "SELECT civitai_trained_words FROM model_resource WHERE id = @Id";
        var result = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<string[]?>(connection, sql, new { Id = resourceId });
        return result ?? Array.Empty<string>();
    }

    /// <summary>
    /// Download image from URL and resize to thumbnail
    /// </summary>
    private async Task<byte[]?> DownloadAndResizeThumbnailAsync(string imageUrl, CancellationToken cancellationToken)
    {
        try
        {
            // Download image
            var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl, cancellationToken);
            
            using var image = ImageSharpImage.Load(imageBytes);
            
            // Calculate new size maintaining aspect ratio
            var maxDimension = Math.Max(image.Width, image.Height);
            if (maxDimension > MaxThumbnailSize)
            {
                var scale = (float)MaxThumbnailSize / maxDimension;
                var newWidth = (int)(image.Width * scale);
                var newHeight = (int)(image.Height * scale);
                
                image.Mutate(x => x.Resize(newWidth, newHeight));
            }
            
            // Encode as JPEG
            using var ms = new System.IO.MemoryStream();
            var encoder = new JpegEncoder { Quality = ThumbnailQuality };
            await image.SaveAsync(ms, encoder, cancellationToken);
            
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Log($"Thumbnail download failed: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Get thumbnail as base64 string (for API responses)
    /// </summary>
    public static string? GetThumbnailBase64(byte[]? thumbnailBytes)
    {
        if (thumbnailBytes == null || thumbnailBytes.Length == 0)
            return null;
        return Convert.ToBase64String(thumbnailBytes);
    }

    /// <summary>
    /// Enrich resources by first checking model headers, sanitizing, then filling gaps from Civitai API
    /// </summary>
    public async Task EnrichFromHeadersThenAPIAsync(CancellationToken cancellationToken, IProgress<(int Current, int Total)>? progress = null)
    {
        var resources = await _dataStore.GetAllModelResourcesAsync();
        
        if (!resources.Any())
        {
            Logger.Log("No model resources found");
            return;
        }

        Logger.Log($"Starting header-first enrichment for {resources.Count} resources...");

        var current = 0;
        var fromHeaders = 0;
        var fromAPI = 0;
        var sanitized = 0;

        foreach (var resource in resources)
        {
            if (cancellationToken.IsCancellationRequested) break;

            current++;
            progress?.Report((current, resources.Count));

            try
            {
                // Step 1: Check if model file has metadata in header
                if (!string.IsNullOrEmpty(resource.LocalMetadata))
                {
                    var cleaned = CleanDescription(resource.LocalMetadata);
                    if (cleaned != resource.LocalMetadata)
                    {
                        resource.LocalMetadata = cleaned;
                        sanitized++;
                    }

                    // Parse Civitai metadata from header into resource fields
                    if (TryParseCivitaiMetadataFromHeader(resource.LocalMetadata, resource))
                    {
                        fromHeaders++;
                        await _dataStore.UpdateResourceCivitaiAsync(resource);
                        continue;
                    }
                }

                // Step 2: If header metadata is missing or incomplete, fill gaps from Civitai API
                if (string.IsNullOrEmpty(resource.CivitaiName) && !string.IsNullOrEmpty(resource.FileHash))
                {
                    if (await EnrichResourceAsync(resource, cancellationToken))
                    {
                        fromAPI++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error enriching {resource.FileName}: {ex.Message}");
            }
        }

        Logger.Log($"Header-first enrichment complete: {fromHeaders} from headers, {fromAPI} from Civitai API, {sanitized} sanitized");
    }

    /// <summary>
    /// Parse Civitai metadata from safetensors header or sidecar JSON
    /// Returns true if valid Civitai data was found and parsed
    /// </summary>
    private bool TryParseCivitaiMetadataFromHeader(string metadataJson, ModelResource resource)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            bool foundData = false;

            // Try to extract Civitai fields from metadata
            if (root.TryGetProperty("civitai_id", out var civitaiId) && civitaiId.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                resource.CivitaiId = civitaiId.GetInt32();
                foundData = true;
            }

            if (root.TryGetProperty("civitai_version_id", out var versionId) && versionId.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                resource.CivitaiVersionId = versionId.GetInt32();
                foundData = true;
            }

            if (root.TryGetProperty("civitai_name", out var name))
            {
                resource.CivitaiName = CleanDescription(name.GetString());
                foundData = true;
            }

            if (root.TryGetProperty("civitai_description", out var desc))
            {
                resource.CivitaiDescription = CleanDescription(desc.GetString());
                foundData = true;
            }

            if (root.TryGetProperty("civitai_base_model", out var baseModel))
            {
                resource.CivitaiBaseModel = baseModel.GetString();
                resource.BaseModel = NormalizeBaseModel(resource.CivitaiBaseModel);
                foundData = true;
            }

            if (root.TryGetProperty("civitai_trained_words", out var trainedWords) && trainedWords.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                resource.CivitaiTrainedWords = trainedWords.EnumerateArray()
                    .Where(w => w.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(w => w.GetString()!)
                    .ToArray();
                foundData = true;
            }

            if (root.TryGetProperty("civitai_author", out var author))
            {
                resource.CivitaiAuthor = author.GetString();
                foundData = true;
            }

            if (root.TryGetProperty("civitai_tags", out var tags) && tags.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                resource.CivitaiTags = tags.EnumerateArray()
                    .Where(t => t.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(t => t.GetString()!)
                    .ToArray();
                foundData = true;
            }

            if (foundData)
            {
                resource.CivitaiFetchedAt = DateTime.UtcNow;
                Logger.Log($"Parsed Civitai metadata from header: {resource.FileName} → {resource.CivitaiName}");
            }

            return foundData;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to parse metadata from header: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write model resource metadata to safetensors file header
    /// </summary>
    public async Task<bool> WriteMetadataToFileAsync(ModelResource resource, CancellationToken cancellationToken)
    {
        if (!resource.FilePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log($"WriteMetadataToFile only supports .safetensors files: {resource.FileName}");
            return false;
        }

        try
        {
            var metadataDict = BuildMetadataObject(resource);
            
            // This will require interaction with safetensors library to read/modify header
            // For now, log what would be written
            Logger.Log($"Would write metadata to {resource.FileName}: {string.Join(", ", metadataDict.Keys)}");
            
            // TODO: Implement actual safetensors header modification
            // This requires reading the full file, modifying the header, and writing it back
            
            return await ModifySafetensorsHeaderAsync(resource.FilePath, metadataDict, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error writing metadata to {resource.FileName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Build metadata dictionary from resource fields
    /// </summary>
    private Dictionary<string, object?> BuildMetadataObject(ModelResource resource)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["civitai_id"] = resource.CivitaiId,
            ["civitai_version_id"] = resource.CivitaiVersionId,
            ["civitai_name"] = resource.CivitaiName,
            ["civitai_description"] = resource.CivitaiDescription,
            ["civitai_base_model"] = resource.CivitaiBaseModel,
            ["civitai_author"] = resource.CivitaiAuthor,
            ["civitai_nsfw"] = resource.CivitaiNsfw,
            ["civitai_default_weight"] = resource.CivitaiDefaultWeight,
            ["civitai_default_clip_weight"] = resource.CivitaiDefaultClipWeight,
            ["civitai_published_at"] = resource.CivitaiPublishedAt,
        };

        if (resource.CivitaiTrainedWords?.Any() == true)
        {
            metadata["civitai_trained_words"] = resource.CivitaiTrainedWords;
        }

        if (resource.CivitaiTags?.Any() == true)
        {
            metadata["civitai_tags"] = resource.CivitaiTags;
        }

        return metadata;
    }

    /// <summary>
    /// Modify safetensors file header to include metadata
    /// </summary>
    private async Task<bool> ModifySafetensorsHeaderAsync(string filePath, Dictionary<string, object?> metadata, CancellationToken cancellationToken)
    {
        try
        {
            var backupPath = filePath + ".backup";
            
            // Create backup
            File.Copy(filePath, backupPath, true);

            using var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
            using var reader = new System.IO.BinaryReader(stream);

            // Read header length
            var headerLengthBytes = reader.ReadBytes(8);
            var headerLength = BitConverter.ToInt64(headerLengthBytes, 0);

            if (headerLength <= 0 || headerLength > 10 * 1024 * 1024)
            {
                Logger.Log($"Invalid header length: {headerLength}");
                return false;
            }

            // Read existing header
            var headerBytes = reader.ReadBytes((int)headerLength);
            var headerJson = System.Text.Encoding.UTF8.GetString(headerBytes);

            // Parse header
            using var doc = System.Text.Json.JsonDocument.Parse(headerJson);
            var headerObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(headerJson) ?? new Dictionary<string, object?>();

            // Update or create __metadata__ section
            if (!headerObj.ContainsKey("__metadata__"))
            {
                headerObj["__metadata__"] = metadata;
            }
            else if (headerObj["__metadata__"] is Dictionary<string, object?> existingMetadata)
            {
                // Merge with existing metadata
                foreach (var kvp in metadata)
                {
                    existingMetadata[kvp.Key] = kvp.Value;
                }
            }

            // Serialize updated header
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
            var updatedHeaderJson = System.Text.Json.JsonSerializer.Serialize(headerObj, options);
            var updatedHeaderBytes = System.Text.Encoding.UTF8.GetBytes(updatedHeaderJson);

            // Check if header size changed significantly
            if (updatedHeaderBytes.Length > 10 * 1024 * 1024)
            {
                Logger.Log($"Updated header too large: {updatedHeaderBytes.Length} bytes");
                File.Copy(backupPath, filePath, true);
                return false;
            }

            // Write new header
            stream.Seek(0, System.IO.SeekOrigin.Begin);
            var newHeaderLength = BitConverter.GetBytes((long)updatedHeaderBytes.Length);
            stream.Write(newHeaderLength, 0, 8);
            stream.Write(updatedHeaderBytes, 0, updatedHeaderBytes.Length);
            stream.Flush();

            Logger.Log($"Successfully wrote metadata to {System.IO.Path.GetFileName(filePath)}");
            File.Delete(backupPath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to modify safetensors header: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _httpClient.Dispose();
        _rateLimiter.Dispose();
    }
}
