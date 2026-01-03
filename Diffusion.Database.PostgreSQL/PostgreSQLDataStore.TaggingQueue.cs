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
    /// Get images that need tagging (batch for processing)
    /// </summary>
    public async Task<List<int>> GetImagesNeedingTagging(int batchSize = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id FROM {Table("image")} 
            WHERE needs_tagging = true AND for_deletion = false
            ORDER BY id
            LIMIT @batchSize";
        
        var result = await connection.QueryAsync<int>(sql, new { batchSize });
        return result.ToList();
    }

    /// <summary>
    /// Get images that need captioning (batch for processing)
    /// </summary>
    public async Task<List<int>> GetImagesNeedingCaptioning(int batchSize = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id FROM {Table("image")} 
            WHERE needs_captioning = true AND for_deletion = false
            ORDER BY id
            LIMIT @batchSize";
        
        var result = await connection.QueryAsync<int>(sql, new { batchSize });
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
    /// Clear all tagging/captioning queue
    /// </summary>
    public async Task ClearTaggingQueue()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"UPDATE {Table("image")} SET needs_tagging = false WHERE needs_tagging = true");
    }

    /// <summary>
    /// Clear all captioning queue
    /// </summary>
    public async Task ClearCaptioningQueue()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"UPDATE {Table("image")} SET needs_captioning = false WHERE needs_captioning = true");
    }
}
