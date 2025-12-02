using Dapper;
using Diffusion.Database.PostgreSQL.Models;
using System.IO;

namespace Diffusion.Database.PostgreSQL;

public partial class PostgreSQLDataStore
{
    private static Dictionary<string, string>? _tagDeduplicationMap;
    private static readonly object _tagMapLock = new object();

    /// <summary>
    /// Load tag deduplication map (lazy, thread-safe)
    /// </summary>
    private static void EnsureTagMapLoaded()
    {
        if (_tagDeduplicationMap != null) return;

        lock (_tagMapLock)
        {
            if (_tagDeduplicationMap != null) return;

            try
            {
                var mapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "tag_deduplication_map.json");
                if (File.Exists(mapPath))
                {
                    var json = File.ReadAllText(mapPath);
                    _tagDeduplicationMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _tagDeduplicationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _tagDeduplicationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Get canonical form of a tag
    /// </summary>
    private static string GetCanonicalTag(string tag)
    {
        EnsureTagMapLoaded();
        return _tagDeduplicationMap!.TryGetValue(tag, out var canonical) ? canonical : tag;
    }

    // ==================== Tag Storage Operations ====================

    /// <summary>
    /// Store tags for an image with confidence scores.
    /// Automatically deduplicates after insert using pre-computed mapping.
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
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (var (tag, confidence) in tags)
        {
            await connection.ExecuteAsync(sql, new { imageId, tag, confidence, source }).ConfigureAwait(false);
        }

        // Deduplicate tags for this image after insert
        await DeduplicateImageTagsAsync(imageId, connection).ConfigureAwait(false);
    }

    /// <summary>
    /// Store tags in batch (faster for multiple images).
    /// Automatically deduplicates after insert using pre-computed mapping.
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
        await connection.OpenAsync().ConfigureAwait(false);

        var batchData = new List<object>();
        foreach (var (imageId, tags) in imageTags)
        {
            foreach (var (tag, confidence) in tags)
            {
                batchData.Add(new { imageId, tag, confidence, source });
            }
        }

        if (batchData.Count > 0)
        {
            await connection.ExecuteAsync(sql, batchData).ConfigureAwait(false);

            // Deduplicate tags for all images in batch
            foreach (var imageId in imageTags.Keys)
            {
                await DeduplicateImageTagsAsync(imageId, connection).ConfigureAwait(false);
            }
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
        await connection.OpenAsync().ConfigureAwait(false);

        var tags = await connection.QueryAsync<ImageTag>(sql, new { imageId, source }).ConfigureAwait(false);
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
        await connection.OpenAsync().ConfigureAwait(false);

        var parameters = new DynamicParameters();
        parameters.Add("@imageIds", imageIds.ToArray());
        parameters.Add("@source", source);

        var tags = await connection.QueryAsync<ImageTag>(sql, parameters).ConfigureAwait(false);
        
        return tags
            .GroupBy(t => t.ImageId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Get tag string for an image (comma-separated)
    /// </summary>
    public async Task<string> GetImageTagStringAsync(int imageId, string? source = null, bool includeConfidence = false)
    {
        var tags = await GetImageTagsAsync(imageId, source).ConfigureAwait(false);
        
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
        await connection.OpenAsync().ConfigureAwait(false);

        var imageIds = await connection.QueryAsync<int>(sql, new { tag, minConfidence, source }).ConfigureAwait(false);
        return imageIds.ToList();
    }

    /// <summary>
    /// Search images by multiple tags (AND logic - must have all tags)
    /// </summary>
    public async Task<List<int>> SearchImagesByTagsAndAsync(IEnumerable<string> tags, float minConfidence = 0.0f, string? source = null)
    {
        var tagsList = tags.ToList();
        if (tagsList.Count == 0) return new List<int>();

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
        await connection.OpenAsync().ConfigureAwait(false);

        var parameters = new DynamicParameters();
        parameters.Add("@tags", tagsList.ToArray());
        parameters.Add("@minConfidence", minConfidence);
        parameters.Add("@source", source);
        parameters.Add("@tagCount", tagsList.Count);

        var imageIds = await connection.QueryAsync<int>(sql, parameters).ConfigureAwait(false);
        
        return imageIds.ToList();
    }

    /// <summary>
    /// Search images by multiple tags (OR logic - must have at least one tag)
    /// </summary>
    public async Task<List<int>> SearchImagesByTagsOrAsync(IEnumerable<string> tags, float minConfidence = 0.0f, string? source = null)
    {
        var tagsList = tags.ToList();
        if (tagsList.Count == 0) return new List<int>();

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
        await connection.OpenAsync().ConfigureAwait(false);

        var parameters = new DynamicParameters();
        parameters.Add("@tags", tagsList.ToArray());
        parameters.Add("@minConfidence", minConfidence);
        parameters.Add("@source", source);

        var imageIds = await connection.QueryAsync<int>(sql, parameters).ConfigureAwait(false);
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
        await connection.OpenAsync().ConfigureAwait(false);

        var tags = await connection.QueryAsync<(string tag, int count)>(sql, new { source, minCount }).ConfigureAwait(false);
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
        await connection.OpenAsync().ConfigureAwait(false);

        await connection.ExecuteAsync(sql, new { imageId, source }).ConfigureAwait(false);
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
        await connection.OpenAsync().ConfigureAwait(false);

        await connection.ExecuteAsync(sql, new { imageId, tag, source }).ConfigureAwait(false);
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
        await connection.OpenAsync().ConfigureAwait(false);

        var tags = await connection.QueryAsync<(string tag, int count)>(sql, new { folderId, source }).ConfigureAwait(false);
        return tags.ToDictionary(t => t.tag, t => t.count);
    }

    // ==================== Tag Deduplication ====================

    /// <summary>
    /// Deduplicate tags for an image using pre-computed canonical mapping.
    /// Keeps the canonical form, merges confidence scores (averages), and removes duplicates.
    /// Works across ALL sources for complete deduplication.
    /// </summary>
    private async Task DeduplicateImageTagsAsync(int imageId, System.Data.Common.DbConnection connection)
    {
        EnsureTagMapLoaded();

        // Get all tags for this image (all sources)
        const string selectSql = @"
            SELECT id, tag, confidence, source
            FROM image_tags
            WHERE image_id = @imageId
            ORDER BY id;
        ";

        var allTags = (await connection.QueryAsync<(int id, string tag, float confidence, string source)>(selectSql, new { imageId }).ConfigureAwait(false)).ToList();
        
        if (allTags.Count <= 1) return; // No deduplication needed

        // Group by canonical tag (case-insensitive)
        var canonicalGroups = allTags
            .GroupBy(t => GetCanonicalTag(t.tag), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 || g.Key != g.First().tag) // Only process if duplicates exist or tag needs canonicalization
            .ToList();

        if (canonicalGroups.Count == 0) return; // No duplicates found

        foreach (var group in canonicalGroups)
        {
            var canonicalTag = group.Key;
            var tagRecords = group.ToList();

            // Keep one record (prefer existing canonical if it exists, otherwise first record)
            var canonicalMatch = tagRecords.FirstOrDefault(t => t.tag.Equals(canonicalTag, StringComparison.Ordinal));
            var keepRecord = canonicalMatch.id != 0 ? canonicalMatch : tagRecords.First();
            
            // Average confidence from all duplicate records
            var avgConfidence = tagRecords.Average(t => t.confidence);

            // Update the kept record with canonical tag and averaged confidence
            const string updateSql = @"
                UPDATE image_tags
                SET tag = @canonicalTag, confidence = @avgConfidence, created_at = NOW()
                WHERE id = @id;
            ";
            await connection.ExecuteAsync(updateSql, new { id = keepRecord.id, canonicalTag, avgConfidence }).ConfigureAwait(false);

            // Delete duplicate records (all except the kept one)
            var deleteIds = tagRecords.Where(t => t.id != keepRecord.id).Select(t => t.id).ToList();
            if (deleteIds.Any())
            {
                const string deleteSql = "DELETE FROM image_tags WHERE id = ANY(@ids);";
                await connection.ExecuteAsync(deleteSql, new { ids = deleteIds.ToArray() }).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Public method to manually trigger deduplication for an image (useful for batch cleanup)
    /// </summary>
    public async Task DeduplicateImageTagsAsync(int imageId)
    {
        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await DeduplicateImageTagsAsync(imageId, connection).ConfigureAwait(false);
    }

    /// <summary>
    /// Deduplicate tags for all images in a folder (maintenance operation)
    /// </summary>
    public async Task DeduplicateFolderTagsAsync(int folderId, bool includeSubfolders = false)
    {
        var sql = includeSubfolders ? @"
            WITH RECURSIVE folder_tree AS (
                SELECT id FROM folder WHERE id = @folderId
                UNION ALL
                SELECT f.id FROM folder f
                INNER JOIN folder_tree ft ON f.parent_folder_id = ft.id
            )
            SELECT DISTINCT i.id
            FROM image i
            WHERE i.folder_id IN (SELECT id FROM folder_tree)
              AND EXISTS (SELECT 1 FROM image_tags WHERE image_id = i.id);
        " : @"
            SELECT DISTINCT i.id
            FROM image i
            WHERE i.folder_id = @folderId
              AND EXISTS (SELECT 1 FROM image_tags WHERE image_id = i.id);
        ";

        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        var imageIds = await connection.QueryAsync<int>(sql, new { folderId }).ConfigureAwait(false);
        
        foreach (var imageId in imageIds)
        {
            await DeduplicateImageTagsAsync(imageId, connection).ConfigureAwait(false);
        }
    }
}
