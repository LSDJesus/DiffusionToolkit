using Dapper;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

public partial class PostgreSQLDataStore
{
    // ==================== Caption Storage Operations ====================

    /// <summary>
    /// Store a caption for an image
    /// </summary>
    public async Task StoreCaptionAsync(int imageId, string caption, string source = "joycaption", 
        string? promptUsed = null, int? tokenCount = null, float? generationTimeMs = null)
    {
        ArgumentNullException.ThrowIfNull(caption);
        
        Logger.Log($"StoreCaptionAsync called: imageId={imageId}, captionLen={caption.Length}, source={source}");
        
        const string sql = @"
            INSERT INTO image_captions (image_id, caption, source, prompt_used, token_count, generation_time_ms)
            VALUES (@imageId, @caption, @source, @promptUsed, @tokenCount, @generationTimeMs);
        ";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var rowsAffected = await connection.ExecuteAsync(sql, new 
        { 
            imageId, 
            caption, 
            source, 
            promptUsed, 
            tokenCount, 
            generationTimeMs 
        }).ConfigureAwait(false);
        
        Logger.Log($"StoreCaptionAsync: {rowsAffected} rows inserted");
        
        // Flag BGE caption embedding for regeneration (caption changed)
        // Also flag T5-XXL caption embedding (stub for future Flux support)
        await connection.ExecuteAsync(
            $"UPDATE {Table("image")} SET needs_bge_caption_embedding = true, needs_t5xxl_caption_embedding = true WHERE id = @imageId",
            new { imageId }).ConfigureAwait(false);
        
        // Verify it was saved
        var verify = await GetLatestCaptionAsync(imageId, source).ConfigureAwait(false);
        Logger.Log($"StoreCaptionAsync verification: caption found={verify != null}, len={verify?.Caption?.Length ?? 0}");
    }

    /// <summary>
    /// Store captions in batch (faster for multiple images)
    /// </summary>
    public async Task StoreCaptionsBatchAsync(Dictionary<int, (string caption, string? promptUsed, int? tokenCount, float? generationTimeMs)> imageCaptions, 
        string source = "joycaption")
    {
        ArgumentNullException.ThrowIfNull(imageCaptions);
        
        if (!imageCaptions.Any()) return;
        
        const string sql = @"
            INSERT INTO image_captions (image_id, caption, source, prompt_used, token_count, generation_time_ms)
            VALUES (@imageId, @caption, @source, @promptUsed, @tokenCount, @generationTimeMs);
        ";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var batchData = imageCaptions.Select(kvp => new
        {
            imageId = kvp.Key,
            caption = kvp.Value.caption,
            source,
            promptUsed = kvp.Value.promptUsed,
            tokenCount = kvp.Value.tokenCount,
            generationTimeMs = kvp.Value.generationTimeMs
        }).ToList();

        await connection.ExecuteAsync(sql, batchData).ConfigureAwait(false);
        
        // Flag BGE caption embedding for regeneration for all images in batch
        // Also flag T5-XXL caption embedding (stub for future Flux support)
        if (imageCaptions.Count > 0)
        {
            var imageIds = imageCaptions.Keys.ToArray();
            await connection.ExecuteAsync(
                $"UPDATE {Table("image")} SET needs_bge_caption_embedding = true, needs_t5xxl_caption_embedding = true WHERE id = ANY(@ids)",
                new { ids = imageIds }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Update an existing caption (marks as user-edited)
    /// </summary>
    public async Task UpdateCaptionAsync(int captionId, string newCaption)
    {
        ArgumentNullException.ThrowIfNull(newCaption);
        
        // First get the image_id for this caption so we can flag it for re-embedding
        const string getImageIdSql = "SELECT image_id FROM image_captions WHERE id = @captionId";
        
        const string updateSql = @"
            UPDATE image_captions
            SET caption = @newCaption, 
                is_user_edited = TRUE
            WHERE id = @captionId;
        ";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var imageId = await connection.ExecuteScalarAsync<int?>(getImageIdSql, new { captionId }).ConfigureAwait(false);
        
        await connection.ExecuteAsync(updateSql, new { captionId, newCaption }).ConfigureAwait(false);
        
        // Flag BGE caption embedding for regeneration (caption changed)
        // Also flag T5-XXL caption embedding (stub for future Flux support)
        if (imageId.HasValue)
        {
            await connection.ExecuteAsync(
                $"UPDATE {Table("image")} SET needs_bge_caption_embedding = true, needs_t5xxl_caption_embedding = true WHERE id = @imageId",
                new { imageId = imageId.Value }).ConfigureAwait(false);
        }
    }

    // ==================== Caption Retrieval Operations ====================

    /// <summary>
    /// Get all captions for an image
    /// </summary>
    public async Task<List<ImageCaption>> GetImageCaptionsAsync(int imageId, string? source = null)
    {
        var sql = @"
            SELECT id, image_id, caption, source, prompt_used, is_user_edited, 
                   token_count, generation_time_ms, created_at, updated_at
            FROM image_captions
            WHERE image_id = @imageId
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        sql += " ORDER BY created_at DESC;";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var captions = await connection.QueryAsync<ImageCaption>(sql, new { imageId, source }).ConfigureAwait(false);
        return captions.ToList();
    }

    /// <summary>
    /// Get the most recent caption for an image
    /// </summary>
    public async Task<ImageCaption?> GetLatestCaptionAsync(int imageId, string? source = null)
    {
        var sql = @"
            SELECT id, image_id, caption, source, prompt_used, is_user_edited, 
                   token_count, generation_time_ms, created_at, updated_at
            FROM image_captions
            WHERE image_id = @imageId
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        sql += " ORDER BY created_at DESC LIMIT 1;";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        return await connection.QueryFirstOrDefaultAsync<ImageCaption>(sql, new { imageId, source }).ConfigureAwait(false);
    }

    /// <summary>
    /// Get captions for multiple images
    /// </summary>
    public async Task<Dictionary<int, List<ImageCaption>>> GetImageCaptionsBatchAsync(IEnumerable<int> imageIds, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        
        var sql = @"
            SELECT id, image_id, caption, source, prompt_used, is_user_edited, 
                   token_count, generation_time_ms, created_at, updated_at
            FROM image_captions
            WHERE image_id = ANY(@imageIds)
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        sql += " ORDER BY created_at DESC;";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var captions = await connection.QueryAsync<ImageCaption>(sql, new { imageIds = imageIds.ToArray(), source }).ConfigureAwait(false);
        
        return captions
            .GroupBy(c => c.ImageId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    // ==================== Caption Search Operations ====================

    /// <summary>
    /// Full-text search for images by caption content
    /// </summary>
    public async Task<List<int>> SearchImagesByCaptionAsync(string searchText, string? source = null, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(searchText);
        
        var sql = @"
            SELECT DISTINCT image_id
            FROM image_captions
            WHERE to_tsvector('english', caption) @@ plainto_tsquery('english', @searchText)
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        sql += " LIMIT @limit;";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var imageIds = await connection.QueryAsync<int>(sql, new { searchText, source, limit }).ConfigureAwait(false);
        return imageIds.ToList();
    }

    /// <summary>
    /// Get images without captions (need captioning)
    /// </summary>
    public async Task<List<int>> GetImagesWithoutCaptionsAsync(int? folderId = null, string? source = null, int limit = 1000)
    {
        var sql = @"
            SELECT i.id
            FROM image i
            LEFT JOIN image_captions ic ON i.id = ic.image_id
        ";

        var conditions = new List<string>();
        
        if (source != null)
        {
            conditions.Add("(ic.source = @source OR ic.source IS NULL)");
        }
        
        if (folderId != null)
        {
            conditions.Add("i.folder_id = @folderId");
        }

        conditions.Add("ic.id IS NULL");

        sql += " WHERE " + string.Join(" AND ", conditions);
        sql += " LIMIT @limit;";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var imageIds = await connection.QueryAsync<int>(sql, new { folderId, source, limit }).ConfigureAwait(false);
        return imageIds.ToList();
    }

    // ==================== Caption Deletion Operations ====================

    /// <summary>
    /// Delete all captions for an image
    /// </summary>
    public async Task DeleteImageCaptionsAsync(int imageId, string? source = null)
    {
        var sql = "DELETE FROM image_captions WHERE image_id = @imageId";
        
        if (source != null)
        {
            sql += " AND source = @source";
        }

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        await connection.ExecuteAsync(sql, new { imageId, source }).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a specific caption by ID
    /// </summary>
    public async Task DeleteCaptionAsync(int captionId)
    {
        const string sql = "DELETE FROM image_captions WHERE id = @captionId";

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        await connection.ExecuteAsync(sql, new { captionId }).ConfigureAwait(false);
    }

    // ==================== Caption Statistics ====================

    /// <summary>
    /// Get caption statistics
    /// </summary>
    public async Task<(int totalCaptions, int aiGenerated, int userEdited, int uniqueImages)> GetCaptionStatisticsAsync(string? source = null)
    {
        var sql = @"
            SELECT 
                COUNT(*) as total_captions,
                COUNT(*) FILTER (WHERE is_user_edited = FALSE) as ai_generated,
                COUNT(*) FILTER (WHERE is_user_edited = TRUE) as user_edited,
                COUNT(DISTINCT image_id) as unique_images
            FROM image_captions
        ";

        if (source != null)
        {
            sql += " WHERE source = @source";
        }

        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);

        var result = await connection.QueryFirstAsync<dynamic>(sql, new { source }).ConfigureAwait(false);
        
        return (
            totalCaptions: (int)result.total_captions,
            aiGenerated: (int)result.ai_generated,
            userEdited: (int)result.user_edited,
            uniqueImages: (int)result.unique_images
        );
    }
}
