using Npgsql;
using Dapper;
using Diffusion.Common;
using Diffusion.Common.Query;
using Diffusion.Database.PostgreSQL.Models;
using System.Text;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Helper to convert positional bindings (IEnumerable<object>) to named Dapper parameters
/// and replace ? placeholders with @p0, @p1, @p2, etc.
/// </summary>
internal static class BindingHelper
{
    public static (string Query, DynamicParameters Parameters) ConvertBindingsToNamedParameters(string query, IEnumerable<object> bindings)
    {
        var parameters = new DynamicParameters();
        var bindingArray = bindings.ToArray();
        
        // Replace ? with @p0, @p1, @p2, etc. in order
        var paramIndex = 0;
        var result = new StringBuilder();
        var inQuotes = false;
        var inIdentifier = false;
        
        for (int i = 0; i < query.Length; i++)
        {
            var ch = query[i];
            
            // Track if we're inside quotes
            if (ch == '\'' && (i == 0 || query[i-1] != '\\'))
            {
                inQuotes = !inQuotes;
                result.Append(ch);
            }
            // Track if we're inside double-quoted identifiers
            else if (ch == '"' && (i == 0 || query[i-1] != '\\'))
            {
                inIdentifier = !inIdentifier;
                result.Append(ch);
            }
            // Replace ? with named parameter if not inside quotes/identifiers
            else if (ch == '?' && !inQuotes && !inIdentifier)
            {
                var paramName = $"p{paramIndex}";
                result.Append($"@{paramName}");
                
                if (paramIndex < bindingArray.Length)
                {
                    parameters.Add(paramName, bindingArray[paramIndex]);
                }
                paramIndex++;
            }
            else
            {
                result.Append(ch);
            }
        }
        
        return (result.ToString(), parameters);
    }
}

public class Paging
{
    public int PageSize { get; set; }
    public int Offset { get; set; }
}

public class Sorting
{
    public string SortBy { get; set; }
    public string SortDirection { get; set; }

    public Sorting(string sortBy, string sortDirection)
    {
        SortBy = sortBy switch
        {
            "Date Created" => "created_date",
            "Last Viewed" => "viewed_date",
            "Last Updated" => "touched_date",
            "Date Modified" => "modified_date",
            "Rating" => "rating",
            "Aesthetic Score" => "aesthetic_score",
            "Prompt" => "prompt",
            "Random" => "RANDOM()",
            "Name" => "file_name",
            "File Size" => "file_size",
            _ => "created_date",
        };

        SortDirection = sortDirection switch
        {
            "A-Z" => "ASC",
            "Z-A" => "DESC",
            _ => "DESC",
        };
    }

    public void Deconstruct(out string sortBy, out string sortDir)
    {
        sortBy = SortBy;
        sortDir = SortDirection;
    }
}

public class CountSize
{
    public long Total { get; set; }
    public long Size { get; set; }
    
    public void Deconstruct(out long total, out long size)
    {
        total = Total;
        size = Size;
    }
}

public class UsedPrompt
{
    public string Prompt { get; set; } = "";
    public int Usage { get; set; }
}

/// <summary>
/// Search and query operations
/// </summary>
public partial class PostgreSQLDataStore
{
    private const string ViewColumns = "favorite, for_deletion, rating, aesthetic_score, created_date, nsfw, has_error";
    
    /// <summary>
    /// Convert positional bindings from QueryCombiner to named Dapper parameters and update query
    /// </summary>
    private static (string Query, DynamicParameters Parameters) ConvertBindingsToNamedParameters(string query, IEnumerable<object> bindings)
    {
        return BindingHelper.ConvertBindingsToNamedParameters(query, bindings);
    }
    
    public IEnumerable<ModelView> GetImageModels()
    {
        using var conn = OpenConnection();
        
        string whereClause = GetInitialWhereClause();
        
        var query = $"SELECT model AS name, model_hash AS hash, COUNT(*) AS image_count FROM image {whereClause} GROUP BY model, model_hash";
        
        var models = conn.Query<ModelView>(query);
        
        return models;
    }

    public int GetTotal()
    {
        using var conn = OpenConnection();
        
        var query = "SELECT COUNT(*) FROM image";
        
        var count = conn.ExecuteScalar<int>(query + GetInitialWhereClause());
        
        return count;
    }

    public long CountFileSize(QueryOptions queryOptions)
    {
        using var conn = OpenConnection();

        var q = QueryCombiner.Parse(queryOptions);

        var (convertedSql, parameters) = ConvertBindingsToNamedParameters(q.Query, q.Bindings);

        var size = conn.ExecuteScalar<long>(
            $"SELECT SUM(file_size) FROM image main INNER JOIN ({convertedSql}) sub ON sub.id = main.id",
            parameters);

        return size;
    }

    public long CountPromptFileSize(string prompt)
    {
        using var conn = OpenConnection();

        var q = QueryBuilder.QueryPrompt(prompt);

        var (convertedSql, parameters) = ConvertBindingsToNamedParameters(q.WhereClause, q.Bindings);

        var size = conn.ExecuteScalar<long>(
            $"SELECT SUM(file_size) FROM image m1 {string.Join(' ', q.Joins)} WHERE {convertedSql}",
            parameters);

        return size;
    }

    private string GetInitialWhereClause(bool forceDeleted = false)
    {
        var whereClauses = new List<string>();

        if (QueryBuilder.HideNSFW)
        {
            whereClauses.Add("(nsfw = false OR nsfw IS NULL)");
        }

        if (forceDeleted)
        {
            whereClauses.Add("for_deletion = true");
        }
        else if (QueryBuilder.HideDeleted)
        {
            whereClauses.Add("for_deletion = false");
        }

        if (QueryBuilder.HideUnavailable)
        {
            whereClauses.Add("(unavailable = false)");
        }

        var whereExpression = string.Join(" AND ", whereClauses);

        return whereClauses.Any() ? $" WHERE {whereExpression}" : "";
    }

    public CountSize CountAndSize(QueryOptions options)
    {
        using var conn = OpenConnection();
        
        var q = QueryCombiner.Parse(options);
        
        var whereClause = QueryCombiner.GetInitialWhereClause("main", options);
        
        // Convert subquery first
        var (convertedSubQuery, parameters) = ConvertBindingsToNamedParameters(q.Query, q.Bindings);
        
        var join = $"INNER JOIN ({convertedSubQuery}) sub ON main.id = sub.id";
        
        var where = whereClause.Length > 0 ? $"WHERE {whereClause}" : "";
        
        var sql = $"SELECT COUNT(*) AS total, COALESCE(SUM(file_size),0) AS size FROM image main {join} {where}";
        
        var countSize = conn.Query<CountSize>(sql, parameters);
        
        return countSize.First();
    }

    public int CountPrompt(string prompt)
    {
        using var conn = OpenConnection();

        var q = QueryBuilder.QueryPrompt(prompt);

        var (convertedSql, parameters) = ConvertBindingsToNamedParameters(q.WhereClause, q.Bindings);

        var count = conn.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM image m1 {string.Join(' ', q.Joins)} WHERE {convertedSql}",
            parameters);

        return count;
    }

    public CountSize CountAndFileSizeEx(QueryOptions options)
    {
        using var conn = OpenConnection();

        var q = QueryCombiner.ParseEx(options);

        var whereClause = QueryCombiner.GetInitialWhereClause("main", options);

        var (convertedSubQuery, parameters) = ConvertBindingsToNamedParameters(q.Query, q.Bindings);

        var join = $"INNER JOIN ({convertedSubQuery}) sub ON main.id = sub.id";

        var where = whereClause.Length > 0 ? $"WHERE {whereClause}" : "";

        var countSize = conn.Query<CountSize>(
            $"SELECT COUNT(*) AS total, SUM(file_size) AS size FROM image main {join} {where}",
            parameters);

        return countSize.First();
    }

    public ImageEntity? GetImage(int id)
    {
        using var conn = OpenConnection();
        
        return conn.QueryFirstOrDefault<ImageEntity>(
            "SELECT * FROM image WHERE id = @id", 
            new { id });
    }

    public IEnumerable<ImageView> GetImagesView(IEnumerable<int> ids)
    {
        using var conn = OpenConnection();
        
        var idList = ids.ToList();
        
        var images = conn.Query<ImageView>(
            $@"SELECT i.id, i.path, {ViewColumns}, 
               (SELECT COUNT(1) FROM album_image WHERE image_id = i.id) AS album_count 
               FROM image i WHERE i.id = ANY(@ids)",
            new { ids = idList.ToArray() });
        
        return images;
    }

    public IEnumerable<Album> GetImageAlbums(int id)
    {
        using var conn = OpenConnection();
        
        var albums = conn.Query<Album>(
            @"SELECT a.* FROM image i 
              INNER JOIN album_image ai ON ai.image_id = i.id 
              INNER JOIN album a ON ai.album_id = a.id 
              WHERE i.id = @id",
            new { id });
        
        return albums;
    }

    public IEnumerable<Album> GetImageAlbums(IEnumerable<int> ids)
    {
        using var conn = OpenConnection();
        
        var idList = ids.ToList();
        
        var albums = conn.Query<Album>(
            @"SELECT DISTINCT a.* FROM image i 
              INNER JOIN album_image ai ON ai.image_id = i.id 
              INNER JOIN album a ON ai.album_id = a.id 
              WHERE i.id = ANY(@ids)",
            new { ids = idList.ToArray() });
        
        return albums;
    }

    public IEnumerable<ImageEntity> QueryAll()
    {
        using var conn = OpenConnection();
        
        var query = "SELECT * FROM image";
        
        var images = conn.Query<ImageEntity>(query + GetInitialWhereClause());
        
        return images;
    }

    public IEnumerable<ImageView> SearchPrompt(string? prompt, int pageSize, int offset, string sortBy, string sortDirection)
    {
        using var conn = OpenConnection();
        
        if (string.IsNullOrEmpty(prompt))
            return Enumerable.Empty<ImageView>();
            
        var sorting = new Sorting(sortBy, sortDirection);
        var (sortField, sortDir) = sorting;
        
        var q = QueryBuilder.QueryPrompt(prompt!);
        
        // Convert query and bindings
        var sql = $@"SELECT m1.id, m1.path, {ViewColumns}, 
               (SELECT COUNT(1) FROM album_image WHERE image_id = m1.id) AS album_count 
               FROM image m1 {string.Join(' ', q.Joins)} 
               WHERE {q.WhereClause} 
               ORDER BY {sortField} {sortDir} 
               LIMIT @pageSize OFFSET @offset";
               
        var (convertedSql, parameters) = ConvertBindingsToNamedParameters(sql, q.Bindings);
        parameters.Add("pageSize", pageSize);
        parameters.Add("offset", offset);
        
        var images = conn.Query<ImageView>(convertedSql, parameters);
        
        return images;
    }

    public IEnumerable<ImageView> Search(QueryOptions queryOptions, Sorting sorting, Paging? paging = null)
    {
        using var conn = OpenConnection();
        
        var q = QueryCombiner.Parse(queryOptions);
        
        var whereClause = QueryCombiner.GetInitialWhereClause("main", queryOptions);
        
        // Convert subquery first
        var (convertedSubQuery, parameters) = ConvertBindingsToNamedParameters(q.Query, q.Bindings);
        
        var join = $"INNER JOIN ({convertedSubQuery}) sub ON main.id = sub.id";
        
        var where = whereClause.Length > 0 ? $"WHERE {whereClause}" : "";
        
        var page = "";
        if (paging != null)
        {
            page = " LIMIT @pageSize OFFSET @offset";
        }
        
        var (sortField, sortDir) = sorting;
        
        var sql = $@"SELECT main.id, main.path, {ViewColumns}, 
               (SELECT COUNT(1) FROM album_image WHERE image_id = main.id) AS album_count 
               FROM image main {join} {where} 
               ORDER BY {sortField} {sortDir} {page}";
        
        if (paging != null)
        {
            parameters.Add("pageSize", paging.PageSize);
            parameters.Add("offset", paging.Offset);
        }
        
        var images = conn.Query<ImageView>(sql, parameters);
        
        return images;
    }

    public IEnumerable<ImageView> SearchEx(QueryOptions options, Sorting sorting, Paging? paging = null)
    {
        using var conn = OpenConnection();
        
        var q = QueryCombiner.ParseEx(options);
        
        var whereClause = QueryCombiner.GetInitialWhereClause("main", options);
        
        // Convert subquery first
        var (convertedSubQuery, parameters) = ConvertBindingsToNamedParameters(q.Query, q.Bindings);
        
        var join = $"INNER JOIN ({convertedSubQuery}) sub ON main.id = sub.id";
        
        var where = whereClause.Length > 0 ? $"WHERE {whereClause}" : "";
        
        var page = "";
        if (paging != null)
        {
            page = " LIMIT @pageSize OFFSET @offset";
        }
        
        var (sortField, sortDir) = sorting;
        
        var sql = $@"SELECT main.id, main.path, {ViewColumns}, 
               (SELECT COUNT(1) FROM album_image WHERE image_id = main.id) AS album_count 
               FROM image main {join} {where} 
               ORDER BY {sortField} {sortDir} {page}";
        
        if (paging != null)
        {
            parameters.Add("pageSize", paging.PageSize);
            parameters.Add("offset", paging.Offset);
        }
        
        var images = conn.Query<ImageView>(sql, parameters);
        
        return images;
    }

    public IEnumerable<ImageView> Search(Filter filter, QueryOptions options, Sorting sorting, Paging? paging = null)
    {
        using var conn = OpenConnection();
        
        var q = QueryCombiner.Filter(filter, options);
        
        var whereClause = QueryCombiner.GetInitialWhereClause("main", options);
        
        // Convert subquery first
        var (convertedSubQuery, parameters) = ConvertBindingsToNamedParameters(q.Query, q.Bindings);
        
        var join = $"INNER JOIN ({convertedSubQuery}) sub ON main.id = sub.id";
        
        var where = whereClause.Length > 0 ? $"WHERE {whereClause}" : "";
        
        var page = "";
        if (paging != null)
        {
            page = " LIMIT @pageSize OFFSET @offset";
        }
        
        var (sortField, sortDir) = sorting;
        
        var sql = $@"SELECT main.id, main.path, {ViewColumns}, 
               (SELECT COUNT(1) FROM album_image WHERE image_id = main.id) AS album_count 
               FROM image main {join} {where} 
               ORDER BY {sortField} {sortDir} {page}";
        
        if (paging != null)
        {
            parameters.Add("pageSize", paging.PageSize);
            parameters.Add("offset", paging.Offset);
        }
        
        var images = conn.Query<ImageView>(sql, parameters);
        
        return images;
    }

    public IEnumerable<UsedPrompt> SearchPrompts(string? prompt, bool fullText, int distance)
    {
        using var conn = OpenConnection();
        
        if (string.IsNullOrEmpty(prompt))
        {
            var query = "SELECT prompt, COUNT(*) AS usage FROM image";
            
            query += GetInitialWhereClause();
            query += " GROUP BY prompt ORDER BY usage DESC LIMIT 100";
            
            return conn.Query<UsedPrompt>(query);
        }
        
        // Simple LIKE search for PostgreSQL
        var likePattern = $"%{prompt}%";
        var results = conn.Query<UsedPrompt>(
            @"SELECT prompt, COUNT(*) AS usage FROM image 
              WHERE prompt ILIKE @pattern 
              GROUP BY prompt 
              ORDER BY usage DESC 
              LIMIT 100",
            new { pattern = likePattern });
        
        return results;
    }

    public IEnumerable<UsedPrompt> SearchNegativePrompts(string prompt, bool fullText, int distance)
    {
        using var conn = OpenConnection();
        
        if (string.IsNullOrEmpty(prompt))
        {
            var query = "SELECT negative_prompt AS prompt, COUNT(*) AS usage FROM image";
            
            query += GetInitialWhereClause();
            query += " GROUP BY negative_prompt ORDER BY usage DESC LIMIT 100";
            
            return conn.Query<UsedPrompt>(query);
        }
        
        var likePattern = $"%{prompt}%";
        var results = conn.Query<UsedPrompt>(
            @"SELECT negative_prompt AS prompt, COUNT(*) AS usage FROM image 
              WHERE negative_prompt ILIKE @pattern 
              GROUP BY negative_prompt 
              ORDER BY usage DESC 
              LIMIT 100",
            new { pattern = likePattern });
        
        return results;
    }
}

public class ImageView
{
    public int Id { get; set; }
    public bool Favorite { get; set; }
    public bool ForDeletion { get; set; }
    public int? Rating { get; set; }
    public decimal? AestheticScore { get; set; }
    public string Path { get; set; } = "";
    public DateTime CreatedDate { get; set; }
    public bool Nsfw { get; set; }
    public bool NSFW { get => Nsfw; set => Nsfw = value; }  // Alias for backward compatibility
    public int AlbumCount { get; set; }
    public bool HasError { get; set; }
}

// Note: Album class moved to Models/Album.cs to avoid duplication
