using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Diffusion.Database.PostgreSQL;

public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Set the needs_tagging flag for a list of images
    /// </summary>
    public async Task SetNeedsTagging(List<int> imageIds, bool needsTagging)
    {
        if (imageIds.Count == 0) return;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"UPDATE {Table("image")} SET needs_tagging = @needsTagging WHERE id = ANY(@ids)";
        await connection.ExecuteAsync(sql, new { needsTagging, ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Set the needs_captioning flag for a list of images
    /// </summary>
    public async Task SetNeedsCaptioning(List<int> imageIds, bool needsCaptioning)
    {
        if (imageIds.Count == 0) return;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"UPDATE {Table("image")} SET needs_captioning = @needsCaptioning WHERE id = ANY(@ids)";
        await connection.ExecuteAsync(sql, new { needsCaptioning, ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Update the generated_tags column in the image table
    /// </summary>
    public async Task UpdateImageTags(int imageId, string tags)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = $"UPDATE {Table("image")} SET generated_tags = @tags WHERE id = @imageId";
        await connection.ExecuteAsync(sql, new { tags, imageId });
    }

    /// <summary>
    /// Update the caption for an image (in the image table if column exists, or just image_captions)
    /// </summary>
    public async Task UpdateImageCaption(int imageId, string caption)
    {
        // For now we just store it in image_captions via StoreCaptionAsync
        // but we could also add a caption column to the image table
        await StoreCaptionAsync(imageId, caption);
    }

    /// <summary>
    /// Get images that need tagging (cursor-based pagination for large datasets)
    /// </summary>
    /// <param name="batchSize">Number of images to fetch</param>
    /// <param name="lastId">Fetch images with id > lastId (use 0 for first batch)</param>
    public async Task<List<int>> GetImagesNeedingTagging(int batchSize = 100, int lastId = 0)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id FROM {Table("image")} 
            WHERE needs_tagging = true AND for_deletion = false AND id > @lastId
            ORDER BY id
            LIMIT @batchSize";
        
        var result = await connection.QueryAsync<int>(sql, new { batchSize, lastId });
        return result.ToList();
    }

    /// <summary>
    /// Get images that need captioning (cursor-based pagination for large datasets)
    /// </summary>
    /// <param name="batchSize">Number of images to fetch</param>
    /// <param name="lastId">Fetch images with id > lastId (use 0 for first batch)</param>
    public async Task<List<int>> GetImagesNeedingCaptioning(int batchSize = 100, int lastId = 0)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id FROM {Table("image")} 
            WHERE needs_captioning = true AND for_deletion = false AND id > @lastId
            ORDER BY id
            LIMIT @batchSize";
        
        var result = await connection.QueryAsync<int>(sql, new { batchSize, lastId });
        return result.ToList();
    }

    /// <summary>
    /// Count images that need tagging
    /// </summary>
    public async Task<int> CountImagesNeedingTagging()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"SELECT COUNT(1) FROM {Table("image")} WHERE needs_tagging = true AND for_deletion = false";
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    /// <summary>
    /// Count images that need captioning
    /// </summary>
    public async Task<int> CountImagesNeedingCaptioning()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"SELECT COUNT(1) FROM {Table("image")} WHERE needs_captioning = true AND for_deletion = false";
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    /// <summary>
    /// Queue folder images for tagging (only those without tags)
    /// </summary>
    public async Task<int> QueueFolderForTagging(int folderId, bool includeSubfolders)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string sql;
        if (includeSubfolders)
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_tagging = true
                FROM {Table("folder")} f
                WHERE i.folder_id = f.id
                  AND f.path LIKE (SELECT path || '%' FROM {Table("folder")} WHERE id = @folderId)
                  AND i.for_deletion = false
                  AND NOT EXISTS (SELECT 1 FROM {Table("image_tag")} it WHERE it.image_id = i.id)";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")}
                SET needs_tagging = true
                WHERE folder_id = @folderId
                  AND for_deletion = false
                  AND NOT EXISTS (SELECT 1 FROM {Table("image_tag")} it WHERE it.image_id = id)";
        }
        
        return await connection.ExecuteAsync(sql, new { folderId });
    }

    /// <summary>
    /// Queue folder images for captioning (only those without captions)
    /// </summary>
    public async Task<int> QueueFolderForCaptioning(int folderId, bool includeSubfolders)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string sql;
        if (includeSubfolders)
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_captioning = true
                FROM {Table("folder")} f
                WHERE i.folder_id = f.id
                  AND f.path LIKE (SELECT path || '%' FROM {Table("folder")} WHERE id = @folderId)
                  AND i.for_deletion = false
                  AND NOT EXISTS (SELECT 1 FROM {Table("image_caption")} ic WHERE ic.image_id = i.id)";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")}
                SET needs_captioning = true
                WHERE folder_id = @folderId
                  AND for_deletion = false
                  AND NOT EXISTS (SELECT 1 FROM {Table("image_caption")} ic WHERE ic.image_id = id)";
        }
        
        return await connection.ExecuteAsync(sql, new { folderId });
    }

    /// <summary>
    /// Clear all tagging queue - sets to null (not queued, not processed) rather than false (processed)
    /// </summary>
    public async Task ClearTaggingQueue()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"UPDATE {Table("image")} SET needs_tagging = null WHERE needs_tagging = true");
    }

    /// <summary>
    /// Clear all captioning queue - sets to null (not queued, not processed) rather than false (processed)
    /// </summary>
    public async Task ClearCaptioningQueue()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"UPDATE {Table("image")} SET needs_captioning = null WHERE needs_captioning = true");
    }

    /// <summary>
    /// Set the needs_embedding flag for a list of images
    /// </summary>
    public async Task SetNeedsEmbedding(List<int> imageIds, bool needsEmbedding)
    {
        if (imageIds.Count == 0) return;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"UPDATE {Table("image")} SET needs_embedding = @needsEmbedding WHERE id = ANY(@ids)";
        await connection.ExecuteAsync(sql, new { needsEmbedding, ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Get images that need embedding (cursor-based pagination for large datasets)
    /// </summary>
    /// <param name="batchSize">Number of images to fetch</param>
    /// <param name="lastId">Fetch images with id > lastId (use 0 for first batch)</param>
    public async Task<List<int>> GetImagesNeedingEmbedding(int batchSize = 100, int lastId = 0)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Get images that need embedding OR don't have embeddings yet
        var sql = $@"
            SELECT id FROM {Table("image")} 
            WHERE (needs_embedding = true OR (prompt_embedding IS NULL AND image_embedding IS NULL))
              AND for_deletion = false AND id > @lastId
            ORDER BY id
            LIMIT @batchSize";
        
        var result = await connection.QueryAsync<int>(sql, new { batchSize, lastId });
        return result.ToList();
    }

    /// <summary>
    /// Count images that need embedding
    /// </summary>
    public async Task<int> CountImagesNeedingEmbedding()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT COUNT(1) FROM {Table("image")} 
            WHERE (needs_embedding = true OR (prompt_embedding IS NULL AND image_embedding IS NULL))
              AND for_deletion = false";
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    /// <summary>
    /// Queue folder images for embedding
    /// </summary>
    public async Task<int> QueueFolderForEmbedding(int folderId, bool includeSubfolders)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string sql;
        if (includeSubfolders)
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_embedding = true
                FROM {Table("folder")} f
                WHERE i.folder_id = f.id
                  AND f.path LIKE (SELECT path || '%' FROM {Table("folder")} WHERE id = @folderId)
                  AND i.for_deletion = false
                  AND i.prompt_embedding IS NULL
                  AND i.image_embedding IS NULL";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")}
                SET needs_embedding = true
                WHERE folder_id = @folderId
                  AND for_deletion = false
                  AND prompt_embedding IS NULL
                  AND image_embedding IS NULL";
        }

        return await connection.ExecuteAsync(sql, new { folderId });
    }

    /// <summary>
    /// Clear all embedding queue - sets to null (not queued, not processed) rather than false (processed)
    /// </summary>
    public async Task ClearEmbeddingQueue()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"UPDATE {Table("image")} SET needs_embedding = null WHERE needs_embedding = true");
    }

    /// <summary>
    /// Store embeddings for an image
    /// </summary>
    public async Task StoreImageEmbeddingsAsync(int imageId, float[]? promptEmbedding, float[]? imageEmbedding)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            UPDATE {Table("image")}
            SET prompt_embedding = @promptEmbedding::vector,
                image_embedding = @imageEmbedding::vector,
                needs_embedding = false
            WHERE id = @imageId";
        
        await connection.ExecuteAsync(sql, new { 
            imageId, 
            promptEmbedding = promptEmbedding != null ? string.Join(",", promptEmbedding.Select(f => f.ToString("G9"))) : null,
            imageEmbedding = imageEmbedding != null ? string.Join(",", imageEmbedding.Select(f => f.ToString("G9"))) : null
        });
    }
}
