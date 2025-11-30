namespace Diffusion.Common;

/// <summary>
/// Centralized configuration for database operations.
/// Extracts magic numbers and provides tunable parameters.
/// </summary>
public class DatabaseConfiguration
{
    /// <summary>
    /// Default configuration instance
    /// </summary>
    public static DatabaseConfiguration Default { get; } = new();
    
    // === Compile-time Constants (for default parameter values) ===
    
    /// <summary>
    /// Default batch size for embedding/processing operations (const for default params)
    /// </summary>
    public const int BatchSize = 33;
    
    /// <summary>
    /// Default embedding queue batch size (const for default params)
    /// </summary>
    public const int EmbeddingBatchSize = 32;
    
    // === Connection Pool Settings ===
    
    /// <summary>
    /// Maximum number of connections in the pool (default: 100)
    /// Increased from 50 to handle FileSystemWatcher bursts during batch generation
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;
    
    /// <summary>
    /// Minimum number of connections to keep warm (default: 5)
    /// </summary>
    public int MinPoolSize { get; set; } = 5;
    
    /// <summary>
    /// Seconds before idle connections are closed (default: 300 = 5 minutes)
    /// </summary>
    public int ConnectionIdleLifetimeSeconds { get; set; } = 300;
    
    /// <summary>
    /// Seconds between checks for idle connections (default: 10)
    /// </summary>
    public int ConnectionPruningIntervalSeconds { get; set; } = 10;
    
    /// <summary>
    /// Seconds to wait for a connection from the pool (default: 30)
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    
    // === Batch Operation Settings ===
    
    /// <summary>
    /// Number of records to process before updating progress UI (default: 33)
    /// </summary>
    public int ProgressUpdateInterval { get; set; } = BatchSize;
    
    /// <summary>
    /// Maximum batch size for bulk insert operations (default: 1000)
    /// </summary>
    public int BulkInsertBatchSize { get; set; } = 1000;
    
    /// <summary>
    /// Maximum batch size for COPY operations (default: 5000)
    /// </summary>
    public int CopyBatchSize { get; set; } = 5000;
    
    /// <summary>
    /// Number of images to process per scanning batch (default: 100)
    /// </summary>
    public int ScanningBatchSize { get; set; } = 100;
    
    // === Retry Settings ===
    
    /// <summary>
    /// Maximum number of connection retry attempts (default: 3)
    /// </summary>
    public int MaxConnectionRetries { get; set; } = 3;
    
    /// <summary>
    /// Delay between connection retry attempts in milliseconds (default: 2000)
    /// </summary>
    public int ConnectionRetryDelayMs { get; set; } = 2000;
    
    // === Search Settings ===
    
    /// <summary>
    /// Default page size for paginated queries (default: 100)
    /// </summary>
    public int DefaultPageSize { get; set; } = 100;
    
    /// <summary>
    /// Maximum page size allowed (default: 1000)
    /// </summary>
    public int MaxPageSize { get; set; } = 1000;
    
    /// <summary>
    /// Default similarity threshold for vector search (default: 0.7)
    /// </summary>
    public float DefaultSimilarityThreshold { get; set; } = 0.7f;
    
    // === Timeout Settings ===
    
    /// <summary>
    /// Command timeout in seconds for normal queries (default: 30)
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Command timeout in seconds for long-running operations (default: 300)
    /// </summary>
    public int LongRunningCommandTimeoutSeconds { get; set; } = 300;
}
