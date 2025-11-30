using Dapper;
using Npgsql;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Image-specific operations for PostgreSQLDataStore
/// Handles CRUD operations, path updates, and image lifecycle management
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Remove a single image by ID
    /// </summary>
    public async Task RemoveImageAsync(int id)
    {
        await RemoveImagesAsync(new[] { id }).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove a single image by ID (synchronous)
    /// </summary>
    public void RemoveImage(int id)
    {
        RemoveImages(new[] { id });
    }

    /// <summary>
    /// Remove multiple images by IDs (synchronous)
    /// Cascades to related tables (nodes, node properties, album images)
    /// </summary>
    public void RemoveImages(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        
        var idArray = ids.ToArray();
        if (idArray.Length == 0) return;

        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();

        try
        {
            lock (_lock)
            {
                // Create temporary table for IDs
                conn.Execute(@"
                    CREATE TEMP TABLE temp_deleted_ids (id INTEGER) ON COMMIT DROP;
                    ", transaction: transaction);

                // Insert IDs to delete
                conn.Execute("INSERT INTO temp_deleted_ids (id) VALUES (@Id)", 
                    idArray.Select(id => new { Id = id }), 
                    transaction: transaction);

                // Delete cascade: node_property → node → album_image → image
                conn.Execute(@"
                    DELETE FROM node_property 
                    WHERE node_id IN (
                        SELECT id FROM node WHERE image_id IN (SELECT id FROM temp_deleted_ids)
                    );", transaction: transaction);

                conn.Execute(@"
                    DELETE FROM node 
                    WHERE image_id IN (SELECT id FROM temp_deleted_ids);", 
                    transaction: transaction);

                conn.Execute(@"
                    DELETE FROM album_image 
                    WHERE image_id IN (SELECT id FROM temp_deleted_ids);", 
                    transaction: transaction);

                conn.Execute(@"
                    DELETE FROM image 
                    WHERE id IN (SELECT id FROM temp_deleted_ids);", 
                    transaction: transaction);

                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Logger.LogError($"Failed to remove {idArray.Length} images", ex);
            throw;
        }
    }

    /// <summary>
    /// Remove multiple images by IDs (async)
    /// </summary>
    public async Task RemoveImagesAsync(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        
        var idArray = ids.ToArray();
        if (idArray.Length == 0) return;

        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        await using var transaction = await conn.BeginTransactionAsync().ConfigureAwait(false);

        try
        {
            // Create temporary table for IDs
            await conn.ExecuteAsync(@"
                CREATE TEMP TABLE temp_deleted_ids (id INTEGER) ON COMMIT DROP;
                ", transaction: transaction).ConfigureAwait(false);

            // Insert IDs to delete
            await conn.ExecuteAsync("INSERT INTO temp_deleted_ids (id) VALUES (@Id)", 
                idArray.Select(id => new { Id = id }), 
                transaction: transaction).ConfigureAwait(false);

            // Delete cascade
            await conn.ExecuteAsync(@"
                DELETE FROM node_property 
                WHERE node_id IN (
                    SELECT id FROM node WHERE image_id IN (SELECT id FROM temp_deleted_ids)
                );", transaction: transaction).ConfigureAwait(false);

            await conn.ExecuteAsync(@"
                DELETE FROM node 
                WHERE image_id IN (SELECT id FROM temp_deleted_ids);", 
                transaction: transaction).ConfigureAwait(false);

            await conn.ExecuteAsync(@"
                DELETE FROM album_image 
                WHERE image_id IN (SELECT id FROM temp_deleted_ids);", 
                transaction: transaction).ConfigureAwait(false);

            await conn.ExecuteAsync(@"
                DELETE FROM image 
                WHERE id IN (SELECT id FROM temp_deleted_ids);", 
                transaction: transaction).ConfigureAwait(false);

            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            Logger.LogError($"Failed to remove {idArray.Length} images", ex);
            throw;
        }
    }

    /// <summary>
    /// Get all images marked for deletion
    /// </summary>
    public IEnumerable<ImagePath> GetImagesTaggedForDeletion()
    {
        using var conn = OpenConnection();
        
        var images = conn.Query<ImagePath>(
            "SELECT id, path FROM image WHERE for_deletion = true ORDER BY id");

        foreach (var image in images)
        {
            yield return image;
        }
    }

    /// <summary>
    /// Get all images marked for deletion (async)
    /// </summary>
    public async Task<IEnumerable<ImagePath>> GetImagesTaggedForDeletionAsync()
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        return await conn.QueryAsync<ImagePath>(
            "SELECT id, path FROM image WHERE for_deletion = true ORDER BY id").ConfigureAwait(false);
    }

    /// <summary>
    /// Move an image to a new path and update folder association
    /// </summary>
    public void MoveImage(int id, string newPath, Dictionary<string, Folder> folderCache)
    {
        ArgumentNullException.ThrowIfNull(newPath);
        ArgumentNullException.ThrowIfNull(folderCache);
        
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var dirName = Path.GetDirectoryName(newPath);
            
            if (string.IsNullOrEmpty(dirName) || !EnsureFolderExists(conn, dirName, folderCache, out var folderId))
            {
                Logger.LogWarn($"Root folder not found for {StringUtility.TruncatePath(dirName)}");
                return;
            }

            conn.Execute(
                "UPDATE image SET path = @Path, folder_id = @FolderId WHERE id = @Id",
                new { Path = newPath, FolderId = folderId, Id = id });
        }
    }

    /// <summary>
    /// Move an image to a new path using existing connection
    /// </summary>
    public void MoveImage(NpgsqlConnection conn, int id, string newPath, Dictionary<string, Folder> folderCache)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(newPath);
        ArgumentNullException.ThrowIfNull(folderCache);
        
        var dirName = Path.GetDirectoryName(newPath);
        
        if (string.IsNullOrEmpty(dirName) || !EnsureFolderExists(conn, dirName, folderCache, out var folderId))
        {
            Logger.LogWarn($"Root folder not found for {StringUtility.TruncatePath(dirName)}");
            return;
        }

        lock (_lock)
        {
            conn.Execute(
                "UPDATE image SET path = @Path, folder_id = @FolderId WHERE id = @Id",
                new { Path = newPath, FolderId = folderId, Id = id });
        }
    }

    /// <summary>
    /// Find images by hash (for duplicate detection)
    /// </summary>
    public IReadOnlyCollection<HashMatch> GetImageIdByHash(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        
        using var conn = OpenConnection();
        
        return conn.Query<HashMatch>(
            "SELECT id, path FROM image WHERE hash = @Hash",
            new { Hash = hash }).ToList();
    }

    /// <summary>
    /// Find images by hash (async)
    /// </summary>
    public async Task<IReadOnlyCollection<HashMatch>> GetImageIdByHashAsync(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        var results = await conn.QueryAsync<HashMatch>(
            "SELECT id, path FROM image WHERE hash = @Hash",
            new { Hash = hash }).ConfigureAwait(false);
        
        return results.ToList();
    }

    /// <summary>
    /// Update image path by ID
    /// </summary>
    public int UpdateImagePath(int id, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        
        lock (_lock)
        {
            using var conn = OpenConnection();
            return conn.Execute(
                "UPDATE image SET path = @Path WHERE id = @Id",
                new { Path = path, Id = id });
        }
    }

    /// <summary>
    /// Update image path by ID (async)
    /// </summary>
    public async Task<int> UpdateImagePathAsync(int id, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        return await conn.ExecuteAsync(
            "UPDATE image SET path = @Path WHERE id = @Id",
            new { Path = path, Id = id }).ConfigureAwait(false);
    }

    /// <summary>
    /// Check if an image exists by path
    /// </summary>
    public bool ImageExists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        
        using var conn = OpenConnection();
        
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM image WHERE path = @Path",
            new { Path = path });

        return count > 0;
    }

    /// <summary>
    /// Check if an image exists by path (async)
    /// </summary>
    public async Task<bool> ImageExistsAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM image WHERE path = @Path",
            new { Path = path }).ConfigureAwait(false);

        return count > 0;
    }

    /// <summary>
    /// Update viewed date for a single image
    /// </summary>
    public int UpdateViewed(int id)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            return conn.Execute(
                "UPDATE image SET viewed_date = @ViewedDate WHERE id = @Id",
                new { ViewedDate = DateTime.UtcNow, Id = id });
        }
    }

    /// <summary>
    /// Batch update viewed dates for multiple images
    /// </summary>
    public int UpdateViewed(IReadOnlyCollection<UpdateViewed> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        if (views.Count == 0) return 0;

        lock (_lock)
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();

            try
            {
                // Create temp table
                conn.Execute(@"
                    CREATE TEMP TABLE temp_update_views (
                        id INTEGER, 
                        viewed TIMESTAMP
                    ) ON COMMIT DROP;
                    ", transaction: transaction);

                // Bulk insert
                conn.Execute(
                    "INSERT INTO temp_update_views (id, viewed) VALUES (@Id, @Viewed)",
                    views,
                    transaction: transaction);

                // Update from temp table
                var updated = conn.Execute(@"
                    UPDATE image 
                    SET viewed_date = t.viewed 
                    FROM temp_update_views t 
                    WHERE image.id = t.id",
                    transaction: transaction);

                transaction.Commit();
                return updated;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Logger.LogError($"Failed to update {views.Count} viewed dates", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Update image filename and path
    /// </summary>
    public int UpdateImageFilename(int id, string path, string filename)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filename);
        
        lock (_lock)
        {
            using var conn = OpenConnection();
            return conn.Execute(
                "UPDATE image SET path = @Path, file_name = @FileName WHERE id = @Id",
                new { Path = path, FileName = filename, Id = id });
        }
    }

    /// <summary>
    /// Update image filename and path (async)
    /// </summary>
    public async Task<int> UpdateImageFilenameAsync(int id, string path, string filename)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filename);
        
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        return await conn.ExecuteAsync(
            "UPDATE image SET path = @Path, file_name = @FileName WHERE id = @Id",
            new { Path = path, FileName = filename, Id = id }).ConfigureAwait(false);
    }

    /// <summary>
    /// Helper method to ensure folder exists in the database
    /// Reuses folder cache to avoid repeated lookups
    /// NOTE: This overload is DEPRECATED - it opens a new connection for each call.
    /// Callers should use EnsureFolderExists(NpgsqlConnection, string, Dictionary, out int) directly
    /// with their existing connection to avoid connection pool exhaustion.
    /// </summary>
    [Obsolete("Use EnsureFolderExists(NpgsqlConnection conn, ...) to reuse connections and avoid pool exhaustion")]
    private bool EnsureFolderExists(string folderPath, Dictionary<string, Folder> folderCache, out int folderId)
    {
        folderId = 0;

        if (string.IsNullOrEmpty(folderPath))
            return false;

        // WARNING: Opens a new connection - prefer passing existing connection
        using var conn = OpenConnection();
        return EnsureFolderExists(conn, folderPath, folderCache, out folderId);
    }

    /// <summary>
    /// Get all images under a path (recursive)
    /// </summary>
    public IEnumerable<ImagePath> GetAllPathImages(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        
        using var conn = OpenConnection();
        
        var images = conn.Query<ImagePath>(
            "SELECT id, folder_id, path, unavailable FROM image WHERE path LIKE @Path || '%'",
            new { Path = path });

        foreach (var image in images)
        {
            yield return image;
        }
    }

    /// <summary>
    /// Count all images under a path (recursive)
    /// </summary>
    public int CountAllPathImages(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        
        using var conn = OpenConnection();
        
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM image WHERE path LIKE @Path || '%'",
            new { Path = path });
    }

    /// <summary>
    /// Get images in a folder (optionally recursive)
    /// </summary>
    public IEnumerable<ImagePath> GetFolderImages(int folderId, bool recursive)
    {
        using var conn = OpenConnection();
        
        string query;
        if (recursive)
        {
            // Use recursive CTE to get all descendant folders
            query = @"
                WITH RECURSIVE folder_tree AS (
                    SELECT id FROM folder WHERE id = @FolderId
                    UNION ALL
                    SELECT f.id FROM folder f
                    INNER JOIN folder_tree ft ON f.parent_id = ft.id
                )
                SELECT id, folder_id, path, unavailable 
                FROM image 
                WHERE folder_id IN (SELECT id FROM folder_tree)
                ORDER BY id";
        }
        else
        {
            query = "SELECT id, folder_id, path, unavailable FROM image WHERE folder_id = @FolderId ORDER BY id";
        }

        var images = conn.Query<ImagePath>(query, new { FolderId = folderId });

        foreach (var image in images)
        {
            yield return image;
        }
    }

    /// <summary>
    /// Get all image paths (for migration/export)
    /// </summary>
    public IEnumerable<ImagePath> GetImagePaths()
    {
        using var conn = OpenConnection();
        
        var images = conn.Query<ImagePath>(
            "SELECT id, folder_id, path FROM image ORDER BY id");

        foreach (var image in images)
        {
            yield return image;
        }
    }

    /// <summary>
    /// Get image paths for specific IDs
    /// </summary>
    public IEnumerable<ImagePath> GetImagePaths(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        
        var idArray = ids.ToArray();
        if (idArray.Length == 0) yield break;

        using var conn = OpenConnection();
        
        var images = conn.Query<ImagePath>(
            "SELECT id, folder_id, path FROM image WHERE id = ANY(@Ids)",
            new { Ids = idArray });

        foreach (var image in images)
        {
            yield return image;
        }
    }

    /// <summary>
    /// Update image folder ID when path changes
    /// </summary>
    public void UpdateImageFolderId(int id, string path, Dictionary<string, Folder> folderCache)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folderCache);
        
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var dirName = Path.GetDirectoryName(path);
            
            if (string.IsNullOrEmpty(dirName) || !EnsureFolderExists(conn, dirName, folderCache, out var folderId))
            {
                Logger.LogWarn($"Root folder not found for {StringUtility.TruncatePath(dirName)}");
                return;
            }

            conn.Execute(
                "UPDATE image SET folder_id = @FolderId WHERE id = @Id",
                new { FolderId = folderId, Id = id });
        }
    }
}
