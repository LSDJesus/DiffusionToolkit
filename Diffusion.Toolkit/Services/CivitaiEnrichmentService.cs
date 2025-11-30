using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Civitai;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Enriches model resources with metadata from Civitai API
/// </summary>
public class CivitaiEnrichmentService
{
    private readonly CivitaiClient _client;
    private PostgreSQLDataStore _dataStore => ServiceLocator.DataStore!;
    
    // Rate limiting - Civitai has request limits
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private readonly TimeSpan _minRequestInterval = TimeSpan.FromMilliseconds(500);

    public CivitaiEnrichmentService()
    {
        _client = new CivitaiClient();
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

            // Populate Civitai fields
            resource.CivitaiId = modelVersion.Model?.Name != null ? modelVersion.ModelId : null;
            resource.CivitaiVersionId = modelVersion.Id;
            resource.CivitaiName = modelVersion.Model?.Name ?? modelVersion.Name;
            resource.CivitaiDescription = CleanDescription(modelVersion.Description);
            resource.CivitaiTrainedWords = modelVersion.TrainedWords?.ToArray();
            resource.CivitaiBaseModel = modelVersion.BaseModel;
            resource.CivitaiNsfw = modelVersion.Model?.Nsfw ?? false;
            
            // Store full response as JSON for future use
            resource.CivitaiMetadata = System.Text.Json.JsonSerializer.Serialize(modelVersion);
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
            // Not on Civitai
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
    /// Clean HTML from Civitai description
    /// </summary>
    private static string? CleanDescription(string? description)
    {
        if (string.IsNullOrEmpty(description)) return null;

        // Simple HTML tag removal
        var cleaned = System.Text.RegularExpressions.Regex.Replace(description, "<[^>]+>", " ");
        cleaned = System.Net.WebUtility.HtmlDecode(cleaned);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
        
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

    public void Dispose()
    {
        _client.Dispose();
        _rateLimiter.Dispose();
    }
}
