using Diffusion.Common;
using Diffusion.Common.Query;
using System.Text.RegularExpressions;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL-specific query combiner that uses proper boolean literals (true/false instead of 1/0)
/// and PostgreSQL table naming conventions (lowercase/snake_case)
/// </summary>
public static class PostgreSQLQueryCombiner
{
    /// <summary>
    /// Convert SQLite-style query to PostgreSQL-compatible query
    /// - Lowercase table names (Image -> image, Folder -> folder, Album -> album)
    /// - Lowercase column references (Id -> id, Path -> path)
    /// - Handle boolean comparisons (= 0 -> = false, = 1 -> = true)
    /// </summary>
    private static string ToPostgreSqlQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return query;
        
        // Replace table names with lowercase versions
        var result = query;
        
        // Replace FROM/JOIN table names
        result = Regex.Replace(result, @"\bFROM\s+Image\b", "FROM image", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bFROM\s+Folder\b", "FROM folder", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bFROM\s+Album\b", "FROM album", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bFROM\s+Node\b", "FROM node", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bFROM\s+NodeProperty\b", "FROM node_property", RegexOptions.IgnoreCase);
        
        result = Regex.Replace(result, @"\bJOIN\s+Image\b", "JOIN image", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bJOIN\s+Folder\b", "JOIN folder", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bJOIN\s+Album\b", "JOIN album", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bJOIN\s+AlbumImage\b", "JOIN album_image", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bJOIN\s+Node\b", "JOIN node", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bJOIN\s+NodeProperty\b", "JOIN node_property", RegexOptions.IgnoreCase);
        
        // Replace column references - be careful with aliases like m1.Id, f.Id
        result = Regex.Replace(result, @"\.Id\b", ".id");
        result = Regex.Replace(result, @"\.Path\b", ".path");
        result = Regex.Replace(result, @"\.Name\b", ".name");
        result = Regex.Replace(result, @"\.Model\b", ".model");
        result = Regex.Replace(result, @"\.Workflow\b", ".workflow");
        result = Regex.Replace(result, @"\.Value\b", ".value");
        result = Regex.Replace(result, @"\.FolderId\b", ".folder_id");
        result = Regex.Replace(result, @"\.NodeId\b", ".node_id");
        result = Regex.Replace(result, @"\.ImageId\b", ".image_id");
        result = Regex.Replace(result, @"\.AlbumId\b", ".album_id");
        
        // Replace standalone column names with lowercase (for WHERE clauses)
        result = Regex.Replace(result, @"\(ForDeletion\s*=", "(for_deletion =", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\(NSFW\s*=", "(nsfw =", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\(Favorite\s*=", "(favorite =", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\(Rating\s*=", "(rating =", RegexOptions.IgnoreCase);
        
        // Replace SELECT Id with SELECT id
        result = Regex.Replace(result, @"\bSELECT\s+Id\b", "SELECT id", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bSELECT\s+DISTINCT\s+Id\b", "SELECT DISTINCT id", RegexOptions.IgnoreCase);
        
        // Replace AS Id with AS id
        result = Regex.Replace(result, @"\bAS\s+Id\b", "AS id", RegexOptions.IgnoreCase);
        
        // Replace boolean comparisons (SQLite uses 0/1, PostgreSQL uses false/true)
        result = Regex.Replace(result, @"=\s*0\b", "= false");
        result = Regex.Replace(result, @"=\s*1\b", "= true");
        
        return result;
    }

    /// <summary>
    /// Get the initial WHERE clause for filtering images based on query options
    /// Uses PostgreSQL boolean literals (true/false) instead of SQLite integers (1/0)
    /// </summary>
    public static string GetInitialWhereClause(string imageAlias, QueryOptions options)
    {
        var whereClauses = new List<string>();

        if (options.HideNSFW)
        {
            whereClauses.Add($"({imageAlias}.nsfw = false OR {imageAlias}.nsfw IS NULL)");
        }

        if (options.SearchView != SearchView.Deleted && options.HideDeleted)
        {
            whereClauses.Add($"({imageAlias}.for_deletion = false)");
        }

        if (options.HideUnavailable)
        {
            whereClauses.Add($"({imageAlias}.unavailable = false)");
        }

        var whereExpression = string.Join(" AND ", whereClauses);

        return whereClauses.Any() ? $"{whereExpression}" : "";
    }

    /// <summary>
    /// Parse query options into a PostgreSQL-compatible query with bindings
    /// </summary>
    public static (string Query, IEnumerable<object> Bindings) ParseEx(QueryOptions options)
    {
        if (!options.Filter.IsEmpty)
        {
            return Filter(options.Filter, options);
        }
        else
        {
            return Parse(options);
        }
    }

    public static (string Query, IEnumerable<object> Bindings) SearchRawData(string prompt, string query, IEnumerable<object> bindings, QueryOptions options)
    {
        if (prompt is { Length: > 0 } && options.SearchRawData)
        {
            var conditions = new List<KeyValuePair<string, object>>();

            var tokens = CSVParser.Parse(prompt);

            foreach (var token in tokens)
            {
                conditions.Add(new KeyValuePair<string, object>("(workflow LIKE ?)", $"%{token.Trim()}%"));
            }

            var whereClause = string.Join(" AND ", conditions.Select(c => c.Key));
            var pbindings = conditions.SelectMany(c =>
            {
                return c.Value switch
                {
                    IEnumerable<object> orConditions => orConditions.Select(o => o),
                    _ => new[] { c.Value }
                };
            }).Where(o => o != null);

            var q = $"SELECT m1.id FROM image m1 WHERE {whereClause}";

            query = $"{query} UNION {q}";

            bindings = bindings.Concat(pbindings);

            return (query, bindings);
        }

        return (query, bindings);
    }

    public static (string Query, IEnumerable<object> Bindings) Parse(QueryOptions options)
    {
        var r = QueryBuilder.ParseParameters(options.Query ?? string.Empty);

        var where0Clause = r.WhereClause is { Length: > 0 } ? $" WHERE {ToPostgreSqlQuery(r.WhereClause)}" : "";

        var query0 = $"SELECT m1.id FROM image m1 {ToPostgreSqlQuery(string.Join(' ', r.Joins))} {where0Clause}";

        var q = QueryBuilder.Parse(r.TextPrompt);

        var where1Clause = q.WhereClause is { Length: > 0 } ? $" WHERE {ToPostgreSqlQuery(q.WhereClause)}" : "";

        var query = $"SELECT m1.id FROM image m1 {ToPostgreSqlQuery(string.Join(' ', q.Joins))} {where1Clause}";

        var bindings = q.Bindings;

        (query, bindings) = SearchRawData(q.TextPrompt, query, bindings, options);

        if (q.TextPrompt is { Length: > 0 } && (options.SearchAllProperties || options.SearchNodes))
        {
            var p = ComfyUIQueryBuilder.Parse(q.TextPrompt, options);

            query += " UNION " +
                     $"{ToPostgreSqlQuery(p.Query)}";

            bindings = bindings.Concat(p.Bindings);
        }

        query += " INTERSECT " +
                 $"{query0}";

        bindings = bindings.Concat(r.Bindings);

        ApplyFilters(ref query, ref bindings, options);

        return (query, bindings);
    }

    public static (string Query, IEnumerable<object> Bindings) Filter(Filter filter, QueryOptions options)
    {
        if (options.SearchView == SearchView.Deleted)
        {
            filter.UseForDeletion = false;
        }

        var r = QueryBuilder.PreFilter(filter);

        var where0Clause = r.WhereClause is { Length: > 0 } ? $" WHERE {ToPostgreSqlQuery(r.WhereClause)}" : "";

        var query0 = $"SELECT m1.id FROM image m1 {ToPostgreSqlQuery(string.Join(' ', r.Joins))} {where0Clause}";

        var q = QueryBuilder.Filter(filter);

        var where1Clause = q.WhereClause is { Length: > 0 } ? $" WHERE {ToPostgreSqlQuery(q.WhereClause)}" : "";

        var query = $"SELECT m1.id FROM image m1 {ToPostgreSqlQuery(string.Join(' ', q.Joins))} {where1Clause}";

        var bindings = q.Bindings;

        if (filter.NodeFilters != null && filter.NodeFilters.Any(d => d is { IsActive: true, Property.Length: > 0, Value.Length: > 0 }))
        {
            var p = ComfyUIQueryBuilder.Filter(filter);

            query = (where1Clause.Length > 0 ? query + " UNION " : "") +
                     $"SELECT id FROM ({ToPostgreSqlQuery(p.Query)})";

            bindings = bindings.Concat(p.Bindings);
        }

        query += " INTERSECT " +
                 $"{query0}";

        bindings = bindings.Concat(r.Bindings);

        ApplyFilters(ref query, ref bindings, options);

        return (query, bindings);
    }

    /// <summary>
    /// Apply additional filters to query using PostgreSQL syntax
    /// Uses true/false for booleans and lowercase table names
    /// </summary>
    private static void ApplyFilters(ref string query, ref IEnumerable<object> bindings, QueryOptions options)
    {
        var filters = new List<string>();

        switch (options.SearchView)
        {
            case SearchView.Favorites:
                // PostgreSQL uses true/false for booleans
                filters.Add($"SELECT m1.id FROM image m1 WHERE m1.favorite = true");
                break;
            case SearchView.Deleted:
                filters.Add($"SELECT m1.id FROM image m1 WHERE m1.for_deletion = true");
                break;
        }

        if (options.AlbumIds is { Count: > 0 })
        {
            var placeholders = string.Join(",", options.AlbumIds.Select(a => "?"));
            // PostgreSQL uses lowercase table names
            filters.Add($"SELECT DISTINCT m1.id FROM image m1 INNER JOIN album_image ai ON ai.image_id = m1.id INNER JOIN album a ON a.id = ai.album_id WHERE a.id IN ({placeholders})");
            bindings = bindings.Concat(options.AlbumIds.Cast<object>());
        }

        if (options.Models is { Count: > 0 })
        {
            var orFilters = new List<string>();

            var hashes = options.Models.Where(d => !string.IsNullOrEmpty(d.Hash)).Select(d => d.Hash).ToList();
            var hashPlaceholders = string.Join(",", hashes.Select(a => "?"));

            orFilters.Add($"SELECT m1.id FROM image m1 WHERE m1.model_hash IN ({hashPlaceholders})");
            bindings = bindings.Concat(hashes);

            var names = options.Models.Where(d => !string.IsNullOrEmpty(d.Name)).Select(d => d.Name)
                .Except(hashes)
                .ToList();

            var namePlaceholders = string.Join(",", names.Select(a => "?"));

            orFilters.Add($"SELECT m1.id FROM image m1 WHERE m1.model IN ({namePlaceholders})");
            bindings = bindings.Concat(names);

            var modelUnion = string.Join(" UNION ", orFilters.Select(d => $"{d}"));

            filters.Add($"SELECT id FROM ({modelUnion})");
        }

        if (options.SearchView == SearchView.Folder)
        {
            filters.Add($"SELECT m1.id FROM image m1 INNER JOIN folder f ON f.id = m1.folder_id WHERE f.path = ?");
            bindings = bindings.Concat(new[] { (object)options.Folder! });
        }

        if (filters.Any())
        {
            query = $"SELECT id FROM ({query}) INTERSECT " + string.Join(" INTERSECT ", filters);
        }
    }
}
