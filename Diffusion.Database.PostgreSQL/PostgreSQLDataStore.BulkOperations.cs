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

            if (!EnsureFolderExists(dirName ?? "", folderCache, out var folderId))
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

            if (!EnsureFolderExists(dirName ?? "", folderCache, out var folderId))
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
}
