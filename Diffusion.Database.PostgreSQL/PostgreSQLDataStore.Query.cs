using Dapper;
using System.Text.Json;
using Diffusion.Common.Query;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Saved query management for PostgreSQLDataStore
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Check if a query with the given name exists
    /// </summary>
    public bool QueryExists(string name)
    {
        using var conn = OpenConnection();
        
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM query WHERE name = @Name",
            new { Name = name });

        return count == 1;
    }

    /// <summary>
    /// Create or update a saved query
    /// </summary>
    public void CreateOrUpdateQuery(string name, QueryOptions queryOptions)
    {
        var json = JsonSerializer.Serialize(queryOptions);

        using var conn = OpenConnection();

        lock (_lock)
        {
            conn.Execute(@"
                INSERT INTO query (name, query_json, created_date, modified_date)
                VALUES (@Name, @Json, @Now, @Now)
                ON CONFLICT (name) 
                DO UPDATE SET query_json = @Json, modified_date = @Now",
                new { Name = name, Json = json, Now = DateTime.UtcNow });
        }
    }

    /// <summary>
    /// Get all saved queries
    /// </summary>
    public IEnumerable<QueryItem> GetQueries()
    {
        using var conn = OpenConnection();
        
        return conn.Query<QueryItem>(
            "SELECT id, name, created_date FROM query ORDER BY name DESC");
    }

    /// <summary>
    /// Get a saved query by ID
    /// </summary>
    public QueryOptions GetQuery(int id)
    {
        using var conn = OpenConnection();
        
        var query = conn.QueryFirstOrDefault<Query>(
            "SELECT name, query_json, created_date FROM query WHERE id = @Id",
            new { Id = id });

        if (query == null)
            throw new InvalidOperationException($"Query with ID {id} not found");

        var options = JsonSerializer.Deserialize<QueryOptions>(query.QueryJson);
        
        if (options == null)
            throw new InvalidOperationException($"Failed to deserialize query {id}");

        return options;
    }

    /// <summary>
    /// Rename a saved query
    /// </summary>
    public void RenameQuery(int id, string name)
    {
        using var conn = OpenConnection();

        lock (_lock)
        {
            conn.Execute(
                "UPDATE query SET name = @Name, modified_date = @Now WHERE id = @Id",
                new { Name = name, Now = DateTime.UtcNow, Id = id });
        }
    }

    /// <summary>
    /// Delete a saved query
    /// </summary>
    public void RemoveQuery(int id)
    {
        using var conn = OpenConnection();

        lock (_lock)
        {
            conn.Execute("DELETE FROM query WHERE id = @Id", new { Id = id });
        }
    }
}
