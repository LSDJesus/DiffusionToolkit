using Dapper;
using Diffusion.Database.PostgreSQL.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL DataStore methods for intelligent embedding processing
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Get unique prompt combinations from database (for preloading cache)
    /// </summary>
    public async Task<List<(string? Prompt, string? NegativePrompt)>> GetUniquePromptsAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            SELECT DISTINCT 
                prompt,
                negative_prompt
            FROM image
            WHERE prompt IS NOT NULL
            ORDER BY prompt, negative_prompt";
        
        if (limit.HasValue)
        {
            sql += $" LIMIT {limit.Value}";
        }
        
        await using var conn = await OpenConnectionAsync();
        var results = await conn.QueryAsync<(string? Prompt, string? NegativePrompt)>(sql);
        return results.ToList();
    }
    
    /// <summary>
    /// Get total image count
    /// </summary>
    public async Task<int> GetTotalImageCountAsync(CancellationToken cancellationToken = default)
    {
        var sql = "SELECT COUNT(*) FROM image;";
        await using var conn = await OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(sql);
    }
    
    /// <summary>
    /// Compute metadata hashes for all images that don't have one yet
    /// Uses the compute_metadata_hash() database function created in migration V5
    /// </summary>
    public async Task ComputeAllMetadataHashesAsync(CancellationToken cancellationToken = default)
    {
        var sql = @"
            UPDATE image
            SET metadata_hash = compute_metadata_hash(
                prompt,
                negative_prompt,
                model,
                seed,
                steps,
                sampler,
                cfg_scale,
                width,
                height
            )
            WHERE metadata_hash IS NULL;";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql);
    }
    
    /// <summary>
    /// Select representative images (largest file_size) for each metadata_hash group
    /// These are the FINAL versions that will be embedded
    /// </summary>
    public async Task<List<RepresentativeImage>> SelectRepresentativeImagesAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            WITH ranked_images AS (
                SELECT 
                    id,
                    path,
                    prompt,
                    negative_prompt,
                    metadata_hash,
                    file_size,
                    ROW_NUMBER() OVER (
                        PARTITION BY metadata_hash 
                        ORDER BY file_size DESC, id ASC
                    ) as rn
                FROM image
                WHERE metadata_hash IS NOT NULL
            )
            SELECT 
                id AS ImageId,
                path AS FilePath,
                prompt AS Prompt,
                negative_prompt AS NegativePrompt,
                metadata_hash AS MetadataHash,
                file_size AS FileSize
            FROM ranked_images
            WHERE rn = 1
            ORDER BY id;";
        
        await using var conn = await OpenConnectionAsync();
        var results = await conn.QueryAsync<RepresentativeImage>(sql);
        return results.ToList();
    }
    
    /// <summary>
    /// Check if an image needs embedding (doesn't have embeddings yet)
    /// </summary>
    public async Task<bool> ImageNeedsEmbeddingAsync(
        int imageId,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            SELECT COUNT(*) 
            FROM image 
            WHERE id = @imageId 
              AND (prompt_embedding IS NULL OR image_embedding IS NULL);";
        
        await using var conn = await OpenConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>(sql, new { imageId });
        return count > 0;
    }
    
    /// <summary>
    /// Store embeddings for an image and mark if it's a representative
    /// </summary>
    public async Task StoreImageEmbeddingsAsync(
        int imageId,
        float[] bgeEmbedding,
        float[] clipLEmbedding,
        float[] clipGEmbedding,
        float[] imageEmbedding,
        bool isRepresentative,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            UPDATE image
            SET prompt_embedding = @bgeEmbedding,
                clip_l_embedding = @clipLEmbedding,
                clip_g_embedding = @clipGEmbedding,
                image_embedding = @imageEmbedding,
                is_embedding_representative = @isRepresentative,
                embedding_source_id = CASE 
                    WHEN @isRepresentative THEN id 
                    ELSE embedding_source_id 
                END
            WHERE id = @imageId;";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql, new
        {
            imageId,
            bgeEmbedding,
            clipLEmbedding,
            clipGEmbedding,
            imageEmbedding,
            isRepresentative
        });
    }
    
    /// <summary>
    /// Copy embeddings from representative to all other images in the same metadata_hash group
    /// Sets embedding_source_id to point to the representative
    /// </summary>
    public async Task CopyEmbeddingsToAllNonRepresentativesAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            WITH representatives AS (
                SELECT 
                    id,
                    metadata_hash,
                    prompt_embedding,
                    clip_l_embedding,
                    clip_g_embedding,
                    image_embedding
                FROM image
                WHERE is_embedding_representative = TRUE
                  AND metadata_hash IS NOT NULL
            )
            UPDATE image AS target
            SET prompt_embedding = rep.prompt_embedding,
                clip_l_embedding = rep.clip_l_embedding,
                clip_g_embedding = rep.clip_g_embedding,
                image_embedding = rep.image_embedding,
                embedding_source_id = rep.id,
                is_embedding_representative = FALSE
            FROM representatives AS rep
            WHERE target.metadata_hash = rep.metadata_hash
              AND target.id != rep.id
              AND target.is_embedding_representative = FALSE;";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql);
    }
    
    /// <summary>
    /// Mark images that have no representative in their group (orphaned)
    /// This can happen if the representative image was deleted
    /// </summary>
    public async Task MarkOrphanedImagesAsync(CancellationToken cancellationToken = default)
    {
        var sql = @"
            UPDATE image
            SET embedding_source_id = NULL,
                image_embedding = NULL,
                is_embedding_representative = FALSE
            WHERE embedding_source_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 
                  FROM image AS source 
                  WHERE source.id = image.embedding_source_id
              );";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql);
    }
}
