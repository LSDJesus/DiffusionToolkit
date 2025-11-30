using Dapper;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Duplicate detection and workflow variant management
/// Specifically designed for ComfyUI workflows that generate ORIG/FINAL pairs
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Find all workflow variant groups (images with same seed + model + prompt)
    /// This catches ORIG/FINAL pairs from ComfyUI workflows that save both base and upscaled versions
    /// </summary>
    public async Task<List<WorkflowVariantGroup>> FindWorkflowVariantGroupsAsync(
        int? folderId = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        // Group by seed + model + prompt (ignoring dimensions which differ between ORIG/FINAL)
        var sql = @"
            WITH variant_groups AS (
                SELECT 
                    seed,
                    model,
                    model_hash,
                    LEFT(prompt, 500) AS prompt_prefix,
                    ARRAY_AGG(id ORDER BY width * height DESC, file_size DESC) AS image_ids,
                    COUNT(*) AS variant_count,
                    MAX(width * height) AS max_resolution,
                    MIN(width * height) AS min_resolution
                FROM image
                WHERE seed IS NOT NULL 
                  AND seed > 0
                  AND model IS NOT NULL
                  AND prompt IS NOT NULL
                  AND for_deletion = FALSE
                  AND unavailable = FALSE
                  " + (folderId.HasValue ? "AND folder_id = @folderId" : "") + @"
                GROUP BY seed, model, model_hash, LEFT(prompt, 500)
                HAVING COUNT(*) > 1
            )
            SELECT 
                seed AS Seed,
                model AS Model,
                model_hash AS ModelHash,
                prompt_prefix AS PromptPrefix,
                image_ids AS ImageIds,
                variant_count AS VariantCount,
                max_resolution AS MaxResolution,
                min_resolution AS MinResolution,
                CASE 
                    WHEN max_resolution > 0 AND min_resolution > 0 
                    THEN SQRT(max_resolution::float / min_resolution::float)
                    ELSE 1.0
                END AS UpscaleFactor
            FROM variant_groups
            ORDER BY variant_count DESC, seed DESC
            " + (limit.HasValue ? "LIMIT @limit" : "") + ";";

        var results = await conn.QueryAsync<WorkflowVariantGroup>(
            sql, 
            new { folderId, limit }).ConfigureAwait(false);

        return results.ToList();
    }

    /// <summary>
    /// Find variants of a specific image by matching seed + model + prompt
    /// Returns all images in the same "generation family"
    /// </summary>
    public async Task<List<ImageEntity>> FindWorkflowVariantsAsync(
        int imageId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var sql = @"
            SELECT i2.*
            FROM image i1
            JOIN image i2 ON 
                i1.seed = i2.seed
                AND i1.model = i2.model
                AND LEFT(i1.prompt, 500) = LEFT(i2.prompt, 500)
            WHERE i1.id = @imageId
              AND i2.for_deletion = FALSE
              AND i2.unavailable = FALSE
            ORDER BY i2.width * i2.height DESC, i2.file_size DESC;";

        var results = await conn.QueryAsync<ImageEntity>(sql, new { imageId }).ConfigureAwait(false);
        return results.ToList();
    }

    /// <summary>
    /// Find ORIG/FINAL pairs by filename pattern matching
    /// For workflows using consistent naming like %time_%model_ORIG.png / %time_%model_FINAL.png
    /// </summary>
    public async Task<List<OrigFinalPair>> FindOrigFinalPairsByFilenameAsync(
        int? folderId = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var sql = @"
            WITH originals AS (
                SELECT 
                    id,
                    path,
                    file_name,
                    width,
                    height,
                    file_size,
                    seed,
                    model,
                    -- Extract base name before _ORIG suffix
                    REGEXP_REPLACE(file_name, '_ORIG\.(png|jpg|jpeg|webp)$', '', 'i') AS base_name
                FROM image
                WHERE file_name ~* '_ORIG\.(png|jpg|jpeg|webp)$'
                  AND for_deletion = FALSE
                  AND unavailable = FALSE
                  " + (folderId.HasValue ? "AND folder_id = @folderId" : "") + @"
            ),
            finals AS (
                SELECT 
                    id,
                    path,
                    file_name,
                    width,
                    height,
                    file_size,
                    seed,
                    model,
                    -- Extract base name before _FINAL suffix
                    REGEXP_REPLACE(file_name, '_FINAL\.(png|jpg|jpeg|webp)$', '', 'i') AS base_name
                FROM image
                WHERE file_name ~* '_FINAL\.(png|jpg|jpeg|webp)$'
                  AND for_deletion = FALSE
                  AND unavailable = FALSE
                  " + (folderId.HasValue ? "AND folder_id = @folderId" : "") + @"
            )
            SELECT 
                o.id AS OriginalId,
                o.path AS OriginalPath,
                o.width AS OriginalWidth,
                o.height AS OriginalHeight,
                o.file_size AS OriginalFileSize,
                f.id AS FinalId,
                f.path AS FinalPath,
                f.width AS FinalWidth,
                f.height AS FinalHeight,
                f.file_size AS FinalFileSize,
                o.seed AS Seed,
                o.model AS Model,
                CASE 
                    WHEN o.width > 0 AND o.height > 0 
                    THEN (f.width::float / o.width + f.height::float / o.height) / 2.0
                    ELSE 1.0
                END AS UpscaleFactor
            FROM originals o
            JOIN finals f ON o.seed = f.seed AND o.model = f.model
            ORDER BY f.file_size DESC
            " + (limit.HasValue ? "LIMIT @limit" : "") + ";";

        var results = await conn.QueryAsync<OrigFinalPair>(sql, new { folderId, limit }).ConfigureAwait(false);
        return results.ToList();
    }

    /// <summary>
    /// Find near-duplicates using CLIP embedding similarity
    /// This catches visually similar images that may have different prompts/seeds
    /// </summary>
    public async Task<List<DuplicateCluster>> FindVisualDuplicatesAsync(
        float similarityThreshold = 0.95f,
        int? folderId = null,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        // Find pairs of images with very high visual similarity
        var sql = @"
            WITH similar_pairs AS (
                SELECT 
                    i1.id AS id1,
                    i2.id AS id2,
                    (1 - (i1.image_embedding <=> i2.image_embedding)) AS similarity,
                    i1.width * i1.height AS resolution1,
                    i2.width * i2.height AS resolution2,
                    i1.file_size AS size1,
                    i2.file_size AS size2
                FROM image i1
                JOIN image i2 ON i1.id < i2.id  -- Avoid duplicates and self-joins
                WHERE i1.image_embedding IS NOT NULL
                  AND i2.image_embedding IS NOT NULL
                  AND i1.for_deletion = FALSE
                  AND i2.for_deletion = FALSE
                  AND i1.unavailable = FALSE
                  AND i2.unavailable = FALSE
                  AND (1 - (i1.image_embedding <=> i2.image_embedding)) >= @threshold
                  " + (folderId.HasValue ? "AND i1.folder_id = @folderId AND i2.folder_id = @folderId" : "") + @"
                ORDER BY similarity DESC
                LIMIT @limit
            )
            SELECT 
                id1 AS ImageId1,
                id2 AS ImageId2,
                similarity AS Similarity,
                resolution1 AS Resolution1,
                resolution2 AS Resolution2,
                size1 AS FileSize1,
                size2 AS FileSize2,
                CASE WHEN resolution1 >= resolution2 THEN id1 ELSE id2 END AS PreferredId,
                CASE WHEN resolution1 < resolution2 THEN id1 ELSE id2 END AS DuplicateId
            FROM similar_pairs;";

        var pairs = await conn.QueryAsync<DuplicatePair>(
            sql, 
            new { threshold = similarityThreshold, folderId, limit }).ConfigureAwait(false);

        // Cluster pairs into groups using Union-Find
        var clusters = ClusterDuplicatePairs(pairs.ToList());
        return clusters;
    }

    /// <summary>
    /// Get duplicate detection summary statistics
    /// </summary>
    public async Task<DuplicateDetectionStats> GetDuplicateDetectionStatsAsync(
        int? folderId = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var folderFilter = folderId.HasValue ? "WHERE folder_id = @folderId" : "";
        var andFolderFilter = folderId.HasValue ? "AND folder_id = @folderId" : "";

        var sql = $@"
            SELECT 
                (SELECT COUNT(*) FROM image {folderFilter}) AS TotalImages,
                
                (SELECT COUNT(*) FROM image 
                 WHERE file_name ~* '_ORIG\.(png|jpg|jpeg|webp)$' 
                   AND for_deletion = FALSE {andFolderFilter}) AS OriginalCount,
                
                (SELECT COUNT(*) FROM image 
                 WHERE file_name ~* '_FINAL\.(png|jpg|jpeg|webp)$' 
                   AND for_deletion = FALSE {andFolderFilter}) AS FinalCount,
                
                (SELECT COUNT(DISTINCT seed || '|' || model || '|' || LEFT(prompt, 500))
                 FROM image 
                 WHERE seed IS NOT NULL AND seed > 0 
                   AND model IS NOT NULL 
                   AND prompt IS NOT NULL {andFolderFilter}) AS UniqueGenerations,
                
                (SELECT COUNT(*) FROM (
                    SELECT seed, model, LEFT(prompt, 500)
                    FROM image
                    WHERE seed IS NOT NULL AND seed > 0 
                      AND model IS NOT NULL 
                      AND prompt IS NOT NULL 
                      AND for_deletion = FALSE {andFolderFilter}
                    GROUP BY seed, model, LEFT(prompt, 500)
                    HAVING COUNT(*) > 1
                ) AS dups) AS DuplicateGroups,
                
                (SELECT COUNT(*) FROM image 
                 WHERE image_embedding IS NOT NULL {andFolderFilter}) AS ImagesWithEmbeddings;";

        var stats = await conn.QueryFirstAsync<DuplicateDetectionStats>(sql, new { folderId }).ConfigureAwait(false);
        return stats;
    }

    /// <summary>
    /// Mark original images as duplicates (for batch cleanup)
    /// Keeps FINAL/upscaled versions, marks ORIG/base for deletion
    /// </summary>
    public async Task<int> MarkOriginalImagesForDeletionAsync(
        int? folderId = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        if (dryRun)
        {
            // Just count how many would be marked
            var countSql = @"
                SELECT COUNT(*) FROM image o
                WHERE o.file_name ~* '_ORIG\.(png|jpg|jpeg|webp)$'
                  AND o.for_deletion = FALSE
                  AND EXISTS (
                      SELECT 1 FROM image f
                      WHERE f.seed = o.seed
                        AND f.model = o.model
                        AND f.file_name ~* '_FINAL\.(png|jpg|jpeg|webp)$'
                        AND f.for_deletion = FALSE
                  )
                  " + (folderId.HasValue ? "AND o.folder_id = @folderId" : "") + ";";

            return await conn.ExecuteScalarAsync<int>(countSql, new { folderId }).ConfigureAwait(false);
        }
        else
        {
            // Actually mark for deletion
            var updateSql = @"
                UPDATE image o
                SET for_deletion = TRUE
                WHERE o.file_name ~* '_ORIG\.(png|jpg|jpeg|webp)$'
                  AND o.for_deletion = FALSE
                  AND EXISTS (
                      SELECT 1 FROM image f
                      WHERE f.seed = o.seed
                        AND f.model = o.model
                        AND f.file_name ~* '_FINAL\.(png|jpg|jpeg|webp)$'
                        AND f.for_deletion = FALSE
                  )
                  " + (folderId.HasValue ? "AND o.folder_id = @folderId" : "") + ";";

            return await conn.ExecuteAsync(updateSql, new { folderId }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Link ORIG images to their FINAL counterparts (set base_image_id)
    /// This establishes the parent-child relationship for the UI
    /// </summary>
    public async Task<int> LinkOrigFinalPairsAsync(
        int? folderId = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync().ConfigureAwait(false);

        var sql = @"
            WITH pairs AS (
                SELECT 
                    o.id AS orig_id,
                    f.id AS final_id,
                    (f.width::float / NULLIF(o.width, 0)) AS scale
                FROM image o
                JOIN image f ON 
                    o.seed = f.seed
                    AND o.model = f.model
                    AND o.file_name ~* '_ORIG\.(png|jpg|jpeg|webp)$'
                    AND f.file_name ~* '_FINAL\.(png|jpg|jpeg|webp)$'
                WHERE o.for_deletion = FALSE
                  AND f.for_deletion = FALSE
                  AND o.unavailable = FALSE
                  AND f.unavailable = FALSE
                  " + (folderId.HasValue ? "AND o.folder_id = @folderId AND f.folder_id = @folderId" : "") + @"
            )
            UPDATE image
            SET is_upscaled = TRUE,
                base_image_id = pairs.orig_id,
                upscale_factor = pairs.scale
            FROM pairs
            WHERE image.id = pairs.final_id
              AND (image.base_image_id IS NULL OR image.base_image_id != pairs.orig_id);";

        return await conn.ExecuteAsync(sql, new { folderId }).ConfigureAwait(false);
    }

    /// <summary>
    /// Helper method to cluster duplicate pairs into groups using Union-Find
    /// </summary>
    private static List<DuplicateCluster> ClusterDuplicatePairs(List<DuplicatePair> pairs)
    {
        if (pairs.Count == 0) return new List<DuplicateCluster>();

        // Union-Find data structure
        var parent = new Dictionary<int, int>();
        var rank = new Dictionary<int, int>();

        int Find(int x)
        {
            if (!parent.ContainsKey(x))
            {
                parent[x] = x;
                rank[x] = 0;
            }
            if (parent[x] != x)
                parent[x] = Find(parent[x]); // Path compression
            return parent[x];
        }

        void Union(int x, int y)
        {
            int rootX = Find(x);
            int rootY = Find(y);
            if (rootX != rootY)
            {
                // Union by rank
                if (rank[rootX] < rank[rootY])
                    parent[rootX] = rootY;
                else if (rank[rootX] > rank[rootY])
                    parent[rootY] = rootX;
                else
                {
                    parent[rootY] = rootX;
                    rank[rootX]++;
                }
            }
        }

        // Build clusters
        foreach (var pair in pairs)
        {
            Union(pair.ImageId1, pair.ImageId2);
        }

        // Group by root
        var clusters = new Dictionary<int, DuplicateCluster>();
        foreach (var pair in pairs)
        {
            int root = Find(pair.ImageId1);
            if (!clusters.ContainsKey(root))
            {
                clusters[root] = new DuplicateCluster
                {
                    ClusterId = root,
                    ImageIds = new List<int>(),
                    MaxSimilarity = 0,
                    MinSimilarity = 1
                };
            }
            
            var cluster = clusters[root];
            if (!cluster.ImageIds.Contains(pair.ImageId1))
                cluster.ImageIds.Add(pair.ImageId1);
            if (!cluster.ImageIds.Contains(pair.ImageId2))
                cluster.ImageIds.Add(pair.ImageId2);
            
            cluster.MaxSimilarity = Math.Max(cluster.MaxSimilarity, pair.Similarity);
            cluster.MinSimilarity = Math.Min(cluster.MinSimilarity, pair.Similarity);
            
            // Preferred is the highest resolution
            if (pair.PreferredId.HasValue && 
                (!cluster.PreferredImageId.HasValue || pair.Resolution1 > cluster.ImageIds.Count))
            {
                cluster.PreferredImageId = pair.PreferredId;
            }
        }

        return clusters.Values.OrderByDescending(c => c.ImageIds.Count).ToList();
    }
}

/// <summary>
/// Represents a group of images with same seed + model + prompt (workflow variants)
/// </summary>
public class WorkflowVariantGroup
{
    public long Seed { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? ModelHash { get; set; }
    public string? PromptPrefix { get; set; }
    public int[] ImageIds { get; set; } = Array.Empty<int>();
    public int VariantCount { get; set; }
    public long MaxResolution { get; set; }
    public long MinResolution { get; set; }
    public double UpscaleFactor { get; set; }
    
    /// <summary>
    /// True if this looks like an ORIG/FINAL pair (2x resolution difference)
    /// </summary>
    public bool IsLikelyOrigFinalPair => UpscaleFactor >= 1.8 && UpscaleFactor <= 2.2 && VariantCount == 2;
}

/// <summary>
/// Represents a matched ORIG/FINAL image pair
/// </summary>
public class OrigFinalPair
{
    public int OriginalId { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public long OriginalFileSize { get; set; }
    
    public int FinalId { get; set; }
    public string FinalPath { get; set; } = string.Empty;
    public int FinalWidth { get; set; }
    public int FinalHeight { get; set; }
    public long FinalFileSize { get; set; }
    
    public long Seed { get; set; }
    public string Model { get; set; } = string.Empty;
    public double UpscaleFactor { get; set; }
}

/// <summary>
/// Represents a pair of visually similar images
/// </summary>
public class DuplicatePair
{
    public int ImageId1 { get; set; }
    public int ImageId2 { get; set; }
    public float Similarity { get; set; }
    public long Resolution1 { get; set; }
    public long Resolution2 { get; set; }
    public long FileSize1 { get; set; }
    public long FileSize2 { get; set; }
    public int? PreferredId { get; set; }
    public int? DuplicateId { get; set; }
}

/// <summary>
/// Represents a cluster of duplicate/similar images
/// </summary>
public class DuplicateCluster
{
    public int ClusterId { get; set; }
    public List<int> ImageIds { get; set; } = new();
    public int? PreferredImageId { get; set; }
    public float MaxSimilarity { get; set; }
    public float MinSimilarity { get; set; }
    public int Count => ImageIds.Count;
}

/// <summary>
/// Statistics about duplicate detection
/// </summary>
public class DuplicateDetectionStats
{
    public long TotalImages { get; set; }
    public long OriginalCount { get; set; }
    public long FinalCount { get; set; }
    public long UniqueGenerations { get; set; }
    public long DuplicateGroups { get; set; }
    public long ImagesWithEmbeddings { get; set; }
    
    public long PotentialDuplicates => OriginalCount;
    public double DuplicatePercentage => TotalImages > 0 ? (double)PotentialDuplicates / TotalImages * 100 : 0;
}
