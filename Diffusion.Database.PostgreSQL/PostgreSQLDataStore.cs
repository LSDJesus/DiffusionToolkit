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
            if (oldValue != _currentSchema)
            {
                Logger.Log($"CurrentSchema changed: '{oldValue}' -> '{_currentSchema}'");
            }
        }
    }
    
    /// <summary>
    /// Detected schema type: "diffusion_toolkit", "luna", "custom", or null if not detected
    /// </summary>
    public string? DetectedSchemaType { get; private set; }
    
    /// <summary>
    /// True if database is in read-only mode (no write permissions)
    /// </summary>
    public bool IsReadOnlyMode { get; private set; }

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
    /// Add schema prefixes to common table names in a SQL query
    /// </summary>
    private string WithSchema(string sql)
    {
        return sql
            .Replace(" image ", $" {Table("image")} ")
            .Replace(" folder ", $" {Table("folder")} ")
            .Replace(" album ", $" {Table("album")} ")
            .Replace(" album_image ", $" {Table("album_image")} ")
            .Replace(" node ", $" {Table("node")} ")
            .Replace(" node_property ", $" {Table("node_property")} ")
            .Replace("FROM image ", $"FROM {Table("image")} ")
            .Replace("FROM folder ", $"FROM {Table("folder")} ")
            .Replace("FROM album ", $"FROM {Table("album")} ")
            .Replace("JOIN image ", $"JOIN {Table("image")} ")
            .Replace("JOIN folder ", $"JOIN {Table("folder")} ")
            .Replace("JOIN album ", $"JOIN {Table("album")} ")
            .Replace("JOIN album_image ", $"JOIN {Table("album_image")} ")
            .Replace("JOIN node ", $"JOIN {Table("node")} ")
            .Replace("JOIN node_property ", $"JOIN {Table("node_property")} ");
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
            Logger.Log($"PostgreSQL initialization failed: {ex.Message}");
            Logger.Log($"Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Logger.Log($"Inner exception: {ex.InnerException.Message}");
            }
            throw new InvalidOperationException("Failed to initialize PostgreSQL database", ex);
        }
    }

    /// <summary>
    /// Get all schemas that contain the 'image' table (our application schemas)
    /// </summary>
    public async Task<List<string>> GetApplicationSchemasAsync()
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);
        
        var schemas = await conn.QueryAsync<string>(
            @"SELECT DISTINCT table_schema 
              FROM information_schema.tables 
              WHERE table_name = 'image' 
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
              ORDER BY table_schema").ConfigureAwait(false);
        
        return schemas.ToList();
    }

    /// <summary>
    /// Create a new schema and initialize all tables in it (does NOT fall back to public)
    /// </summary>
    public async Task CreateSchemaWithTables(string schemaName)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            Logger.Log($"CreateSchemaWithTables: Creating schema '{schemaName}'");
            
            // Create the schema
            await conn.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\";").ConfigureAwait(false);
            Logger.Log($"CreateSchemaWithTables: Schema '{schemaName}' created");
            
            // Set search path to the new schema
            await conn.ExecuteAsync($"SET search_path TO \"{schemaName}\", public;").ConfigureAwait(false);
            
            // Run migrations in the new schema
            var migrations = new PostgreSQLMigrations(conn, schemaName);
            await migrations.UpdateAsync().ConfigureAwait(false);
            
            Logger.Log($"CreateSchemaWithTables: Tables created in schema '{schemaName}'");
        }
        catch (Exception ex)
        {
            Logger.Log($"CreateSchemaWithTables: Error - {ex}");
            throw new InvalidOperationException($"Failed to create schema '{schemaName}'", ex);
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

    /// <summary>
    /// Detect schema type based on table structure
    /// Returns: "diffusion_toolkit", "luna", or "custom"
    /// </summary>
    public async Task<string> DetectSchemaTypeAsync()
    {
        try
        {
            using var conn = OpenConnection();
            
            // Check for Luna-specific tables
            var hasStories = await conn.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'stories')",
                new { schema = CurrentSchema });
            var hasTurnImages = await conn.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'turn_images')",
                new { schema = CurrentSchema });
            var hasCharacterPortraits = await conn.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'character_portraits')",
                new { schema = CurrentSchema });
            
            if (hasStories && (hasTurnImages || hasCharacterPortraits))
            {
                DetectedSchemaType = "luna";
                Logger.Log($"Detected Luna Narrated schema in '{CurrentSchema}'");
                return "luna";
            }
            
            // Check for standard Diffusion Toolkit tables
            var hasImage = await conn.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'image')",
                new { schema = CurrentSchema });
            var hasFolder = await conn.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'folder')",
                new { schema = CurrentSchema });
            
            if (hasImage && hasFolder)
            {
                DetectedSchemaType = "diffusion_toolkit";
                Logger.Log($"Detected Diffusion Toolkit schema in '{CurrentSchema}'");
                return "diffusion_toolkit";
            }
            
            DetectedSchemaType = "custom";
            Logger.Log($"Unknown schema type in '{CurrentSchema}'");
            return "custom";
        }
        catch (Exception ex)
        {
            Logger.Log($"Schema detection failed: {ex.Message}");
            DetectedSchemaType = "unknown";
            return "unknown";
        }
    }

    /// <summary>
    /// Test if current connection has write permissions
    /// </summary>
    public async Task<bool> HasWritePermissionAsync()
    {
        try
        {
            using var conn = OpenConnection();
            
            // Try to create and drop a temp table
            await conn.ExecuteAsync($"CREATE TEMP TABLE _write_test (id INT)");
            await conn.ExecuteAsync($"DROP TABLE _write_test");
            
            IsReadOnlyMode = false;
            return true;
        }
        catch
        {
            IsReadOnlyMode = true;
            Logger.Log($"Database is in read-only mode");
            return false;
        }
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
