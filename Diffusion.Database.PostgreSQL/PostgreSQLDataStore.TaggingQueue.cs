using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Diffusion.Database.PostgreSQL;

public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Set the needs_tagging flag for a list of images (raw, no filtering)
    /// </summary>
    public async Task SetNeedsTagging(List<int> imageIds, bool needsTagging)
    {
        if (imageIds.Count == 0) return;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"UPDATE {Table("image")} SET needs_tagging = @needsTagging WHERE id = ANY(@ids)";
        await connection.ExecuteAsync(sql, new { needsTagging, ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Smart queue for tagging: respects needs_tagging flag state and skipAlreadyProcessed setting.
    /// - needs_tagging = true → Skip (already queued)
    /// - needs_tagging = false → Skip if skipAlreadyProcessed is true (already processed)
    /// - needs_tagging = NULL → Queue (never queued/processed)
    /// </summary>
    /// <returns>Number of images actually queued</returns>
    public async Task<int> SmartQueueForTagging(List<int> imageIds, bool skipAlreadyProcessed)
    {
        if (imageIds.Count == 0) return 0;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string sql;
        if (skipAlreadyProcessed)
        {
            // Only queue images that are NULL (never processed)
            sql = $@"
                UPDATE {Table("image")} 
                SET needs_tagging = true 
                WHERE id = ANY(@ids) 
                  AND needs_tagging IS NULL 
                  AND for_deletion = false";
        }
        else
        {
            // Queue NULL + false (reprocess already-completed)
            // Skip true (already in queue)
            sql = $@"
                UPDATE {Table("image")} 
                SET needs_tagging = true 
                WHERE id = ANY(@ids) 
                  AND (needs_tagging IS NULL OR needs_tagging = false)
                  AND for_deletion = false";
        }
        
        return await connection.ExecuteAsync(sql, new { ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Set the needs_captioning flag for a list of images (raw, no filtering)
    /// </summary>
    public async Task SetNeedsCaptioning(List<int> imageIds, bool needsCaptioning)
    {
        if (imageIds.Count == 0) return;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"UPDATE {Table("image")} SET needs_captioning = @needsCaptioning WHERE id = ANY(@ids)";
        await connection.ExecuteAsync(sql, new { needsCaptioning, ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Smart queue for captioning: respects needs_captioning flag state and skipAlreadyProcessed setting.
    /// </summary>
    /// <returns>Number of images actually queued</returns>
    public async Task<int> SmartQueueForCaptioning(List<int> imageIds, bool skipAlreadyProcessed)
    {
        if (imageIds.Count == 0) return 0;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string sql;
        if (skipAlreadyProcessed)
        {
            sql = $@"
                UPDATE {Table("image")} 
                SET needs_captioning = true 
                WHERE id = ANY(@ids) 
                  AND needs_captioning IS NULL 
                  AND for_deletion = false";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")} 
                SET needs_captioning = true 
                WHERE id = ANY(@ids) 
                  AND (needs_captioning IS NULL OR needs_captioning = false)
                  AND for_deletion = false";
        }
        
        return await connection.ExecuteAsync(sql, new { ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Smart queue for embedding: ALWAYS skips already-processed (needs_embedding = false).
    /// Embedding should never be reprocessed - embeddings don't change for same image.
    /// </summary>
    /// <returns>Number of images actually queued</returns>
    public async Task<int> SmartQueueForEmbedding(List<int> imageIds)
    {
        if (imageIds.Count == 0) return 0;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Only queue images that are NULL (never processed)
        // NEVER requeue false (already processed) - embeddings don't change
        var sql = $@"
            UPDATE {Table("image")} 
            SET needs_embedding = true 
            WHERE id = ANY(@ids) 
              AND needs_embedding IS NULL 
              AND for_deletion = false";
        
        return await connection.ExecuteAsync(sql, new { ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Smart queue for face detection: ALWAYS skips already-processed (needs_face_detection = false).
    /// Face detection should never be reprocessed - faces don't change for same image.
    /// </summary>
    /// <returns>Number of images actually queued</returns>
    public async Task<int> SmartQueueForFaceDetection(List<int> imageIds)
    {
        if (imageIds.Count == 0) return 0;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Only queue images that are NULL (never processed)
        // NEVER requeue false (already processed) - faces don't change
        var sql = $@"
            UPDATE {Table("image")} 
            SET needs_face_detection = true 
            WHERE id = ANY(@ids) 
              AND needs_face_detection IS NULL 
              AND for_deletion = false";
        
        return await connection.ExecuteAsync(sql, new { ids = imageIds.ToArray() });
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
    /// Queue folder images for tagging (respects needs_tagging state + skipAlreadyProcessed)
    /// </summary>
    public async Task<int> QueueFolderForTagging(int folderId, bool includeSubfolders, bool skipAlreadyProcessed = true)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Build the condition based on skipAlreadyProcessed
        // - Always skip needs_tagging = true (already queued)
        // - If skipAlreadyProcessed: also skip needs_tagging = false (already processed)
        var flagCondition = skipAlreadyProcessed 
            ? "i.needs_tagging IS NULL"  // Only queue never-processed
            : "(i.needs_tagging IS NULL OR i.needs_tagging = false)";  // Queue never-processed + requeue completed
        
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
                  AND {flagCondition}";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_tagging = true
                WHERE i.folder_id = @folderId
                  AND i.for_deletion = false
                  AND {flagCondition}";
        }
        
        return await connection.ExecuteAsync(sql, new { folderId });
    }

    /// <summary>
    /// Queue folder images for captioning (respects needs_captioning state + skipAlreadyProcessed)
    /// </summary>
    public async Task<int> QueueFolderForCaptioning(int folderId, bool includeSubfolders, bool skipAlreadyProcessed = true)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var flagCondition = skipAlreadyProcessed 
            ? "i.needs_captioning IS NULL"
            : "(i.needs_captioning IS NULL OR i.needs_captioning = false)";
        
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
                  AND {flagCondition}";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_captioning = true
                WHERE i.folder_id = @folderId
                  AND i.for_deletion = false
                  AND {flagCondition}";
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
    /// Queue folder images for embedding (ALWAYS skips already-processed)
    /// </summary>
    public async Task<int> QueueFolderForEmbedding(int folderId, bool includeSubfolders)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Only queue images that are NULL (never processed)
        // NEVER requeue false (already processed) - embeddings don't change
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
                  AND i.needs_embedding IS NULL";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_embedding = true
                WHERE i.folder_id = @folderId
                  AND i.for_deletion = false
                  AND i.needs_embedding IS NULL";
        }

        return await connection.ExecuteAsync(sql, new { folderId });
    }

    /// <summary>
    /// Queue folder images for face detection (ALWAYS skips already-processed)
    /// </summary>
    public async Task<int> QueueFolderForFaceDetection(int folderId, bool includeSubfolders)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Only queue images that are NULL (never processed)
        // NEVER requeue false (already processed) - faces don't change
        string sql;
        if (includeSubfolders)
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_face_detection = true
                FROM {Table("folder")} f
                WHERE i.folder_id = f.id
                  AND f.path LIKE (SELECT path || '%' FROM {Table("folder")} WHERE id = @folderId)
                  AND i.for_deletion = false
                  AND i.needs_face_detection IS NULL";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_face_detection = true
                WHERE i.folder_id = @folderId
                  AND i.for_deletion = false
                  AND i.needs_face_detection IS NULL";
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
