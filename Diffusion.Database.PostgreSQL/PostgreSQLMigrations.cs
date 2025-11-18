using Npgsql;
using Dapper;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL schema migrations with pgvector support
/// </summary>
public class PostgreSQLMigrations
{
    private readonly NpgsqlConnection _connection;
    private const int CurrentVersion = 12;

    public PostgreSQLMigrations(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    public async Task UpdateAsync()
    {
        // Get current schema version
        var versionSql = @"
            CREATE TABLE IF NOT EXISTS schema_version (
                version INT PRIMARY KEY,
                applied_at TIMESTAMP DEFAULT NOW()
            );";

        await _connection.ExecuteAsync(versionSql);

        var currentVersion = await _connection.ExecuteScalarAsync<int?>(
            "SELECT MAX(version) FROM schema_version;") ?? 0;

        // Apply pending migrations
        if (currentVersion < 1) await ApplyV1Async();
        if (currentVersion < 2) await ApplyV2Async();
        if (currentVersion < 3) await ApplyV3Async();
        if (currentVersion < 4) await ApplyV4Async();
        if (currentVersion < 5) await ApplyV5Async();
        if (currentVersion < 6) await ApplyV6Async();
        if (currentVersion < 7) await ApplyV7Async();
        if (currentVersion < 8) await ApplyV8Async();
        if (currentVersion < 9) await ApplyV9Async();
        if (currentVersion < 10) await ApplyV10Async();
        if (currentVersion < 11) await ApplyV11Async();
        if (currentVersion < 12) await ApplyV12Async();

        // Only insert version if migrations were applied
        if (currentVersion < CurrentVersion)
        {
            await _connection.ExecuteAsync(
                "INSERT INTO schema_version (version) VALUES (@version) ON CONFLICT (version) DO NOTHING", 
                new { version = CurrentVersion });
        }
    }

    private async Task ApplyV1Async()
    {
        var sql = @"
            -- Main image table
            CREATE TABLE IF NOT EXISTS image (
                id SERIAL PRIMARY KEY,
                root_folder_id INT NOT NULL,
                folder_id INT NOT NULL,
                path TEXT NOT NULL UNIQUE,
                file_name TEXT NOT NULL,
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
                created_date TIMESTAMP NOT NULL,
                modified_date TIMESTAMP NOT NULL,
                custom_tags TEXT,
                rating INT,
                favorite BOOLEAN DEFAULT FALSE,
                for_deletion BOOLEAN DEFAULT FALSE,
                nsfw BOOLEAN DEFAULT FALSE,
                unavailable BOOLEAN DEFAULT FALSE,
                aesthetic_score DECIMAL(4, 2),
                hyper_network TEXT,
                hyper_network_strength DECIMAL(4, 2),
                clip_skip INT,
                ensd INT,
                file_size BIGINT DEFAULT 0,
                no_metadata BOOLEAN DEFAULT FALSE,
                workflow TEXT,
                workflow_id TEXT,
                has_error BOOLEAN DEFAULT FALSE,
                hash TEXT,
                viewed_date TIMESTAMP,
                touched_date TIMESTAMP,
                
                -- Vector embeddings for similarity search and ComfyUI integration
                prompt_embedding vector(1024),              -- BGE-large-en-v1.5 (semantic search)
                negative_prompt_embedding vector(1024),     -- BGE-large-en-v1.5 (semantic search)
                image_embedding vector(1024),               -- CLIP-ViT-H/14 (vision)
                clip_l_embedding vector(768),               -- SDXL CLIP-L text encoder
                clip_g_embedding vector(1280),              -- SDXL CLIP-G text encoder
                
                created_at TIMESTAMP DEFAULT NOW()
            );

            -- Indexes for metadata queries
            CREATE INDEX IF NOT EXISTS idx_image_root_folder_id ON image (root_folder_id);
            CREATE INDEX IF NOT EXISTS idx_image_folder_id ON image (folder_id);
            CREATE INDEX IF NOT EXISTS idx_image_path ON image (path);
            CREATE INDEX IF NOT EXISTS idx_image_file_name ON image (file_name);
            CREATE INDEX IF NOT EXISTS idx_image_model_hash ON image (model_hash);
            CREATE INDEX IF NOT EXISTS idx_image_model ON image (model);
            CREATE INDEX IF NOT EXISTS idx_image_seed ON image (seed);
            CREATE INDEX IF NOT EXISTS idx_image_sampler ON image (sampler);
            CREATE INDEX IF NOT EXISTS idx_image_height ON image (height);
            CREATE INDEX IF NOT EXISTS idx_image_width ON image (width);
            CREATE INDEX IF NOT EXISTS idx_image_cfg_scale ON image (cfg_scale);
            CREATE INDEX IF NOT EXISTS idx_image_steps ON image (steps);
            CREATE INDEX IF NOT EXISTS idx_image_aesthetic_score ON image (aesthetic_score);
            CREATE INDEX IF NOT EXISTS idx_image_favorite ON image (favorite);
            CREATE INDEX IF NOT EXISTS idx_image_rating ON image (rating);
            CREATE INDEX IF NOT EXISTS idx_image_for_deletion ON image (for_deletion);
            CREATE INDEX IF NOT EXISTS idx_image_nsfw ON image (nsfw);
            CREATE INDEX IF NOT EXISTS idx_image_unavailable ON image (unavailable);
            CREATE INDEX IF NOT EXISTS idx_image_created_date ON image (created_date);
            CREATE INDEX IF NOT EXISTS idx_image_hyper_network ON image (hyper_network);
            CREATE INDEX IF NOT EXISTS idx_image_file_size ON image (file_size);
            CREATE INDEX IF NOT EXISTS idx_image_modified_date ON image (modified_date);
            CREATE INDEX IF NOT EXISTS idx_image_has_error ON image (has_error);
            CREATE INDEX IF NOT EXISTS idx_image_hash ON image (hash);
            CREATE INDEX IF NOT EXISTS idx_image_viewed_date ON image (viewed_date);
            CREATE INDEX IF NOT EXISTS idx_image_touched_date ON image (touched_date);

            -- Composite indexes for common queries
            CREATE INDEX IF NOT EXISTS idx_image_for_deletion_created_date ON image (for_deletion, created_date);
            CREATE INDEX IF NOT EXISTS idx_image_nsfw_for_deletion_unavailable ON image (nsfw, for_deletion, unavailable, created_date);

            -- Vector indexes for similarity search (IVFFlat for faster approximate nearest neighbor)
            CREATE INDEX IF NOT EXISTS idx_image_prompt_embedding ON image USING ivfflat (prompt_embedding vector_cosine_ops) 
                WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_negative_prompt_embedding ON image USING ivfflat (negative_prompt_embedding vector_cosine_ops) 
                WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_image_embedding ON image USING ivfflat (image_embedding vector_cosine_ops) 
                WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_clip_l_embedding ON image USING ivfflat (clip_l_embedding vector_cosine_ops) 
                WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_clip_g_embedding ON image USING ivfflat (clip_g_embedding vector_cosine_ops) 
                WITH (lists = 100);

            -- Album table
            CREATE TABLE IF NOT EXISTS album (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                ""order"" INT DEFAULT 0,
                last_updated TIMESTAMP NOT NULL,
                created_at TIMESTAMP DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_album_name ON album (name);
            CREATE INDEX IF NOT EXISTS idx_album_order ON album (""order"");
            CREATE INDEX IF NOT EXISTS idx_album_last_updated ON album (last_updated);

            -- Album image junction table
            CREATE TABLE IF NOT EXISTS album_image (
                id SERIAL PRIMARY KEY,
                album_id INT NOT NULL REFERENCES album(id) ON DELETE CASCADE,
                image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
                added_at TIMESTAMP DEFAULT NOW(),
                UNIQUE(album_id, image_id)
            );

            CREATE INDEX IF NOT EXISTS idx_album_image_album_id ON album_image (album_id);
            CREATE INDEX IF NOT EXISTS idx_album_image_image_id ON album_image (image_id);

            -- Node/workflow table
            CREATE TABLE IF NOT EXISTS node (
                id SERIAL PRIMARY KEY,
                image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
                node_index INT,
                node_id TEXT,
                class_type TEXT,
                data JSONB,
                created_at TIMESTAMP DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_node_image_id ON node (image_id);
            CREATE INDEX IF NOT EXISTS idx_node_class_type ON node (class_type);
            
            -- Node property table for workflow node properties
            CREATE TABLE IF NOT EXISTS node_property (
                id SERIAL PRIMARY KEY,
                node_id TEXT NOT NULL,
                name TEXT NOT NULL,
                value TEXT,
                created_at TIMESTAMP DEFAULT NOW()
            );
            
            CREATE INDEX IF NOT EXISTS idx_node_property_node_id ON node_property (node_id);
            CREATE INDEX IF NOT EXISTS idx_node_property_name ON node_property (name);

            -- Folder table
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

            CREATE INDEX IF NOT EXISTS idx_folder_parent_id ON folder (parent_id);
            CREATE INDEX IF NOT EXISTS idx_folder_root_folder_id ON folder (root_folder_id);
            CREATE INDEX IF NOT EXISTS idx_folder_path ON folder (path);
            CREATE INDEX IF NOT EXISTS idx_folder_path_tree ON folder USING gist(path_tree);
            CREATE INDEX IF NOT EXISTS idx_folder_unavailable ON folder (unavailable);
            CREATE INDEX IF NOT EXISTS idx_folder_is_root ON folder (is_root);
            CREATE INDEX IF NOT EXISTS idx_folder_watched ON folder (watched);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV2Async()
    {
        var sql = @"
            -- Textual embedding library (positive/negative embeddings, characters, etc.)
            CREATE TABLE IF NOT EXISTS textual_embedding (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                file_path TEXT NOT NULL,
                category TEXT NOT NULL,             -- 'negative', 'positive', 'quality', 'character', 'detail_enhancer', 'other'
                model_type TEXT NOT NULL,           -- 'SDXL', 'Pony', 'Illustrious', 'SD1.5'
                description TEXT,
                
                -- CLIP embeddings for similarity search
                clip_l_embedding vector(768),       -- CLIP-L for SDXL
                clip_g_embedding vector(1280),      -- CLIP-G for SDXL
                
                -- Store raw safetensors data for re-export
                raw_embedding BYTEA,
                
                created_at TIMESTAMP DEFAULT NOW()
            );

            -- Indexes for textual embedding search
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_name ON textual_embedding (name);
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_category ON textual_embedding (category);
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_model_type ON textual_embedding (model_type);
            
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_clip_l ON textual_embedding 
                USING ivfflat (clip_l_embedding vector_cosine_ops) WITH (lists = 50);
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_clip_g ON textual_embedding 
                USING ivfflat (clip_g_embedding vector_cosine_ops) WITH (lists = 50);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV3Async()
    {
        var sql = @"
            -- Embedding cache table for deduplication
            -- Stores unique embeddings once, referenced by multiple images
            CREATE TABLE IF NOT EXISTS embedding_cache (
                id SERIAL PRIMARY KEY,
                
                -- Content identification
                content_hash TEXT NOT NULL UNIQUE,      -- SHA256 of prompt text or image hash
                content_type TEXT NOT NULL,             -- 'prompt', 'negative_prompt', 'image'
                content_text TEXT,                      -- Original text (for prompts)
                
                -- Embeddings (only populate relevant ones per content_type)
                bge_embedding vector(1024),             -- BGE for text (prompt/negative_prompt)
                clip_l_embedding vector(768),           -- CLIP-L for text
                clip_g_embedding vector(1280),          -- CLIP-G for text
                clip_h_embedding vector(1024),          -- CLIP-H for images only
                
                -- Usage statistics
                reference_count INTEGER DEFAULT 0,      -- How many images use this
                created_at TIMESTAMP DEFAULT NOW(),
                last_used_at TIMESTAMP DEFAULT NOW()
            );

            -- Indexes for embedding cache
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_hash ON embedding_cache(content_hash);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_type ON embedding_cache(content_type);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_refcount ON embedding_cache(reference_count DESC);
            
            -- Vector indexes for cache search
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_bge 
                ON embedding_cache USING ivfflat (bge_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_clip_l 
                ON embedding_cache USING ivfflat (clip_l_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_clip_g 
                ON embedding_cache USING ivfflat (clip_g_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_clip_h 
                ON embedding_cache USING ivfflat (clip_h_embedding vector_cosine_ops) WITH (lists = 100);

            -- Add foreign key columns to image table for embedding cache references
            ALTER TABLE image ADD COLUMN IF NOT EXISTS prompt_embedding_id INTEGER REFERENCES embedding_cache(id);
            ALTER TABLE image ADD COLUMN IF NOT EXISTS negative_prompt_embedding_id INTEGER REFERENCES embedding_cache(id);
            ALTER TABLE image ADD COLUMN IF NOT EXISTS image_embedding_id INTEGER REFERENCES embedding_cache(id);
            
            -- Add columns for base/upscale linking and visual embedding optimization
            ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_visual_embedding BOOLEAN DEFAULT true;
            ALTER TABLE image ADD COLUMN IF NOT EXISTS is_upscaled BOOLEAN DEFAULT false;
            ALTER TABLE image ADD COLUMN IF NOT EXISTS base_image_id INTEGER REFERENCES image(id);
            
            -- Indexes for new image columns
            CREATE INDEX IF NOT EXISTS idx_image_prompt_cache ON image(prompt_embedding_id);
            CREATE INDEX IF NOT EXISTS idx_image_negative_cache ON image(negative_prompt_embedding_id);
            CREATE INDEX IF NOT EXISTS idx_image_visual_cache ON image(image_embedding_id);
            CREATE INDEX IF NOT EXISTS idx_image_needs_visual ON image(needs_visual_embedding) 
                WHERE needs_visual_embedding = true;
            CREATE INDEX IF NOT EXISTS idx_image_base_id ON image(base_image_id);
            CREATE INDEX IF NOT EXISTS idx_image_is_upscaled ON image(is_upscaled);
            
            -- Function to automatically update last_used_at when embedding referenced
            CREATE OR REPLACE FUNCTION update_embedding_last_used()
            RETURNS TRIGGER AS $$
            BEGIN
                UPDATE embedding_cache 
                SET last_used_at = NOW() 
                WHERE id IN (NEW.prompt_embedding_id, NEW.negative_prompt_embedding_id, NEW.image_embedding_id);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            -- Trigger to update last_used_at on image insert/update
            DROP TRIGGER IF EXISTS trigger_update_embedding_last_used ON image;
            CREATE TRIGGER trigger_update_embedding_last_used
                AFTER INSERT OR UPDATE OF prompt_embedding_id, negative_prompt_embedding_id, image_embedding_id
                ON image
                FOR EACH ROW
                EXECUTE FUNCTION update_embedding_last_used();
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV4Async()
    {
        var sql = @"
            -- Sidecar .txt file content (AI-generated tags from WD14, BLIP, DeepDanbooru, etc.)
            ALTER TABLE image ADD COLUMN IF NOT EXISTS generated_tags TEXT;
            CREATE INDEX IF NOT EXISTS idx_image_generated_tags 
                ON image USING gin(to_tsvector('english', COALESCE(generated_tags, '')));
            
            -- LoRA/LyCORIS models with strengths (stored as JSONB array)
            ALTER TABLE image ADD COLUMN IF NOT EXISTS loras JSONB;
            CREATE INDEX IF NOT EXISTS idx_image_loras ON image USING gin(loras);
            
            -- VAE model
            ALTER TABLE image ADD COLUMN IF NOT EXISTS vae TEXT;
            CREATE INDEX IF NOT EXISTS idx_image_vae ON image(vae);
            
            -- Refiner model (SDXL)
            ALTER TABLE image ADD COLUMN IF NOT EXISTS refiner_model TEXT;
            ALTER TABLE image ADD COLUMN IF NOT EXISTS refiner_switch DECIMAL(4, 2);
            CREATE INDEX IF NOT EXISTS idx_image_refiner_model ON image(refiner_model);
            
            -- Upscaler
            ALTER TABLE image ADD COLUMN IF NOT EXISTS upscaler TEXT;
            ALTER TABLE image ADD COLUMN IF NOT EXISTS upscale_factor DECIMAL(3, 1);
            CREATE INDEX IF NOT EXISTS idx_image_upscaler ON image(upscaler);
            
            -- Hires fix parameters
            ALTER TABLE image ADD COLUMN IF NOT EXISTS hires_steps INT;
            ALTER TABLE image ADD COLUMN IF NOT EXISTS hires_upscaler TEXT;
            ALTER TABLE image ADD COLUMN IF NOT EXISTS hires_upscale DECIMAL(3, 1);
            ALTER TABLE image ADD COLUMN IF NOT EXISTS denoising_strength DECIMAL(4, 2);
            
            -- ControlNet configurations (JSONB array for multiple ControlNets)
            ALTER TABLE image ADD COLUMN IF NOT EXISTS controlnets JSONB;
            CREATE INDEX IF NOT EXISTS idx_image_controlnets ON image USING gin(controlnets);
            
            -- IP-Adapter
            ALTER TABLE image ADD COLUMN IF NOT EXISTS ip_adapter TEXT;
            ALTER TABLE image ADD COLUMN IF NOT EXISTS ip_adapter_strength DECIMAL(4, 2);
            
            -- Wildcards used in prompt (text array)
            ALTER TABLE image ADD COLUMN IF NOT EXISTS wildcards_used TEXT[];
            CREATE INDEX IF NOT EXISTS idx_image_wildcards ON image USING gin(wildcards_used);
            
            -- Generation time (for performance tracking)
            ALTER TABLE image ADD COLUMN IF NOT EXISTS generation_time_seconds DECIMAL(6, 2);
            
            -- Scheduler/sampler schedule type
            ALTER TABLE image ADD COLUMN IF NOT EXISTS scheduler TEXT;
            CREATE INDEX IF NOT EXISTS idx_image_scheduler ON image(scheduler);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV5Async()
    {
        var sql = @"
            -- Intelligent embedding deduplication (V5)
            -- Enables grouping of ORIG/FINAL pairs and prompt caching
            
            -- Metadata hash for identifying duplicate generation parameters
            ALTER TABLE image ADD COLUMN IF NOT EXISTS metadata_hash TEXT;
            CREATE INDEX IF NOT EXISTS idx_image_metadata_hash ON image(metadata_hash);
            
            -- Embedding source tracking for reference-based deduplication
            ALTER TABLE image ADD COLUMN IF NOT EXISTS embedding_source_id INT;
            CREATE INDEX IF NOT EXISTS idx_image_embedding_source_id ON image(embedding_source_id);
            
            -- Flag to identify which images were actually embedded (vs referenced)
            ALTER TABLE image ADD COLUMN IF NOT EXISTS is_embedding_representative BOOLEAN DEFAULT FALSE;
            CREATE INDEX IF NOT EXISTS idx_image_is_embedding_representative ON image(is_embedding_representative);
            
            -- Foreign key constraint (self-referencing)
            ALTER TABLE image ADD CONSTRAINT fk_image_embedding_source 
                FOREIGN KEY (embedding_source_id) REFERENCES image(id) ON DELETE SET NULL;
            
            -- Trigger to handle cascade when embedding source is deleted
            -- Automatically marks dependent images for re-embedding
            CREATE OR REPLACE FUNCTION handle_embedding_source_deletion()
            RETURNS TRIGGER AS $$
            BEGIN
                -- Find images that referenced the deleted image as their embedding source
                UPDATE image 
                SET embedding_source_id = NULL,
                    image_embedding = NULL,
                    is_embedding_representative = FALSE
                WHERE embedding_source_id = OLD.id;
                RETURN OLD;
            END;
            $$ LANGUAGE plpgsql;
            
            DROP TRIGGER IF EXISTS trg_handle_embedding_source_deletion ON image;
            CREATE TRIGGER trg_handle_embedding_source_deletion
                BEFORE DELETE ON image
                FOR EACH ROW
                EXECUTE FUNCTION handle_embedding_source_deletion();
            
            -- Function to compute metadata hash from image parameters
            -- Used by EmbeddingCacheService to identify duplicate generation settings
            CREATE OR REPLACE FUNCTION compute_metadata_hash(
                p_prompt TEXT,
                p_negative_prompt TEXT,
                p_model TEXT,
                p_seed BIGINT,
                p_steps INT,
                p_sampler TEXT,
                p_cfg_scale DECIMAL,
                p_width INT,
                p_height INT
            )
            RETURNS TEXT AS $$
            BEGIN
                -- SHA256 hash of concatenated parameters
                RETURN encode(
                    digest(
                        COALESCE(p_prompt, '') || '|' ||
                        COALESCE(p_negative_prompt, '') || '|' ||
                        COALESCE(p_model, '') || '|' ||
                        COALESCE(p_seed::TEXT, '0') || '|' ||
                        COALESCE(p_steps::TEXT, '0') || '|' ||
                        COALESCE(p_sampler, '') || '|' ||
                        COALESCE(p_cfg_scale::TEXT, '0.0') || '|' ||
                        COALESCE(p_width::TEXT, '0') || '|' ||
                        COALESCE(p_height::TEXT, '0'),
                        'sha256'
                    ),
                    'hex'
                );
            END;
            $$ LANGUAGE plpgsql IMMUTABLE;
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV6Async()
    {
        var sql = @"
            -- Embedding queue for manual user-controlled embedding generation (V6)
            -- Users right-click folders/images to queue them for embedding
            -- Control bar with Start/Pause/Stop buttons manages processing
            
            CREATE TABLE IF NOT EXISTS embedding_queue (
                id SERIAL PRIMARY KEY,
                image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
                folder_id INT NOT NULL,
                priority INT DEFAULT 0,  -- Higher = processed first (0=normal, 100=embed now)
                status TEXT DEFAULT 'pending',  -- pending, processing, completed, failed
                queued_at TIMESTAMP DEFAULT NOW(),
                started_at TIMESTAMP,
                completed_at TIMESTAMP,
                error_message TEXT,
                retry_count INT DEFAULT 0,
                queued_by TEXT DEFAULT 'user'  -- user, system, auto
            );
            
            -- Indexes for efficient queue processing
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_status ON embedding_queue(status);
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_priority ON embedding_queue(priority DESC, queued_at ASC);
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_image_id ON embedding_queue(image_id);
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_folder_id ON embedding_queue(folder_id);
            
            -- Composite index for queue processing (status + priority + queued_at)
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_processing 
                ON embedding_queue(status, priority DESC, queued_at ASC)
                WHERE status = 'pending';
            
            -- Prevent duplicate queue entries
            CREATE UNIQUE INDEX IF NOT EXISTS idx_embedding_queue_unique_pending
                ON embedding_queue(image_id)
                WHERE status IN ('pending', 'processing');
            
            -- Worker state table (persists across app restarts)
            CREATE TABLE IF NOT EXISTS embedding_worker_state (
                id INT PRIMARY KEY DEFAULT 1,  -- Singleton row
                status TEXT DEFAULT 'stopped',  -- stopped, running, paused
                models_loaded BOOLEAN DEFAULT FALSE,
                started_at TIMESTAMP,
                paused_at TIMESTAMP,
                stopped_at TIMESTAMP,
                total_processed INT DEFAULT 0,
                total_failed INT DEFAULT 0,
                last_error TEXT,
                last_error_at TIMESTAMP,
                settings JSONB DEFAULT '{}'::jsonb,  -- batch_size, auto_pause, etc.
                CONSTRAINT chk_worker_status CHECK (status IN ('stopped', 'running', 'paused'))
            );
            
            -- Initialize worker state
            INSERT INTO embedding_worker_state (id, status, models_loaded)
            VALUES (1, 'stopped', FALSE)
            ON CONFLICT (id) DO NOTHING;
            
            -- Function to get next batch from queue (priority-ordered)
            CREATE OR REPLACE FUNCTION get_next_embedding_batch(batch_size INT DEFAULT 32)
            RETURNS TABLE (
                queue_id INT,
                image_id INT,
                folder_id INT,
                priority INT
            ) AS $$
            BEGIN
                RETURN QUERY
                UPDATE embedding_queue
                SET status = 'processing',
                    started_at = NOW()
                WHERE id IN (
                    SELECT embedding_queue.id
                    FROM embedding_queue
                    WHERE status = 'pending'
                    ORDER BY priority DESC, queued_at ASC
                    LIMIT batch_size
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING 
                    embedding_queue.id AS queue_id,
                    embedding_queue.image_id,
                    embedding_queue.folder_id,
                    embedding_queue.priority;
            END;
            $$ LANGUAGE plpgsql;
            
            -- Function to mark queue item as completed
            CREATE OR REPLACE FUNCTION complete_embedding_queue_item(queue_item_id INT)
            RETURNS VOID AS $$
            BEGIN
                UPDATE embedding_queue
                SET status = 'completed',
                    completed_at = NOW()
                WHERE id = queue_item_id;
            END;
            $$ LANGUAGE plpgsql;
            
            -- Function to mark queue item as failed
            CREATE OR REPLACE FUNCTION fail_embedding_queue_item(
                queue_item_id INT,
                error_msg TEXT
            )
            RETURNS VOID AS $$
            BEGIN
                UPDATE embedding_queue
                SET status = 'failed',
                    error_message = error_msg,
                    retry_count = retry_count + 1,
                    completed_at = NOW()
                WHERE id = queue_item_id;
            END;
            $$ LANGUAGE plpgsql;
            
            -- Function to reset failed items for retry
            CREATE OR REPLACE FUNCTION retry_failed_embeddings()
            RETURNS INT AS $$
            DECLARE
                affected_count INT;
            BEGIN
                UPDATE embedding_queue
                SET status = 'pending',
                    started_at = NULL,
                    completed_at = NULL,
                    error_message = NULL
                WHERE status = 'failed' AND retry_count < 3;
                
                GET DIAGNOSTICS affected_count = ROW_COUNT;
                RETURN affected_count;
            END;
            $$ LANGUAGE plpgsql;
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV7Async()
    {
        var sql = @"
            -- Image tags table for storing auto-generated and manual tags
            CREATE TABLE IF NOT EXISTS image_tags (
                id SERIAL PRIMARY KEY,
                image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
                tag TEXT NOT NULL,
                confidence REAL NOT NULL DEFAULT 1.0,
                source TEXT NOT NULL DEFAULT 'manual',
                created_at TIMESTAMP DEFAULT NOW(),
                CONSTRAINT unique_image_tag_source UNIQUE(image_id, tag, source)
            );
            
            -- Indexes for fast tag lookups
            CREATE INDEX IF NOT EXISTS idx_image_tags_image_id ON image_tags(image_id);
            CREATE INDEX IF NOT EXISTS idx_image_tags_tag ON image_tags(tag);
            CREATE INDEX IF NOT EXISTS idx_image_tags_confidence ON image_tags(confidence DESC);
            CREATE INDEX IF NOT EXISTS idx_image_tags_source ON image_tags(source);
            
            -- Composite index for common queries
            CREATE INDEX IF NOT EXISTS idx_image_tags_tag_confidence 
                ON image_tags(tag, confidence DESC);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV8Async()
    {
        var sql = @"
            -- Image captions table for storing AI-generated and user-edited captions
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
            
            -- Indexes for fast caption lookups
            CREATE INDEX IF NOT EXISTS idx_image_captions_image_id ON image_captions(image_id);
            CREATE INDEX IF NOT EXISTS idx_image_captions_source ON image_captions(source);
            CREATE INDEX IF NOT EXISTS idx_image_captions_is_user_edited ON image_captions(is_user_edited);
            
            -- Full-text search index for caption content
            CREATE INDEX IF NOT EXISTS idx_image_captions_caption_fts 
                ON image_captions USING gin(to_tsvector('english', caption));
            
            -- Function to update updated_at timestamp
            CREATE OR REPLACE FUNCTION update_caption_timestamp()
            RETURNS TRIGGER AS $$
            BEGIN
                NEW.updated_at = NOW();
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            
            -- Trigger to automatically update timestamp on edit
            DROP TRIGGER IF EXISTS trigger_update_caption_timestamp ON image_captions;
            CREATE TRIGGER trigger_update_caption_timestamp
                BEFORE UPDATE ON image_captions
                FOR EACH ROW
                EXECUTE FUNCTION update_caption_timestamp();
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV9Async()
    {
        var sql = @"
            -- Add missing columns to folder table for complete SQLite compatibility
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS parent_id INT DEFAULT 0;
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS image_count INT DEFAULT 0;
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS scanned_date TIMESTAMP;
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS archived BOOLEAN DEFAULT FALSE;
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS excluded BOOLEAN DEFAULT FALSE;
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS is_root BOOLEAN DEFAULT FALSE;
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS recursive BOOLEAN DEFAULT FALSE;
            ALTER TABLE folder ADD COLUMN IF NOT EXISTS watched BOOLEAN DEFAULT FALSE;
            
            -- Add missing indexes for folder table
            CREATE INDEX IF NOT EXISTS idx_folder_parent_id ON folder (parent_id);
            CREATE INDEX IF NOT EXISTS idx_folder_is_root ON folder (is_root);
            CREATE INDEX IF NOT EXISTS idx_folder_watched ON folder (watched);
            
            -- Add missing 'order' column to album table
            ALTER TABLE album ADD COLUMN IF NOT EXISTS ""order"" INT DEFAULT 0;
            CREATE INDEX IF NOT EXISTS idx_album_order ON album (""order"");
            
            -- Create query table for saved search queries
            CREATE TABLE IF NOT EXISTS query (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                query_json TEXT NOT NULL,
                created_date TIMESTAMP DEFAULT NOW(),
                modified_date TIMESTAMP DEFAULT NOW()
            );
            
            CREATE INDEX IF NOT EXISTS idx_query_name ON query (name);
            
            -- Create query_item table for query components (legacy support)
            CREATE TABLE IF NOT EXISTS query_item (
                id SERIAL PRIMARY KEY,
                query_id INT NOT NULL REFERENCES query(id) ON DELETE CASCADE,
                type TEXT NOT NULL,
                value TEXT NOT NULL,
                created_date TIMESTAMP DEFAULT NOW()
            );
            
            CREATE INDEX IF NOT EXISTS idx_query_item_query_id ON query_item (query_id);
            CREATE INDEX IF NOT EXISTS idx_query_item_type ON query_item (type);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV10Async()
    {
        var sql = @"
            -- Create query table for saved search queries
            CREATE TABLE IF NOT EXISTS query (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                query_json TEXT NOT NULL,
                created_date TIMESTAMP DEFAULT NOW(),
                modified_date TIMESTAMP DEFAULT NOW()
            );
            
            CREATE INDEX IF NOT EXISTS idx_query_name ON query (name);
            
            -- Create query_item table for query components (legacy support)
            CREATE TABLE IF NOT EXISTS query_item (
                id SERIAL PRIMARY KEY,
                query_id INT NOT NULL REFERENCES query(id) ON DELETE CASCADE,
                type TEXT NOT NULL,
                value TEXT NOT NULL,
                created_date TIMESTAMP DEFAULT NOW()
            );
            
            CREATE INDEX IF NOT EXISTS idx_query_item_query_id ON query_item (query_id);
            CREATE INDEX IF NOT EXISTS idx_query_item_type ON query_item (type);
            
            -- Create node_property table for workflow node properties
            CREATE TABLE IF NOT EXISTS node_property (
                id SERIAL PRIMARY KEY,
                node_id TEXT NOT NULL,
                name TEXT NOT NULL,
                value TEXT,
                created_at TIMESTAMP DEFAULT NOW()
            );
            
            CREATE INDEX IF NOT EXISTS idx_node_property_node_id ON node_property (node_id);
            CREATE INDEX IF NOT EXISTS idx_node_property_name ON node_property (name);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV11Async()
    {
        // Fix node_property.node_id type: TEXT → INTEGER to match node.id
        var sql = @"
            -- Drop existing indexes
            DROP INDEX IF EXISTS idx_node_property_node_id;
            
            -- Drop existing data (feature doesn't exist in SQLite, safe to clear)
            TRUNCATE TABLE node_property CASCADE;
            
            -- Change node_id type from TEXT to INTEGER
            ALTER TABLE node_property ALTER COLUMN node_id TYPE INTEGER USING node_id::integer;
            
            -- Add foreign key constraint
            ALTER TABLE node_property ADD CONSTRAINT fk_node_property_node 
                FOREIGN KEY (node_id) REFERENCES node(id) ON DELETE CASCADE;
            
            -- Recreate index
            CREATE INDEX idx_node_property_node_id ON node_property (node_id);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV12Async()
    {
        // Add scan_phase column for two-phase scanning (quick scan → deep metadata scan)
        // Phase 0 (QuickScan) = only basic file info indexed
        // Phase 1 (DeepScan) = full metadata extracted and embedded
        // Existing images default to DeepScan (1) as they're already fully scanned
        var sql = @"
            -- Add scan_phase column to track metadata extraction completeness
            ALTER TABLE image ADD COLUMN IF NOT EXISTS scan_phase INTEGER DEFAULT 1 NOT NULL;
            
            -- Create index for finding quick-scanned images needing deep scan
            CREATE INDEX IF NOT EXISTS idx_image_scan_phase ON image (scan_phase) WHERE scan_phase = 0;
            
            -- Update all existing images to DeepScan (they already have metadata)
            UPDATE image SET scan_phase = 1 WHERE scan_phase = 0 OR scan_phase IS NULL;
        ";

        await _connection.ExecuteAsync(sql);
    }
}
