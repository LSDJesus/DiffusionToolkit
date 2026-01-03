-- Consolidated PostgreSQL Schema V1
-- Complete schema for Diffusion Toolkit with pgvector support
-- Combines all historical migrations (V1-V14) into single initial setup

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
    
    -- Vector embeddings for similarity search
    prompt_embedding vector(1024),              -- BGE-large-en-v1.5
    negative_prompt_embedding vector(1024),     -- BGE-large-en-v1.5
    image_embedding vector(1024),               -- CLIP-ViT-H/14
    clip_l_embedding vector(768),               -- SDXL CLIP-L
    clip_g_embedding vector(1280),              -- SDXL CLIP-G
    
    created_at TIMESTAMP DEFAULT NOW()
);

-- =============================================================================
-- EMBEDDING CACHE TABLE (V3)
-- =============================================================================
CREATE TABLE IF NOT EXISTS embedding_cache (
    id SERIAL PRIMARY KEY,
    content_hash TEXT NOT NULL UNIQUE,
    content_type TEXT NOT NULL,
    content_text TEXT,
    
    bge_embedding vector(1024),
    clip_l_embedding vector(768),
    clip_g_embedding vector(1280),
    clip_h_embedding vector(1024),
    
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
