-- Consolidated PostgreSQL Schema - Current Production State
-- Complete schema for Diffusion Toolkit with pgvector support
-- Includes: Base schema + Embeddings + Tagging + Captions + Civitai metadata
-- Last updated: 2026-01-21

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS ltree;
CREATE EXTENSION IF NOT EXISTS pgcrypto;  -- For digest() function

-- =============================================================================
-- CORE IMAGE TABLE
-- =============================================================================
CREATE TABLE IF NOT EXISTS image (
    id SERIAL PRIMARY KEY,
    root_folder_id INT NOT NULL,
    folder_id INT NOT NULL,
    path TEXT NOT NULL UNIQUE,
    file_name TEXT NOT NULL,
    
    -- Generation parameters
    prompt TEXT,
    negative_prompt TEXT,
    steps INT DEFAULT 0,
    sampler TEXT,
    cfg_scale DECIMAL(5, 2) DEFAULT 0.0,
    seed BIGINT DEFAULT 0,
    width INT DEFAULT 0,
    height INT DEFAULT 0,
    model_hash TEXT,
    model TEXT,
    batch_size INT DEFAULT 1,
    batch_pos INT DEFAULT 0,
    
    -- Additional generation parameters (V4)
    generated_tags TEXT,
    loras JSONB,
    vae TEXT,
    refiner_model TEXT,
    refiner_switch DECIMAL(4, 2),
    upscaler TEXT,
    upscale_factor DECIMAL(3, 1),
    hires_steps INT,
    hires_upscaler TEXT,
    hires_upscale DECIMAL(3, 1),
    denoising_strength DECIMAL(4, 2),
    controlnets JSONB,
    ip_adapter TEXT,
    ip_adapter_strength DECIMAL(4, 2),
    wildcards_used TEXT[],
    generation_time_seconds DECIMAL(6, 2),
    scheduler TEXT,
    
    -- File metadata
    created_date TIMESTAMP NOT NULL,
    modified_date TIMESTAMP NOT NULL,
    file_size BIGINT DEFAULT 0,
    hash TEXT,
    
    -- User metadata
    custom_tags TEXT,
    rating INT,
    favorite BOOLEAN DEFAULT FALSE,
    for_deletion BOOLEAN DEFAULT FALSE,
    nsfw BOOLEAN DEFAULT FALSE,
    unavailable BOOLEAN DEFAULT FALSE,
    viewed_date TIMESTAMP,
    touched_date TIMESTAMP,
    
    -- Civitai metadata (from JSON sidecar files)
    civitai_image_id BIGINT,
    civitai_post_id BIGINT,
    civitai_username VARCHAR(255),
    civitai_nsfw_level VARCHAR(50),
    civitai_browsing_level INTEGER,
    civitai_base_model VARCHAR(255),
    civitai_created_at TIMESTAMP,
    civitai_image_url TEXT,
    civitai_like_count INTEGER,
    
    -- Quality metrics
    aesthetic_score DECIMAL(4, 2),
    hyper_network TEXT,
    hyper_network_strength DECIMAL(4, 2),
    clip_skip INT,
    ensd INT,
    
    -- ComfyUI workflow
    workflow TEXT,
    workflow_id TEXT,
    no_metadata BOOLEAN DEFAULT FALSE,
    has_error BOOLEAN DEFAULT FALSE,
    
    -- Scanning phases (V12)
    scan_phase INTEGER DEFAULT 1 NOT NULL,  -- 0=QuickScan, 1=DeepScan
    
    -- Embedding deduplication (V3, V5)
    prompt_embedding_id INTEGER,
    negative_prompt_embedding_id INTEGER,
    image_embedding_id INTEGER,
    needs_visual_embedding BOOLEAN DEFAULT true,
    is_upscaled BOOLEAN DEFAULT false,
    base_image_id INTEGER,
    metadata_hash TEXT,
    embedding_source_id INT,
    is_embedding_representative BOOLEAN DEFAULT FALSE,
    
    -- Unified schema with UUID and narrative integration (V8)
    image_uuid UUID DEFAULT gen_random_uuid() UNIQUE NOT NULL,
    story_id UUID,
    turn_number INTEGER,
    character_id UUID,
    location_id UUID,
    image_type VARCHAR(50),
    status VARCHAR(20),
    comfyui_workflow VARCHAR(100),
    error_message TEXT,
    
    -- Vector embeddings for similarity search (legacy columns - kept for compatibility)
    prompt_embedding vector(1024),              -- BGE-large-en-v1.5 (legacy - use bge_prompt_embedding)
    negative_prompt_embedding vector(1024),     -- BGE-large-en-v1.5 (legacy - not used for embeddings)
    image_embedding vector(1280),               -- CLIP-ViT-H/14 (legacy - use clip_vision_embedding)
    clip_l_embedding vector(768),               -- SDXL CLIP-L (legacy - use clip_l_prompt_embedding)
    clip_g_embedding vector(1280),              -- SDXL CLIP-G (legacy - use clip_g_prompt_embedding)
    
    -- V10: Granular BGE embeddings for semantic search (separate for prompt, caption, tags)
    bge_prompt_embedding vector(1024),          -- BGE embedding of original prompt
    bge_caption_embedding vector(1024),         -- BGE embedding of AI-generated caption
    bge_tags_embedding vector(1024),            -- BGE embedding of tags (comma-joined)
    
    -- V10: CLIP embeddings for SDXL regeneration (prompt only)
    clip_l_prompt_embedding vector(768),        -- CLIP-L embedding of prompt for SDXL
    clip_g_prompt_embedding vector(1280),       -- CLIP-G embedding of prompt for SDXL
    clip_vision_embedding vector(1280),         -- CLIP-ViT-H/14 embedding of image pixels
    
    -- V10: T5-XXL stubs for future Flux support
    t5xxl_prompt_embedding vector(4096),        -- T5-XXL embedding of prompt (stub)
    t5xxl_caption_embedding vector(4096),       -- T5-XXL embedding of caption (stub)
    
    -- V10: Granular embedding status flags (true=pending, false=done, null=not queued)
    needs_bge_prompt_embedding BOOLEAN,
    needs_bge_caption_embedding BOOLEAN,
    needs_bge_tags_embedding BOOLEAN,
    needs_clip_l_prompt_embedding BOOLEAN,
    needs_clip_g_prompt_embedding BOOLEAN,
    needs_clip_vision_embedding BOOLEAN,
    needs_t5xxl_prompt_embedding BOOLEAN,
    needs_t5xxl_caption_embedding BOOLEAN,
    
    created_at TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- EMBEDDING CACHE TABLE (V3, updated V10)
-- =============================================================================
CREATE TABLE IF NOT EXISTS embedding_cache (
    id SERIAL PRIMARY KEY,
    content_hash TEXT NOT NULL UNIQUE,
    content_type TEXT NOT NULL,
    content_text TEXT,
    
    -- Legacy columns (kept for compatibility)
    bge_embedding vector(1024),
    clip_l_embedding vector(768),
    clip_g_embedding vector(1280),
    clip_h_embedding vector(1024),
    
    -- V10: Granular embedding columns
    bge_prompt_embedding vector(1024),
    bge_caption_embedding vector(1024),
    bge_tags_embedding vector(1024),
    clip_l_prompt_embedding vector(768),
    clip_g_prompt_embedding vector(1280),
    clip_vision_embedding vector(1280),
    t5xxl_prompt_embedding vector(4096),
    t5xxl_caption_embedding vector(4096),
    
    reference_count INTEGER DEFAULT 0,
    created_at TIMESTAMP DEFAULT NOW(),
    last_used_at TIMESTAMP DEFAULT NOW()
);

-- Foreign key constraints for image table (must be after embedding_cache is created)
-- Use DO blocks since PostgreSQL doesn't support ADD CONSTRAINT IF NOT EXISTS
DO $$ BEGIN
    ALTER TABLE image ADD CONSTRAINT fk_image_prompt_cache FOREIGN KEY (prompt_embedding_id) REFERENCES embedding_cache(id);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE image ADD CONSTRAINT fk_image_negative_cache FOREIGN KEY (negative_prompt_embedding_id) REFERENCES embedding_cache(id);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE image ADD CONSTRAINT fk_image_visual_cache FOREIGN KEY (image_embedding_id) REFERENCES embedding_cache(id);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE image ADD CONSTRAINT fk_image_base_image FOREIGN KEY (base_image_id) REFERENCES image(id);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    ALTER TABLE image ADD CONSTRAINT fk_image_embedding_source FOREIGN KEY (embedding_source_id) REFERENCES image(id) ON DELETE SET NULL;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- =============================================================================
-- ALBUMS
-- =============================================================================
CREATE TABLE IF NOT EXISTS album (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    "order" INT DEFAULT 0,
    last_updated TIMESTAMP NOT NULL,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS album_image (
    id SERIAL PRIMARY KEY,
    album_id INT NOT NULL REFERENCES album(id) ON DELETE CASCADE,
    image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    added_at TIMESTAMP DEFAULT NOW(),
    UNIQUE(album_id, image_id)
);

-- =============================================================================
-- COMFYUI NODES AND WORKFLOWS
-- =============================================================================
CREATE TABLE IF NOT EXISTS node (
    id SERIAL PRIMARY KEY,
    image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    node_index INT,
    node_id TEXT,
    class_type TEXT,
    data JSONB,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS node_property (
    id SERIAL PRIMARY KEY,
    node_id INTEGER NOT NULL REFERENCES node(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    value TEXT,
    created_at TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- FOLDERS
-- =============================================================================
CREATE TABLE IF NOT EXISTS folder (
    id SERIAL PRIMARY KEY,
    parent_id INT DEFAULT 0,
    root_folder_id INT NOT NULL,
    path TEXT NOT NULL UNIQUE,
    path_tree ltree,
    image_count INT DEFAULT 0,
    scanned_date TIMESTAMP,
    unavailable BOOLEAN DEFAULT FALSE,
    archived BOOLEAN DEFAULT FALSE,
    excluded BOOLEAN DEFAULT FALSE,
    is_root BOOLEAN DEFAULT FALSE,
    recursive BOOLEAN DEFAULT FALSE,
    watched BOOLEAN DEFAULT FALSE,
    schema_name TEXT,
    created_at TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- SAVED QUERIES (V9, V10)
-- =============================================================================
CREATE TABLE IF NOT EXISTS query (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    query_json TEXT NOT NULL,
    created_date TIMESTAMP DEFAULT NOW(),
    modified_date TIMESTAMP DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS query_item (
    id SERIAL PRIMARY KEY,
    query_id INT NOT NULL REFERENCES query(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    value TEXT NOT NULL,
    created_date TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- TEXTUAL EMBEDDINGS LIBRARY (V2)
-- =============================================================================
CREATE TABLE IF NOT EXISTS textual_embedding (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    file_path TEXT NOT NULL,
    category TEXT NOT NULL,
    model_type TEXT NOT NULL,
    description TEXT,
    clip_l_embedding vector(768),
    clip_g_embedding vector(1280),
    raw_embedding BYTEA,
    created_at TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- EMBEDDING QUEUE (V6)
-- =============================================================================
CREATE TABLE IF NOT EXISTS embedding_queue (
    id SERIAL PRIMARY KEY,
    image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    folder_id INT NOT NULL,
    priority INT DEFAULT 0,
    status TEXT DEFAULT 'pending',
    queued_at TIMESTAMP DEFAULT NOW(),
    started_at TIMESTAMP,
    completed_at TIMESTAMP,
    error_message TEXT,
    retry_count INT DEFAULT 0,
    queued_by TEXT DEFAULT 'user'
);

CREATE TABLE IF NOT EXISTS embedding_worker_state (
    id INT PRIMARY KEY DEFAULT 1,
    status TEXT DEFAULT 'stopped',
    models_loaded BOOLEAN DEFAULT FALSE,
    started_at TIMESTAMP,
    paused_at TIMESTAMP,
    stopped_at TIMESTAMP,
    total_processed INT DEFAULT 0,
    total_failed INT DEFAULT 0,
    last_error TEXT,
    last_error_at TIMESTAMP,
    settings JSONB DEFAULT '{}'::jsonb,
    CONSTRAINT chk_worker_status CHECK (status IN ('stopped', 'running', 'paused'))
);

INSERT INTO embedding_worker_state (id, status, models_loaded)
VALUES (1, 'stopped', FALSE)
ON CONFLICT (id) DO NOTHING;

-- =============================================================================
-- TAGGING AND CAPTIONS (V7, V8)
-- =============================================================================
CREATE TABLE IF NOT EXISTS image_tags (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    tag TEXT NOT NULL,
    confidence REAL NOT NULL DEFAULT 1.0,
    source TEXT NOT NULL DEFAULT 'manual',
    created_at TIMESTAMP DEFAULT NOW(),
    CONSTRAINT unique_image_tag_source UNIQUE(image_id, tag, source)
);

CREATE TABLE IF NOT EXISTS image_captions (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    caption TEXT NOT NULL,
    source TEXT NOT NULL DEFAULT 'manual',
    prompt_used TEXT,
    is_user_edited BOOLEAN DEFAULT FALSE,
    token_count INTEGER,
    generation_time_ms REAL,
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- MODEL RESOURCE LIBRARY (V13)
-- =============================================================================
CREATE TABLE IF NOT EXISTS model_resource (
    id SERIAL PRIMARY KEY,
    file_path TEXT NOT NULL UNIQUE,
    file_name TEXT NOT NULL,
    file_hash TEXT,
    file_size BIGINT,
    resource_type TEXT NOT NULL,
    base_model TEXT,
    local_metadata JSONB,
    has_preview_image BOOLEAN DEFAULT FALSE,
    
    -- Civitai enrichment
    civitai_id INT,
    civitai_version_id INT,
    civitai_name TEXT,
    civitai_description TEXT,
    civitai_tags TEXT[],
    civitai_nsfw BOOLEAN DEFAULT FALSE,
    civitai_trained_words TEXT[],
    civitai_base_model TEXT,
    civitai_metadata JSONB,
    civitai_author TEXT,
    civitai_cover_image_url TEXT,
    civitai_thumbnail BYTEA,
    civitai_default_weight DECIMAL(5,3),
    civitai_default_clip_weight DECIMAL(5,3),
    civitai_published_at TIMESTAMP,
    
    -- Embeddings
    clip_l_embedding vector(768),
    clip_g_embedding vector(1280),
    
    unavailable BOOLEAN DEFAULT FALSE,
    scanned_at TIMESTAMP DEFAULT NOW(),
    civitai_fetched_at TIMESTAMP,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS image_resource (
    id SERIAL PRIMARY KEY,
    image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    resource_id INT REFERENCES model_resource(id) ON DELETE SET NULL,
    resource_name TEXT NOT NULL,
    resource_type TEXT NOT NULL,
    strength DECIMAL(5,3),
    created_at TIMESTAMP DEFAULT NOW(),
    UNIQUE(image_id, resource_name, resource_type)
);

CREATE TABLE IF NOT EXISTS model_folder (
    id SERIAL PRIMARY KEY,
    path TEXT NOT NULL UNIQUE,
    resource_type TEXT NOT NULL,
    recursive BOOLEAN DEFAULT TRUE,
    enabled BOOLEAN DEFAULT TRUE,
    last_scanned TIMESTAMP,
    created_at TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- THUMBNAIL CACHE (V14)
-- =============================================================================
CREATE TABLE IF NOT EXISTS thumbnail (
    id SERIAL PRIMARY KEY,
    path TEXT NOT NULL,
    size INT NOT NULL,
    data BYTEA NOT NULL,
    created_at TIMESTAMP DEFAULT NOW(),
    UNIQUE(path, size)
);

-- =============================================================================
-- DAAM (DIFFUSION ATTENTIVE ATTRIBUTION MAPS)
-- =============================================================================
-- Stores cross-attention heatmaps for semantic understanding of generated images

CREATE TABLE IF NOT EXISTS daam_heatmap (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    
    -- Token information
    token TEXT NOT NULL,
    token_index INTEGER NOT NULL,
    is_negative BOOLEAN DEFAULT FALSE,
    
    -- Heatmap data
    heatmap_width INTEGER NOT NULL,
    heatmap_height INTEGER NOT NULL,
    heatmap_data BYTEA NOT NULL,
    compression_type TEXT DEFAULT 'zlib',
    
    -- Statistical metadata
    max_attention REAL NOT NULL,
    mean_attention REAL NOT NULL,
    total_attention REAL NOT NULL,
    coverage_area REAL NOT NULL,
    
    -- Spatial bounds
    bbox_x INTEGER,
    bbox_y INTEGER,
    bbox_width INTEGER,
    bbox_height INTEGER,
    
    -- DAAM generation metadata
    sampling_config JSONB,
    timestep_range TEXT,
    layer_aggregation TEXT DEFAULT 'mean',
    
    created_date TIMESTAMP DEFAULT NOW(),
    CONSTRAINT unique_token_per_image UNIQUE (image_id, token_index, is_negative)
);

CREATE INDEX IF NOT EXISTS idx_daam_heatmap_image_id ON daam_heatmap(image_id);
CREATE INDEX IF NOT EXISTS idx_daam_heatmap_token ON daam_heatmap(token);
CREATE INDEX IF NOT EXISTS idx_daam_heatmap_is_negative ON daam_heatmap(is_negative);
CREATE INDEX IF NOT EXISTS idx_daam_heatmap_max_attention ON daam_heatmap(max_attention DESC);

CREATE TABLE IF NOT EXISTS daam_semantic_group (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    
    group_name TEXT NOT NULL,
    member_tokens TEXT[] NOT NULL,
    is_negative BOOLEAN DEFAULT FALSE,
    
    merged_heatmap_data BYTEA NOT NULL,
    heatmap_width INTEGER NOT NULL,
    heatmap_height INTEGER NOT NULL,
    compression_type TEXT DEFAULT 'zlib',
    
    attention_density REAL NOT NULL,
    total_attention REAL NOT NULL,
    coverage_area REAL NOT NULL,
    auto_weight REAL NOT NULL,
    
    bbox_x INTEGER,
    bbox_y INTEGER,
    bbox_width INTEGER,
    bbox_height INTEGER,
    
    overlap_threshold REAL DEFAULT 0.9,
    merge_count INTEGER DEFAULT 1,
    
    created_date TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_daam_semantic_group_image_id ON daam_semantic_group(image_id);
CREATE INDEX IF NOT EXISTS idx_daam_semantic_group_name ON daam_semantic_group(group_name);
CREATE INDEX IF NOT EXISTS idx_daam_semantic_group_density ON daam_semantic_group(attention_density DESC);

CREATE TABLE IF NOT EXISTS daam_spatial_index (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    token TEXT NOT NULL,
    
    grid_size INTEGER DEFAULT 4,
    grid_cell_id INTEGER NOT NULL,
    cell_attention REAL NOT NULL,
    
    CONSTRAINT unique_token_cell_per_image UNIQUE (image_id, token, grid_cell_id)
);

CREATE INDEX IF NOT EXISTS idx_daam_spatial_token ON daam_spatial_index(token);
CREATE INDEX IF NOT EXISTS idx_daam_spatial_cell ON daam_spatial_index(grid_cell_id);
CREATE INDEX IF NOT EXISTS idx_daam_spatial_attention ON daam_spatial_index(cell_attention DESC);

-- DAAM helper functions
CREATE OR REPLACE FUNCTION find_images_by_token_location(
    query_token TEXT,
    grid_cells INTEGER[],
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

-- =============================================================================
-- FACE DETECTION
-- =============================================================================
CREATE TABLE IF NOT EXISTS face_detection (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    face_index INTEGER NOT NULL DEFAULT 0,
    
    -- Bounding box
    x INTEGER NOT NULL DEFAULT 0,
    y INTEGER NOT NULL DEFAULT 0,
    width INTEGER NOT NULL DEFAULT 0,
    height INTEGER NOT NULL DEFAULT 0,
    
    -- Detection metadata
    confidence REAL NOT NULL DEFAULT 0.0,
    quality_score REAL DEFAULT 0.0,
    sharpness_score REAL DEFAULT 0.0,
    
    -- Head pose
    pose_yaw REAL,
    pose_pitch REAL,
    pose_roll REAL,
    
    -- Cropped face image
    face_crop BYTEA,
    crop_width INTEGER DEFAULT 0,
    crop_height INTEGER DEFAULT 0,
    
    -- Face landmarks (5-point)
    landmarks JSONB,
    
    -- ArcFace embedding (512D)
    arcface_embedding vector(512),
    has_embedding BOOLEAN DEFAULT FALSE,
    
    -- Expression/Emotion
    expression TEXT,
    expression_confidence REAL,
    
    detection_model TEXT DEFAULT 'yolo11-face',
    face_group_id INTEGER,
    
    detected_date TIMESTAMP DEFAULT NOW(),
    processing_time_ms REAL DEFAULT 0.0
);

CREATE INDEX IF NOT EXISTS idx_face_detection_image_id ON face_detection(image_id);
CREATE INDEX IF NOT EXISTS idx_face_detection_group_id ON face_detection(face_group_id);
CREATE INDEX IF NOT EXISTS idx_face_detection_has_embedding ON face_detection(has_embedding) WHERE has_embedding = TRUE;
CREATE INDEX IF NOT EXISTS idx_face_detection_embedding 
    ON face_detection 
    USING ivfflat (arcface_embedding vector_cosine_ops)
    WITH (lists = 100)
    WHERE arcface_embedding IS NOT NULL;

CREATE TABLE IF NOT EXISTS face_group (
    id SERIAL PRIMARY KEY,
    name TEXT,
    representative_face_id INTEGER REFERENCES face_detection(id) ON DELETE SET NULL,
    
    face_count INTEGER DEFAULT 0,
    avg_confidence REAL DEFAULT 0.0,
    avg_quality_score REAL DEFAULT 0.0,
    thumbnail BYTEA,
    
    is_manual_group BOOLEAN DEFAULT FALSE,
    cluster_cohesion REAL,
    notes TEXT,
    
    created_date TIMESTAMP DEFAULT NOW(),
    modified_date TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_face_group_name ON face_group(name) WHERE name IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_face_group_representative ON face_group(representative_face_id);

CREATE TABLE IF NOT EXISTS face_group_member (
    id SERIAL PRIMARY KEY,
    face_group_id INTEGER NOT NULL REFERENCES face_group(id) ON DELETE CASCADE,
    face_detection_id INTEGER NOT NULL REFERENCES face_detection(id) ON DELETE CASCADE,
    similarity_score REAL DEFAULT 0.0,
    added_date TIMESTAMP DEFAULT NOW(),
    CONSTRAINT unique_face_in_group UNIQUE (face_detection_id)
);

CREATE INDEX IF NOT EXISTS idx_face_group_member_group ON face_group_member(face_group_id);
CREATE INDEX IF NOT EXISTS idx_face_group_member_face ON face_group_member(face_detection_id);

CREATE TABLE IF NOT EXISTS face_similarity (
    face_id_1 INTEGER NOT NULL REFERENCES face_detection(id) ON DELETE CASCADE,
    face_id_2 INTEGER NOT NULL REFERENCES face_detection(id) ON DELETE CASCADE,
    cosine_similarity REAL,
    computed_date TIMESTAMP DEFAULT NOW(),
    PRIMARY KEY (face_id_1, face_id_2)
);

CREATE INDEX IF NOT EXISTS idx_face_similarity_face1 ON face_similarity(face_id_1);
CREATE INDEX IF NOT EXISTS idx_face_similarity_face2 ON face_similarity(face_id_2);
CREATE INDEX IF NOT EXISTS idx_face_similarity_score ON face_similarity(cosine_similarity DESC);

-- Face detection trigger
CREATE OR REPLACE FUNCTION update_face_group_stats()
RETURNS TRIGGER AS $$
BEGIN
    UPDATE face_group
    SET 
        face_count = (SELECT COUNT(*) FROM face_group_member WHERE face_group_id = NEW.face_group_id),
        avg_confidence = (
            SELECT AVG(fd.confidence) 
            FROM face_group_member fgm 
            JOIN face_detection fd ON fgm.face_detection_id = fd.id 
            WHERE fgm.face_group_id = NEW.face_group_id
        ),
        avg_quality_score = (
            SELECT AVG(fd.quality_score) 
            FROM face_group_member fgm 
            JOIN face_detection fd ON fgm.face_detection_id = fd.id 
            WHERE fgm.face_group_id = NEW.face_group_id
        ),
        modified_date = NOW()
    WHERE id = NEW.face_group_id;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trigger_update_face_group_stats ON face_group_member;
CREATE TRIGGER trigger_update_face_group_stats
AFTER INSERT OR DELETE ON face_group_member
FOR EACH ROW
EXECUTE FUNCTION update_face_group_stats();

CREATE OR REPLACE FUNCTION find_similar_faces(
    query_face_id INTEGER,
    similarity_threshold REAL DEFAULT 0.6,
    max_results INTEGER DEFAULT 50
)
RETURNS TABLE (
    face_id INTEGER,
    image_id INTEGER,
    similarity REAL
) AS $$
BEGIN
    RETURN QUERY
    SELECT 
        fd.id AS face_id,
        fd.image_id,
        1 - (qfd.arcface_embedding <=> fd.arcface_embedding) AS similarity
    FROM 
        face_detection qfd
        CROSS JOIN face_detection fd
    WHERE 
        qfd.id = query_face_id
        AND fd.id != query_face_id
        AND fd.arcface_embedding IS NOT NULL
        AND qfd.arcface_embedding IS NOT NULL
        AND 1 - (qfd.arcface_embedding <=> fd.arcface_embedding) >= similarity_threshold
    ORDER BY 
        similarity DESC
    LIMIT max_results;
END;
$$ LANGUAGE plpgsql;
