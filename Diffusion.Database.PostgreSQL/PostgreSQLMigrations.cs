using Npgsql;
using Dapper;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// PostgreSQL schema migrations with pgvector support
/// </summary>
public class PostgreSQLMigrations
{
    private readonly NpgsqlConnection _connection;
    private readonly string _schema;
    private const int CurrentVersion = 11;

    public PostgreSQLMigrations(NpgsqlConnection connection, string schema = "public")
    {
        _connection = connection;
        _schema = schema;
    }

    public async Task UpdateAsync()
    {
        try
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

            // Apply schema migrations in order
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

            // Only insert version if migrations were applied
            if (currentVersion < CurrentVersion)
            {
                await _connection.ExecuteAsync(
                    "INSERT INTO schema_version (version) VALUES (@version) ON CONFLICT (version) DO NOTHING", 
                    new { version = CurrentVersion });
            }
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration UpdateAsync failed: {ex.Message}");
            Diffusion.Common.Logger.Log($"Stack: {ex.StackTrace}");
            throw;
        }
    }

    private async Task ApplyV1Async()
    {
        try
        {
            // Consolidated schema V1 - complete Diffusion Toolkit schema
            // Combines all historical migrations (V1-V14) into single setup
            
            // Read SQL from embedded resource
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = "Diffusion.Database.PostgreSQL.PostgreSQLMigrations_V1_Schema.sql";
            
            Diffusion.Common.Logger.Log($"Loading embedded resource: {resourceName}");
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                var available = string.Join(", ", assembly.GetManifestResourceNames());
                throw new InvalidOperationException($"Embedded resource not found: {resourceName}. Available: {available}");
            }
            
            using var reader = new System.IO.StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new InvalidOperationException("Migration SQL is empty");
            }
            
            Diffusion.Common.Logger.Log($"Executing V1 schema SQL ({sql.Length} chars)...");
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("Creating indexes...");
            // Create indexes
            await CreateIndexesAsync();
            
            Diffusion.Common.Logger.Log("Creating functions and triggers...");
            // Create functions and triggers
            await CreateFunctionsAsync();
            
            Diffusion.Common.Logger.Log("V1 migration completed successfully");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"ApplyV1Async failed: {ex.Message}");
            Diffusion.Common.Logger.Log($"Stack: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// V2: Add tagging/captioning queue columns
    /// </summary>
    private async Task ApplyV2Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V2 migration: tagging/captioning queue columns...");
            
            var sql = @"
                -- Add queue columns for background tagging/captioning
                ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_tagging BOOLEAN DEFAULT FALSE;
                ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_captioning BOOLEAN DEFAULT FALSE;
                
                -- Create indexes for efficient queue queries
                CREATE INDEX IF NOT EXISTS idx_image_needs_tagging ON image (needs_tagging) WHERE needs_tagging = true;
                CREATE INDEX IF NOT EXISTS idx_image_needs_captioning ON image (needs_captioning) WHERE needs_captioning = true;
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V2 migration completed successfully");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"ApplyV2Async failed: {ex.Message}");
            Diffusion.Common.Logger.Log($"Stack: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// V3: Fix image_embedding dimension (1024 → 1280 for CLIP-ViT-H/14)
    /// </summary>
    private async Task ApplyV3Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V3 migration: Fix CLIP-ViT-H/14 embedding dimension + add needs_embedding...");
            
            var sql = @"
                -- Add needs_embedding queue column
                ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_embedding BOOLEAN DEFAULT FALSE;
                CREATE INDEX IF NOT EXISTS idx_image_needs_embedding ON image (needs_embedding) WHERE needs_embedding = true;
                
                -- Check if image_embedding column exists and has wrong dimension
                DO $$ 
                BEGIN
                    -- Drop existing image_embedding if it's 1024D (wrong dimension)
                    -- Note: This will delete any existing embeddings - they'll need to be regenerated
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns 
                        WHERE table_name = 'image' 
                        AND column_name = 'image_embedding'
                    ) THEN
                        -- Drop the old column (CASCADE drops the index too)
                        ALTER TABLE image DROP COLUMN image_embedding CASCADE;
                        
                        -- Recreate with correct dimension
                        ALTER TABLE image ADD COLUMN image_embedding vector(1280);
                        
                        -- Recreate the IVFFlat index for vector search
                        CREATE INDEX idx_image_image_embedding ON image 
                        USING ivfflat (image_embedding vector_cosine_ops) 
                        WITH (lists = 100);
                        
                        RAISE NOTICE 'Updated image_embedding to 1280D (CLIP-ViT-H/14). Previous embeddings deleted.';
                    ELSE
                        RAISE NOTICE 'image_embedding column does not exist yet';
                    END IF;
                END $$;
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V3 migration completed successfully");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"ApplyV3Async failed: {ex.Message}");
            Diffusion.Common.Logger.Log($"Stack: {ex.StackTrace}");
            throw;
        }
    }

    private async Task ApplyV4Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V4 migration: Face detection tables...");
            
            var sql = @"
                -- Face cluster table (groups of similar faces / characters)
                CREATE TABLE IF NOT EXISTS face_cluster (
                    id SERIAL PRIMARY KEY,
                    label TEXT,
                    representative_face_ids INTEGER[],
                    face_count INTEGER DEFAULT 0,
                    avg_quality_score REAL,
                    avg_confidence REAL,
                    cluster_thumbnail BYTEA,
                    is_manual BOOLEAN DEFAULT FALSE,
                    notes TEXT,
                    created_at TIMESTAMP DEFAULT NOW(),
                    updated_at TIMESTAMP DEFAULT NOW()
                );

                -- Face detection table (individual face detections)
                CREATE TABLE IF NOT EXISTS face_detection (
                    id SERIAL PRIMARY KEY,
                    image_id INTEGER REFERENCES image(id) ON DELETE CASCADE,
                    
                    -- Bounding box
                    bbox_x INTEGER,
                    bbox_y INTEGER,
                    bbox_width INTEGER,
                    bbox_height INTEGER,
                    
                    -- Cropped face image (JPEG bytes)
                    face_crop BYTEA,
                    crop_width INTEGER,
                    crop_height INTEGER,
                    
                    -- ArcFace embedding (512D)
                    arcface_embedding vector(512),
                    
                    -- Detection metadata
                    detection_model TEXT,
                    confidence REAL,
                    quality_score REAL,
                    sharpness_score REAL,
                    
                    -- Head pose
                    pose_yaw REAL,
                    pose_pitch REAL,
                    pose_roll REAL,
                    
                    -- Landmarks (5-point, stored as JSON)
                    landmarks JSONB,
                    
                    -- Character labeling
                    face_cluster_id INTEGER REFERENCES face_cluster(id) ON DELETE SET NULL,
                    character_label TEXT,
                    manual_label BOOLEAN DEFAULT FALSE,
                    
                    created_at TIMESTAMP DEFAULT NOW()
                );

                -- Face similarity pairs (pre-computed for fast lookups)
                CREATE TABLE IF NOT EXISTS face_similarity (
                    face_id_1 INTEGER REFERENCES face_detection(id) ON DELETE CASCADE,
                    face_id_2 INTEGER REFERENCES face_detection(id) ON DELETE CASCADE,
                    similarity_score REAL,
                    PRIMARY KEY (face_id_1, face_id_2)
                );

                -- Face timeline (track same character across images)
                CREATE TABLE IF NOT EXISTS face_timeline (
                    id SERIAL PRIMARY KEY,
                    cluster_id INTEGER REFERENCES face_cluster(id) ON DELETE CASCADE,
                    image_id INTEGER REFERENCES image(id) ON DELETE CASCADE,
                    face_id INTEGER REFERENCES face_detection(id) ON DELETE CASCADE,
                    appearance_date TIMESTAMP,
                    sequence_order INTEGER
                );

                -- Scene composition (multi-face analysis)
                CREATE TABLE IF NOT EXISTS scene_composition (
                    id SERIAL PRIMARY KEY,
                    image_id INTEGER REFERENCES image(id) ON DELETE CASCADE UNIQUE,
                    face_count INTEGER,
                    characters_present TEXT[],
                    spatial_layout TEXT,
                    interaction_type TEXT,
                    created_at TIMESTAMP DEFAULT NOW()
                );

                -- Add face-related columns to image table
                ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_face_detection BOOLEAN DEFAULT FALSE;
                ALTER TABLE image ADD COLUMN IF NOT EXISTS face_count INTEGER DEFAULT 0;
                ALTER TABLE image ADD COLUMN IF NOT EXISTS primary_character TEXT;
                ALTER TABLE image ADD COLUMN IF NOT EXISTS characters_detected TEXT[];

                -- Indexes for face detection
                CREATE INDEX IF NOT EXISTS idx_face_detection_image_id ON face_detection(image_id);
                CREATE INDEX IF NOT EXISTS idx_face_detection_cluster_id ON face_detection(face_cluster_id);
                CREATE INDEX IF NOT EXISTS idx_face_detection_character_label ON face_detection(character_label);
                CREATE INDEX IF NOT EXISTS idx_face_detection_confidence ON face_detection(confidence);
                CREATE INDEX IF NOT EXISTS idx_face_detection_quality ON face_detection(quality_score);

                -- Vector index for face similarity search (ArcFace 512D)
                CREATE INDEX IF NOT EXISTS idx_face_arcface_embedding ON face_detection 
                    USING ivfflat (arcface_embedding vector_cosine_ops) WITH (lists = 100);

                -- Indexes for face cluster
                CREATE INDEX IF NOT EXISTS idx_face_cluster_label ON face_cluster(label);
                CREATE INDEX IF NOT EXISTS idx_face_cluster_face_count ON face_cluster(face_count);

                -- Indexes for similarity pairs
                CREATE INDEX IF NOT EXISTS idx_face_similarity_score ON face_similarity(similarity_score);

                -- Indexes for timeline
                CREATE INDEX IF NOT EXISTS idx_face_timeline_cluster_id ON face_timeline(cluster_id);
                CREATE INDEX IF NOT EXISTS idx_face_timeline_image_id ON face_timeline(image_id);

                -- Index for image face columns
                CREATE INDEX IF NOT EXISTS idx_image_needs_face_detection ON image(needs_face_detection) WHERE needs_face_detection = true;
                CREATE INDEX IF NOT EXISTS idx_image_face_count ON image(face_count);
                CREATE INDEX IF NOT EXISTS idx_image_primary_character ON image(primary_character);

                -- GIN index for character array search
                CREATE INDEX IF NOT EXISTS idx_image_characters_detected ON image USING GIN(characters_detected);
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V4 migration completed successfully");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"ApplyV4Async failed: {ex.Message}");
            Diffusion.Common.Logger.Log($"Stack: {ex.StackTrace}");
            throw;
        }
    }

    private async Task CreateIndexesAsync()
    {
        var sql = @"
            -- IMAGE TABLE INDEXES
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
            CREATE INDEX IF NOT EXISTS idx_image_vae ON image(vae);
            CREATE INDEX IF NOT EXISTS idx_image_refiner_model ON image(refiner_model);
            CREATE INDEX IF NOT EXISTS idx_image_upscaler ON image(upscaler);
            CREATE INDEX IF NOT EXISTS idx_image_scheduler ON image(scheduler);
            CREATE INDEX IF NOT EXISTS idx_image_metadata_hash ON image(metadata_hash);
            CREATE INDEX IF NOT EXISTS idx_image_embedding_source_id ON image(embedding_source_id);
            CREATE INDEX IF NOT EXISTS idx_image_is_embedding_representative ON image(is_embedding_representative);
            CREATE INDEX IF NOT EXISTS idx_image_prompt_cache ON image(prompt_embedding_id);
            CREATE INDEX IF NOT EXISTS idx_image_negative_cache ON image(negative_prompt_embedding_id);
            CREATE INDEX IF NOT EXISTS idx_image_visual_cache ON image(image_embedding_id);
            CREATE INDEX IF NOT EXISTS idx_image_base_id ON image(base_image_id);
            CREATE INDEX IF NOT EXISTS idx_image_is_upscaled ON image(is_upscaled);
            CREATE INDEX IF NOT EXISTS idx_image_scan_phase ON image (scan_phase) WHERE scan_phase = 0;
            
            -- V8: Unified schema indexes
            CREATE UNIQUE INDEX IF NOT EXISTS idx_image_uuid ON image(image_uuid);
            CREATE INDEX IF NOT EXISTS idx_image_story_id ON image(story_id) WHERE story_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_image_character_id ON image(character_id) WHERE character_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_image_location_id ON image(location_id) WHERE location_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_image_type ON image(image_type) WHERE image_type IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_image_status ON image(status) WHERE status IS NOT NULL;
            
            -- Composite indexes
            CREATE INDEX IF NOT EXISTS idx_image_for_deletion_created_date ON image (for_deletion, created_date);
            CREATE INDEX IF NOT EXISTS idx_image_nsfw_for_deletion_unavailable ON image (nsfw, for_deletion, unavailable, created_date);
            CREATE INDEX IF NOT EXISTS idx_image_needs_visual ON image(needs_visual_embedding) WHERE needs_visual_embedding = true;
            
            -- Full-text search
            CREATE INDEX IF NOT EXISTS idx_image_generated_tags ON image USING gin(to_tsvector('english', COALESCE(generated_tags, '')));
            
            -- JSONB indexes
            CREATE INDEX IF NOT EXISTS idx_image_loras ON image USING gin(loras);
            CREATE INDEX IF NOT EXISTS idx_image_controlnets ON image USING gin(controlnets);
            CREATE INDEX IF NOT EXISTS idx_image_wildcards ON image USING gin(wildcards_used);
            
            -- Vector indexes (IVFFlat for approximate nearest neighbor)
            CREATE INDEX IF NOT EXISTS idx_image_prompt_embedding ON image USING ivfflat (prompt_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_negative_prompt_embedding ON image USING ivfflat (negative_prompt_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_image_embedding ON image USING ivfflat (image_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_clip_l_embedding ON image USING ivfflat (clip_l_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_image_clip_g_embedding ON image USING ivfflat (clip_g_embedding vector_cosine_ops) WITH (lists = 100);

            -- EMBEDDING CACHE INDEXES
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_hash ON embedding_cache(content_hash);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_type ON embedding_cache(content_type);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_refcount ON embedding_cache(reference_count DESC);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_bge ON embedding_cache USING ivfflat (bge_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_clip_l ON embedding_cache USING ivfflat (clip_l_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_clip_g ON embedding_cache USING ivfflat (clip_g_embedding vector_cosine_ops) WITH (lists = 100);
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_clip_h ON embedding_cache USING ivfflat (clip_h_embedding vector_cosine_ops) WITH (lists = 100);

            -- ALBUM INDEXES
            CREATE INDEX IF NOT EXISTS idx_album_name ON album (name);
            CREATE INDEX IF NOT EXISTS idx_album_order ON album (""order"");
            CREATE INDEX IF NOT EXISTS idx_album_last_updated ON album (last_updated);
            CREATE INDEX IF NOT EXISTS idx_album_image_album_id ON album_image (album_id);
            CREATE INDEX IF NOT EXISTS idx_album_image_image_id ON album_image (image_id);

            -- NODE INDEXES
            CREATE INDEX IF NOT EXISTS idx_node_image_id ON node (image_id);
            CREATE INDEX IF NOT EXISTS idx_node_class_type ON node (class_type);
            CREATE INDEX IF NOT EXISTS idx_node_property_node_id ON node_property (node_id);
            CREATE INDEX IF NOT EXISTS idx_node_property_name ON node_property (name);

            -- FOLDER INDEXES
            CREATE INDEX IF NOT EXISTS idx_folder_parent_id ON folder (parent_id);
            CREATE INDEX IF NOT EXISTS idx_folder_root_folder_id ON folder (root_folder_id);
            CREATE INDEX IF NOT EXISTS idx_folder_path ON folder (path);
            CREATE INDEX IF NOT EXISTS idx_folder_path_tree ON folder USING gist(path_tree);
            CREATE INDEX IF NOT EXISTS idx_folder_unavailable ON folder (unavailable);
            CREATE INDEX IF NOT EXISTS idx_folder_is_root ON folder (is_root);
            CREATE INDEX IF NOT EXISTS idx_folder_watched ON folder (watched);

            -- QUERY INDEXES
            CREATE INDEX IF NOT EXISTS idx_query_name ON query (name);
            CREATE INDEX IF NOT EXISTS idx_query_item_query_id ON query_item (query_id);
            CREATE INDEX IF NOT EXISTS idx_query_item_type ON query_item (type);

            -- TEXTUAL EMBEDDING INDEXES
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_name ON textual_embedding (name);
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_category ON textual_embedding (category);
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_model_type ON textual_embedding (model_type);
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_clip_l ON textual_embedding USING ivfflat (clip_l_embedding vector_cosine_ops) WITH (lists = 50);
            CREATE INDEX IF NOT EXISTS idx_textual_embedding_clip_g ON textual_embedding USING ivfflat (clip_g_embedding vector_cosine_ops) WITH (lists = 50);

            -- EMBEDDING QUEUE INDEXES
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_status ON embedding_queue(status);
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_priority ON embedding_queue(priority DESC, queued_at ASC);
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_image_id ON embedding_queue(image_id);
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_folder_id ON embedding_queue(folder_id);
            CREATE INDEX IF NOT EXISTS idx_embedding_queue_processing ON embedding_queue(status, priority DESC, queued_at ASC) WHERE status = 'pending';
            CREATE UNIQUE INDEX IF NOT EXISTS idx_embedding_queue_unique_pending ON embedding_queue(image_id) WHERE status IN ('pending', 'processing');

            -- IMAGE TAGS INDEXES
            CREATE INDEX IF NOT EXISTS idx_image_tags_image_id ON image_tags(image_id);
            CREATE INDEX IF NOT EXISTS idx_image_tags_tag ON image_tags(tag);
            CREATE INDEX IF NOT EXISTS idx_image_tags_confidence ON image_tags(confidence DESC);
            CREATE INDEX IF NOT EXISTS idx_image_tags_source ON image_tags(source);
            CREATE INDEX IF NOT EXISTS idx_image_tags_tag_confidence ON image_tags(tag, confidence DESC);

            -- IMAGE CAPTIONS INDEXES
            CREATE INDEX IF NOT EXISTS idx_image_captions_image_id ON image_captions(image_id);
            CREATE INDEX IF NOT EXISTS idx_image_captions_source ON image_captions(source);
            CREATE INDEX IF NOT EXISTS idx_image_captions_is_user_edited ON image_captions(is_user_edited);
            CREATE INDEX IF NOT EXISTS idx_image_captions_caption_fts ON image_captions USING gin(to_tsvector('english', caption));

            -- MODEL RESOURCE INDEXES
            CREATE INDEX IF NOT EXISTS idx_model_resource_hash ON model_resource(file_hash);
            CREATE INDEX IF NOT EXISTS idx_model_resource_type ON model_resource(resource_type);
            CREATE INDEX IF NOT EXISTS idx_model_resource_base_model ON model_resource(base_model);
            CREATE INDEX IF NOT EXISTS idx_model_resource_civitai_id ON model_resource(civitai_id);
            CREATE INDEX IF NOT EXISTS idx_model_resource_name ON model_resource(file_name);
            CREATE INDEX IF NOT EXISTS idx_model_resource_unavailable ON model_resource(unavailable);
            CREATE INDEX IF NOT EXISTS idx_model_resource_clip_l ON model_resource USING ivfflat (clip_l_embedding vector_cosine_ops) WITH (lists = 50) WHERE clip_l_embedding IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_model_resource_clip_g ON model_resource USING ivfflat (clip_g_embedding vector_cosine_ops) WITH (lists = 50) WHERE clip_g_embedding IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_image_resource_image ON image_resource(image_id);
            CREATE INDEX IF NOT EXISTS idx_image_resource_resource ON image_resource(resource_id);
            CREATE INDEX IF NOT EXISTS idx_image_resource_name ON image_resource(resource_name);
            CREATE INDEX IF NOT EXISTS idx_image_resource_type ON image_resource(resource_type);

            -- THUMBNAIL INDEXES
            CREATE INDEX IF NOT EXISTS idx_thumbnail_path ON thumbnail(path);
            CREATE INDEX IF NOT EXISTS idx_thumbnail_created ON thumbnail(created_at);
        ";

        await _connection.ExecuteAsync(sql);
    }

    private async Task CreateFunctionsAsync()
    {
        var sql = @"
            -- Update embedding cache last_used_at when referenced
            CREATE OR REPLACE FUNCTION update_embedding_last_used()
            RETURNS TRIGGER AS $$
            BEGIN
                UPDATE embedding_cache 
                SET last_used_at = NOW() 
                WHERE id IN (NEW.prompt_embedding_id, NEW.negative_prompt_embedding_id, NEW.image_embedding_id);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trigger_update_embedding_last_used ON image;
            CREATE TRIGGER trigger_update_embedding_last_used
                AFTER INSERT OR UPDATE OF prompt_embedding_id, negative_prompt_embedding_id, image_embedding_id
                ON image
                FOR EACH ROW
                EXECUTE FUNCTION update_embedding_last_used();

            -- Handle embedding source deletion
            CREATE OR REPLACE FUNCTION handle_embedding_source_deletion()
            RETURNS TRIGGER AS $$
            BEGIN
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

            -- Compute metadata hash
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

            -- Get next embedding batch from queue
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

            -- Complete embedding queue item
            CREATE OR REPLACE FUNCTION complete_embedding_queue_item(queue_item_id INT)
            RETURNS VOID AS $$
            BEGIN
                UPDATE embedding_queue
                SET status = 'completed',
                    completed_at = NOW()
                WHERE id = queue_item_id;
            END;
            $$ LANGUAGE plpgsql;

            -- Fail embedding queue item
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

            -- Retry failed embeddings
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

            -- Update caption timestamp
            CREATE OR REPLACE FUNCTION update_caption_timestamp()
            RETURNS TRIGGER AS $$
            BEGIN
                NEW.updated_at = NOW();
                RETURN NEW;
            END;
                $$ LANGUAGE plpgsql;
                
                DROP TRIGGER IF EXISTS trigger_update_caption_timestamp ON image_captions;
                CREATE TRIGGER trigger_update_caption_timestamp
                    BEFORE UPDATE ON image_captions
                    FOR EACH ROW
                    EXECUTE FUNCTION update_caption_timestamp();
        ";
    
        await _connection.ExecuteAsync(sql);
    }

    private async Task ApplyV5Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V5 migration: Face Detection Schema");
            
            // Read SQL from embedded resource
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = "Diffusion.Database.PostgreSQL.PostgreSQLMigrations_FaceDetection.sql";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
            }
            
            using var reader = new System.IO.StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            
            Diffusion.Common.Logger.Log($"Executing Face Detection schema SQL ({sql.Length} chars)...");
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V5 Face Detection migration completed successfully");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration V5 failed: {ex.Message}");
            throw;
        }
    }

    private async Task ApplyV6Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V6 migration: DAAM Heatmaps Schema");
            
            // Read SQL from embedded resource
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = "Diffusion.Database.PostgreSQL.PostgreSQLMigrations_DAAM.sql";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
            }
            
            using var reader = new System.IO.StreamReader(stream);
            var sql = await reader.ReadToEndAsync();
            
            Diffusion.Common.Logger.Log($"Executing DAAM schema SQL ({sql.Length} chars)...");
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V6 DAAM migration completed successfully");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration V6 failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// V7: Embedding extraction tables + CivitAI registry
    /// </summary>
    private async Task ApplyV7Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V7 migration: Embedding extraction + CivitAI registry...");
            
            var sql = @"
-- Image embeddings junction table (explicit and implicit extracted embeddings)
CREATE TABLE IF NOT EXISTS image_embeddings (
    id SERIAL PRIMARY KEY,
    image_id INT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    embedding_name VARCHAR(255) NOT NULL,
    weight DECIMAL(5, 3) DEFAULT 1.0,
    is_implicit BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMP DEFAULT NOW(),
    UNIQUE(image_id, embedding_name)
);

CREATE INDEX IF NOT EXISTS idx_image_embeddings_image_id ON image_embeddings(image_id);
CREATE INDEX IF NOT EXISTS idx_image_embeddings_name ON image_embeddings(embedding_name);
CREATE INDEX IF NOT EXISTS idx_image_embeddings_is_implicit ON image_embeddings(is_implicit);

-- CivitAI embedding registry (Phase 2: populated from model_resource table)
CREATE TABLE IF NOT EXISTS embedding_registry (
    id SERIAL PRIMARY KEY,
    civitai_id INT UNIQUE,
    embedding_name VARCHAR(255) NOT NULL UNIQUE,
    description TEXT,
    trigger_phrase VARCHAR(255),
    base_model VARCHAR(100),
    trained_words TEXT[],
    nsfw BOOLEAN DEFAULT FALSE,
    author VARCHAR(255),
    published_at TIMESTAMP,
    civitai_metadata JSONB,
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_embedding_registry_name ON embedding_registry(embedding_name);
CREATE INDEX IF NOT EXISTS idx_embedding_registry_base_model ON embedding_registry(base_model);
CREATE INDEX IF NOT EXISTS idx_embedding_registry_civitai_id ON embedding_registry(civitai_id);
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V7 migration completed successfully");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration V7 failed: {ex.Message}");
            throw;
        }
    }

    private async Task ApplyV8Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V8 migration: Unified schema with UUID + narrative system integration...");
            
            var sql = @"
-- Add UUID column for cross-project integration
ALTER TABLE image ADD COLUMN IF NOT EXISTS image_uuid UUID DEFAULT gen_random_uuid() UNIQUE NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_image_uuid ON image(image_uuid);

-- Add narrative system relationship fields
ALTER TABLE image ADD COLUMN IF NOT EXISTS story_id UUID;
ALTER TABLE image ADD COLUMN IF NOT EXISTS turn_number INTEGER;
ALTER TABLE image ADD COLUMN IF NOT EXISTS character_id UUID;
ALTER TABLE image ADD COLUMN IF NOT EXISTS location_id UUID;

-- Add image categorization and workflow fields
ALTER TABLE image ADD COLUMN IF NOT EXISTS image_type VARCHAR(50);
ALTER TABLE image ADD COLUMN IF NOT EXISTS status VARCHAR(20);
ALTER TABLE image ADD COLUMN IF NOT EXISTS generation_time_seconds DECIMAL(6, 2);
ALTER TABLE image ADD COLUMN IF NOT EXISTS comfyui_workflow VARCHAR(100);
ALTER TABLE image ADD COLUMN IF NOT EXISTS error_message TEXT;

-- Create indexes for narrative relationships
CREATE INDEX IF NOT EXISTS idx_image_story_id ON image(story_id) WHERE story_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_image_character_id ON image(character_id) WHERE character_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_image_location_id ON image(location_id) WHERE location_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_image_type ON image(image_type) WHERE image_type IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_image_status ON image(status) WHERE status IS NOT NULL;

-- Populate UUIDs for existing records
UPDATE image SET image_uuid = gen_random_uuid() WHERE image_uuid IS NULL;

-- Fix image sequence to prevent ID gaps after bulk operations
SELECT setval('image_id_seq', COALESCE((SELECT MAX(id) FROM image), 1), true);
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V8 migration completed successfully - UUID and narrative integration enabled");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration V8 failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// V9: Convert needs_* boolean columns to nullable with three-state logic:
    /// NULL = never queued, TRUE = needs processing, FALSE = completed
    /// </summary>
    private async Task ApplyV9Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V9 migration: Convert queue columns to nullable three-state logic...");
            
            var sql = @"
-- Change needs_* columns to nullable with NULL as default
-- NULL = never queued, TRUE = needs processing, FALSE = completed

-- needs_tagging: drop default, allow nulls
ALTER TABLE image ALTER COLUMN needs_tagging DROP DEFAULT;
ALTER TABLE image ALTER COLUMN needs_tagging DROP NOT NULL;
UPDATE image SET needs_tagging = NULL WHERE needs_tagging = false;

-- needs_captioning: drop default, allow nulls
ALTER TABLE image ALTER COLUMN needs_captioning DROP DEFAULT;
ALTER TABLE image ALTER COLUMN needs_captioning DROP NOT NULL;
UPDATE image SET needs_captioning = NULL WHERE needs_captioning = false;

-- needs_embedding: drop default, allow nulls
ALTER TABLE image ALTER COLUMN needs_embedding DROP DEFAULT;
ALTER TABLE image ALTER COLUMN needs_embedding DROP NOT NULL;
UPDATE image SET needs_embedding = NULL WHERE needs_embedding = false;

-- needs_face_detection: drop default, allow nulls
ALTER TABLE image ALTER COLUMN needs_face_detection DROP DEFAULT;
ALTER TABLE image ALTER COLUMN needs_face_detection DROP NOT NULL;
UPDATE image SET needs_face_detection = NULL WHERE needs_face_detection = false;

-- Add individual embedding status columns (nullable three-state)
-- NULL = not started, TRUE = in progress/queued, FALSE = completed
ALTER TABLE image ADD COLUMN IF NOT EXISTS bge_embedding_status BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS clip_l_embedding_status BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS clip_g_embedding_status BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS clip_vision_embedding_status BOOLEAN;

-- Add face detection step status columns
ALTER TABLE image ADD COLUMN IF NOT EXISTS face_detection_status BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS face_embedding_status BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS face_clustering_status BOOLEAN;

-- Recreate partial indexes for pending items (WHERE column = true)
DROP INDEX IF EXISTS idx_image_needs_tagging;
DROP INDEX IF EXISTS idx_image_needs_captioning;
DROP INDEX IF EXISTS idx_image_needs_embedding;
DROP INDEX IF EXISTS idx_image_needs_face_detection;

CREATE INDEX IF NOT EXISTS idx_image_needs_tagging ON image (needs_tagging) WHERE needs_tagging = true;
CREATE INDEX IF NOT EXISTS idx_image_needs_captioning ON image (needs_captioning) WHERE needs_captioning = true;
CREATE INDEX IF NOT EXISTS idx_image_needs_embedding ON image (needs_embedding) WHERE needs_embedding = true;
CREATE INDEX IF NOT EXISTS idx_image_needs_face_detection ON image (needs_face_detection) WHERE needs_face_detection = true;

-- Indexes for new embedding status columns
CREATE INDEX IF NOT EXISTS idx_image_bge_status ON image (bge_embedding_status) WHERE bge_embedding_status = true;
CREATE INDEX IF NOT EXISTS idx_image_clip_l_status ON image (clip_l_embedding_status) WHERE clip_l_embedding_status = true;
CREATE INDEX IF NOT EXISTS idx_image_clip_g_status ON image (clip_g_embedding_status) WHERE clip_g_embedding_status = true;
CREATE INDEX IF NOT EXISTS idx_image_clip_vision_status ON image (clip_vision_embedding_status) WHERE clip_vision_embedding_status = true;

-- Indexes for face detection status columns
CREATE INDEX IF NOT EXISTS idx_image_face_det_status ON image (face_detection_status) WHERE face_detection_status = true;
CREATE INDEX IF NOT EXISTS idx_image_face_emb_status ON image (face_embedding_status) WHERE face_embedding_status = true;
CREATE INDEX IF NOT EXISTS idx_image_face_cluster_status ON image (face_clustering_status) WHERE face_clustering_status = true;
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V9 migration completed successfully - queue columns now use three-state logic");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration V9 failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// V10: Granular embedding schema - separate BGE for prompt/caption/tags, CLIP for prompt only, T5-XXL stubs
    /// </summary>
    private async Task ApplyV10Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V10 migration: Granular embedding schema...");
            
            var sql = @"
-- =============================================================================
-- V10: GRANULAR EMBEDDING SCHEMA
-- =============================================================================
-- Purpose: Separate embeddings for different use cases:
--   - BGE embeddings: Semantic search (prompt, caption, tags separately)
--   - CLIP embeddings: SDXL regeneration (prompt only)
--   - CLIP Vision: Visual similarity (image pixels)
--   - T5-XXL: Flux regeneration (stubs for future implementation)
-- =============================================================================

-- STEP 1: Add new granular BGE embedding columns
-- BGE-large-en-v1.5 produces 1024-dim embeddings
ALTER TABLE image ADD COLUMN IF NOT EXISTS bge_prompt_embedding vector(1024);
ALTER TABLE image ADD COLUMN IF NOT EXISTS bge_caption_embedding vector(1024);
ALTER TABLE image ADD COLUMN IF NOT EXISTS bge_tags_embedding vector(1024);

-- STEP 2: Rename existing prompt_embedding to bge_prompt_embedding (migrate data)
-- Only if we have data in the old column and new column is empty
UPDATE image 
SET bge_prompt_embedding = prompt_embedding 
WHERE prompt_embedding IS NOT NULL AND bge_prompt_embedding IS NULL;

-- STEP 3: Rename existing CLIP columns to be prompt-specific
-- clip_l_embedding -> clip_l_prompt_embedding (SDXL regeneration)
-- clip_g_embedding -> clip_g_prompt_embedding (SDXL regeneration)
ALTER TABLE image ADD COLUMN IF NOT EXISTS clip_l_prompt_embedding vector(768);
ALTER TABLE image ADD COLUMN IF NOT EXISTS clip_g_prompt_embedding vector(1280);

-- Migrate existing data
UPDATE image 
SET clip_l_prompt_embedding = clip_l_embedding 
WHERE clip_l_embedding IS NOT NULL AND clip_l_prompt_embedding IS NULL;

UPDATE image 
SET clip_g_prompt_embedding = clip_g_embedding 
WHERE clip_g_embedding IS NOT NULL AND clip_g_prompt_embedding IS NULL;

-- STEP 4: Rename image_embedding to clip_vision_embedding for clarity
ALTER TABLE image ADD COLUMN IF NOT EXISTS clip_vision_embedding vector(1280);

UPDATE image 
SET clip_vision_embedding = image_embedding 
WHERE image_embedding IS NOT NULL AND clip_vision_embedding IS NULL;

-- STEP 5: Add T5-XXL stubs for future Flux support
-- T5-XXL produces 4096-dim embeddings
ALTER TABLE image ADD COLUMN IF NOT EXISTS t5xxl_prompt_embedding vector(4096);
ALTER TABLE image ADD COLUMN IF NOT EXISTS t5xxl_caption_embedding vector(4096);

-- STEP 6: Add granular status flags for each embedding type
-- true = needs processing, false = processed, null = not queued
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_bge_prompt_embedding BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_bge_caption_embedding BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_bge_tags_embedding BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_clip_l_prompt_embedding BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_clip_g_prompt_embedding BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_clip_vision_embedding BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_t5xxl_prompt_embedding BOOLEAN;
ALTER TABLE image ADD COLUMN IF NOT EXISTS needs_t5xxl_caption_embedding BOOLEAN;

-- STEP 7: Create partial indexes for queue processing (only index pending items)
CREATE INDEX IF NOT EXISTS idx_bge_prompt_pending ON image (needs_bge_prompt_embedding) WHERE needs_bge_prompt_embedding = true;
CREATE INDEX IF NOT EXISTS idx_bge_caption_pending ON image (needs_bge_caption_embedding) WHERE needs_bge_caption_embedding = true;
CREATE INDEX IF NOT EXISTS idx_bge_tags_pending ON image (needs_bge_tags_embedding) WHERE needs_bge_tags_embedding = true;
CREATE INDEX IF NOT EXISTS idx_clip_l_prompt_pending ON image (needs_clip_l_prompt_embedding) WHERE needs_clip_l_prompt_embedding = true;
CREATE INDEX IF NOT EXISTS idx_clip_g_prompt_pending ON image (needs_clip_g_prompt_embedding) WHERE needs_clip_g_prompt_embedding = true;
CREATE INDEX IF NOT EXISTS idx_clip_vision_pending ON image (needs_clip_vision_embedding) WHERE needs_clip_vision_embedding = true;
CREATE INDEX IF NOT EXISTS idx_t5xxl_prompt_pending ON image (needs_t5xxl_prompt_embedding) WHERE needs_t5xxl_prompt_embedding = true;
CREATE INDEX IF NOT EXISTS idx_t5xxl_caption_pending ON image (needs_t5xxl_caption_embedding) WHERE needs_t5xxl_caption_embedding = true;

-- STEP 8: Create vector similarity indexes for new columns (IVFFlat)
-- Only create for columns likely to have data - skip stubs
CREATE INDEX IF NOT EXISTS idx_bge_prompt_sim ON image USING ivfflat (bge_prompt_embedding vector_cosine_ops) WITH (lists = 100);
CREATE INDEX IF NOT EXISTS idx_bge_caption_sim ON image USING ivfflat (bge_caption_embedding vector_cosine_ops) WITH (lists = 100);
CREATE INDEX IF NOT EXISTS idx_bge_tags_sim ON image USING ivfflat (bge_tags_embedding vector_cosine_ops) WITH (lists = 100);
CREATE INDEX IF NOT EXISTS idx_clip_l_prompt_sim ON image USING ivfflat (clip_l_prompt_embedding vector_cosine_ops) WITH (lists = 100);
CREATE INDEX IF NOT EXISTS idx_clip_g_prompt_sim ON image USING ivfflat (clip_g_prompt_embedding vector_cosine_ops) WITH (lists = 100);
CREATE INDEX IF NOT EXISTS idx_clip_vision_sim ON image USING ivfflat (clip_vision_embedding vector_cosine_ops) WITH (lists = 100);

-- STEP 9: Update embedding_cache table to support new embedding types
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS bge_prompt_embedding vector(1024);
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS bge_caption_embedding vector(1024);
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS bge_tags_embedding vector(1024);
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS clip_l_prompt_embedding vector(768);
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS clip_g_prompt_embedding vector(1280);
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS clip_vision_embedding vector(1280);
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS t5xxl_prompt_embedding vector(4096);
ALTER TABLE embedding_cache ADD COLUMN IF NOT EXISTS t5xxl_caption_embedding vector(4096);

-- STEP 10: Drop old status columns from V9 (replaced by granular ones)
-- Keep old columns for now to avoid breaking existing code
-- ALTER TABLE image DROP COLUMN IF EXISTS bge_embedding_status;
-- ALTER TABLE image DROP COLUMN IF EXISTS clip_l_embedding_status;
-- ALTER TABLE image DROP COLUMN IF EXISTS clip_g_embedding_status;
-- ALTER TABLE image DROP COLUMN IF EXISTS clip_vision_embedding_status;

-- Add comment documenting the schema
COMMENT ON COLUMN image.bge_prompt_embedding IS 'BGE-large-en-v1.5 embedding of original prompt for semantic search';
COMMENT ON COLUMN image.bge_caption_embedding IS 'BGE-large-en-v1.5 embedding of AI-generated caption for semantic search';
COMMENT ON COLUMN image.bge_tags_embedding IS 'BGE-large-en-v1.5 embedding of tags (comma-joined) for semantic search';
COMMENT ON COLUMN image.clip_l_prompt_embedding IS 'CLIP-L (768d) embedding of prompt for SDXL regeneration';
COMMENT ON COLUMN image.clip_g_prompt_embedding IS 'CLIP-G (1280d) embedding of prompt for SDXL regeneration';
COMMENT ON COLUMN image.clip_vision_embedding IS 'CLIP-ViT-H/14 (1280d) embedding of image pixels for visual similarity';
COMMENT ON COLUMN image.t5xxl_prompt_embedding IS 'T5-XXL (4096d) embedding of prompt for Flux regeneration (stub)';
COMMENT ON COLUMN image.t5xxl_caption_embedding IS 'T5-XXL (4096d) embedding of caption for Flux regeneration (stub)';
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V10 migration completed successfully - granular embedding schema");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration V10 failed: {ex.Message}");
            throw;
        }
    }
    
    private async Task ApplyV11Async()
    {
        try
        {
            Diffusion.Common.Logger.Log("Applying V11 migration: Add schema_name to folder table for multi-schema support");
            
            var sql = @"
-- Add schema_name column to folder table for multi-collection support
ALTER TABLE folder ADD COLUMN IF NOT EXISTS schema_name TEXT DEFAULT 'public';

-- Update existing folders to current schema (run AFTER search_path is set)
UPDATE folder SET schema_name = current_schema() WHERE schema_name IS NULL OR schema_name = 'public';

-- Create index for fast schema lookups
CREATE INDEX IF NOT EXISTS idx_folder_schema_name ON folder(schema_name);

-- Create index for path prefix matching (used by Watcher to find folder for file path)
CREATE INDEX IF NOT EXISTS idx_folder_path_prefix ON folder(path text_pattern_ops);

-- Add comment documenting the column
COMMENT ON COLUMN folder.schema_name IS 'Schema/collection this folder belongs to - enables Watcher auto-routing';
            ";
            
            await _connection.ExecuteAsync(sql);
            
            Diffusion.Common.Logger.Log("V11 migration completed successfully - folder schema tracking added");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Migration V11 failed: {ex.Message}");
            throw;
        }
    }
}