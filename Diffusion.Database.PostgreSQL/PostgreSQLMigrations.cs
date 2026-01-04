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
    private const int CurrentVersion = 4;

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
}
