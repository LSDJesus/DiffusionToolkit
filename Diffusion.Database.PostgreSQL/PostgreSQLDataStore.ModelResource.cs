using Dapper;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Model resource operations for PostgreSQLDataStore
/// </summary>
public partial class PostgreSQLDataStore
{
    #region Model Folders

    /// <summary>
    /// Get all configured model folders
    /// </summary>
    public async Task<List<ModelFolder>> GetModelFoldersAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = "SELECT id, path, resource_type, recursive, enabled, last_scanned, created_at FROM model_folder ORDER BY path";
        var results = await connection.QueryAsync<ModelFolder>(sql);
        return results.ToList();
    }

    /// <summary>
    /// Add a new model folder configuration
    /// </summary>
    public async Task<int> InsertModelFolderAsync(ModelFolder folder)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            INSERT INTO model_folder (path, resource_type, recursive, enabled, created_at)
            VALUES (@Path, @ResourceType, @Recursive, @Enabled, NOW())
            ON CONFLICT (path) DO UPDATE SET
                resource_type = EXCLUDED.resource_type,
                recursive = EXCLUDED.recursive,
                enabled = EXCLUDED.enabled
            RETURNING id";
        return await connection.ExecuteScalarAsync<int>(sql, folder);
    }

    /// <summary>
    /// Insert or update a model folder configuration (alias for InsertModelFolderAsync which has UPSERT logic)
    /// </summary>
    public Task<int> UpsertModelFolderAsync(ModelFolder folder) => InsertModelFolderAsync(folder);

    /// <summary>
    /// Update last scanned timestamp for a folder
    /// </summary>
    public async Task UpdateModelFolderScannedAsync(int folderId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = "UPDATE model_folder SET last_scanned = NOW() WHERE id = @Id";
        await connection.ExecuteAsync(sql, new { Id = folderId });
    }

    /// <summary>
    /// Delete a model folder configuration
    /// </summary>
    public async Task DeleteModelFolderAsync(int folderId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = "DELETE FROM model_folder WHERE id = @Id";
        await connection.ExecuteAsync(sql, new { Id = folderId });
    }

    #endregion

    #region Model Resources

    /// <summary>
    /// Get a model resource by file path
    /// </summary>
    public async Task<ModelResource?> GetModelResourceByPathAsync(string filePath)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE file_path = @FilePath";
        return await connection.QueryFirstOrDefaultAsync<ModelResource>(sql, new { FilePath = filePath });
    }

    /// <summary>
    /// Get a model resource by hash (for Civitai lookup)
    /// </summary>
    public async Task<ModelResource?> GetModelResourceByHashAsync(string fileHash)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE file_hash = @FileHash";
        return await connection.QueryFirstOrDefaultAsync<ModelResource>(sql, new { FileHash = fileHash });
    }

    /// <summary>
    /// Find a model resource by name and type (for linking images)
    /// </summary>
    public async Task<ModelResource?> FindModelResourceByNameAsync(string name, string resourceType)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        // Try exact match first, then partial match
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE resource_type = @ResourceType
              AND (file_name = @Name OR file_name ILIKE @NamePattern)
              AND unavailable = FALSE
            ORDER BY 
                CASE WHEN file_name = @Name THEN 0 ELSE 1 END,
                created_at DESC
            LIMIT 1";
        return await connection.QueryFirstOrDefaultAsync<ModelResource>(sql, new 
        { 
            Name = name, 
            NamePattern = $"%{name}%",
            ResourceType = resourceType 
        });
    }

    /// <summary>
    /// Get all model resources
    /// </summary>
    public async Task<List<ModelResource>> GetAllModelResourcesAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            ORDER BY resource_type, file_name";
        var results = await connection.QueryAsync<ModelResource>(sql);
        return results.ToList();
    }

    /// <summary>
    /// Get model resources by type with optional pagination
    /// </summary>
    public async Task<List<ModelResource>> GetModelResourcesByTypeAsync(string resourceType, int limit = 0, int offset = 0)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE resource_type = @ResourceType AND unavailable = FALSE
            ORDER BY COALESCE(civitai_name, file_name)";
        
        if (limit > 0)
        {
            sql += " LIMIT @Limit OFFSET @Offset";
        }
        
        var results = await connection.QueryAsync<ModelResource>(sql, new { ResourceType = resourceType, Limit = limit, Offset = offset });
        return results.ToList();
    }

    /// <summary>
    /// Get model resources by folder path (models within a directory)
    /// </summary>
    public async Task<List<ModelResource>> GetModelResourcesByFolderAsync(string folderPath, int limit = 100, int offset = 0)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE file_path LIKE @FolderPattern AND unavailable = FALSE
            ORDER BY COALESCE(civitai_name, file_name)
            LIMIT @Limit OFFSET @Offset";
        
        var results = await connection.QueryAsync<ModelResource>(sql, new 
        { 
            FolderPattern = folderPath.TrimEnd('\\', '/') + "%", 
            Limit = limit, 
            Offset = offset 
        });
        return results.ToList();
    }

    /// <summary>
    /// Search model resources by name (filename or Civitai name)
    /// </summary>
    public async Task<List<ModelResource>> SearchModelResourcesAsync(string searchTerm, int limit = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE (file_name ILIKE @SearchPattern OR civitai_name ILIKE @SearchPattern)
              AND unavailable = FALSE
            ORDER BY 
                CASE WHEN file_name ILIKE @ExactPattern OR civitai_name ILIKE @ExactPattern THEN 0 ELSE 1 END,
                COALESCE(civitai_name, file_name)
            LIMIT @Limit";
        
        var results = await connection.QueryAsync<ModelResource>(sql, new 
        { 
            SearchPattern = $"%{searchTerm}%", 
            ExactPattern = searchTerm,
            Limit = limit 
        });
        return results.ToList();
    }

    /// <summary>
    /// Get model resources needing Civitai enrichment
    /// </summary>
    public async Task<List<ModelResource>> GetResourcesNeedingCivitaiAsync(int limit = 100)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE file_hash IS NOT NULL 
              AND civitai_fetched_at IS NULL
              AND unavailable = FALSE
            ORDER BY created_at
            LIMIT @Limit";
        var results = await connection.QueryAsync<ModelResource>(sql, new { Limit = limit });
        return results.ToList();
    }

    /// <summary>
    /// Get all model resources for header-first enrichment
    /// </summary>
    public async Task<List<ModelResource>> GetAllModelResourcesAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT id, file_path, file_name, file_hash, file_size, resource_type, base_model,
                   local_metadata, civitai_id, civitai_version_id, civitai_name, civitai_description,
                   civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                   civitai_author, civitai_default_weight, civitai_default_clip_weight, civitai_published_at,
                   civitai_cover_image_url, civitai_thumbnail, unavailable, scanned_at, civitai_fetched_at, created_at
            FROM model_resource
            WHERE unavailable = FALSE
            ORDER BY created_at";
        var results = await connection.QueryAsync<ModelResource>(sql);
        return results.ToList();
    }

    /// <summary>
    /// Insert a new model resource (including Civitai fields if populated from sidecar JSON)
    /// </summary>
    public async Task<int> InsertModelResourceAsync(ModelResource resource)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            INSERT INTO model_resource (
                file_path, file_name, file_hash, file_size, resource_type, base_model,
                local_metadata, has_preview_image, civitai_id, civitai_version_id, civitai_name, civitai_description,
                civitai_tags, civitai_nsfw, civitai_trained_words, civitai_base_model, civitai_metadata,
                civitai_fetched_at, scanned_at, created_at
            )
            VALUES (
                @FilePath, @FileName, @FileHash, @FileSize, @ResourceType, @BaseModel,
                @LocalMetadata::jsonb, @HasPreviewImage, @CivitaiId, @CivitaiVersionId, @CivitaiName, @CivitaiDescription,
                @CivitaiTags, @CivitaiNsfw, @CivitaiTrainedWords, @CivitaiBaseModel, @CivitaiMetadata::jsonb,
                @CivitaiFetchedAt, @ScannedAt, NOW()
            )
            ON CONFLICT (file_path) DO UPDATE SET
                file_hash = EXCLUDED.file_hash,
                file_size = EXCLUDED.file_size,
                resource_type = EXCLUDED.resource_type,
                base_model = COALESCE(EXCLUDED.base_model, model_resource.base_model),
                local_metadata = EXCLUDED.local_metadata,
                has_preview_image = EXCLUDED.has_preview_image OR model_resource.has_preview_image,
                civitai_id = COALESCE(EXCLUDED.civitai_id, model_resource.civitai_id),
                civitai_version_id = COALESCE(EXCLUDED.civitai_version_id, model_resource.civitai_version_id),
                civitai_name = COALESCE(EXCLUDED.civitai_name, model_resource.civitai_name),
                civitai_description = COALESCE(EXCLUDED.civitai_description, model_resource.civitai_description),
                civitai_trained_words = COALESCE(EXCLUDED.civitai_trained_words, model_resource.civitai_trained_words),
                civitai_base_model = COALESCE(EXCLUDED.civitai_base_model, model_resource.civitai_base_model),
                civitai_metadata = COALESCE(EXCLUDED.civitai_metadata, model_resource.civitai_metadata),
                civitai_fetched_at = COALESCE(EXCLUDED.civitai_fetched_at, model_resource.civitai_fetched_at),
                scanned_at = EXCLUDED.scanned_at,
                unavailable = FALSE
            RETURNING id";
        return await connection.ExecuteScalarAsync<int>(sql, resource);
    }

    /// <summary>
    /// Update a model resource
    /// </summary>
    public async Task UpdateModelResourceAsync(ModelResource resource)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            UPDATE model_resource SET
                file_hash = @FileHash,
                file_size = @FileSize,
                resource_type = @ResourceType,
                base_model = @BaseModel,
                local_metadata = @LocalMetadata::jsonb,
                has_preview_image = @HasPreviewImage,
                scanned_at = @ScannedAt,
                unavailable = @Unavailable
            WHERE id = @Id";
        await connection.ExecuteAsync(sql, resource);
    }

    /// <summary>
    /// Update Civitai metadata for a resource
    /// </summary>
    public async Task UpdateResourceCivitaiAsync(ModelResource resource)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            UPDATE model_resource SET
                civitai_id = @CivitaiId,
                civitai_version_id = @CivitaiVersionId,
                civitai_name = @CivitaiName,
                civitai_description = @CivitaiDescription,
                civitai_tags = @CivitaiTags,
                civitai_nsfw = @CivitaiNsfw,
                civitai_trained_words = @CivitaiTrainedWords,
                civitai_base_model = @CivitaiBaseModel,
                civitai_metadata = @CivitaiMetadata::jsonb,
                civitai_author = @CivitaiAuthor,
                civitai_cover_image_url = @CivitaiCoverImageUrl,
                civitai_thumbnail = @CivitaiThumbnail,
                civitai_default_weight = @CivitaiDefaultWeight,
                civitai_default_clip_weight = @CivitaiDefaultClipWeight,
                civitai_published_at = @CivitaiPublishedAt,
                civitai_fetched_at = NOW()
            WHERE id = @Id";
        await connection.ExecuteAsync(sql, resource);
    }

    /// <summary>
    /// Update availability status for a resource
    /// </summary>
    public async Task UpdateModelResourceAvailabilityAsync(int resourceId, bool unavailable)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = "UPDATE model_resource SET unavailable = @Unavailable WHERE id = @Id";
        await connection.ExecuteAsync(sql, new { Id = resourceId, Unavailable = unavailable });
    }

    #endregion

    #region Image-Resource Links

    /// <summary>
    /// Link an image to a resource
    /// </summary>
    public async Task LinkImageToResourceAsync(int imageId, int? resourceId, string resourceName, string resourceType, decimal? strength)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            INSERT INTO image_resource (image_id, resource_id, resource_name, resource_type, strength, created_at)
            VALUES (@ImageId, @ResourceId, @ResourceName, @ResourceType, @Strength, NOW())
            ON CONFLICT (image_id, resource_name, resource_type) DO UPDATE SET
                resource_id = EXCLUDED.resource_id,
                strength = EXCLUDED.strength";
        await connection.ExecuteAsync(sql, new 
        { 
            ImageId = imageId, 
            ResourceId = resourceId, 
            ResourceName = resourceName, 
            ResourceType = resourceType, 
            Strength = strength 
        });
    }

    /// <summary>
    /// Get resources used by an image
    /// </summary>
    public async Task<List<ImageResource>> GetImageResourcesAsync(int imageId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT ir.id, ir.image_id, ir.resource_id, ir.resource_name, ir.resource_type, ir.strength, ir.created_at,
                   mr.file_path, mr.civitai_name, mr.civitai_trained_words, mr.base_model, mr.unavailable
            FROM image_resource ir
            LEFT JOIN model_resource mr ON ir.resource_id = mr.id
            WHERE ir.image_id = @ImageId
            ORDER BY ir.resource_type, ir.resource_name";
        var results = await connection.QueryAsync<ImageResource, ModelResource?, ImageResource>(
            sql,
            (ir, mr) => { ir.Resource = mr; return ir; },
            new { ImageId = imageId },
            splitOn: "file_path");
        return results.ToList();
    }

    /// <summary>
    /// Get image IDs that use a specific resource
    /// </summary>
    public async Task<List<int>> GetImagesUsingResourceAsync(int resourceId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = "SELECT image_id FROM image_resource WHERE resource_id = @ResourceId";
        var results = await connection.QueryAsync<int>(sql, new { ResourceId = resourceId });
        return results.ToList();
    }

    /// <summary>
    /// Get resource names that are referenced but don't have a matched file on disk
    /// </summary>
    public async Task<List<string>> GetMissingResourceNamesAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT DISTINCT ir.resource_name
            FROM image_resource ir
            LEFT JOIN model_resource mr ON ir.resource_id = mr.id
            WHERE ir.resource_id IS NULL OR mr.unavailable = TRUE
            ORDER BY ir.resource_name";
        var results = await connection.QueryAsync<string>(sql);
        return results.ToList();
    }

    /// <summary>
    /// Get count of images using each resource
    /// </summary>
    public async Task<Dictionary<int, int>> GetResourceUsageCountsAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            SELECT resource_id, COUNT(*) as count
            FROM image_resource
            WHERE resource_id IS NOT NULL
            GROUP BY resource_id";
        var results = await connection.QueryAsync<(int ResourceId, int Count)>(sql);
        return results.ToDictionary(r => r.ResourceId, r => r.Count);
    }

    /// <summary>
    /// Update resource links for existing images based on current resource inventory
    /// (Re-match resource_name to resource_id after scanning new resources)
    /// </summary>
    public async Task RefreshResourceLinksAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var sql = @"
            UPDATE image_resource ir
            SET resource_id = mr.id
            FROM model_resource mr
            WHERE ir.resource_id IS NULL
              AND mr.resource_type = ir.resource_type
              AND (mr.file_name = ir.resource_name OR mr.file_name ILIKE '%' || ir.resource_name || '%')
              AND mr.unavailable = FALSE";
        var updated = await connection.ExecuteAsync(sql);
        if (updated > 0)
        {
            Common.Logger.Log($"Refreshed {updated} image-resource links");
        }
    }

    #endregion
}
