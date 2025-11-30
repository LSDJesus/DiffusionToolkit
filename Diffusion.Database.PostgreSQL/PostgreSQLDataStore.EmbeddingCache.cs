using Dapper;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Get embedding from cache by content hash
    /// </summary>
    public async Task<EmbeddingCache?> GetEmbeddingByHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        
        const string sql = @"
            SELECT * FROM embedding_cache
            WHERE content_hash = @ContentHash
            LIMIT 1";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        return await conn.QueryFirstOrDefaultAsync<EmbeddingCache>(
            new CommandDefinition(sql, new { ContentHash = contentHash }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert new embedding into cache
    /// Returns the ID of the inserted embedding
    /// </summary>
    public async Task<int> InsertEmbeddingCacheAsync(
        EmbeddingCache embedding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        
        const string sql = @"
            INSERT INTO embedding_cache (
                content_hash,
                content_type,
                content_text,
                bge_embedding,
                clip_l_embedding,
                clip_g_embedding,
                clip_h_embedding,
                reference_count,
                created_at,
                last_used_at
            ) VALUES (
                @ContentHash,
                @ContentType,
                @ContentText,
                @BgeEmbedding,
                @ClipLEmbedding,
                @ClipGEmbedding,
                @ClipHEmbedding,
                @ReferenceCount,
                @CreatedAt,
                @LastUsedAt
            ) RETURNING id";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, embedding, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Increment reference count for embedding
    /// </summary>
    public async Task IncrementEmbeddingReferenceCountAsync(
        int embeddingId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE embedding_cache
            SET reference_count = reference_count + 1,
                last_used_at = NOW()
            WHERE id = @EmbeddingId";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { EmbeddingId = embeddingId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Decrement reference count for embedding
    /// </summary>
    public async Task DecrementEmbeddingReferenceCountAsync(
        int embeddingId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE embedding_cache
            SET reference_count = GREATEST(0, reference_count - 1)
            WHERE id = @EmbeddingId";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { EmbeddingId = embeddingId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete embeddings with zero references
    /// Returns number of embeddings deleted
    /// </summary>
    public async Task<int> DeleteUnusedEmbeddingsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"
            DELETE FROM embedding_cache
            WHERE reference_count = 0";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        return await conn.ExecuteAsync(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Update image embedding reference IDs
    /// </summary>
    public async Task UpdateImageEmbeddingIdAsync(
        int imageId,
        string embeddingType,  // "prompt", "negative_prompt", "image"
        int embeddingCacheId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddingType);
        
        var columnName = embeddingType switch
        {
            "prompt" => "prompt_embedding_id",
            "negative_prompt" => "negative_prompt_embedding_id",
            "image" => "image_embedding_id",
            _ => throw new ArgumentException($"Invalid embedding type: {embeddingType}", nameof(embeddingType))
        };

        var sql = $@"
            UPDATE image
            SET {columnName} = @EmbeddingCacheId
            WHERE id = @ImageId";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { ImageId = imageId, EmbeddingCacheId = embeddingCacheId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Set needs_visual_embedding flag
    /// </summary>
    public async Task SetNeedsVisualEmbeddingAsync(
        int imageId,
        bool needsEmbedding,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            UPDATE image
            SET needs_visual_embedding = @NeedsEmbedding
            WHERE id = @ImageId";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { ImageId = imageId, NeedsEmbedding = needsEmbedding }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Get images missing embeddings (for batch processing)
    /// </summary>
    public async Task<List<ImageEntity>> GetImagesWithoutEmbeddingsAsync(
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        // Use explicit columns to avoid reading vector columns that Dapper can't handle
        const string sql = @"
            SELECT id, root_folder_id, folder_id, path, file_name, prompt, negative_prompt, steps, sampler, cfg_scale, 
                seed, width, height, model_hash, model, batch_size, batch_pos, created_date, modified_date, 
                custom_tags, rating, favorite, for_deletion, nsfw, unavailable, aesthetic_score, hyper_network, 
                hyper_network_strength, clip_skip, ensd, file_size, no_metadata, workflow, workflow_id, has_error, 
                hash, viewed_date, touched_date, prompt_embedding_id, negative_prompt_embedding_id, image_embedding_id,
                metadata_hash, embedding_source_id, is_embedding_representative, needs_visual_embedding, is_upscaled, 
                base_image_id, generated_tags, loras, vae, refiner_model, refiner_switch, upscaler, upscale_factor, 
                hires_steps, hires_upscaler, hires_upscale, denoising_strength, controlnets, ip_adapter, 
                ip_adapter_strength, wildcards_used, generation_time_seconds, scheduler, duration_ms, video_codec, 
                audio_codec, frame_rate, bitrate, is_video, created_at
            FROM image
            WHERE 
                prompt_embedding_id IS NULL 
                OR negative_prompt_embedding_id IS NULL
                OR (needs_visual_embedding = true AND image_embedding_id IS NULL)
            LIMIT @Limit";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        var images = await conn.QueryAsync<ImageEntity>(
            new CommandDefinition(sql, new { Limit = limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return images.ToList();
    }

    /// <summary>
    /// Get image ID by path (for linking base → upscale)
    /// </summary>
    public async Task<int?> GetImageIdByPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        
        const string sql = @"
            SELECT id FROM image
            WHERE path = @Path
            LIMIT 1";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(sql, new { Path = path }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Get embedding cache statistics
    /// </summary>
    public async Task<EmbeddingCacheStatistics> GetEmbeddingCacheStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        const string statsSql = @"
            SELECT 
                COUNT(*) as TotalEmbeddings,
                COUNT(*) FILTER (WHERE content_type = 'prompt') as PromptEmbeddings,
                COUNT(*) FILTER (WHERE content_type = 'negative_prompt') as NegativePromptEmbeddings,
                COUNT(*) FILTER (WHERE content_type = 'image') as ImageEmbeddings,
                SUM(reference_count) as TotalReferences,
                SUM(
                    COALESCE(pg_column_size(bge_embedding), 0) +
                    COALESCE(pg_column_size(clip_l_embedding), 0) +
                    COALESCE(pg_column_size(clip_g_embedding), 0) +
                    COALESCE(pg_column_size(clip_h_embedding), 0)
                ) as TotalStorageBytes
            FROM embedding_cache";

        const string topSql = @"
            SELECT 
                id,
                content_type as ContentType,
                LEFT(content_text, 50) as ContentTextPreview,
                reference_count as ReferenceCount,
                (
                    COALESCE(pg_column_size(bge_embedding), 0) +
                    COALESCE(pg_column_size(clip_l_embedding), 0) +
                    COALESCE(pg_column_size(clip_g_embedding), 0) +
                    COALESCE(pg_column_size(clip_h_embedding), 0)
                ) as StorageBytes
            FROM embedding_cache
            ORDER BY reference_count DESC
            LIMIT 10";

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        var stats = await conn.QueryFirstOrDefaultAsync<EmbeddingCacheStatistics>(
            new CommandDefinition(statsSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (stats == null)
        {
            stats = new EmbeddingCacheStatistics();
        }

        var topEmbeddings = await conn.QueryAsync<MostReusedEmbedding>(
            new CommandDefinition(topSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        stats.TopReusedEmbeddings = topEmbeddings.ToList();

        // Calculate storage saved
        // Assume 12 KB per embedding (1024×4 + 768×4 + 1280×4 bytes)
        const long bytesPerEmbedding = 12_288;
        stats.StorageSavedBytes = (stats.TotalReferences - stats.TotalEmbeddings) * bytesPerEmbedding;

        return stats;
    }
}

/// <summary>
/// Statistics about embedding cache
/// </summary>
public class EmbeddingCacheStatistics
{
    public int TotalEmbeddings { get; set; }
    public int PromptEmbeddings { get; set; }
    public int NegativePromptEmbeddings { get; set; }
    public int ImageEmbeddings { get; set; }
    public long TotalReferences { get; set; }
    public long TotalStorageBytes { get; set; }
    
    /// <summary>
    /// Average reuse factor (references per embedding)
    /// </summary>
    public double AverageReuseRate => TotalEmbeddings > 0 
        ? (double)TotalReferences / TotalEmbeddings 
        : 0;

    /// <summary>
    /// Storage saved by deduplication (vs storing all embeddings directly)
    /// </summary>
    public long StorageSavedBytes { get; set; }
    
    /// <summary>
    /// Top 10 most-reused embeddings
    /// </summary>
    public List<MostReusedEmbedding> TopReusedEmbeddings { get; set; } = new();
}

/// <summary>
/// Most-reused embedding info
/// </summary>
public class MostReusedEmbedding
{
    public int Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string? ContentTextPreview { get; set; }  // First 50 chars
    public int ReferenceCount { get; set; }
    public long StorageBytes { get; set; }
}
