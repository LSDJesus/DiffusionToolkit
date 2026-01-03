using Npgsql;
using Dapper;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL thumbnail cache operations - replaces SQLite dt_thumbnails.db
/// Uses BYTEA with TOAST compression for efficient blob storage
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Get thumbnail data for a file path at a specific size
    /// </summary>
    /// <param name="path">Full file path to the image</param>
    /// <param name="size">Thumbnail size (e.g., 128, 256)</param>
    /// <returns>JPEG bytes or null if not cached</returns>
    public byte[]? GetThumbnail(string path, int size)
    {
        using var conn = OpenConnection();
        
        var sql = $@"
            SELECT data 
            FROM {Table("thumbnail")} 
            WHERE path = @Path AND size = @Size";
        
        return conn.QueryFirstOrDefault<byte[]>(sql, new { Path = path, Size = size });
    }

    /// <summary>
    /// Get thumbnail data asynchronously
    /// </summary>
    public async Task<byte[]?> GetThumbnailAsync(string path, int size)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        var sql = $@"
            SELECT data 
            FROM {Table("thumbnail")} 
            WHERE path = @Path AND size = @Size";
        
        return await conn.QueryFirstOrDefaultAsync<byte[]>(sql, new { Path = path, Size = size }).ConfigureAwait(false);
    }

    /// <summary>
    /// Check if thumbnail exists without loading data
    /// </summary>
    public bool HasThumbnail(string path, int size)
    {
        using var conn = OpenConnection();
        
        var sql = $@"
            SELECT EXISTS(
                SELECT 1 FROM {Table("thumbnail")} 
                WHERE path = @Path AND size = @Size
            )";
        
        return conn.ExecuteScalar<bool>(sql, new { Path = path, Size = size });
    }

    /// <summary>
    /// Store or update thumbnail data
    /// </summary>
    /// <param name="path">Full file path to the image</param>
    /// <param name="size">Thumbnail size</param>
    /// <param name="data">JPEG bytes</param>
    public void SetThumbnail(string path, int size, byte[] data)
    {
        using var conn = OpenConnection();
        
        var sql = $@"
            INSERT INTO {Table("thumbnail")} (path, size, data, created_at)
            VALUES (@Path, @Size, @Data, NOW())
            ON CONFLICT (path, size) 
            DO UPDATE SET data = @Data, created_at = NOW()";
        
        conn.Execute(sql, new { Path = path, Size = size, Data = data });
    }

    /// <summary>
    /// Store thumbnail asynchronously
    /// </summary>
    public async Task SetThumbnailAsync(string path, int size, byte[] data)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        var sql = $@"
            INSERT INTO {Table("thumbnail")} (path, size, data, created_at)
            VALUES (@Path, @Size, @Data, NOW())
            ON CONFLICT (path, size) 
            DO UPDATE SET data = @Data, created_at = NOW()";
        
        await conn.ExecuteAsync(sql, new { Path = path, Size = size, Data = data }).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete thumbnail for a specific path (all sizes)
    /// </summary>
    public void DeleteThumbnail(string path)
    {
        using var conn = OpenConnection();
        
        var sql = $"DELETE FROM {Table("thumbnail")} WHERE path = @Path";
        conn.Execute(sql, new { Path = path });
    }

    /// <summary>
    /// Delete thumbnails for paths that start with a prefix (folder delete)
    /// </summary>
    public int DeleteThumbnailsByPrefix(string pathPrefix)
    {
        using var conn = OpenConnection();
        
        var sql = $"DELETE FROM {Table("thumbnail")} WHERE path LIKE @Prefix";
        return conn.Execute(sql, new { Prefix = pathPrefix + "%" });
    }

    /// <summary>
    /// Get total size of thumbnail cache in bytes
    /// </summary>
    public long GetThumbnailCacheSize()
    {
        using var conn = OpenConnection();
        
        var sql = $"SELECT COALESCE(SUM(LENGTH(data)), 0) FROM {Table("thumbnail")}";
        return conn.ExecuteScalar<long>(sql);
    }

    /// <summary>
    /// Get count of cached thumbnails
    /// </summary>
    public int GetThumbnailCount()
    {
        using var conn = OpenConnection();
        
        var sql = $"SELECT COUNT(*) FROM {Table("thumbnail")}";
        return conn.ExecuteScalar<int>(sql);
    }

    /// <summary>
    /// Prune old thumbnails - removes thumbnails older than specified days
    /// </summary>
    public int PruneThumbnails(int olderThanDays)
    {
        using var conn = OpenConnection();
        
        var sql = $@"
            DELETE FROM {Table("thumbnail")} 
            WHERE created_at < NOW() - INTERVAL '{olderThanDays} days'";
        
        return conn.Execute(sql);
    }

    /// <summary>
    /// Clear entire thumbnail cache
    /// </summary>
    public int ClearThumbnailCache()
    {
        using var conn = OpenConnection();
        
        var sql = $"DELETE FROM {Table("thumbnail")}";
        return conn.Execute(sql);
    }
}
