using Npgsql;
using Dapper;
using System.Data;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL implementation of DataStore using Npgsql and pgvector for embedding similarity search.
/// Replaces SQLite-based DataStore for enterprise-scale image cataloging (1M+ images).
/// </summary>
public partial class PostgreSQLDataStore : IDisposable
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;
    private readonly object _lock = new object();

    public string DatabasePath => _connectionString;
    public bool RescanRequired { get; set; }

    public PostgreSQLDataStore(string connectionString)
    {
        _connectionString = connectionString;
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableParameterLogging();
        _dataSource = builder.Build();
    }

    /// <summary>
    /// Opens a new database connection from the pool
    /// </summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = await _dataSource.OpenConnectionAsync();
        return conn;
    }

    /// <summary>
    /// Opens a synchronous connection (for compatibility with existing code)
    /// </summary>
    public NpgsqlConnection OpenConnection()
    {
        return _dataSource.OpenConnection();
    }

    /// <summary>
    /// Initialize database schema and pgvector extension
    /// </summary>
    public async Task Create(Func<object> notify, Action<object> complete)
    {
        await using var conn = await OpenConnectionAsync();

        try
        {
            // Enable pgvector extension
            var handle = notify?.Invoke();
            try
            {
                await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;");
                await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS ltree;");
            }
            finally
            {
                if (handle != null)
                    complete.Invoke(handle);
            }

            // Create migrations and schema
            var migrations = new PostgreSQLMigrations(conn);
            await migrations.UpdateAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize PostgreSQL database", ex);
        }
    }

    /// <summary>
    /// Get database statistics
    /// </summary>
    public async Task<DatabaseStats> GetStatsAsync()
    {
        await using var conn = await OpenConnectionAsync();
        
        var total = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM image;");
        var withEmbeddings = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM image WHERE prompt_embedding IS NOT NULL;");
        
        return new DatabaseStats
        {
            TotalImages = total,
            ImagesWithEmbeddings = withEmbeddings,
            EmbeddingCoverage = total > 0 ? (float)withEmbeddings / total : 0f
        };
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
    }
}

/// <summary>
/// Database statistics
/// </summary>
public class DatabaseStats
{
    public long TotalImages { get; set; }
    public long ImagesWithEmbeddings { get; set; }
    public float EmbeddingCoverage { get; set; }
}
