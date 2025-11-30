using Npgsql;
using Dapper;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Vector similarity search using pgvector
/// Supports prompt similarity, image similarity, and cross-modal search
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// All columns for ImageEntity EXCLUDING vector columns - used for vector search results.
    /// Note: This duplicates ImageEntityColumns from Search.cs but is needed for this partial class.
    /// </summary>
    private const string VectorSearchImageColumns = @"
        id, root_folder_id, folder_id, path, file_name, prompt, negative_prompt, steps, sampler, cfg_scale, 
        seed, width, height, model_hash, model, batch_size, batch_pos, created_date, modified_date, 
        custom_tags, rating, favorite, for_deletion, nsfw, unavailable, aesthetic_score, hyper_network, 
        hyper_network_strength, clip_skip, ensd, file_size, no_metadata, workflow, workflow_id, has_error, 
        hash, viewed_date, touched_date, prompt_embedding_id, negative_prompt_embedding_id, image_embedding_id,
        metadata_hash, embedding_source_id, is_embedding_representative, needs_visual_embedding, is_upscaled, 
        base_image_id, generated_tags, loras, vae, refiner_model, refiner_switch, upscaler, upscale_factor, 
        hires_steps, hires_upscaler, hires_upscale, denoising_strength, controlnets, ip_adapter, 
        ip_adapter_strength, wildcards_used, generation_time_seconds, scheduler, duration_ms, video_codec, 
        audio_codec, frame_rate, bitrate, is_video, created_at";

    /// <summary>
    /// Find images with similar prompts using semantic similarity
    /// </summary>
    public async Task<List<ImageEntity>> SearchByPromptSimilarityAsync(
        float[] promptEmbedding, 
        float threshold = 0.85f, 
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(promptEmbedding);
        
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var vector = $"[{string.Join(",", promptEmbedding)}]";
        var query = $@"
            SELECT {VectorSearchImageColumns} FROM image
            WHERE prompt_embedding IS NOT NULL
            AND 1 - (prompt_embedding <=> @embedding::vector) >= @threshold
            ORDER BY prompt_embedding <=> @embedding::vector ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query, 
            new { embedding = vector, threshold, limit }).ConfigureAwait(false);

        return images.ToList();
    }

    /// <summary>
    /// Find images with similar negative prompts
    /// </summary>
    public async Task<List<ImageEntity>> SearchByNegativePromptSimilarityAsync(
        float[] negativePromptEmbedding,
        float threshold = 0.85f,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(negativePromptEmbedding);
        
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var vector = $"[{string.Join(",", negativePromptEmbedding)}]";
        var query = $@"
            SELECT {VectorSearchImageColumns} FROM image
            WHERE negative_prompt_embedding IS NOT NULL
            AND 1 - (negative_prompt_embedding <=> @embedding::vector) >= @threshold
            ORDER BY negative_prompt_embedding <=> @embedding::vector ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query,
            new { embedding = vector, threshold, limit }).ConfigureAwait(false);

        return images.ToList();
    }

    /// <summary>
    /// Find images with similar visual content using CLIP embeddings
    /// </summary>
    public async Task<List<ImageEntity>> SearchSimilarImagesAsync(
        int imageId,
        float threshold = 0.75f,
        int limit = 50)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        // Prefix all columns with i2. for the joined table
        var i2Columns = VectorSearchImageColumns.Replace("\n", "").Split(',')
            .Select(c => $"i2.{c.Trim()}")
            .Aggregate((a, b) => $"{a}, {b}");

        var query = $@"
            SELECT {i2Columns} FROM image i1
            JOIN image i2 ON i1.id != i2.id
            WHERE i1.id = @imageId
            AND i1.image_embedding IS NOT NULL
            AND i2.image_embedding IS NOT NULL
            AND 1 - (i1.image_embedding <=> i2.image_embedding) >= @threshold
            ORDER BY i1.image_embedding <=> i2.image_embedding ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query,
            new { imageId, threshold, limit }).ConfigureAwait(false);

        return images.ToList();
    }

    /// <summary>
    /// Cross-modal search: find images similar to a text query using visual embeddings
    /// </summary>
    public async Task<List<ImageEntity>> CrossModalSearchAsync(
        float[] textEmbedding,
        float threshold = 0.70f,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(textEmbedding);
        
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var vector = $"[{string.Join(",", textEmbedding)}]";
        var query = $@"
            SELECT {VectorSearchImageColumns} FROM image
            WHERE image_embedding IS NOT NULL
            AND 1 - (image_embedding <=> @embedding::vector) >= @threshold
            ORDER BY image_embedding <=> @embedding::vector ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query,
            new { embedding = vector, threshold, limit }).ConfigureAwait(false);

        return images.ToList();
    }

    /// <summary>
    /// Batch similarity search: find similar images to a set of images
    /// Uses single query with ANY for better performance
    /// </summary>
    public async Task<List<(int ImageId, int SimilarImageId, float Similarity)>> BatchSimilaritySearchAsync(
        List<int> imageIds,
        float threshold = 0.75f,
        int topKPerImage = 10)
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        
        if (imageIds.Count == 0) return new List<(int, int, float)>();

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        // Use a single query with LATERAL join for better performance
        var query = @"
            SELECT x.image_id AS ImageId, x.similar_id AS SimilarImageId, x.similarity AS Similarity
            FROM unnest(@imageIds) AS source(id)
            CROSS JOIN LATERAL (
                SELECT 
                    source.id AS image_id,
                    i2.id AS similar_id,
                    (1 - (i1.image_embedding <=> i2.image_embedding)) AS similarity
                FROM image i1
                JOIN image i2 ON i1.id != i2.id
                WHERE i1.id = source.id
                AND i1.image_embedding IS NOT NULL
                AND i2.image_embedding IS NOT NULL
                AND 1 - (i1.image_embedding <=> i2.image_embedding) >= @threshold
                ORDER BY similarity DESC
                LIMIT @topK
            ) x;";

        var rows = await conn.QueryAsync<(int ImageId, int SimilarImageId, float Similarity)>(query,
            new { imageIds = imageIds.ToArray(), threshold, topK = topKPerImage }).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <summary>
    /// Suggest tags based on similar image captions/custom tags
    /// </summary>
    public async Task<List<(int ImageId, string SuggestedTag, float Similarity)>> AutoTagBySimilarityAsync(
        int imageId,
        float threshold = 0.75f,
        int topK = 5)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var query = @"
            SELECT i2.id AS ImageId,
                   i2.custom_tags AS SuggestedTag,
                   (1 - (i1.prompt_embedding <=> i2.prompt_embedding)) AS Similarity
            FROM image i1
            JOIN image i2 ON i1.id != i2.id
            WHERE i1.id = @imageId
            AND i1.prompt_embedding IS NOT NULL
            AND i2.prompt_embedding IS NOT NULL
            AND i2.custom_tags IS NOT NULL
            AND 1 - (i1.prompt_embedding <=> i2.prompt_embedding) >= @threshold
            ORDER BY Similarity DESC
            LIMIT @topK;";

        var suggestions = await conn.QueryAsync<(int, string, float)>(query,
            new { imageId, threshold, topK }).ConfigureAwait(false);

        return suggestions.ToList();
    }

    /// <summary>
    /// Get embedding coverage statistics
    /// </summary>
    public async Task<EmbeddingCoverageStats> GetEmbeddingCoverageAsync()
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var query = @"
            SELECT 
                COUNT(*) AS TotalImages,
                COUNT(CASE WHEN prompt_embedding IS NOT NULL THEN 1 END) AS PromptEmbeddings,
                COUNT(CASE WHEN negative_prompt_embedding IS NOT NULL THEN 1 END) AS NegativePromptEmbeddings,
                COUNT(CASE WHEN image_embedding IS NOT NULL THEN 1 END) AS ImageEmbeddings
            FROM image;";

        var stats = await conn.QueryFirstAsync<EmbeddingCoverageStats>(query).ConfigureAwait(false);
        return stats;
    }

    /// <summary>
    /// Update image embeddings (fully async version)
    /// </summary>
    public async Task UpdateImageEmbeddingsAsync(
        int imageId,
        float[]? promptEmbedding,
        float[]? negativePromptEmbedding,
        float[]? imageEmbedding)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var promptVector = promptEmbedding != null ? $"[{string.Join(",", promptEmbedding)}]" : null;
        var negPromptVector = negativePromptEmbedding != null ? $"[{string.Join(",", negativePromptEmbedding)}]" : null;
        var imageVector = imageEmbedding != null ? $"[{string.Join(",", imageEmbedding)}]" : null;

        var query = @"
            UPDATE image
            SET prompt_embedding = @promptEmbedding::vector,
                negative_prompt_embedding = @negPromptEmbedding::vector,
                image_embedding = @imageEmbedding::vector,
                touched_date = NOW()
            WHERE id = @imageId;";

        await conn.ExecuteAsync(query, new
        {
            imageId,
            promptEmbedding = promptVector,
            negPromptEmbedding = negPromptVector,
            imageEmbedding = imageVector
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Batch update embeddings for performance using COPY for maximum throughput
    /// </summary>
    public async Task UpdateImageEmbeddingsBatchAsync(
        List<(int ImageId, float[]? Prompt, float[]? NegativePrompt, float[]? Image)> embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        
        if (embeddings.Count == 0) return;

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        // Use a transaction for consistency
        await using var transaction = await conn.BeginTransactionAsync().ConfigureAwait(false);
        
        try
        {
            // Use batched updates with a single parameterized query per batch
            const int batchSize = DatabaseConfiguration.BatchSize;
            
            for (int i = 0; i < embeddings.Count; i += batchSize)
            {
                var batch = embeddings.Skip(i).Take(batchSize);
                
                foreach (var (imageId, promptEmb, negPromptEmb, imageEmb) in batch)
                {
                    var promptVector = promptEmb != null ? $"[{string.Join(",", promptEmb)}]" : null;
                    var negPromptVector = negPromptEmb != null ? $"[{string.Join(",", negPromptEmb)}]" : null;
                    var imageVector = imageEmb != null ? $"[{string.Join(",", imageEmb)}]" : null;

                    await conn.ExecuteAsync(@"
                        UPDATE image
                        SET prompt_embedding = @promptEmbedding::vector,
                            negative_prompt_embedding = @negPromptEmbedding::vector,
                            image_embedding = @imageEmbedding::vector,
                            touched_date = NOW()
                        WHERE id = @imageId;",
                        new
                        {
                            imageId,
                            promptEmbedding = promptVector,
                            negPromptEmbedding = negPromptVector,
                            imageEmbedding = imageVector
                        },
                        transaction).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public class EmbeddingCoverageStats
{
    public long TotalImages { get; set; }
    public long PromptEmbeddings { get; set; }
    public long NegativePromptEmbeddings { get; set; }
    public long ImageEmbeddings { get; set; }

    public float PromptCoverage => TotalImages > 0 ? (float)PromptEmbeddings / TotalImages : 0f;
    public float NegativePromptCoverage => TotalImages > 0 ? (float)NegativePromptEmbeddings / TotalImages : 0f;
    public float ImageCoverage => TotalImages > 0 ? (float)ImageEmbeddings / TotalImages : 0f;
}
