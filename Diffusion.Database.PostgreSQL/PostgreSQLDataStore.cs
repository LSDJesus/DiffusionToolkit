using Npgsql;
using Dapper;
using System.Data;
using Diffusion.Common;

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
    private readonly DatabaseConfiguration _config;
    private string _currentSchema = "public";

    public string DatabasePath => _connectionString;
    public bool RescanRequired { get; set; }
    
    /// <summary>
    /// Gets or sets the current database schema to use for all operations
    /// </summary>
    public string CurrentSchema
    {
        get => _currentSchema;
        set
        {
            var oldValue = _currentSchema;
            _currentSchema = value ?? "public";
            Logger.Log($"CurrentSchema changed: '{oldValue}' -> '{_currentSchema}' (instance: {GetHashCode()})");
        }
    }

    public PostgreSQLDataStore(string connectionString) : this(connectionString, DatabaseConfiguration.Default)
    {
    }

    public PostgreSQLDataStore(string connectionString, DatabaseConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(config);
        
        _connectionString = connectionString;
        _config = config;
        
        // Parse and enhance connection string with pooling settings from config
        var connStringBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = _config.MaxPoolSize,
            MinPoolSize = _config.MinPoolSize,
            ConnectionIdleLifetime = _config.ConnectionIdleLifetimeSeconds,
            ConnectionPruningInterval = _config.ConnectionPruningIntervalSeconds,
            Timeout = _config.ConnectionTimeoutSeconds // Time to wait for a connection from the pool
        };
        
        var builder = new NpgsqlDataSourceBuilder(connStringBuilder.ConnectionString);
        builder.EnableParameterLogging();
        _dataSource = builder.Build();
    }

    public NpgsqlConnectionStringBuilder GetConnection()
    {
        return new NpgsqlConnectionStringBuilder(_connectionString);
    }

    public string GetStatus()
    {
        try
        {
            using var conn = OpenConnection();
            return conn.ServerVersion;
        }
        catch (Exception e)
        {
            return $"Error: {e.Message}";
        }
    }

    /// <summary>
    /// Opens a new database connection from the pool
    /// </summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
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
    /// Gets the schema-qualified table name (e.g., "public.image" or "partitioned.image")
    /// </summary>
    public string Table(string tableName)
    {
        return $"{_currentSchema}.{tableName}";
    }

    /// <summary>
    /// Initialize database schema and pgvector extension
    /// </summary>
    public async Task Create(Func<object> notify, Action<object> complete)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            // Enable pgvector extension
            var handle = notify?.Invoke();
            try
            {
                await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;").ConfigureAwait(false);
                await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS ltree;").ConfigureAwait(false);
                
                Logger.Log($"Schema detection: requested schema = '{_currentSchema}'");
                
                // Check if the requested schema has our tables
                if (!string.Equals(_currentSchema, "public", StringComparison.OrdinalIgnoreCase))
                {
                    var hasImage = await conn.ExecuteScalarAsync<bool>(
                        @"SELECT EXISTS (
                            SELECT 1 FROM information_schema.tables 
                            WHERE table_schema = @schema AND table_name = 'image'
                        );", new { schema = _currentSchema }).ConfigureAwait(false);
                    
                    Logger.Log($"Schema detection: '{_currentSchema}' has image table = {hasImage}");
                    
                    if (!hasImage)
                    {
                        // Check if public schema has tables instead
                        var publicHasImage = await conn.ExecuteScalarAsync<bool>(
                            @"SELECT EXISTS (
                                SELECT 1 FROM information_schema.tables 
                                WHERE table_schema = 'public' AND table_name = 'image'
                            );").ConfigureAwait(false);
                        
                        Logger.Log($"Schema detection: 'public' has image table = {publicHasImage}");
                        
                        if (publicHasImage)
                        {
                            // Fall back to public schema where the data actually is
                            Logger.Log($"Schema detection: Falling back from '{_currentSchema}' to 'public' (instance: {GetHashCode()})");
                            _currentSchema = "public";
                        }
                        else
                        {
                            // Create the new schema if needed
                            Logger.Log($"Schema detection: Creating new schema '{_currentSchema}'");
                            await conn.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {_currentSchema};").ConfigureAwait(false);
                        }
                    }
                }
                
                Logger.Log($"Schema detection: final schema = '{_currentSchema}'");
                
                // Set search path to include our schema
                await conn.ExecuteAsync($"SET search_path TO {_currentSchema}, public;").ConfigureAwait(false);
            }
            finally
            {
                if (handle != null)
                    complete.Invoke(handle);
            }

            // Create migrations and schema (uses current search_path)
            var migrations = new PostgreSQLMigrations(conn, _currentSchema);
            await migrations.UpdateAsync().ConfigureAwait(false);
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
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        var total = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM image;").ConfigureAwait(false);
        var withEmbeddings = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM image WHERE prompt_embedding IS NOT NULL;").ConfigureAwait(false);
        
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
