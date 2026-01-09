-- Face Detection Schema Migration
-- Adds tables for storing face detections, groups, and relationships

-- =============================================================================
-- FACE DETECTIONS TABLE
-- =============================================================================
CREATE TABLE IF NOT EXISTS face_detection (
    id SERIAL PRIMARY KEY,
    image_id INTEGER NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    face_index INTEGER NOT NULL,  -- 0-based index for faces in same image
    
    -- Bounding box coordinates (in original image pixels)
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    
    -- Detection metadata
    confidence REAL NOT NULL,
    quality_score REAL DEFAULT 0.0,
    sharpness_score REAL DEFAULT 0.0,
    
    -- Head pose (optional)
    pose_yaw REAL,
    pose_pitch REAL,
    pose_roll REAL,
    
    -- Cropped face image
    face_crop BYTEA,
    crop_width INTEGER DEFAULT 0,
    crop_height INTEGER DEFAULT 0,
    
    -- Face landmarks (5-point stored as JSON)
    landmarks JSONB,
    
    -- ArcFace embedding (512D vector for face recognition)
    arcface_embedding vector(512),
    has_embedding BOOLEAN DEFAULT FALSE,
    
    -- Expression/Emotion (optional - for future expansion)
    expression TEXT,
    expression_confidence REAL,
    
    -- Detection model used
    detection_model TEXT DEFAULT 'yolo11-face',
    
    -- Assignment to face group
    face_group_id INTEGER,
    
    -- Metadata
    detected_date TIMESTAMP DEFAULT NOW(),
    processing_time_ms REAL DEFAULT 0.0,
    
    -- Unique constraint: one face per index per image
    CONSTRAINT unique_face_per_image UNIQUE (image_id, face_index)
);

-- Create indexes for efficient querying
CREATE INDEX IF NOT EXISTS idx_face_detection_image_id ON face_detection(image_id);
CREATE INDEX IF NOT EXISTS idx_face_detection_group_id ON face_detection(face_group_id);
CREATE INDEX IF NOT EXISTS idx_face_detection_has_embedding ON face_detection(has_embedding) WHERE has_embedding = TRUE;

-- Create vector index for similarity search
CREATE INDEX IF NOT EXISTS idx_face_detection_embedding 
    ON face_detection 
    USING ivfflat (arcface_embedding vector_cosine_ops)
    WITH (lists = 100)
    WHERE arcface_embedding IS NOT NULL;

-- =============================================================================
-- FACE GROUPS TABLE (clusters of similar faces)
-- =============================================================================
CREATE TABLE IF NOT EXISTS face_group (
    id SERIAL PRIMARY KEY,
    name TEXT,  -- User-editable name/ID for the person
    
    -- Representative face (best quality face from this group)
    representative_face_id INTEGER REFERENCES face_detection(id) ON DELETE SET NULL,
    
    -- Group statistics
    face_count INTEGER DEFAULT 0,
    avg_confidence REAL DEFAULT 0.0,
    avg_quality_score REAL DEFAULT 0.0,
    
    -- Thumbnail (JPEG bytes of representative face)
    thumbnail BYTEA,
    
    -- Clustering metadata
    is_manual_group BOOLEAN DEFAULT FALSE,  -- User-created vs auto-clustered
    cluster_cohesion REAL,  -- Average similarity within cluster
    
    -- User notes
    notes TEXT,
    
    -- Timestamps
    created_date TIMESTAMP DEFAULT NOW(),
    modified_date TIMESTAMP
);

-- Create indexes
CREATE INDEX IF NOT EXISTS idx_face_group_name ON face_group(name) WHERE name IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_face_group_representative ON face_group(representative_face_id);

-- =============================================================================
-- FACE GROUP MEMBERS TABLE (many-to-many relationship)
-- =============================================================================
CREATE TABLE IF NOT EXISTS face_group_member (
    id SERIAL PRIMARY KEY,
    face_group_id INTEGER NOT NULL REFERENCES face_group(id) ON DELETE CASCADE,
    face_detection_id INTEGER NOT NULL REFERENCES face_detection(id) ON DELETE CASCADE,
    similarity_score REAL DEFAULT 0.0,  -- Cosine similarity to representative
    added_date TIMESTAMP DEFAULT NOW(),
    
    -- Ensure a face can only belong to one group
    CONSTRAINT unique_face_in_group UNIQUE (face_detection_id)
);

-- Create indexes
CREATE INDEX IF NOT EXISTS idx_face_group_member_group ON face_group_member(face_group_id);
CREATE INDEX IF NOT EXISTS idx_face_group_member_face ON face_group_member(face_detection_id);

-- =============================================================================
-- FACE SIMILARITY TABLE (pre-computed similarities for fast lookup)
-- =============================================================================
CREATE TABLE IF NOT EXISTS face_similarity (
    id SERIAL PRIMARY KEY,
    face_id_1 INTEGER NOT NULL REFERENCES face_detection(id) ON DELETE CASCADE,
    face_id_2 INTEGER NOT NULL REFERENCES face_detection(id) ON DELETE CASCADE,
    cosine_similarity REAL NOT NULL,
    computed_date TIMESTAMP DEFAULT NOW(),
    
    -- Ensure we don't store duplicates (always store lower ID first)
    CONSTRAINT unique_face_pair UNIQUE (face_id_1, face_id_2),
    CONSTRAINT ordered_face_ids CHECK (face_id_1 < face_id_2)
);

-- Create indexes
CREATE INDEX IF NOT EXISTS idx_face_similarity_face1 ON face_similarity(face_id_1);
CREATE INDEX IF NOT EXISTS idx_face_similarity_face2 ON face_similarity(face_id_2);
CREATE INDEX IF NOT EXISTS idx_face_similarity_score ON face_similarity(cosine_similarity DESC);

-- =============================================================================
-- TRIGGER: Update face_group statistics when members change
-- =============================================================================
CREATE OR REPLACE FUNCTION update_face_group_stats()
RETURNS TRIGGER AS $$
BEGIN
    -- Update face count and averages
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

CREATE TRIGGER trigger_update_face_group_stats
AFTER INSERT OR DELETE ON face_group_member
FOR EACH ROW
EXECUTE FUNCTION update_face_group_stats();

-- =============================================================================
-- HELPER FUNCTION: Find similar faces using cosine similarity
-- =============================================================================
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
