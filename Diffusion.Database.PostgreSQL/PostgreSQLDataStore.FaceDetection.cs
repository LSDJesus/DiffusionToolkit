using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Common.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Face detection database operations
/// </summary>
public partial class PostgreSQLDataStore
{
    #region Face Detection Queue

    /// <summary>
    /// Set the needs_face_detection flag for a list of images
    /// </summary>
    public async Task SetNeedsFaceDetection(List<int> imageIds, bool needsFaceDetection)
    {
        if (imageIds.Count == 0) return;

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"UPDATE {Table("image")} SET needs_face_detection = @needsFaceDetection WHERE id = ANY(@ids)";
        await connection.ExecuteAsync(sql, new { needsFaceDetection, ids = imageIds.ToArray() });
    }

    /// <summary>
    /// Get images that need face detection (batch for processing)
    /// </summary>
    public async Task<List<int>> GetImagesNeedingFaceDetection(int batchSize = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id FROM {Table("image")} 
            WHERE needs_face_detection = true AND for_deletion = false
            ORDER BY id
            LIMIT @batchSize";
        
        var result = await connection.QueryAsync<int>(sql, new { batchSize });
        return result.ToList();
    }

    /// <summary>
    /// Count images that need face detection
    /// </summary>
    public async Task<int> CountImagesNeedingFaceDetection()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"SELECT COUNT(1) FROM {Table("image")} WHERE needs_face_detection = true AND for_deletion = false";
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    /// <summary>
    /// Queue folder images for face detection
    /// </summary>
    public async Task<int> QueueFolderForFaceDetection(int folderId, bool includeSubfolders)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        string sql;
        if (includeSubfolders)
        {
            sql = $@"
                UPDATE {Table("image")} i
                SET needs_face_detection = true
                FROM {Table("folder")} f
                WHERE i.folder_id = f.id
                  AND f.path LIKE (SELECT path || '%' FROM {Table("folder")} WHERE id = @folderId)
                  AND i.for_deletion = false
                  AND i.face_count = 0";
        }
        else
        {
            sql = $@"
                UPDATE {Table("image")}
                SET needs_face_detection = true
                WHERE folder_id = @folderId
                  AND for_deletion = false
                  AND face_count = 0";
        }

        return await connection.ExecuteAsync(sql, new { folderId });
    }

    /// <summary>
    /// Clear face detection queue
    /// </summary>
    public async Task ClearFaceDetectionQueue()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync($"UPDATE {Table("image")} SET needs_face_detection = false WHERE needs_face_detection = true");
    }

    #endregion

    #region Face Detection Storage

    /// <summary>
    /// Store multiple detected faces for an image
    /// </summary>
    public async Task StoreFaceDetections(int imageId, IEnumerable<FaceDetectionResult> faces)
    {
        var faceList = faces.ToList();
        if (!faceList.Any())
        {
            await UpdateImageFaceInfo(imageId, 0, null, null);
            return;
        }

        foreach (var face in faceList)
        {
            var landmarksJson = face.Landmarks != null 
                ? System.Text.Json.JsonSerializer.Serialize(face.Landmarks)
                : null;

            await StoreFaceDetectionAsync(
                imageId,
                face.X, face.Y, face.Width, face.Height,
                face.FaceCrop, face.Width, face.Height, // Assuming crop matches bbox for now
                face.ArcFaceEmbedding,
                "yolo11-face",
                face.Confidence, face.QualityScore, face.SharpnessScore,
                face.PoseYaw, face.PosePitch, face.PoseRoll,
                landmarksJson
            );
        }

        await UpdateImageFaceInfo(imageId, faceList.Count, null, null);
    }

    /// <summary>
    /// Store a detected face
    /// </summary>
    public async Task<int> StoreFaceDetectionAsync(
        int imageId,
        int bboxX, int bboxY, int bboxWidth, int bboxHeight,
        byte[]? faceCrop, int cropWidth, int cropHeight,
        float[]? arcfaceEmbedding,
        string detectionModel,
        float confidence, float qualityScore, float sharpnessScore,
        float poseYaw, float posePitch, float poseRoll,
        string? landmarks = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            INSERT INTO {Table("face_detection")} (
                image_id, bbox_x, bbox_y, bbox_width, bbox_height,
                face_crop, crop_width, crop_height,
                arcface_embedding, detection_model,
                confidence, quality_score, sharpness_score,
                pose_yaw, pose_pitch, pose_roll, landmarks
            ) VALUES (
                @imageId, @bboxX, @bboxY, @bboxWidth, @bboxHeight,
                @faceCrop, @cropWidth, @cropHeight,
                @arcfaceEmbedding::vector, @detectionModel,
                @confidence, @qualityScore, @sharpnessScore,
                @poseYaw, @posePitch, @poseRoll, @landmarks::jsonb
            ) RETURNING id";
        
        var embeddingStr = arcfaceEmbedding != null 
            ? "[" + string.Join(",", arcfaceEmbedding.Select(f => f.ToString("G9"))) + "]"
            : null;
        
        return await connection.ExecuteScalarAsync<int>(sql, new { 
            imageId, bboxX, bboxY, bboxWidth, bboxHeight,
            faceCrop, cropWidth, cropHeight,
            arcfaceEmbedding = embeddingStr, detectionModel,
            confidence, qualityScore, sharpnessScore,
            poseYaw, posePitch, poseRoll, landmarks
        });
    }

    /// <summary>
    /// Update image face count and characters
    /// </summary>
    public async Task UpdateImageFaceInfo(int imageId, int faceCount, string? primaryCharacter, string[]? charactersDetected)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            UPDATE {Table("image")} 
            SET face_count = @faceCount, 
                primary_character = @primaryCharacter,
                characters_detected = @charactersDetected,
                needs_face_detection = false
            WHERE id = @imageId";
        
        await connection.ExecuteAsync(sql, new { imageId, faceCount, primaryCharacter, charactersDetected });
    }

    /// <summary>
    /// Get faces for an image
    /// </summary>
    public async Task<List<FaceDetectionEntity>> GetFacesForImage(int imageId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, image_id, bbox_x, bbox_y, bbox_width, bbox_height,
                   face_crop, crop_width, crop_height,
                   detection_model, confidence, quality_score, sharpness_score,
                   pose_yaw, pose_pitch, pose_roll,
                   face_cluster_id, character_label, manual_label
            FROM {Table("face_detection")}
            WHERE image_id = @imageId
            ORDER BY confidence DESC";
        
        var result = await connection.QueryAsync<FaceDetectionEntity>(sql, new { imageId });
        return result.ToList();
    }

    /// <summary>
    /// Get face crop image
    /// </summary>
    public async Task<byte[]?> GetFaceCrop(int faceId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $"SELECT face_crop FROM {Table("face_detection")} WHERE id = @faceId";
        return await connection.ExecuteScalarAsync<byte[]?>(sql, new { faceId });
    }

    /// <summary>
    /// Find similar faces using vector search
    /// </summary>
    public async Task<List<(int faceId, int imageId, float similarity)>> FindSimilarFaces(
        float[] embedding, 
        float threshold = 0.6f, 
        int limit = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var embeddingStr = "[" + string.Join(",", embedding.Select(f => f.ToString("G9"))) + "]";
        
        var sql = $@"
            SELECT fd.id, fd.image_id, 
                   1 - (fd.arcface_embedding <=> @embedding::vector) as similarity
            FROM {Table("face_detection")} fd
            WHERE fd.arcface_embedding IS NOT NULL
              AND 1 - (fd.arcface_embedding <=> @embedding::vector) >= @threshold
            ORDER BY similarity DESC
            LIMIT @limit";
        
        var result = await connection.QueryAsync<(int id, int image_id, float similarity)>(sql, 
            new { embedding = embeddingStr, threshold, limit });
        
        return result.Select(r => (r.id, r.image_id, r.similarity)).ToList();
    }

    #endregion

    #region Face Clusters

    /// <summary>
    /// Create a new face cluster
    /// </summary>
    public async Task<int> CreateFaceClusterAsync(string? label = null, bool isManual = false)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            INSERT INTO {Table("face_cluster")} (label, is_manual, face_count)
            VALUES (@label, @isManual, 0)
            RETURNING id";
        
        return await connection.ExecuteScalarAsync<int>(sql, new { label, isManual });
    }

    /// <summary>
    /// Assign face to cluster
    /// </summary>
    public async Task AssignFaceToCluster(int faceId, int clusterId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            UPDATE {Table("face_detection")} 
            SET face_cluster_id = @clusterId 
            WHERE id = @faceId";
        
        await connection.ExecuteAsync(sql, new { faceId, clusterId });
        
        // Update cluster face count
        await UpdateClusterStats(clusterId);
    }

    /// <summary>
    /// Update cluster label
    /// </summary>
    public async Task UpdateClusterLabel(int clusterId, string label)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            UPDATE {Table("face_cluster")} 
            SET label = @label, updated_at = NOW()
            WHERE id = @clusterId";
        
        await connection.ExecuteAsync(sql, new { clusterId, label });
        
        // Update all faces in cluster
        var updateFacesSql = $@"
            UPDATE {Table("face_detection")} 
            SET character_label = @label 
            WHERE face_cluster_id = @clusterId";
        
        await connection.ExecuteAsync(updateFacesSql, new { clusterId, label });
    }

    /// <summary>
    /// Update cluster statistics
    /// </summary>
    public async Task UpdateClusterStats(int clusterId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            UPDATE {Table("face_cluster")} fc
            SET face_count = (SELECT COUNT(*) FROM {Table("face_detection")} WHERE face_cluster_id = @clusterId),
                avg_quality_score = (SELECT AVG(quality_score) FROM {Table("face_detection")} WHERE face_cluster_id = @clusterId),
                avg_confidence = (SELECT AVG(confidence) FROM {Table("face_detection")} WHERE face_cluster_id = @clusterId),
                updated_at = NOW()
            WHERE id = @clusterId";
        
        await connection.ExecuteAsync(sql, new { clusterId });
    }

    /// <summary>
    /// Get all clusters
    /// </summary>
    public async Task<List<FaceClusterEntity>> GetAllClusters()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, label, representative_face_ids, face_count, 
                   avg_quality_score, avg_confidence, is_manual, notes
            FROM {Table("face_cluster")}
            ORDER BY face_count DESC";
        
        var result = await connection.QueryAsync<FaceClusterEntity>(sql);
        return result.ToList();
    }

    /// <summary>
    /// Get faces in a cluster
    /// </summary>
    public async Task<List<FaceDetectionEntity>> GetFacesInCluster(int clusterId, int limit = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, image_id, bbox_x, bbox_y, bbox_width, bbox_height,
                   crop_width, crop_height,
                   detection_model, confidence, quality_score, sharpness_score,
                   pose_yaw, pose_pitch, pose_roll,
                   face_cluster_id, character_label, manual_label
            FROM {Table("face_detection")}
            WHERE face_cluster_id = @clusterId
            ORDER BY quality_score DESC
            LIMIT @limit";
        
        var result = await connection.QueryAsync<FaceDetectionEntity>(sql, new { clusterId, limit });
        return result.ToList();
    }

    /// <summary>
    /// Get unclustered faces
    /// </summary>
    public async Task<List<FaceDetectionEntity>> GetUnclusteredFaces(int limit = 1000)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, image_id, bbox_x, bbox_y, bbox_width, bbox_height,
                   crop_width, crop_height,
                   detection_model, confidence, quality_score, sharpness_score,
                   pose_yaw, pose_pitch, pose_roll,
                   face_cluster_id, character_label, manual_label
            FROM {Table("face_detection")}
            WHERE face_cluster_id IS NULL
            ORDER BY quality_score DESC
            LIMIT @limit";
        
        var result = await connection.QueryAsync<FaceDetectionEntity>(sql, new { limit });
        return result.ToList();
    }

    #endregion

    #region Face Statistics

    /// <summary>
    /// Get face detection statistics
    /// </summary>
    public async Task<FaceDetectionStats> GetFaceDetectionStats()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT 
                (SELECT COUNT(*) FROM {Table("face_detection")}) as total_faces,
                (SELECT COUNT(*) FROM {Table("face_cluster")}) as total_clusters,
                (SELECT COUNT(*) FROM {Table("face_cluster")} WHERE label IS NOT NULL) as labeled_clusters,
                (SELECT COUNT(*) FROM {Table("face_detection")} WHERE face_cluster_id IS NULL) as unclustered_faces,
                (SELECT COUNT(*) FROM {Table("image")} WHERE face_count > 0) as images_with_faces,
                (SELECT AVG(face_count) FROM {Table("image")} WHERE face_count > 0) as avg_faces_per_image";
        
        return await connection.QueryFirstAsync<FaceDetectionStats>(sql);
    }
    
    /// <summary>
    /// Update cluster label for a face
    /// </summary>
    public async Task UpdateFaceClusterLabel(int faceId, string label)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            UPDATE {Table("face_detection")}
            SET character_label = @label,
                manual_label = true
            WHERE id = @faceId";
        
        await connection.ExecuteAsync(sql, new { faceId, label });
    }

    /// <summary>
    /// Get cluster info by ID
    /// </summary>
    public async Task<FaceClusterEntity?> GetFaceCluster(int clusterId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, label, representative_face_ids, face_count,
                   avg_quality_score, avg_confidence, is_manual, notes
            FROM {Table("face_cluster")}
            WHERE id = @clusterId";
        
        return await connection.QueryFirstOrDefaultAsync<FaceClusterEntity>(sql, new { clusterId });
    }

    #endregion
}
// Supporting classes for face detection
public class FaceClusterInfo
{
    public int Id { get; set; }
    public string? Label { get; set; }
    public int[]? RepresentativeFaceIds { get; set; }
    public int FaceCount { get; set; }
    public float AvgQualityScore { get; set; }
    public float AvgConfidence { get; set; }
    public bool IsManual { get; set; }
    public string? Notes { get; set; }
}

public class FaceDetectionStats
{
    public int TotalFaces { get; set; }
    public int TotalClusters { get; set; }
    public int LabeledClusters { get; set; }
    public int UnclusteredFaces { get; set; }
    public int ImagesWithFaces { get; set; }
    public double AvgFacesPerImage { get; set; }
}