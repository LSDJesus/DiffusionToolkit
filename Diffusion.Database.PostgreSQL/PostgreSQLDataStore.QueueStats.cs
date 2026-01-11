using Dapper;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Queue statistics for all processing queues
/// </summary>
public class QueueStatistics
{
    // Tagging queue
    public int TaggingNotQueued { get; set; }      // NULL count
    public int TaggingPending { get; set; }         // TRUE count
    public int TaggingCompleted { get; set; }       // FALSE count
    
    // Captioning queue
    public int CaptioningNotQueued { get; set; }
    public int CaptioningPending { get; set; }
    public int CaptioningCompleted { get; set; }
    
    // Embedding queue (overall)
    public int EmbeddingNotQueued { get; set; }
    public int EmbeddingPending { get; set; }
    public int EmbeddingCompleted { get; set; }
    
    // Individual embedding types
    public int BgeNotQueued { get; set; }
    public int BgePending { get; set; }
    public int BgeCompleted { get; set; }
    
    public int ClipLNotQueued { get; set; }
    public int ClipLPending { get; set; }
    public int ClipLCompleted { get; set; }
    
    public int ClipGNotQueued { get; set; }
    public int ClipGPending { get; set; }
    public int ClipGCompleted { get; set; }
    
    public int ClipVisionNotQueued { get; set; }
    public int ClipVisionPending { get; set; }
    public int ClipVisionCompleted { get; set; }
    
    // Face detection queue (overall)
    public int FaceDetectionNotQueued { get; set; }
    public int FaceDetectionPending { get; set; }
    public int FaceDetectionCompleted { get; set; }
    
    // Individual face detection steps
    public int FaceDetNotQueued { get; set; }
    public int FaceDetPending { get; set; }
    public int FaceDetCompleted { get; set; }
    
    public int FaceEmbNotQueued { get; set; }
    public int FaceEmbPending { get; set; }
    public int FaceEmbCompleted { get; set; }
    
    public int FaceClusterNotQueued { get; set; }
    public int FaceClusterPending { get; set; }
    public int FaceClusterCompleted { get; set; }
    
    // Computed properties for backward compatibility
    public int TotalPending => TaggingPending + CaptioningPending + EmbeddingPending + FaceDetectionPending;
    public int HighPriorityCount => 0; // Not currently tracked - placeholder for future priority queue
}

public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Get comprehensive queue statistics for all processing queues in a single query
    /// </summary>
    public async Task<QueueStatistics> GetQueueStatisticsAsync()
    {
        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
        
        var sql = $@"
            SELECT 
                -- Tagging
                COUNT(1) FILTER (WHERE needs_tagging IS NULL) AS TaggingNotQueued,
                COUNT(1) FILTER (WHERE needs_tagging = true) AS TaggingPending,
                COUNT(1) FILTER (WHERE needs_tagging = false) AS TaggingCompleted,
                
                -- Captioning
                COUNT(1) FILTER (WHERE needs_captioning IS NULL) AS CaptioningNotQueued,
                COUNT(1) FILTER (WHERE needs_captioning = true) AS CaptioningPending,
                COUNT(1) FILTER (WHERE needs_captioning = false) AS CaptioningCompleted,
                
                -- Embedding (overall)
                COUNT(1) FILTER (WHERE needs_embedding IS NULL) AS EmbeddingNotQueued,
                COUNT(1) FILTER (WHERE needs_embedding = true) AS EmbeddingPending,
                COUNT(1) FILTER (WHERE needs_embedding = false) AS EmbeddingCompleted,
                
                -- BGE embedding
                COUNT(1) FILTER (WHERE bge_embedding_status IS NULL) AS BgeNotQueued,
                COUNT(1) FILTER (WHERE bge_embedding_status = true) AS BgePending,
                COUNT(1) FILTER (WHERE bge_embedding_status = false) AS BgeCompleted,
                
                -- CLIP-L embedding
                COUNT(1) FILTER (WHERE clip_l_embedding_status IS NULL) AS ClipLNotQueued,
                COUNT(1) FILTER (WHERE clip_l_embedding_status = true) AS ClipLPending,
                COUNT(1) FILTER (WHERE clip_l_embedding_status = false) AS ClipLCompleted,
                
                -- CLIP-G embedding
                COUNT(1) FILTER (WHERE clip_g_embedding_status IS NULL) AS ClipGNotQueued,
                COUNT(1) FILTER (WHERE clip_g_embedding_status = true) AS ClipGPending,
                COUNT(1) FILTER (WHERE clip_g_embedding_status = false) AS ClipGCompleted,
                
                -- CLIP Vision embedding
                COUNT(1) FILTER (WHERE clip_vision_embedding_status IS NULL) AS ClipVisionNotQueued,
                COUNT(1) FILTER (WHERE clip_vision_embedding_status = true) AS ClipVisionPending,
                COUNT(1) FILTER (WHERE clip_vision_embedding_status = false) AS ClipVisionCompleted,
                
                -- Face detection (overall)
                COUNT(1) FILTER (WHERE needs_face_detection IS NULL) AS FaceDetectionNotQueued,
                COUNT(1) FILTER (WHERE needs_face_detection = true) AS FaceDetectionPending,
                COUNT(1) FILTER (WHERE needs_face_detection = false) AS FaceDetectionCompleted,
                
                -- Face detection step
                COUNT(1) FILTER (WHERE face_detection_status IS NULL) AS FaceDetNotQueued,
                COUNT(1) FILTER (WHERE face_detection_status = true) AS FaceDetPending,
                COUNT(1) FILTER (WHERE face_detection_status = false) AS FaceDetCompleted,
                
                -- Face embedding step
                COUNT(1) FILTER (WHERE face_embedding_status IS NULL) AS FaceEmbNotQueued,
                COUNT(1) FILTER (WHERE face_embedding_status = true) AS FaceEmbPending,
                COUNT(1) FILTER (WHERE face_embedding_status = false) AS FaceEmbCompleted,
                
                -- Face clustering step
                COUNT(1) FILTER (WHERE face_clustering_status IS NULL) AS FaceClusterNotQueued,
                COUNT(1) FILTER (WHERE face_clustering_status = true) AS FaceClusterPending,
                COUNT(1) FILTER (WHERE face_clustering_status = false) AS FaceClusterCompleted
                
            FROM {Table("image")}
            WHERE for_deletion = false
        ";
        
        return await connection.QuerySingleAsync<QueueStatistics>(sql).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Get just the pending counts for all queues (fast query for UI updates)
    /// </summary>
    public async Task<(int Tagging, int Captioning, int Embedding, int FaceDetection)> GetPendingCountsAsync()
    {
        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
        
        var sql = $@"
            SELECT 
                COUNT(1) FILTER (WHERE needs_tagging = true) AS Tagging,
                COUNT(1) FILTER (WHERE needs_captioning = true) AS Captioning,
                COUNT(1) FILTER (WHERE needs_embedding = true) AS Embedding,
                COUNT(1) FILTER (WHERE needs_face_detection = true) AS FaceDetection
            FROM {Table("image")}
            WHERE for_deletion = false
        ";
        
        var result = await connection.QuerySingleAsync<dynamic>(sql).ConfigureAwait(false);
        return ((int)result.tagging, (int)result.captioning, (int)result.embedding, (int)result.facedetection);
    }
}
