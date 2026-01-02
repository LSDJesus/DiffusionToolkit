using Npgsql;
using Dapper;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Folder operations (hierarchy management, watched folders, exclusions)
/// Uses recursive CTEs for tree traversal
/// </summary>
public partial class PostgreSQLDataStore
{
    private string DirectoryTreeCTE => $@"
        WITH RECURSIVE directory_tree(id, parent_id, root_id, depth) AS (
            SELECT id, parent_id, id AS root_id, 0 AS depth 
            FROM {Table("folder")}
            UNION ALL
            SELECT f.id, f.parent_id, t.root_id, t.depth + 1 
            FROM {Table("folder")} f 
            JOIN directory_tree t ON f.parent_id = t.id
        )";

    private string DirectoryTreeWithPathCTE => $@"
        WITH RECURSIVE directory_tree(id, parent_id, path, root_id, depth) AS (
            SELECT id, parent_id, path, id AS root_id, 0 AS depth 
            FROM {Table("folder")}
            UNION ALL
            SELECT f.id, f.parent_id, f.path, t.root_id, t.depth + 1 
            FROM {Table("folder")} f 
            JOIN directory_tree t ON f.parent_id = t.id
        )";

    private string DirectoryTreeCTEExcluded => $@"
        WITH RECURSIVE directory_tree(id, parent_id, root_id, depth) AS (
            SELECT id, parent_id, id AS root_id, 0 AS depth 
            FROM {Table("folder")} 
            WHERE excluded = true
            UNION ALL
            SELECT f.id, f.parent_id, t.root_id, t.depth + 1 
            FROM {Table("folder")} f 
            JOIN directory_tree t ON f.parent_id = t.id
        )";

    private const string FolderColumns = @"
        id AS Id, 
        parent_id AS ParentId, 
        path AS Path, 
        image_count AS ImageCount, 
        scanned_date AS ScannedDate, 
        unavailable AS Unavailable, 
        archived AS Archived, 
        excluded AS Excluded, 
        is_root AS IsRoot, 
        recursive AS Recursive, 
        watched AS Watched";

    public void SetFolderExcluded(string path, bool excluded, bool recursive)
    {
        using var conn = OpenConnection();

        if (EnsureFolderExists(conn, path, null, out var folderId))
        {
            var query = recursive
                ? $"{DirectoryTreeCTE} UPDATE {Table("folder")} SET excluded = @excluded FROM (SELECT id FROM directory_tree WHERE root_id = @folderId) AS subfolder WHERE folder.id = subfolder.id"
                : $"UPDATE {Table("folder")} SET excluded = @excluded WHERE id = @folderId";

            lock (_lock)
            {
                conn.Execute(query, new { excluded, folderId });
            }
        }
    }

    public void SetFolderExcluded(int id, bool excluded, bool recursive)
    {
        using var conn = OpenConnection();

        var query = recursive
            ? $"{DirectoryTreeCTE} UPDATE {Table("folder")} SET excluded = @excluded FROM (SELECT id FROM directory_tree WHERE root_id = @id) AS subfolder WHERE folder.id = subfolder.id"
            : $"UPDATE {Table("folder")} SET excluded = @excluded WHERE id = @id";

        lock (_lock)
        {
            conn.Execute(query, new { excluded, id });
        }
    }

    public void SetFolderUnavailable(int id, bool unavailable, bool recursive)
    {
        using var conn = OpenConnection();

        var query = recursive
            ? $"{DirectoryTreeCTE} UPDATE {Table("folder")} SET unavailable = @unavailable FROM (SELECT id FROM directory_tree WHERE root_id = @id) AS subfolder WHERE folder.id = subfolder.id"
            : $"UPDATE {Table("folder")} SET unavailable = @unavailable WHERE id = @id";

        lock (_lock)
        {
            conn.Execute(query, new { unavailable, id });
        }
    }

    public void SetFolderArchived(int id, bool archived, bool recursive)
    {
        using var conn = OpenConnection();

        var query = recursive
            ? $"{DirectoryTreeCTE} UPDATE {Table("folder")} SET archived = @archived FROM (SELECT id FROM directory_tree WHERE root_id = @id) AS subfolder WHERE folder.id = subfolder.id"
            : $"UPDATE {Table("folder")} SET archived = @archived WHERE id = @id";

        lock (_lock)
        {
            conn.Execute(query, new { archived, id });
        }
    }

    public void SetFolderWatched(int id, bool watched)
    {
        using var conn = OpenConnection();

        lock (_lock)
        {
            conn.Execute($"UPDATE {Table("folder")} SET watched = @watched WHERE id = @id", new { watched, id });
        }
    }

    public void SetFolderRecursive(int id, bool recursive)
    {
        using var conn = OpenConnection();

        lock (_lock)
        {
            conn.Execute($"UPDATE {Table("folder")} SET recursive = @recursive WHERE id = @id", new { recursive, id });
        }
    }

    public int AddRootFolder(string path, bool watched, bool recursive)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();

            // For root folders, root_folder_id should be the folder's own ID
            // We use a two-step process: insert with temporary value, then update
            var id = conn.QuerySingleOrDefault<int?>(
                $"SELECT id FROM {Table("folder")} WHERE path = @path",
                new { path });

            if (id.HasValue)
            {
                // Update existing folder
                conn.Execute($@"
                    UPDATE {Table("folder")} SET
                        parent_id = 0,
                        excluded = false,
                        is_root = true,
                        recursive = @recursive,
                        watched = @watched,
                        root_folder_id = id
                    WHERE id = @id",
                    new { id = id.Value, recursive, watched });
                return id.Value;
            }
            else
            {
                // Insert new folder with temporary root_folder_id = 0
                var newId = conn.QuerySingle<int>($@"
                    INSERT INTO {Table("folder")} (parent_id, root_folder_id, path, image_count, scanned_date, unavailable, archived, excluded, is_root, recursive, watched)
                    VALUES (0, 0, @path, 0, NULL, false, false, false, true, @recursive, @watched)
                    RETURNING id",
                    new { path, recursive, watched });

                // Update root_folder_id to point to itself
                conn.Execute(
                    $"UPDATE {Table("folder")} SET root_folder_id = @newId WHERE id = @newId",
                    new { newId });

                return newId;
            }
        }
    }

    public int AddExcludedFolder(string path)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();

            // Check if folder exists
            var id = conn.QuerySingleOrDefault<int?>(
                $"SELECT id FROM {Table("folder")} WHERE path = @path",
                new { path });

            if (id.HasValue)
            {
                // Update existing folder
                conn.Execute($@"
                    UPDATE {Table("folder")} SET
                        parent_id = 0,
                        excluded = true,
                        is_root = false,
                        root_folder_id = id
                    WHERE id = @id",
                    new { id = id.Value });
                return id.Value;
            }
            else
            {
                // Insert new excluded folder with temporary root_folder_id = 0
                var newId = conn.QuerySingle<int>($@"
                    INSERT INTO {Table("folder")} (parent_id, root_folder_id, path, image_count, scanned_date, unavailable, archived, excluded, is_root)
                    VALUES (0, 0, @path, 0, NULL, false, false, true, false)
                    RETURNING id",
                    new { path });

                // Update root_folder_id to point to itself
                conn.Execute(
                    $"UPDATE {Table("folder")} SET root_folder_id = @newId WHERE id = @newId",
                    new { newId });

                return newId;
            }
        }
    }

    public void RemoveRootFolder(int id, bool removeImages)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            conn.Execute($"DELETE FROM {Table("folder")} WHERE id = @id", new { id });
        }
    }

    public bool EnsureFolderExists(NpgsqlConnection conn, string path, Dictionary<string, Folder>? folderCache, out int folderId)
    {
        if (folderCache?.TryGetValue(path, out var folder) ?? false)
        {
            folderId = folder.Id;
            return true;
        }

        // Find root folder that matches this path
        var rootFolders = conn.Query<Folder>($"SELECT {FolderColumns} FROM {Table("folder")} WHERE is_root = true");
        var root = rootFolders.FirstOrDefault(d => path.StartsWith(d.Path));
        
        if (root == null)
        {
            folderId = -1;
            return false;
        }

        var current = root.Path;
        var currentParentId = root.Id;
        
        // Traverse path hierarchy
        int nextSeparator;
        do
        {
            // Check bounds before calling IndexOf to avoid ArgumentOutOfRangeException
            int startIndex = current.Length + 1;
            nextSeparator = startIndex < path.Length 
                ? path.IndexOf("\\", startIndex, StringComparison.Ordinal)
                : -1;
            current = nextSeparator > 0 ? path[..nextSeparator] : path;

            var existingId = conn.ExecuteScalar<int?>(
                $"SELECT id FROM {Table("folder")} WHERE path = @current",
                new { current });

            if (existingId.HasValue)
            {
                currentParentId = existingId.Value;
            }
            else
            {
                int id;
                lock (_lock)
                {
                    // Sub-folders inherit root_folder_id from their root
                    id = conn.QuerySingle<int>($@"
                        INSERT INTO {Table("folder")} (parent_id, root_folder_id, path, unavailable, archived, excluded, is_root)
                        VALUES (@currentParentId, @rootId, @current, false, false, false, false)
                        RETURNING id",
                        new { currentParentId, rootId = root.Id, current });
                }
                
                folderCache?.Add(current, new Folder() 
                { 
                    Id = id, 
                    ParentId = currentParentId, 
                    Path = current 
                });
                
                currentParentId = id;
            }
        } while (nextSeparator > 0);

        folderId = currentParentId;
        return true;
    }

    public IEnumerable<FolderView> GetSubFoldersView(int id)
    {
        using var conn = OpenConnection();

        return conn.Query<FolderView>($@"
            {DirectoryTreeCTE}
            SELECT 
                f.id AS Id,
                f.parent_id AS ParentId,
                f.path AS Path,
                f.image_count AS ImageCount,
                f.scanned_date AS ScannedDate,
                f.unavailable AS Unavailable,
                f.archived AS Archived,
                f.excluded AS Excluded,
                f.is_root AS IsRoot,
                f.recursive AS Recursive,
                f.watched AS Watched,
                LEAST(COALESCE(p.children, 0), 1) AS HasChildren
            FROM {Table("folder")} f
            JOIN directory_tree t ON f.id = t.id
            LEFT JOIN (
                SELECT root_id, COUNT(*) AS children 
                FROM directory_tree 
                WHERE depth = 1 
                GROUP BY root_id
            ) p ON f.id = p.root_id
            WHERE t.root_id = @id AND t.depth = 1",
            new { id });
    }

    public IEnumerable<Folder> GetSubfolders(int id)
    {
        using var conn = OpenConnection();

        return conn.Query<Folder>($@"
            {DirectoryTreeCTE}
            SELECT {FolderColumns}
            FROM {Table("folder")} f
            JOIN directory_tree t ON f.id = t.id
            WHERE t.root_id = @id AND t.depth = 1",
            new { id });
    }

    public IEnumerable<Folder> GetDescendants(int id)
    {
        using var conn = OpenConnection();

        return conn.Query<Folder>($@"
            {DirectoryTreeCTE}
            SELECT {FolderColumns}
            FROM {Table("folder")} f
            JOIN directory_tree t ON f.id = t.id
            WHERE t.root_id = @id",
            new { id });
    }

    public IEnumerable<Folder> GetRootExcludedFolders()
    {
        using var conn = OpenConnection();

        return conn.Query<Folder>($@"
            {DirectoryTreeCTEExcluded}
            SELECT {FolderColumns}
            FROM {Table("folder")}
            WHERE id IN (
                SELECT id FROM directory_tree WHERE depth = 0
                EXCEPT
                SELECT id FROM directory_tree WHERE depth > 0
            )");
    }

    public IEnumerable<Folder> GetExcludedFolders()
    {
        using var conn = OpenConnection();
        return conn.Query<Folder>($"SELECT {FolderColumns} FROM {Table("folder")} WHERE excluded = true");
    }

    public IEnumerable<Folder> GetArchivedFolders(bool archived = true)
    {
        using var conn = OpenConnection();
        return conn.Query<Folder>($"SELECT {FolderColumns} FROM {Table("folder")} WHERE archived = @archived", new { archived });
    }

    public IEnumerable<Folder> GetRootFolders()
    {
        using var conn = OpenConnection();
        return conn.Query<Folder>($"SELECT {FolderColumns} FROM {Table("folder")} WHERE is_root = true");
    }

    public IEnumerable<FolderView> GetFoldersView()
    {
        using var conn = OpenConnection();

        return conn.Query<FolderView>($@"
            {DirectoryTreeCTE}
            SELECT 
                f.id AS Id,
                f.parent_id AS ParentId,
                f.path AS Path,
                f.image_count AS ImageCount,
                f.scanned_date AS ScannedDate,
                f.unavailable AS Unavailable,
                f.archived AS Archived,
                f.excluded AS Excluded,
                f.is_root AS IsRoot,
                f.recursive AS Recursive,
                f.watched AS Watched,
                LEAST(COALESCE(p.children, 0), 1) AS HasChildren
            FROM {Table("folder")} f
            LEFT JOIN (
                SELECT root_id, COUNT(*) AS children 
                FROM directory_tree 
                WHERE depth = 1 
                GROUP BY root_id
            ) p ON f.id = p.root_id");
    }

    public IEnumerable<Folder> GetFolders()
    {
        using var conn = OpenConnection();
        return conn.Query<Folder>($"SELECT {FolderColumns} FROM {Table("folder")}");
    }

    public Folder? GetFolder(string path)
    {
        using var conn = OpenConnection();
        return conn.QuerySingleOrDefault<Folder>($"SELECT {FolderColumns} FROM {Table("folder")} WHERE path = @path", new { path });
    }

    public int CleanRemovedFolders()
    {
        // TODO: Implement folder cleanup logic
        return 0;
    }

    public int ChangeFolderPath(string path, string newPath)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();

            try
            {
                // Update image paths
                var imagesUpdated = conn.Execute(@"
                    UPDATE image 
                    SET path = @newPath || SUBSTRING(path FROM LENGTH(@path) + 1)
                    WHERE path LIKE @path || '\\%'",
                    new { path, newPath },
                    transaction);

                // Update subfolder paths
                conn.Execute($@"
                    UPDATE {Table("folder")} 
                    SET path = @newPath || SUBSTRING(path FROM LENGTH(@path) + 1)
                    WHERE path LIKE @path || '\\%'",
                    new { path, newPath },
                    transaction);

                // Update the folder itself
                conn.Execute($@"
                    UPDATE {Table("folder")} 
                    SET path = @newPath
                    WHERE path = @path",
                    new { path, newPath },
                    transaction);

                transaction.Commit();
                return imagesUpdated;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public int RemoveFolder(int id)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();

            try
            {
                // Delete associated data in correct order (respects foreign keys)
                conn.Execute($@"
                    {DirectoryTreeCTE}
                    DELETE FROM node_property
                    WHERE node_id IN (
                        SELECT n.id FROM node n
                        WHERE n.image_id IN (
                            SELECT i.id FROM image i
                            WHERE i.folder_id IN (SELECT id FROM directory_tree WHERE root_id = @id)
                        )
                    )",
                    new { id },
                    transaction);

                conn.Execute($@"
                    {DirectoryTreeCTE}
                    DELETE FROM node
                    WHERE image_id IN (
                        SELECT i.id FROM image i
                        WHERE i.folder_id IN (SELECT id FROM directory_tree WHERE root_id = @id)
                    )",
                    new { id },
                    transaction);

                conn.Execute($@"
                    {DirectoryTreeCTE}
                    DELETE FROM album_image
                    WHERE image_id IN (
                        SELECT i.id FROM image i
                        WHERE i.folder_id IN (SELECT id FROM directory_tree WHERE root_id = @id)
                    )",
                    new { id },
                    transaction);

                var imagesDeleted = conn.Execute($@"
                    {DirectoryTreeCTE}
                    DELETE FROM image
                    WHERE folder_id IN (SELECT id FROM directory_tree WHERE root_id = @id)",
                    new { id },
                    transaction);

                // Delete subfolders
                conn.Execute($@"
                    {DirectoryTreeCTE}
                    DELETE FROM {Table("folder")}
                    WHERE id IN (SELECT id FROM directory_tree WHERE root_id = @id)",
                    new { id },
                    transaction);

                // Delete the folder itself
                conn.Execute($@"DELETE FROM {Table("folder")} WHERE id = @id", new { id }, transaction);

                transaction.Commit();
                return imagesDeleted;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public int RemoveFolder(string path)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();

            try
            {
                // Delete associated data
                conn.Execute($@"
                    DELETE FROM node_property
                    WHERE node_id IN (
                        SELECT n.id FROM node n
                        WHERE n.image_id IN (
                            SELECT i.id FROM image i
                            INNER JOIN {Table("folder")} f ON i.folder_id = f.id
                            WHERE f.path LIKE @path || '%'
                        )
                    )",
                    new { path },
                    transaction);

                conn.Execute($@"
                    DELETE FROM node
                    WHERE image_id IN (
                        SELECT i.id FROM image i
                        INNER JOIN {Table("folder")} f ON i.folder_id = f.id
                        WHERE f.path LIKE @path || '%'
                    )",
                    new { path },
                    transaction);

                conn.Execute($@"
                    DELETE FROM album_image
                    WHERE image_id IN (
                        SELECT i.id FROM image i
                        INNER JOIN {Table("folder")} f ON i.folder_id = f.id
                        WHERE f.path LIKE @path || '%'
                    )",
                    new { path },
                    transaction);

                var imagesDeleted = conn.Execute($@"
                    DELETE FROM image
                    WHERE folder_id IN (SELECT id FROM {Table("folder")} WHERE path LIKE @path || '%')",
                    new { path },
                    transaction);

                conn.Execute($"DELETE FROM {Table("folder")} WHERE path = @path", new { path }, transaction);

                transaction.Commit();
                return imagesDeleted;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public IEnumerable<FolderArchived> GetArchivedStatus()
    {
        using var conn = OpenConnection();
        return conn.Query<FolderArchived>($"SELECT id AS Id, archived AS Archived FROM {Table("folder")} ORDER BY id");
    }

    public bool FolderHasImages(string path)
    {
        using var conn = OpenConnection();
        
        var count = conn.ExecuteScalar<int>($@"
            SELECT COUNT(1) 
            FROM image i 
            INNER JOIN {Table("folder")} f ON i.folder_id = f.id 
            WHERE f.path = @path",
            new { path });

        return count > 0;
    }

    public CountSize FolderCountAndSize(int folderId)
    {
        using var conn = OpenConnection();

        return conn.QuerySingle<CountSize>($@"
            {DirectoryTreeCTE}
            SELECT 
                COUNT(*) AS Total, 
                COALESCE(SUM(file_size), 0) AS Size
            FROM image
            WHERE folder_id IN (SELECT id FROM directory_tree WHERE root_id = @folderId)",
            new { folderId });
    }
}

