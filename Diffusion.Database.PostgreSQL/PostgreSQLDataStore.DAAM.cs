using Dapper;
using Diffusion.Database.PostgreSQL.Models;
using Npgsql;
using System.IO.Compression;

namespace Diffusion.Database.PostgreSQL;

public partial class PostgreSQLDataStore
{
    // =============================================================================
    // DAAM HEATMAP CRUD
    // =============================================================================
    
    /// <summary>
    /// Store DAAM heatmaps for an image
    /// </summary>
    public async Task<int> StoreDaamHeatmapsAsync(int imageId, List<DaamHeatmapEntity> heatmaps)
    {
        const string sql = @"
            INSERT INTO daam_heatmap (
                image_id, token, token_index, is_negative,
                heatmap_width, heatmap_height, heatmap_data, compression_type,
                max_attention, mean_attention, total_attention, coverage_area,
                bbox_x, bbox_y, bbox_width, bbox_height,
                sampling_config, timestep_range, layer_aggregation
            ) VALUES (
                @ImageId, @Token, @TokenIndex, @IsNegative,
                @HeatmapWidth, @HeatmapHeight, @HeatmapData, @CompressionType,
                @MaxAttention, @MeanAttention, @TotalAttention, @CoverageArea,
                @BboxX, @BboxY, @BboxWidth, @BboxHeight,
                @SamplingConfig, @TimestepRange, @LayerAggregation
            )
            ON CONFLICT (image_id, token_index, is_negative) DO UPDATE SET
                token = EXCLUDED.token,
                heatmap_data = EXCLUDED.heatmap_data,
                max_attention = EXCLUDED.max_attention,
                mean_attention = EXCLUDED.mean_attention,
                total_attention = EXCLUDED.total_attention,
                coverage_area = EXCLUDED.coverage_area,
                bbox_x = EXCLUDED.bbox_x,
                bbox_y = EXCLUDED.bbox_y,
                bbox_width = EXCLUDED.bbox_width,
                bbox_height = EXCLUDED.bbox_height
            RETURNING id;";

        using var connection = _dataSource.CreateConnection();
        var insertedIds = new List<int>();
        
        foreach (var heatmap in heatmaps)
        {
            heatmap.ImageId = imageId;
            var id = await connection.ExecuteScalarAsync<int>(sql, heatmap);
            insertedIds.Add(id);
        }

        return insertedIds.Count;
    }

    /// <summary>
    /// Get all DAAM heatmaps for an image
    /// </summary>
    public async Task<List<DaamHeatmapEntity>> GetDaamHeatmapsAsync(int imageId, bool? isNegative = null)
    {
        var sql = "SELECT * FROM daam_heatmap WHERE image_id = @imageId";
        
        if (isNegative.HasValue)
        {
            sql += " AND is_negative = @isNegative";
        }
        
        sql += " ORDER BY token_index;";

        using var connection = _dataSource.CreateConnection();
        var heatmaps = await connection.QueryAsync<DaamHeatmapEntity>(sql, new { imageId, isNegative });
        return heatmaps.ToList();
    }

    /// <summary>
    /// Get DAAM heatmap for specific token
    /// </summary>
    public async Task<DaamHeatmapEntity?> GetDaamHeatmapByTokenAsync(int imageId, string token, bool isNegative = false)
    {
        const string sql = @"
            SELECT * FROM daam_heatmap 
            WHERE image_id = @imageId AND token = @token AND is_negative = @isNegative
            LIMIT 1;";
        
        using var connection = _dataSource.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<DaamHeatmapEntity>(sql, new { imageId, token, isNegative });
    }

    /// <summary>
    /// Delete DAAM heatmaps for an image
    /// </summary>
    public async Task DeleteDaamHeatmapsAsync(int imageId)
    {
        const string sql = "DELETE FROM daam_heatmap WHERE image_id = @imageId;";
        
        using var connection = _dataSource.CreateConnection();
        await connection.ExecuteAsync(sql, new { imageId });
    }

    /// <summary>
    /// Get DAAM summary for an image
    /// </summary>
    public async Task<List<DaamHeatmapSummary>> GetDaamSummaryAsync(int imageId)
    {
        const string sql = "SELECT * FROM get_daam_summary(@imageId);";
        
        using var connection = _dataSource.CreateConnection();
        var summary = await connection.QueryAsync<DaamHeatmapSummary>(sql, new { imageId });
        return summary.ToList();
    }

    // =============================================================================
    // DAAM SEMANTIC GROUPS
    // =============================================================================

    /// <summary>
    /// Store semantic groups (merged DAAM tokens)
    /// </summary>
    public async Task<int> StoreDaamSemanticGroupsAsync(int imageId, List<DaamSemanticGroupEntity> groups)
    {
        const string sql = @"
            INSERT INTO daam_semantic_group (
                image_id, group_name, member_tokens, is_negative,
                merged_heatmap_data, heatmap_width, heatmap_height, compression_type,
                attention_density, total_attention, coverage_area, auto_weight,
                bbox_x, bbox_y, bbox_width, bbox_height,
                overlap_threshold, merge_count
            ) VALUES (
                @ImageId, @GroupName, @MemberTokens, @IsNegative,
                @MergedHeatmapData, @HeatmapWidth, @HeatmapHeight, @CompressionType,
                @AttentionDensity, @TotalAttention, @CoverageArea, @AutoWeight,
                @BboxX, @BboxY, @BboxWidth, @BboxHeight,
                @OverlapThreshold, @MergeCount
            )
            RETURNING id;";

        using var connection = _dataSource.CreateConnection();
        var insertedIds = new List<int>();
        
        foreach (var group in groups)
        {
            group.ImageId = imageId;
            var id = await connection.ExecuteScalarAsync<int>(sql, group);
            insertedIds.Add(id);
        }

        return insertedIds.Count;
    }

    /// <summary>
    /// Get semantic groups for an image
    /// </summary>
    public async Task<List<DaamSemanticGroupEntity>> GetDaamSemanticGroupsAsync(int imageId)
    {
        const string sql = @"
            SELECT * FROM daam_semantic_group 
            WHERE image_id = @imageId 
            ORDER BY attention_density DESC;";

        using var connection = _dataSource.CreateConnection();
        var groups = await connection.QueryAsync<DaamSemanticGroupEntity>(sql, new { imageId });
        return groups.ToList();
    }

    // =============================================================================
    // DAAM SPATIAL QUERIES
    // =============================================================================

    /// <summary>
    /// Store spatial index entries for DAAM heatmaps
    /// </summary>
    public async Task StoreDaamSpatialIndexAsync(int imageId, List<DaamSpatialIndexEntity> entries)
    {
        const string sql = @"
            INSERT INTO daam_spatial_index (
                image_id, token, grid_size, grid_cell_id, cell_attention
            ) VALUES (
                @ImageId, @Token, @GridSize, @GridCellId, @CellAttention
            )
            ON CONFLICT (image_id, token, grid_cell_id) DO UPDATE SET
                cell_attention = EXCLUDED.cell_attention;";

        using var connection = _dataSource.CreateConnection();
        
        foreach (var entry in entries)
        {
            entry.ImageId = imageId;
            await connection.ExecuteAsync(sql, entry);
        }
    }

    /// <summary>
    /// Find images by token spatial location
    /// </summary>
    public async Task<List<DaamSpatialQueryResult>> FindImagesByTokenLocationAsync(
        string token,
        int[] gridCells,
        float minAttention = 0.3f,
        int maxResults = 100)
    {
        const string sql = @"
            SELECT * FROM find_images_by_token_location(
                @token, @gridCells, @minAttention, @maxResults
            );";

        using var connection = _dataSource.CreateConnection();
        var results = await connection.QueryAsync<DaamSpatialQueryResult>(
            sql,
            new { token, gridCells, minAttention, maxResults });
        
        return results.ToList();
    }

    /// <summary>
    /// Get images where a token appears in a specific region
    /// </summary>
    public async Task<List<int>> FindImagesByTokenInRegionAsync(
        string token,
        string region,  // "top-left", "top-right", "bottom-left", "bottom-right", "center"
        float minAttention = 0.3f,
        int maxResults = 100)
    {
        // Map region names to grid cells (4×4 grid)
        var gridCellMap = new Dictionary<string, int[]>
        {
            ["top-left"] = new[] { 0, 1, 4, 5 },
            ["top-right"] = new[] { 2, 3, 6, 7 },
            ["bottom-left"] = new[] { 8, 9, 12, 13 },
            ["bottom-right"] = new[] { 10, 11, 14, 15 },
            ["center"] = new[] { 5, 6, 9, 10 },
            ["top"] = new[] { 0, 1, 2, 3 },
            ["bottom"] = new[] { 12, 13, 14, 15 },
            ["left"] = new[] { 0, 4, 8, 12 },
            ["right"] = new[] { 3, 7, 11, 15 }
        };

        if (!gridCellMap.TryGetValue(region.ToLower(), out var gridCells))
        {
            throw new ArgumentException($"Invalid region: {region}");
        }

        var results = await FindImagesByTokenLocationAsync(token, gridCells, minAttention, maxResults);
        return results.Select(r => r.ImageId).ToList();
    }

    /// <summary>
    /// Get DAAM statistics
    /// </summary>
    public async Task<DaamStats> GetDaamStatsAsync()
    {
        const string sql = "SELECT * FROM daam_stats;";
        
        using var connection = _dataSource.CreateConnection();
        return await connection.QuerySingleAsync<DaamStats>(sql);
    }

    /// <summary>
    /// Count images with DAAM data
    /// </summary>
    public async Task<int> GetImageCountWithDaamAsync()
    {
        const string sql = "SELECT COUNT(DISTINCT image_id) FROM daam_heatmap;";
        
        using var connection = _dataSource.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    // =============================================================================
    // HELPER METHODS
    // =============================================================================

    /// <summary>
    /// Compress heatmap data using zlib
    /// </summary>
    public static byte[] CompressHeatmap(float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Decompress heatmap data
    /// </summary>
    public static float[] DecompressHeatmap(byte[] compressed, int width, int height)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        
        gzip.CopyTo(output);
        var bytes = output.ToArray();
        
        var data = new float[width * height];
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
        return data;
    }

    /// <summary>
    /// Calculate grid-based spatial index from heatmap
    /// </summary>
    public static List<DaamSpatialIndexEntity> CalculateSpatialIndex(
        float[] heatmap,
        int width,
        int height,
        string token,
        int gridSize = 4)
    {
        var entries = new List<DaamSpatialIndexEntity>();
        var cellWidth = width / gridSize;
        var cellHeight = height / gridSize;

        for (int gridY = 0; gridY < gridSize; gridY++)
        {
            for (int gridX = 0; gridX < gridSize; gridX++)
            {
                var cellId = gridY * gridSize + gridX;
                float totalAttention = 0;
                int pixelCount = 0;

                // Sum attention in this grid cell
                for (int y = gridY * cellHeight; y < (gridY + 1) * cellHeight && y < height; y++)
                {
                    for (int x = gridX * cellWidth; x < (gridX + 1) * cellWidth && x < width; x++)
                    {
                        totalAttention += heatmap[y * width + x];
                        pixelCount++;
                    }
                }

                var avgAttention = pixelCount > 0 ? totalAttention / pixelCount : 0;

                entries.Add(new DaamSpatialIndexEntity
                {
                    Token = token,
                    GridSize = gridSize,
                    GridCellId = cellId,
                    CellAttention = avgAttention
                });
            }
        }

        return entries;
    }
}
