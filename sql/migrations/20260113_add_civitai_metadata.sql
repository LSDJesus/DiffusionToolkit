-- Migration: Add Civitai metadata columns to image table
-- Date: 2026-01-13
-- Purpose: Support Civitai JSON sidecar import with creator and platform metadata

-- Add Civitai-specific columns
ALTER TABLE image 
ADD COLUMN IF NOT EXISTS civitai_image_id BIGINT,
ADD COLUMN IF NOT EXISTS civitai_post_id BIGINT,
ADD COLUMN IF NOT EXISTS civitai_username VARCHAR(255),
ADD COLUMN IF NOT EXISTS civitai_nsfw_level VARCHAR(50),
ADD COLUMN IF NOT EXISTS civitai_browsing_level INTEGER,
ADD COLUMN IF NOT EXISTS civitai_base_model VARCHAR(255),
ADD COLUMN IF NOT EXISTS civitai_created_at TIMESTAMP,
ADD COLUMN IF NOT EXISTS civitai_image_url TEXT,
ADD COLUMN IF NOT EXISTS civitai_like_count INTEGER;

-- Create index on civitai_image_id for lookups
CREATE INDEX IF NOT EXISTS idx_image_civitai_image_id ON image(civitai_image_id);

-- Create index on civitai_username for filtering by creator
CREATE INDEX IF NOT EXISTS idx_image_civitai_username ON image(civitai_username);

-- Create index on civitai_base_model for filtering by model type
CREATE INDEX IF NOT EXISTS idx_image_civitai_base_model ON image(civitai_base_model);

-- Comments for documentation
COMMENT ON COLUMN image.civitai_image_id IS 'Civitai image ID from JSON sidecar';
COMMENT ON COLUMN image.civitai_post_id IS 'Civitai post ID from JSON sidecar';
COMMENT ON COLUMN image.civitai_username IS 'Creator username on Civitai platform';
COMMENT ON COLUMN image.civitai_nsfw_level IS 'NSFW level: None, Soft, Mature, X';
COMMENT ON COLUMN image.civitai_browsing_level IS 'Browsing level 1-5 (content rating)';
COMMENT ON COLUMN image.civitai_base_model IS 'Base model: SD 1.5, SDXL, Pony, Illustrious, etc.';
COMMENT ON COLUMN image.civitai_created_at IS 'When image was created on Civitai';
COMMENT ON COLUMN image.civitai_image_url IS 'Original Civitai image URL';
COMMENT ON COLUMN image.civitai_like_count IS 'Number of likes on Civitai';
