using Dapper;
using Diffusion.Common;
using Diffusion.Scanner;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL DataStore methods for image embedding extraction and management
/// Handles explicit (embedding:name:weight) and implicit embedding matching
/// Implements IEmbeddingRegistry for use with EmbeddingExtractor
/// </summary>
public partial class PostgreSQLDataStore : IEmbeddingRegistry
{
    /// <summary>
    /// Insert or update embeddings for an image by path (convenience method)
    /// Uses the existing GetImageIdByPathAsync from EmbeddingCache.cs
    /// </summary>
    public async Task InsertImageEmbeddingsByPathAsync(
        string imagePath,
        List<(string Name, decimal Weight, bool IsImplicit)> embeddings,
        CancellationToken cancellationToken = default)
    {
        if (embeddings?.Count == 0)
            return;

        try
        {
            var imageId = await GetImageIdByPathAsync(imagePath, cancellationToken);
            if (!imageId.HasValue)
                return;

            await InsertImageEmbeddingsAsync(imageId.Value, embeddings ?? new List<(string Name, decimal Weight, bool IsImplicit)>(), cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Log($"InsertImageEmbeddingsByPathAsync failed for {imagePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Insert or update embeddings for an image
    /// </summary>
    public async Task InsertImageEmbeddingsAsync(
        int imageId,
        List<(string Name, decimal Weight, bool IsImplicit)> embeddings,
        CancellationToken cancellationToken = default)
    {
        if (embeddings == null || embeddings.Count == 0)
            return;

        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = @"
                INSERT INTO image_embeddings (image_id, embedding_name, weight, is_implicit)
                VALUES (@ImageId, @EmbeddingName, @Weight, @IsImplicit)
                ON CONFLICT (image_id, embedding_name)
                DO UPDATE SET
                    weight = EXCLUDED.weight,
                    is_implicit = EXCLUDED.is_implicit,
                    created_at = NOW();";

            foreach (var (name, weight, isImplicit) in embeddings)
            {
                await conn.ExecuteAsync(sql, new
                {
                    ImageId = imageId,
                    EmbeddingName = name,
                    Weight = weight,
                    IsImplicit = isImplicit
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"InsertImageEmbeddingsAsync failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Get embeddings for a specific image
    /// </summary>
    public async Task<List<ImageEmbeddingDto>> GetImageEmbeddingsAsync(
        int imageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = @"
                SELECT
                    id,
                    image_id AS ImageId,
                    embedding_name AS EmbeddingName,
                    weight,
                    is_implicit AS IsImplicit,
                    created_at AS CreatedAt
                FROM image_embeddings
                WHERE image_id = @ImageId
                ORDER BY embedding_name;";

            var results = await conn.QueryAsync<ImageEmbeddingDto>(sql, new { ImageId = imageId })
                .ConfigureAwait(false);

            return results.ToList();
        }
        catch (Exception ex)
        {
            Logger.Log($"GetImageEmbeddingsAsync failed: {ex.Message}");
            return new List<ImageEmbeddingDto>();
        }
    }

    /// <summary>
    /// Get all images using a specific embedding
    /// Useful for filtering (e.g., "remove all images using Pony embeddings")
    /// </summary>
    public async Task<List<int>> GetImagesUsingEmbeddingAsync(
        string embeddingName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = @"
                SELECT DISTINCT image_id
                FROM image_embeddings
                WHERE LOWER(embedding_name) = LOWER(@EmbeddingName)
                ORDER BY image_id;";

            var results = await conn.QueryAsync<int>(sql, new { EmbeddingName = embeddingName })
                .ConfigureAwait(false);

            return results.ToList();
        }
        catch (Exception ex)
        {
            Logger.Log($"GetImagesUsingEmbeddingAsync failed: {ex.Message}");
            return new List<int>();
        }
    }

    /// <summary>
    /// Get all unique embeddings used across all images
    /// </summary>
    public async Task<List<EmbeddingStatistic>> GetEmbeddingStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = @"
                SELECT
                    embedding_name AS EmbeddingName,
                    COUNT(DISTINCT image_id) AS ImageCount,
                    AVG(weight) AS AverageWeight,
                    COUNT(CASE WHEN is_implicit THEN 1 END) AS ImplicitCount,
                    COUNT(CASE WHEN NOT is_implicit THEN 1 END) AS ExplicitCount
                FROM image_embeddings
                GROUP BY embedding_name
                ORDER BY ImageCount DESC;";

            var results = await conn.QueryAsync<EmbeddingStatistic>(sql)
                .ConfigureAwait(false);

            return results.ToList();
        }
        catch (Exception ex)
        {
            Logger.Log($"GetEmbeddingStatisticsAsync failed: {ex.Message}");
            return new List<EmbeddingStatistic>();
        }
    }

    /// <summary>
    /// Delete all embeddings for an image
    /// </summary>
    public async Task DeleteImageEmbeddingsAsync(
        int imageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = "DELETE FROM image_embeddings WHERE image_id = @ImageId;";

            await conn.ExecuteAsync(sql, new { ImageId = imageId })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"DeleteImageEmbeddingsAsync failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Populate embedding_registry from model_resource table (Phase 2)
    /// </summary>
    public async Task SyncEmbeddingsFromModelResourceAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = @"
                INSERT INTO embedding_registry (
                    civitai_id,
                    embedding_name,
                    description,
                    base_model,
                    trained_words,
                    nsfw,
                    author,
                    civitai_metadata
                )
                SELECT
                    civitai_id,
                    civitai_name,
                    civitai_description,
                    civitai_base_model,
                    civitai_trained_words,
                    civitai_nsfw,
                    civitai_author,
                    civitai_metadata
                FROM model_resource
                WHERE resource_type = 'embedding'
                    AND civitai_id IS NOT NULL
                    AND civitai_name IS NOT NULL
                ON CONFLICT (civitai_id)
                DO UPDATE SET
                    description = EXCLUDED.description,
                    base_model = EXCLUDED.base_model,
                    trained_words = EXCLUDED.trained_words,
                    nsfw = EXCLUDED.nsfw,
                    author = EXCLUDED.author,
                    civitai_metadata = EXCLUDED.civitai_metadata,
                    updated_at = NOW();";

            var affected = await conn.ExecuteAsync(sql).ConfigureAwait(false);
            Logger.Log($"SyncEmbeddingsFromModelResourceAsync: Synced {affected} embeddings from model_resource");
        }
        catch (Exception ex)
        {
            Logger.Log($"SyncEmbeddingsFromModelResourceAsync failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Get personal embeddings from CivitAI registry (for matching)
    /// </summary>
    public async Task<List<string>> GetPersonalEmbeddingNamesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = @"
                SELECT embedding_name
                FROM embedding_registry
                ORDER BY embedding_name;";

            var results = await conn.QueryAsync<string>(sql)
                .ConfigureAwait(false);

            return results.ToList();
        }
        catch (Exception ex)
        {
            Logger.Log($"GetPersonalEmbeddingNamesAsync failed: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Get embedding metadata from registry
    /// </summary>
    public async Task<EmbeddingRegistryDto> GetEmbeddingMetadataAsync(
        string embeddingName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

            var sql = @"
                SELECT
                    id,
                    civitai_id AS CivitaiId,
                    embedding_name AS EmbeddingName,
                    description,
                    trigger_phrase AS TriggerPhrase,
                    base_model AS BaseModel,
                    trained_words AS TrainedWords,
                    nsfw,
                    author,
                    civitai_metadata AS CivitaiMetadata,
                    created_at AS CreatedAt,
                    updated_at AS UpdatedAt
                FROM embedding_registry
                WHERE LOWER(embedding_name) = LOWER(@EmbeddingName);";

            var result = await conn.QueryFirstOrDefaultAsync<EmbeddingRegistryDto>(
                sql, new { EmbeddingName = embeddingName })
                .ConfigureAwait(false);

            return result ?? new EmbeddingRegistryDto();
        }
        catch (Exception ex)
        {
            Logger.Log($"GetEmbeddingMetadataAsync failed: {ex.Message}");
            return new EmbeddingRegistryDto();
        }
    }
}

/// <summary>
/// Image embedding data transfer object
/// </summary>
public class ImageEmbeddingDto
{
    public int Id { get; set; }
    public int ImageId { get; set; }
    public string? EmbeddingName { get; set; }
    public decimal Weight { get; set; } = 1.0m;
    public bool IsImplicit { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Embedding registry statistics
/// </summary>
public class EmbeddingStatistic
{
    public string? EmbeddingName { get; set; }
    public int ImageCount { get; set; }
    public decimal AverageWeight { get; set; }
    public int ImplicitCount { get; set; }
    public int ExplicitCount { get; set; }
}

/// <summary>
/// Embedding metadata from CivitAI registry
/// </summary>
public class EmbeddingRegistryDto
{
    public int Id { get; set; }
    public int? CivitaiId { get; set; }
    public string? EmbeddingName { get; set; }
    public string? Description { get; set; }
    public string? TriggerPhrase { get; set; }
    public string? BaseModel { get; set; }
    public string[]? TrainedWords { get; set; }
    public bool Nsfw { get; set; }
    public string? Author { get; set; }
    public string? CivitaiMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
