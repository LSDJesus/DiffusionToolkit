using Dapper;
using Npgsql;
using System.Text;
using System.Reflection;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Bulk insert and update operations for PostgreSQLDataStore
/// Handles batch image additions and updates during scanning
/// </summary>
public partial class PostgreSQLDataStore
{
    private class ReturnId
    {
        public int Id { get; set; }
    }

    /// <summary>
    /// Bulk add new images to the database
    /// Uses PostgreSQL COPY or batch INSERT for performance
    /// </summary>
    public int AddImages(NpgsqlConnection conn, IEnumerable<Image> images, IEnumerable<string> includeProperties, Dictionary<string, Folder> folderCache, CancellationToken cancellationToken)
    {
        int added = 0;
        var imageList = images.ToList();
        
        if (imageList.Count == 0) return 0;

        // Build field list excluding user-defined metadata
        var exclude = new string[]
        {
            nameof(Image.Id),
            nameof(Image.CustomTags),
            nameof(Image.Rating),
            nameof(Image.Workflow),
            nameof(Image.ViewedDate),
            nameof(Image.TouchedDate),
        };

        exclude = exclude.Except(includeProperties).ToArray();

        var properties = typeof(Image).GetProperties()
            .Where(p => !exclude.Contains(p.Name))
            .ToList();

        // Map C# property names to snake_case column names
        var columnNames = properties.Select(p => ToSnakeCase(p.Name)).ToList();
        
        var query = new StringBuilder($"INSERT INTO image ({string.Join(", ", columnNames)}) VALUES ");
        var valueGroups = new List<string>();
        var parameters = new DynamicParameters();

        for (int i = 0; i < imageList.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var image = imageList[i];
            var dirName = Path.GetDirectoryName(image.Path);

            if (!EnsureFolderExists(conn, dirName ?? "", folderCache, out var folderId))
            {
                Logger.Log($"Root folder not found for {dirName}");
                image.HasError = true;
            }

            image.FolderId = folderId;

            var paramNames = new List<string>();
            foreach (var property in properties)
            {
                var paramName = $"@p{i}_{property.Name}";
                paramNames.Add(paramName);
                parameters.Add(paramName, property.GetValue(image));
            }

            valueGroups.Add($"({string.Join(", ", paramNames)})");
        }

        if (cancellationToken.IsCancellationRequested || valueGroups.Count == 0)
        {
            return 0;
        }

        query.Append(string.Join(", ", valueGroups));
        query.Append(" ON CONFLICT (path) DO NOTHING RETURNING id");

        try
        {
            lock (_lock)
            {
                var returnedIds = conn.Query<ReturnId>(query.ToString(), parameters).ToList();
                
                // Assign returned IDs to images
                for (int i = 0; i < Math.Min(imageList.Count, returnedIds.Count); i++)
                {
                    imageList[i].Id = returnedIds[i].Id;
                }

                added = returnedIds.Count;
            }
        }
        catch (Exception e)
        {
            Logger.Log($"AddImages error: {e.Message}");
            if (e.StackTrace != null) Logger.Log(e.StackTrace);
            throw;
        }

        return added;
    }

    /// <summary>
    /// Update existing images by path
    /// </summary>
    public int UpdateImagesByPath(NpgsqlConnection conn, IEnumerable<Image> images, IEnumerable<string> includeProperties, Dictionary<string, Folder> folderCache, CancellationToken cancellationToken)
    {
        int updated = 0;

        // Exclude user-defined metadata from updates
        var exclude = new string[]
        {
            nameof(Image.Id),
            nameof(Image.CustomTags),
            nameof(Image.Rating),
            nameof(Image.Favorite),
            nameof(Image.ForDeletion),
            nameof(Image.NSFW),
            nameof(Image.Unavailable),
            nameof(Image.Workflow),
            nameof(Image.ViewedDate),
            nameof(Image.TouchedDate),
        };

        exclude = exclude.Except(includeProperties).ToArray();

        var properties = typeof(Image).GetProperties()
            .Where(p => !exclude.Contains(p.Name) && p.Name != nameof(Image.Path))
            .ToList();

        var setList = new List<string>();
        foreach (var property in properties)
        {
            var columnName = ToSnakeCase(property.Name);
            if (property.Name == nameof(Image.NSFW))
            {
                setList.Add($"{columnName} = @{property.Name} OR {columnName}");
            }
            else
            {
                setList.Add($"{columnName} = @{property.Name}");
            }
        }

        var updateQuery = $"UPDATE image SET {string.Join(", ", setList)} WHERE path = @Path RETURNING id";

        foreach (var image in images)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var dirName = Path.GetDirectoryName(image.Path);

            if (!EnsureFolderExists(conn, dirName ?? "", folderCache, out var folderId))
            {
                Logger.Log($"Root folder not found for {dirName}");
                image.HasError = true;
            }
            else
            {
                image.FolderId = folderId;
            }

            try
            {
                lock (_lock)
                {
                    var returnedIds = conn.Query<ReturnId>(updateQuery, image).ToList();
                    if (returnedIds.Count > 0)
                    {
                        image.Id = returnedIds[0].Id;
                        updated++;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Log($"UpdateImagesByPath error: {e.Message}");
                if (e.StackTrace != null) Logger.Log(e.StackTrace);
                throw;
            }
        }

        return updated;
    }

    /// <summary>
    /// Helper method to convert PascalCase to snake_case for PostgreSQL column names
    /// </summary>
    private string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        
        // Special cases
        if (name == "NSFW") return "nsfw";
        if (name == "CFGScale") return "cfg_scale";
        
        var sb = new StringBuilder();
        sb.Append(char.ToLower(name[0]));
        
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                sb.Append('_');
                sb.Append(char.ToLower(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Quick scan: Bulk insert image records with only basic file information
    /// This is Phase 1 of two-phase scanning for rapid initial indexing
    /// Uses PostgreSQL COPY for maximum bulk insert performance
    /// </summary>
    public int QuickAddImages(NpgsqlConnection conn, IEnumerable<(string path, long fileSize, DateTime createdDate, DateTime modifiedDate)> files, Dictionary<string, Folder> folderCache, CancellationToken cancellationToken)
    {
        var fileList = files.ToList();
        if (fileList.Count == 0) return 0;

        // PHASE 1: Ensure all folders exist BEFORE starting COPY
        // PostgreSQL doesn't allow mixing COPY with regular queries on same connection
        var uniqueFolderPaths = fileList
            .Select(f => Path.GetDirectoryName(f.path))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList();

        Logger.Log($"QuickAddImages: Pre-creating {uniqueFolderPaths.Count} folders before COPY...");
        
        foreach (var folderPath in uniqueFolderPaths)
        {
            if (cancellationToken.IsCancellationRequested) break;
            EnsureFolderExists(conn, folderPath!, folderCache, out _);
        }

        // Pre-load ALL folder and root_folder mappings AFTER folders are created
        var allFolders = conn.Query<(int id, int root_folder_id)>("SELECT id, root_folder_id FROM folder")
            .ToDictionary(f => f.id, f => f.root_folder_id);

        // Build a path-to-folder lookup from the cache
        var pathToFolderInfo = new Dictionary<string, (int folderId, int rootFolderId)>();
        foreach (var kvp in folderCache)
        {
            if (allFolders.TryGetValue(kvp.Value.Id, out var rootFolderId))
            {
                pathToFolderInfo[kvp.Key] = (kvp.Value.Id, rootFolderId);
            }
        }

        // Create a temporary table to stage the data
        var tempTableName = $"temp_quick_import_{Guid.NewGuid():N}";
        
        try
        {
            // Create temp table (preserve rows across implicit commits during COPY)
            conn.Execute($@"
                CREATE TEMP TABLE {tempTableName} (
                    path TEXT,
                    file_name TEXT,
                    file_size BIGINT,
                    created_date TIMESTAMP,
                    modified_date TIMESTAMP,
                    folder_id INT,
                    root_folder_id INT
                )");

            // PHASE 2: Use COPY for blazing fast bulk insert into temp table
            // All folder lookups are now from in-memory cache only
            using (var writer = conn.BeginBinaryImport($"COPY {tempTableName} (path, file_name, file_size, created_date, modified_date, folder_id, root_folder_id) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var (path, fileSize, createdDate, modifiedDate) in fileList)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var dirName = Path.GetDirectoryName(path);
                    var fileName = Path.GetFileName(path);

                    // Use pre-cached folder info (no DB queries during COPY!)
                    if (dirName == null || !pathToFolderInfo.TryGetValue(dirName, out var folderInfo))
                    {
                        // Folder wasn't found - skip this file
                        continue;
                    }

                    writer.StartRow();
                    writer.Write(path, NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(fileName, NpgsqlTypes.NpgsqlDbType.Text);
                    writer.Write(fileSize, NpgsqlTypes.NpgsqlDbType.Bigint);
                    // Convert to Unspecified kind to avoid PostgreSQL timestamp without time zone error
                    writer.Write(DateTime.SpecifyKind(createdDate, DateTimeKind.Unspecified), NpgsqlTypes.NpgsqlDbType.Timestamp);
                    writer.Write(DateTime.SpecifyKind(modifiedDate, DateTimeKind.Unspecified), NpgsqlTypes.NpgsqlDbType.Timestamp);
                    writer.Write(folderInfo.folderId, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(folderInfo.rootFolderId, NpgsqlTypes.NpgsqlDbType.Integer);
                }

                writer.Complete();
            }

            // Bulk insert from temp table into main table (handles conflicts)
            var inserted = conn.Execute($@"
                INSERT INTO image (
                    path, file_name, file_size, created_date, modified_date, 
                    folder_id, root_folder_id, scan_phase, no_metadata, has_error, 
                    width, height, steps, cfg_scale, seed, batch_size, batch_pos, 
                    favorite, for_deletion, nsfw, unavailable
                )
                SELECT 
                    path, file_name, file_size, created_date, modified_date,
                    folder_id, root_folder_id, 
                    0, false, false, 0, 0, 0, 0, 0, 0, 0, false, false, false, false
                FROM {tempTableName}
                ON CONFLICT (path) DO NOTHING");

            // Fix the sequence after bulk insert to prevent ID gaps
            if (inserted > 0)
            {
                var maxId = conn.ExecuteScalar<int?>("SELECT MAX(id) FROM image") ?? 0;
                conn.Execute($"SELECT setval('image_id_seq', {maxId}, true)");
                Logger.Log($"Updated image_id_seq to {maxId} after inserting {inserted} images");
            }

            // Clean up temp table
            conn.Execute($"DROP TABLE IF EXISTS {tempTableName}");
            
            Logger.Log($"QuickAddImages: Inserted {inserted} images");

            return inserted;
        }
        catch (Exception e)
        {
            Logger.Log($"QuickAddImages error: {e.Message}");
            if (e.StackTrace != null) Logger.Log(e.StackTrace);
            
            // Try to clean up temp table on error
            try
            {
                conn.Execute($"DROP TABLE IF EXISTS {tempTableName}");
            }
            catch { /* Ignore cleanup errors */ }
            
            throw;
        }
    }

    /// <summary>
    /// Get all image paths that are in QuickScan phase (need deep metadata extraction)
    /// </summary>
    public IEnumerable<string> GetQuickScannedPaths(int? folderId = null, int limit = 1000)
    {
        using var conn = OpenConnection();
        
        var query = "SELECT path FROM image WHERE scan_phase = 0";
        
        if (folderId.HasValue)
        {
            query += " AND folder_id = @folderId";
        }
        
        query += " LIMIT @limit";

        return conn.Query<string>(query, new { folderId, limit });
    }

    /// <summary>
    /// Fix the PostgreSQL sequence for image.id to match the current max ID
    /// This resolves issues where bulk operations cause sequence misalignment
    /// </summary>
    public void ResetImageSequence()
    {
        using var conn = OpenConnection();
        
        try
        {
            // Get the current max ID from the image table
            var maxId = conn.ExecuteScalar<int?>("SELECT MAX(id) FROM image") ?? 0;
            
            // Reset the sequence to max_id + 1
            conn.Execute($"SELECT setval('image_id_seq', {maxId}, true)");
            
            Logger.Log($"Reset image_id_seq to {maxId}");
        }
        catch (Exception e)
        {
            Logger.Log($"ResetImageSequence error: {e.Message}");
            throw;
        }
    }
}

