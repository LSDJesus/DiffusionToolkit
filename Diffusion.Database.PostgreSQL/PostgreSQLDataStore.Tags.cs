using Dapper;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

public partial class PostgreSQLDataStore
{
    // ==================== Tag Storage Operations ====================

    /// <summary>
    /// Store tags for an image with confidence scores
    /// </summary>
    public async Task StoreImageTagsAsync(int imageId, IEnumerable<(string tag, float confidence)> tags, string source = "joytag")
    {
        const string sql = @"
            INSERT INTO image_tags (image_id, tag, confidence, source)
            VALUES (@imageId, @tag, @confidence, @source)
            ON CONFLICT (image_id, tag, source) 
            DO UPDATE SET confidence = EXCLUDED.confidence, created_at = NOW();
        ";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        foreach (var (tag, confidence) in tags)
        {
            await connection.ExecuteAsync(sql, new { imageId, tag, confidence, source });
        }
    }

    /// <summary>
    /// Store tags in batch (faster for multiple images)
    /// </summary>
    public async Task StoreImageTagsBatchAsync(Dictionary<int, List<(string tag, float confidence)>> imageTags, string source = "joytag")
    {
        const string sql = @"
            INSERT INTO image_tags (image_id, tag, confidence, source)
            VALUES (@imageId, @tag, @confidence, @source)
            ON CONFLICT (image_id, tag, source) 
            DO UPDATE SET confidence = EXCLUDED.confidence, created_at = NOW();
        ";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var batchData = new List<object>();
        foreach (var (imageId, tags) in imageTags)
        {
            foreach (var (tag, confidence) in tags)
            {
                batchData.Add(new { imageId, tag, confidence, source });
            }
        }

        if (batchData.Any())
        {
            await connection.ExecuteAsync(sql, batchData);
        }
    }

    // ==================== Tag Retrieval Operations ====================

    /// <summary>
    /// Get all tags for an image
    /// </summary>
    public async Task<List<ImageTag>> GetImageTagsAsync(int imageId, string? source = null)
    {
        var sql = @"
            SELECT id, image_id, tag, confidence, source, created_at
            FROM image_tags
            WHERE image_id = @imageId
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        sql += " ORDER BY confidence DESC;";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var tags = await connection.QueryAsync<ImageTag>(sql, new { imageId, source });
        return tags.ToList();
    }

    /// <summary>
    /// Get tags for multiple images
    /// </summary>
    public async Task<Dictionary<int, List<ImageTag>>> GetImageTagsBatchAsync(IEnumerable<int> imageIds, string? source = null)
    {
        var sql = @"
            SELECT id, image_id, tag, confidence, source, created_at
            FROM image_tags
            WHERE image_id = ANY(@imageIds)
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        sql += " ORDER BY confidence DESC;";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var parameters = new DynamicParameters();
        parameters.Add("@imageIds", imageIds.ToArray());
        parameters.Add("@source", source);

        var tags = await connection.QueryAsync<ImageTag>(sql, parameters);
        
        return tags
            .GroupBy(t => t.ImageId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Get tag string for an image (comma-separated)
    /// </summary>
    public async Task<string> GetImageTagStringAsync(int imageId, string? source = null, bool includeConfidence = false)
    {
        var tags = await GetImageTagsAsync(imageId, source);
        
        if (includeConfidence)
            return string.Join(", ", tags.Select(t => $"{t.Tag}:{t.Confidence:F2}"));
        else
            return string.Join(", ", tags.Select(t => t.Tag));
    }

    // ==================== Tag Search Operations ====================

    /// <summary>
    /// Search images by tag
    /// </summary>
    public async Task<List<int>> SearchImagesByTagAsync(string tag, float minConfidence = 0.0f, string? source = null)
    {
        var sql = @"
            SELECT DISTINCT image_id
            FROM image_tags
            WHERE tag = @tag
              AND confidence >= @minConfidence
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var imageIds = await connection.QueryAsync<int>(sql, new { tag, minConfidence, source });
        return imageIds.ToList();
    }

    /// <summary>
    /// Search images by multiple tags (AND logic - must have all tags)
    /// </summary>
    public async Task<List<int>> SearchImagesByTagsAndAsync(IEnumerable<string> tags, float minConfidence = 0.0f, string? source = null)
    {
        var tagsList = tags.ToList();
        if (!tagsList.Any()) return new List<int>();

        var sql = @"
            SELECT image_id
            FROM image_tags
            WHERE tag = ANY(@tags)
              AND confidence >= @minConfidence
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        sql += @"
            GROUP BY image_id
            HAVING COUNT(DISTINCT tag) = @tagCount;
        ";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var parameters = new DynamicParameters();
        parameters.Add("@tags", tagsList.ToArray());
        parameters.Add("@minConfidence", minConfidence);
        parameters.Add("@source", source);
        parameters.Add("@tagCount", tagsList.Count);

        var imageIds = await connection.QueryAsync<int>(sql, parameters);
        
        return imageIds.ToList();
    }

    /// <summary>
    /// Search images by multiple tags (OR logic - must have at least one tag)
    /// </summary>
    public async Task<List<int>> SearchImagesByTagsOrAsync(IEnumerable<string> tags, float minConfidence = 0.0f, string? source = null)
    {
        var tagsList = tags.ToList();
        if (!tagsList.Any()) return new List<int>();

        var sql = @"
            SELECT DISTINCT image_id
            FROM image_tags
            WHERE tag = ANY(@tags)
              AND confidence >= @minConfidence
        ";

        if (source != null)
        {
            sql += " AND source = @source";
        }

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var parameters = new DynamicParameters();
        parameters.Add("@tags", tagsList.ToArray());
        parameters.Add("@minConfidence", minConfidence);
        parameters.Add("@source", source);

        var imageIds = await connection.QueryAsync<int>(sql, parameters);
        return imageIds.ToList();
    }

    /// <summary>
    /// Get all unique tags with usage count
    /// </summary>
    public async Task<Dictionary<string, int>> GetAllTagsWithCountAsync(string? source = null, int minCount = 1)
    {
        var sql = @"
            SELECT tag, COUNT(*) as count
            FROM image_tags
        ";

        if (source != null)
        {
            sql += " WHERE source = @source";
        }

        sql += @"
            GROUP BY tag
            HAVING COUNT(*) >= @minCount
            ORDER BY count DESC, tag;
        ";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var tags = await connection.QueryAsync<(string tag, int count)>(sql, new { source, minCount });
        return tags.ToDictionary(t => t.tag, t => t.count);
    }

    // ==================== Tag Deletion Operations ====================

    /// <summary>
    /// Delete all tags for an image
    /// </summary>
    public async Task DeleteImageTagsAsync(int imageId, string? source = null)
    {
        var sql = "DELETE FROM image_tags WHERE image_id = @imageId";
        
        if (source != null)
        {
            sql += " AND source = @source";
        }

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        await connection.ExecuteAsync(sql, new { imageId, source });
    }

    /// <summary>
    /// Delete specific tag from an image
    /// </summary>
    public async Task DeleteImageTagAsync(int imageId, string tag, string? source = null)
    {
        var sql = "DELETE FROM image_tags WHERE image_id = @imageId AND tag = @tag";
        
        if (source != null)
        {
            sql += " AND source = @source";
        }

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        await connection.ExecuteAsync(sql, new { imageId, tag, source });
    }

    // ==================== Tag Statistics ====================

    /// <summary>
    /// Get tag statistics for an image folder
    /// </summary>
    public async Task<Dictionary<string, int>> GetFolderTagStatisticsAsync(int folderId, bool includeSubfolders = false, string? source = null)
    {
        var sql = includeSubfolders ? @"
            WITH RECURSIVE folder_tree AS (
                SELECT id FROM folder WHERE id = @folderId
                UNION ALL
                SELECT f.id FROM folder f
                INNER JOIN folder_tree ft ON f.parent_folder_id = ft.id
            )
            SELECT t.tag, COUNT(*) as count
            FROM image_tags t
            INNER JOIN image i ON t.image_id = i.id
            WHERE i.folder_id IN (SELECT id FROM folder_tree)
        " : @"
            SELECT t.tag, COUNT(*) as count
            FROM image_tags t
            INNER JOIN image i ON t.image_id = i.id
            WHERE i.folder_id = @folderId
        ";

        if (source != null)
        {
            sql += " AND t.source = @source";
        }

        sql += @"
            GROUP BY t.tag
            ORDER BY count DESC, t.tag;
        ";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync();

        var tags = await connection.QueryAsync<(string tag, int count)>(sql, new { folderId, source });
        return tags.ToDictionary(t => t.tag, t => t.count);
    }
}
