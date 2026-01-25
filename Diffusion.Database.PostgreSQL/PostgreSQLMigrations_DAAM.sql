-- DAAM (Diffusion Attentive Attribution Maps) Storage Schema
-- Stores cross-attention heatmaps for semantic understanding of generated images

-- =============================================================================
-- DAAM HEATMAPS TABLE
-- =============================================================================
CREATE TABLE IF NOT EXISTS daam_heatmap (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    
    -- Token information
    token TEXT NOT NULL,
    token_index INTEGER NOT NULL,
    is_negative BOOLEAN DEFAULT FALSE,  -- True for negative prompt tokens
    
    -- Heatmap data (stored as 2D array or compressed bytes)
    heatmap_width INTEGER NOT NULL,
    heatmap_height INTEGER NOT NULL,
    heatmap_data BYTEA NOT NULL,  -- Compressed numpy array or raw float32 data
    compression_type TEXT DEFAULT 'zlib',  -- 'zlib', 'lz4', 'none'
    
    -- Statistical metadata
    max_attention REAL NOT NULL,
    mean_attention REAL NOT NULL,
    total_attention REAL NOT NULL,  -- Sum of all values
    coverage_area REAL NOT NULL,  -- Percentage of image with >threshold attention
    
    -- Spatial bounds (bounding box of significant attention)
    bbox_x INTEGER,
    bbox_y INTEGER,
    bbox_width INTEGER,
    bbox_height INTEGER,
    
    -- DAAM generation metadata
    sampling_config JSONB,  -- skip_early_ratio, stride, etc.
    timestep_range TEXT,  -- e.g., "0.2-1.0"
    layer_aggregation TEXT DEFAULT 'mean',  -- 'mean', 'max', 'weighted'
    
    -- Processing metadata
    created_date TIMESTAMP DEFAULT NOW(),
    
    -- Index: one heatmap per token per image
    CONSTRAINT unique_token_per_image UNIQUE (image_id, token_index, is_negative)
);

-- Create indexes for efficient querying
CREATE INDEX IF NOT EXISTS idx_daam_heatmap_image_id ON daam_heatmap(image_id);
CREATE INDEX IF NOT EXISTS idx_daam_heatmap_token ON daam_heatmap(token);
CREATE INDEX IF NOT EXISTS idx_daam_heatmap_is_negative ON daam_heatmap(is_negative);
CREATE INDEX IF NOT EXISTS idx_daam_heatmap_max_attention ON daam_heatmap(max_attention DESC);

-- =============================================================================
-- DAAM SEMANTIC GROUPS TABLE (Merged overlapping tokens)
-- =============================================================================
CREATE TABLE IF NOT EXISTS daam_semantic_group (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    
    -- Group information
    group_name TEXT NOT NULL,  -- e.g., "eyes" (merged from "blue eyes", "eyes", "detailed eyes")
    member_tokens TEXT[] NOT NULL,  -- All tokens merged into this group
    is_negative BOOLEAN DEFAULT FALSE,
    
    -- Merged heatmap (union of all member heatmaps)
    merged_heatmap_data BYTEA NOT NULL,
    heatmap_width INTEGER NOT NULL,
    heatmap_height INTEGER NOT NULL,
    compression_type TEXT DEFAULT 'zlib',
    
    -- Group statistics
    attention_density REAL NOT NULL,  -- total_attention / coverage_area
    total_attention REAL NOT NULL,
    coverage_area REAL NOT NULL,
    auto_weight REAL NOT NULL,  -- Calculated conditioning weight (0.0-1.0)
    
    -- Spatial bounds
    bbox_x INTEGER,
    bbox_y INTEGER,
    bbox_width INTEGER,
    bbox_height INTEGER,
    
    -- Overlap metadata
    overlap_threshold REAL DEFAULT 0.9,  -- Threshold used for merging
    merge_count INTEGER DEFAULT 1,  -- Number of tokens merged
    
    created_date TIMESTAMP DEFAULT NOW()
);

-- Create indexes
CREATE INDEX IF NOT EXISTS idx_daam_semantic_group_image_id ON daam_semantic_group(image_id);
CREATE INDEX IF NOT EXISTS idx_daam_semantic_group_name ON daam_semantic_group(group_name);
CREATE INDEX IF NOT EXISTS idx_daam_semantic_group_density ON daam_semantic_group(attention_density DESC);

-- =============================================================================
-- DAAM SPATIAL QUERIES TABLE (Pre-computed spatial indices)
-- =============================================================================
CREATE TABLE IF NOT EXISTS daam_spatial_index (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    token TEXT NOT NULL,
    
    -- Grid-based spatial index (divide image into 4×4 or 8×8 grid)
    grid_size INTEGER DEFAULT 4,  -- 4×4 = 16 cells, 8×8 = 64 cells
    grid_cell_id INTEGER NOT NULL,  -- 0-15 for 4×4, 0-63 for 8×8
    
    -- Attention in this grid cell
    cell_attention REAL NOT NULL,
    
    -- Enable queries like "find images where 'eyes' has high attention in top-left quadrant"
    CONSTRAINT unique_token_cell_per_image UNIQUE (image_id, token, grid_cell_id)
);

-- Create indexes
CREATE INDEX IF NOT EXISTS idx_daam_spatial_token ON daam_spatial_index(token);
CREATE INDEX IF NOT EXISTS idx_daam_spatial_cell ON daam_spatial_index(grid_cell_id);
CREATE INDEX IF NOT EXISTS idx_daam_spatial_attention ON daam_spatial_index(cell_attention DESC);

-- =============================================================================
-- HELPER FUNCTION: Query images by token spatial location
-- =============================================================================
CREATE OR REPLACE FUNCTION find_images_by_token_location(
    query_token TEXT,
    grid_cells INTEGER[],  -- e.g., [0, 1, 4, 5] for top-left quadrant in 4×4
    min_attention REAL DEFAULT 0.3,
    max_results INTEGER DEFAULT 100
)
RETURNS TABLE (
    image_id INTEGER,
    total_attention REAL,
    coverage_cells INTEGER
) AS $$
BEGIN
    RETURN QUERY
    SELECT 
        dsi.image_id,
        SUM(dsi.cell_attention) as total_attention,
        COUNT(DISTINCT dsi.grid_cell_id) as coverage_cells
    FROM 
        daam_spatial_index dsi
    WHERE 
        dsi.token = query_token
        AND dsi.grid_cell_id = ANY(grid_cells)
        AND dsi.cell_attention >= min_attention
    GROUP BY 
        dsi.image_id
    HAVING 
        COUNT(DISTINCT dsi.grid_cell_id) >= 1
    ORDER BY 
        total_attention DESC
    LIMIT max_results;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- HELPER FUNCTION: Get heatmap summary for image
-- =============================================================================
CREATE OR REPLACE FUNCTION get_daam_summary(query_image_id INTEGER)
RETURNS TABLE (
    token TEXT,
    is_negative BOOLEAN,
    max_attention REAL,
    coverage_area REAL,
    bbox TEXT
) AS $$
BEGIN
    RETURN QUERY
    SELECT 
        dh.token,
        dh.is_negative,
        dh.max_attention,
        dh.coverage_area,
        CONCAT(dh.bbox_x, ',', dh.bbox_y, ',', dh.bbox_width, ',', dh.bbox_height) as bbox
    FROM 
        daam_heatmap dh
    WHERE 
        dh.image_id = query_image_id
    ORDER BY 
        dh.max_attention DESC;
END;
$$ LANGUAGE plpgsql;

-- =============================================================================
-- TRIGGER: Update spatial index when heatmap is inserted
-- =============================================================================
-- Note: This would require server-side processing of heatmap_data
-- For now, spatial index population will be done client-side

-- =============================================================================
-- VIEW: DAAM Summary Statistics
-- =============================================================================
CREATE OR REPLACE VIEW daam_stats AS
SELECT 
    COUNT(DISTINCT image_id) as images_with_daam,
    COUNT(*) as total_heatmaps,
    COUNT(*) FILTER (WHERE is_negative = TRUE) as negative_heatmaps,
    COUNT(*) FILTER (WHERE is_negative = FALSE) as positive_heatmaps,
    AVG(mean_attention) as avg_mean_attention,
    AVG(coverage_area) as avg_coverage_area,
    (SELECT COUNT(*) FROM daam_semantic_group) as total_semantic_groups,
    (SELECT AVG(merge_count) FROM daam_semantic_group) as avg_merge_count
FROM daam_heatmap;
