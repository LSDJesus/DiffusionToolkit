using Dapper;
using Diffusion.Database.PostgreSQL.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL DataStore methods for embedding queue management
/// </summary>
public partial class PostgreSQLDataStore
{
    #region Queue Operations
    
    /// <summary>
    /// Add images from a folder to the embedding queue (non-recursive)
    /// </summary>
    public async Task<int> QueueFolderForEmbeddingAsync(
        int folderId, 
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            INSERT INTO embedding_queue (image_id, folder_id, priority, status, queued_at)
            SELECT id, folder_id, @priority, 'pending', NOW()
            FROM image
            WHERE folder_id = @folderId
              AND (prompt_embedding IS NULL OR image_embedding IS NULL)
              AND NOT EXISTS (
                  SELECT 1 FROM embedding_queue 
                  WHERE embedding_queue.image_id = image.id 
                    AND status IN ('pending', 'processing')
              )
            ON CONFLICT (image_id) WHERE status IN ('pending', 'processing') DO NOTHING;";
        
        await using var conn = await OpenConnectionAsync();
        var count = await conn.ExecuteAsync(sql, new { folderId, priority });
        return count;
    }
    
    /// <summary>
    /// Add images from a folder and subfolders to the embedding queue (recursive)
    /// </summary>
    public async Task<int> QueueFolderRecursiveForEmbeddingAsync(
        int folderId,
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            WITH RECURSIVE folder_tree AS (
                SELECT id FROM folder WHERE id = @folderId
                UNION ALL
                SELECT f.id 
                FROM folder f
                INNER JOIN folder_tree ft ON f.parent_id = ft.id
            )
            INSERT INTO embedding_queue (image_id, folder_id, priority, status, queued_at)
            SELECT i.id, i.folder_id, @priority, 'pending', NOW()
            FROM image i
            INNER JOIN folder_tree ft ON i.folder_id = ft.id
            WHERE (i.prompt_embedding IS NULL OR i.image_embedding IS NULL)
              AND NOT EXISTS (
                  SELECT 1 FROM embedding_queue eq
                  WHERE eq.image_id = i.id 
                    AND eq.status IN ('pending', 'processing')
              )
            ON CONFLICT (image_id) WHERE status IN ('pending', 'processing') DO NOTHING;";
        
        await using var conn = await OpenConnectionAsync();
        var count = await conn.ExecuteAsync(sql, new { folderId, priority });
        return count;
    }
    
    /// <summary>
    /// Add specific images to the embedding queue
    /// </summary>
    public async Task<int> QueueImagesForEmbeddingAsync(
        IEnumerable<int> imageIds,
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            INSERT INTO embedding_queue (image_id, folder_id, priority, status, queued_at)
            SELECT id, folder_id, @priority, 'pending', NOW()
            FROM image
            WHERE id = ANY(@imageIds)
              AND (prompt_embedding IS NULL OR image_embedding IS NULL)
              AND NOT EXISTS (
                  SELECT 1 FROM embedding_queue 
                  WHERE embedding_queue.image_id = image.id 
                    AND status IN ('pending', 'processing')
              )
            ON CONFLICT (image_id) WHERE status IN ('pending', 'processing') DO NOTHING;";
        
        await using var conn = await OpenConnectionAsync();
        var count = await conn.ExecuteAsync(sql, new { imageIds = imageIds.ToArray(), priority });
        return count;
    }
    
    /// <summary>
    /// Get next batch of images from queue (priority-ordered)
    /// Marks them as 'processing'
    /// </summary>
    public async Task<List<EmbeddingQueueItem>> GetNextEmbeddingBatchAsync(
        int batchSize = 32,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM get_next_embedding_batch(@batchSize);";
        
        await using var conn = await OpenConnectionAsync();
        var results = await conn.QueryAsync<EmbeddingQueueItem>(sql, new { batchSize });
        return results.ToList();
    }
    
    /// <summary>
    /// Mark queue item as completed
    /// </summary>
    public async Task CompleteEmbeddingQueueItemAsync(
        int queueItemId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT complete_embedding_queue_item(@queueItemId);";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql, new { queueItemId });
    }
    
    /// <summary>
    /// Mark queue item as failed
    /// </summary>
    public async Task FailEmbeddingQueueItemAsync(
        int queueItemId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT fail_embedding_queue_item(@queueItemId, @errorMessage);";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql, new { queueItemId, errorMessage });
    }
    
    /// <summary>
    /// Clear completed and failed items from queue
    /// </summary>
    public async Task ClearCompletedQueueItemsAsync(CancellationToken cancellationToken = default)
    {
        var sql = "DELETE FROM embedding_queue WHERE status IN ('completed', 'failed');";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql);
    }
    
    /// <summary>
    /// Clear entire queue (all items)
    /// </summary>
    public async Task ClearAllQueueItemsAsync(CancellationToken cancellationToken = default)
    {
        var sql = "DELETE FROM embedding_queue;";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql);
    }
    
    /// <summary>
    /// Retry failed queue items (resets to pending)
    /// </summary>
    public async Task<int> RetryFailedEmbeddingsAsync(CancellationToken cancellationToken = default)
    {
        var sql = "SELECT retry_failed_embeddings();";
        
        await using var conn = await OpenConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>(sql);
        return count;
    }
    
    /// <summary>
    /// Get queue statistics
    /// </summary>
    public async Task<EmbeddingQueueStatistics> GetQueueStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var sql = @"
            SELECT 
                COUNT(*) FILTER (WHERE status = 'pending') AS TotalPending,
                COUNT(*) FILTER (WHERE status = 'processing') AS TotalProcessing,
                COUNT(*) FILTER (WHERE status = 'completed') AS TotalCompleted,
                COUNT(*) FILTER (WHERE status = 'failed') AS TotalFailed,
                COUNT(*) FILTER (WHERE status = 'pending' AND priority > 50) AS HighPriorityCount,
                MIN(queued_at) AS OldestQueuedAt,
                MAX(queued_at) AS NewestQueuedAt
            FROM embedding_queue;";
        
        await using var conn = await OpenConnectionAsync();
        var stats = await conn.QuerySingleAsync<EmbeddingQueueStatistics>(sql);
        return stats;
    }
    
    /// <summary>
    /// Get image by ID (for queue processing)
    /// </summary>
    public async Task<Image?> GetImageByIdAsync(int imageId, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM image WHERE id = @imageId;";
        
        await using var conn = await OpenConnectionAsync();
        var image = await conn.QueryFirstOrDefaultAsync<Image>(sql, new { imageId });
        return image;
    }
    
    #endregion
    
    #region Worker State Operations
    
    /// <summary>
    /// Get worker state
    /// </summary>
    public async Task<EmbeddingWorkerState> GetWorkerStateAsync(CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM embedding_worker_state WHERE id = 1;";
        
        await using var conn = await OpenConnectionAsync();
        var state = await conn.QuerySingleAsync<EmbeddingWorkerState>(sql);
        return state;
    }
    
    /// <summary>
    /// Update worker state
    /// </summary>
    public async Task UpdateWorkerStateAsync(
        string status,
        bool? modelsLoaded = null,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            UPDATE embedding_worker_state
            SET status = @status,
                models_loaded = COALESCE(@modelsLoaded, models_loaded),
                started_at = CASE WHEN @status = 'running' AND status != 'running' THEN NOW() ELSE started_at END,
                paused_at = CASE WHEN @status = 'paused' THEN NOW() ELSE paused_at END,
                stopped_at = CASE WHEN @status = 'stopped' THEN NOW() ELSE stopped_at END
            WHERE id = 1;";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql, new { status, modelsLoaded });
    }
    
    /// <summary>
    /// Increment processed/failed counters
    /// </summary>
    public async Task IncrementWorkerCountersAsync(
        int processedCount = 0,
        int failedCount = 0,
        string? lastError = null,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            UPDATE embedding_worker_state
            SET total_processed = total_processed + @processedCount,
                total_failed = total_failed + @failedCount,
                last_error = COALESCE(@lastError, last_error),
                last_error_at = CASE WHEN @lastError IS NOT NULL THEN NOW() ELSE last_error_at END
            WHERE id = 1;";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql, new { processedCount, failedCount, lastError });
    }
    
    /// <summary>
    /// Reset worker counters (for new session)
    /// </summary>
    public async Task ResetWorkerCountersAsync(CancellationToken cancellationToken = default)
    {
        var sql = @"
            UPDATE embedding_worker_state
            SET total_processed = 0,
                total_failed = 0,
                last_error = NULL,
                last_error_at = NULL
            WHERE id = 1;";
        
        await using var conn = await OpenConnectionAsync();
        await conn.ExecuteAsync(sql);
    }
    
    #endregion
}
