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
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync();
        
        for (int i = 0; i < faceList.Count; i++)
        {
            var face = faceList[i];
            var landmarksJson = face.Landmarks != null 
                ? System.Text.Json.JsonSerializer.Serialize(face.Landmarks)
                : null;

            var embeddingStr = face.ArcFaceEmbedding != null 
                ? "[" + string.Join(",", face.ArcFaceEmbedding.Select(f => f.ToString("G9"))) + "]"
                : null;

            var sql = @"
                INSERT INTO face_detection (
                    image_id, face_index, x, y, width, height,
                    confidence, quality_score, sharpness_score,
                    pose_yaw, pose_pitch, pose_roll,
                    face_crop, crop_width, crop_height,
                    landmarks, arcface_embedding, has_embedding,
                    detection_model, detected_date
                ) VALUES (
                    @imageId, @faceIndex, @x, @y, @width, @height,
                    @confidence, @qualityScore, @sharpnessScore,
                    @poseYaw, @posePitch, @poseRoll,
                    @faceCrop, @cropWidth, @cropHeight,
                    @landmarks::jsonb, @embedding::vector, @hasEmbedding,
                    @detectionModel, NOW()
                )
                ON CONFLICT (image_id, face_index) DO UPDATE SET
                    x = EXCLUDED.x,
                    y = EXCLUDED.y,
                    width = EXCLUDED.width,
                    height = EXCLUDED.height,
                    confidence = EXCLUDED.confidence,
                    quality_score = EXCLUDED.quality_score,
                    sharpness_score = EXCLUDED.sharpness_score,
                    pose_yaw = EXCLUDED.pose_yaw,
                    pose_pitch = EXCLUDED.pose_pitch,
                    pose_roll = EXCLUDED.pose_roll,
                    face_crop = EXCLUDED.face_crop,
                    crop_width = EXCLUDED.crop_width,
                    crop_height = EXCLUDED.crop_height,
                    landmarks = EXCLUDED.landmarks,
                    arcface_embedding = EXCLUDED.arcface_embedding,
                    has_embedding = EXCLUDED.has_embedding,
                    detection_model = EXCLUDED.detection_model,
                    detected_date = EXCLUDED.detected_date";

            await connection.ExecuteAsync(sql, new { 
                imageId, 
                faceIndex = i,
                x = face.X,
                y = face.Y,
                width = face.Width,
                height = face.Height,
                faceCrop = face.FaceCrop,
                cropWidth = face.CropWidth,
                cropHeight = face.CropHeight,
                embedding = embeddingStr,
                hasEmbedding = face.ArcFaceEmbedding != null,
                detectionModel = face.DetectionModel,
                confidence = face.Confidence,
                qualityScore = face.QualityScore,
                sharpnessScore = face.SharpnessScore,
                poseYaw = face.PoseYaw,
                posePitch = face.PosePitch,
                poseRoll = face.PoseRoll,
                landmarks = landmarksJson
            });
        }
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
    /// Get face with embedding for similarity search
    /// </summary>
    public async Task<FaceDetectionEntity?> GetFaceWithEmbedding(int faceId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, image_id, bbox_x, bbox_y, bbox_width, bbox_height,
                   arcface_embedding, detection_model, confidence, quality_score
            FROM {Table("face_detection")}
            WHERE id = @faceId";
        
        var face = await connection.QueryFirstOrDefaultAsync<FaceDetectionEntity>(sql, new { faceId });
        
        // Parse the vector embedding from PostgreSQL format
        if (face != null && face.ArcFaceEmbedding == null)
        {
            var embeddingStr = await connection.ExecuteScalarAsync<string>(
                $"SELECT arcface_embedding::text FROM {Table("face_detection")} WHERE id = @faceId",
                new { faceId });
            
            if (!string.IsNullOrEmpty(embeddingStr))
            {
                // Parse "[0.1,0.2,...]" format
                embeddingStr = embeddingStr.Trim('[', ']');
                face.ArcFaceEmbedding = embeddingStr.Split(',').Select(float.Parse).ToArray();
            }
        }
        
        return face;
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
    public async Task<List<FaceGroupEntity>> GetAllClusters()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, label, representative_face_ids, face_count, 
                   avg_quality_score, avg_confidence, is_manual, notes
            FROM {Table("face_cluster")}
            ORDER BY face_count DESC";
        
        var result = await connection.QueryAsync<FaceGroupEntity>(sql);
        return result.ToList();
    }

    /// <summary>
    /// Remove face from cluster
    /// </summary>
    public async Task RemoveFaceFromCluster(int faceId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        // Get current cluster before removing
        var clusterId = await connection.ExecuteScalarAsync<int?>(
            $"SELECT face_cluster_id FROM {Table("face_detection")} WHERE id = @faceId",
            new { faceId });
        
        // Remove from cluster
        await connection.ExecuteAsync(
            $"UPDATE {Table("face_detection")} SET face_cluster_id = NULL, character_label = NULL WHERE id = @faceId",
            new { faceId });
        
        // Update cluster stats if it had a cluster
        if (clusterId.HasValue)
        {
            await UpdateClusterStats(clusterId.Value);
        }
    }

    /// <summary>
    /// Get all faces in a cluster
    /// </summary>
    public async Task<List<FaceDetectionEntity>> GetFacesInCluster(int clusterId, int limit = 500)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, image_id, bbox_x, bbox_y, bbox_width, bbox_height,
                   face_crop, crop_width, crop_height,
                   detection_model, confidence, quality_score, sharpness_score,
                   pose_yaw, pose_pitch, pose_roll,
                   face_cluster_id, character_label, manual_label
            FROM {Table("face_detection")}
            WHERE face_cluster_id = @clusterId
            ORDER BY quality_score DESC, confidence DESC
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
    public async Task<FaceGroupEntity?> GetFaceCluster(int clusterId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = $@"
            SELECT id, label, representative_face_ids, face_count,
                   avg_quality_score, avg_confidence, is_manual, notes
            FROM {Table("face_cluster")}
            WHERE id = @clusterId";
        
        return await connection.QueryFirstOrDefaultAsync<FaceGroupEntity>(sql, new { clusterId });
    }

    #endregion

    #region Face Groups (V5 Schema)

    /// <summary>
    /// Create a new face group
    /// </summary>
    public async Task<int> CreateFaceGroupAsync(string? name = null, int? representativeFaceId = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            INSERT INTO face_group (name, representative_face_id, created_date)
            VALUES (@name, @representativeFaceId, NOW())
            RETURNING id";
        
        return await connection.ExecuteScalarAsync<int>(sql, new { name, representativeFaceId });
    }

    /// <summary>
    /// Update face group name
    /// </summary>
    public async Task UpdateFaceGroupNameAsync(int groupId, string name)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            UPDATE face_group 
            SET name = @name, modified_date = NOW()
            WHERE id = @groupId";
        
        await connection.ExecuteAsync(sql, new { groupId, name });
    }

    /// <summary>
    /// Get face group by ID
    /// </summary>
    public async Task<FaceGroupInfo?> GetFaceGroupAsync(int groupId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            SELECT id, name, representative_face_id as RepresentativeFaceId, 
                   face_count as FaceCount, avg_confidence as AvgConfidence, 
                   avg_quality_score as AvgQualityScore, thumbnail,
                   is_manual_group as IsManualGroup, cluster_cohesion as ClusterCohesion,
                   notes, created_date as CreatedDate, modified_date as ModifiedDate
            FROM face_group 
            WHERE id = @groupId";
        
        return await connection.QuerySingleOrDefaultAsync<FaceGroupInfo>(sql, new { groupId });
    }

    /// <summary>
    /// Get all face groups
    /// </summary>
    public async Task<List<FaceGroupInfo>> GetAllFaceGroupsAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            SELECT id, name, representative_face_id as RepresentativeFaceId,
                   face_count as FaceCount, avg_confidence as AvgConfidence,
                   avg_quality_score as AvgQualityScore, thumbnail,
                   is_manual_group as IsManualGroup, cluster_cohesion as ClusterCohesion,
                   notes, created_date as CreatedDate, modified_date as ModifiedDate
            FROM face_group 
            ORDER BY face_count DESC, name";
        
        var groups = await connection.QueryAsync<FaceGroupInfo>(sql);
        return groups.ToList();
    }

    /// <summary>
    /// Add face to group
    /// </summary>
    public async Task AddFaceToGroupAsync(int faceId, int groupId, float similarityScore = 0.0f)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            INSERT INTO face_group_member (face_group_id, face_detection_id, similarity_score, added_date)
            VALUES (@groupId, @faceId, @similarityScore, NOW())
            ON CONFLICT (face_detection_id) DO UPDATE 
            SET face_group_id = EXCLUDED.face_group_id,
                similarity_score = EXCLUDED.similarity_score,
                added_date = EXCLUDED.added_date;
            
            UPDATE face_detection 
            SET face_group_id = @groupId 
            WHERE id = @faceId";
        
        await connection.ExecuteAsync(sql, new { faceId, groupId, similarityScore });
    }

    /// <summary>
    /// Remove face from its group
    /// </summary>
    public async Task RemoveFaceFromGroupAsync(int faceId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            DELETE FROM face_group_member WHERE face_detection_id = @faceId;
            UPDATE face_detection SET face_group_id = NULL WHERE id = @faceId";
        
        await connection.ExecuteAsync(sql, new { faceId });
    }

    /// <summary>
    /// Get all faces in a group with image metadata
    /// </summary>
    public async Task<List<FaceWithImageInfo>> GetFacesInGroupAsync(int groupId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            SELECT 
                fd.id, fd.image_id as ImageId, fd.face_index as FaceIndex,
                fd.x, fd.y, fd.width, fd.height,
                fd.confidence, fd.quality_score as QualityScore, fd.sharpness_score as SharpnessScore,
                fd.face_crop as FaceCrop, fd.crop_width as CropWidth, fd.crop_height as CropHeight,
                fd.has_embedding as HasEmbedding, fd.face_group_id as FaceGroupId,
                i.path as ImagePath,
                i.file_name as ImageFileName,
                i.width as ImageWidth,
                i.height as ImageHeight,
                i.created_date as ImageCreatedDate,
                fgm.similarity_score as SimilarityScore
            FROM face_group_member fgm
            JOIN face_detection fd ON fgm.face_detection_id = fd.id
            JOIN image i ON fd.image_id = i.id
            WHERE fgm.face_group_id = @groupId
            ORDER BY fgm.similarity_score DESC, fd.quality_score DESC";
        
        var faces = await connection.QueryAsync<FaceWithImageInfo>(sql, new { groupId });
        return faces.ToList();
    }

    /// <summary>
    /// Delete a face group (faces remain, just unassigned)
    /// </summary>
    public async Task DeleteFaceGroupAsync(int groupId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            UPDATE face_detection SET face_group_id = NULL WHERE face_group_id = @groupId;
            DELETE FROM face_group WHERE id = @groupId";
        
        await connection.ExecuteAsync(sql, new { groupId });
    }

    /// <summary>
    /// Find similar faces using pgvector cosine similarity
    /// </summary>
    public async Task<List<SimilarFaceResult>> FindSimilarFacesAsync(
        int queryFaceId, 
        float similarityThreshold = 0.6f, 
        int maxResults = 50)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            SELECT 
                fd.id as FaceId,
                fd.image_id as ImageId,
                1 - (qfd.arcface_embedding <=> fd.arcface_embedding) AS similarity
            FROM 
                face_detection qfd
                CROSS JOIN face_detection fd
            WHERE 
                qfd.id = @queryFaceId
                AND fd.id != @queryFaceId
                AND fd.arcface_embedding IS NOT NULL
                AND qfd.arcface_embedding IS NOT NULL
                AND 1 - (qfd.arcface_embedding <=> fd.arcface_embedding) >= @similarityThreshold
            ORDER BY 
                similarity DESC
            LIMIT @maxResults";
        
        var results = await connection.QueryAsync<SimilarFaceResult>(
            sql, 
            new { queryFaceId, similarityThreshold, maxResults });
        
        return results.ToList();
    }

    /// <summary>
    /// Get faces that don't belong to any group yet
    /// </summary>
    public async Task<List<FaceDetectionEntity>> GetUngroupedFacesAsync(int limit = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            SELECT id, image_id as ImageId, face_index as FaceIndex,
                   x, y, width, height,
                   confidence, quality_score as QualityScore, sharpness_score as SharpnessScore,
                   face_crop as FaceCrop, crop_width as CropWidth, crop_height as CropHeight,
                   has_embedding as HasEmbedding, face_group_id as FaceGroupId
            FROM face_detection 
            WHERE face_group_id IS NULL 
              AND has_embedding = TRUE
            ORDER BY quality_score DESC, confidence DESC
            LIMIT @limit";
        
        var faces = await connection.QueryAsync<FaceDetectionEntity>(sql, new { limit });
        return faces.ToList();
    }

    /// <summary>
    /// Get statistics about face groups
    /// </summary>
    public async Task<FaceGroupStats> GetFaceGroupStatsAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        
        var sql = @"
            SELECT 
                COUNT(*) as TotalGroups,
                COALESCE(SUM(face_count), 0) as TotalGroupedFaces,
                COALESCE(AVG(face_count), 0) as AvgFacesPerGroup,
                COALESCE(MAX(face_count), 0) as LargestGroupSize,
                (SELECT COUNT(*) FROM face_detection WHERE face_group_id IS NULL AND has_embedding = TRUE) as UngroupedFaces
            FROM face_group";
        
        return await connection.QuerySingleAsync<FaceGroupStats>(sql);
    }

    #endregion
}

// =============================================================================
// Supporting Classes
// =============================================================================

public class FaceGroupInfo
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int? RepresentativeFaceId { get; set; }
    public int FaceCount { get; set; }
    public float AvgConfidence { get; set; }
    public float AvgQualityScore { get; set; }
    public byte[]? Thumbnail { get; set; }
    public bool IsManualGroup { get; set; }
    public float? ClusterCohesion { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

public class FaceWithImageInfo
{
    public int Id { get; set; }
    public int ImageId { get; set; }
    public int FaceIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public float Confidence { get; set; }
    public float QualityScore { get; set; }
    public float SharpnessScore { get; set; }
    public byte[]? FaceCrop { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }
    public bool HasEmbedding { get; set; }
    public int? FaceGroupId { get; set; }
    public string ImagePath { get; set; } = "";
    public string ImageFileName { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public DateTime ImageCreatedDate { get; set; }
    public float SimilarityScore { get; set; }
}

public class SimilarFaceResult
{
    public int FaceId { get; set; }
    public int ImageId { get; set; }
    public float Similarity { get; set; }
}

public class FaceGroupStats
{
    public int TotalGroups { get; set; }
    public int TotalGroupedFaces { get; set; }
    public float AvgFacesPerGroup { get; set; }
    public int LargestGroupSize { get; set; }
    public int UngroupedFaces { get; set; }
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