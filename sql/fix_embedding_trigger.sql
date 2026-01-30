-- Fix the trg_handle_embedding_source_deletion trigger
-- Problem: BEFORE DELETE trigger tries to update rows that are also being deleted in bulk operations
-- Solution: Change to AFTER trigger and exclude self-referential updates

-- Drop the existing BEFORE trigger
DROP TRIGGER IF EXISTS trg_handle_embedding_source_deletion ON public.image;

-- Recreate the function to handle the case where the target row is also being deleted
CREATE OR REPLACE FUNCTION public.handle_embedding_source_deletion()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    -- Only update images that still exist and reference the deleted image
    -- The id != OLD.id check prevents attempting to update the row being deleted
    UPDATE image 
    SET embedding_source_id = NULL,
        image_embedding = NULL,
        is_embedding_representative = FALSE
    WHERE embedding_source_id = OLD.id
      AND id != OLD.id;
    RETURN OLD;
END;
$$;

-- Create as AFTER trigger instead of BEFORE
-- AFTER triggers run after the row is deleted, so there's no conflict
CREATE TRIGGER trg_handle_embedding_source_deletion
    AFTER DELETE ON public.image
    FOR EACH ROW
    EXECUTE FUNCTION public.handle_embedding_source_deletion();

-- Do the same for civitai_downloads schema if it exists
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'civitai_downloads') THEN
        DROP TRIGGER IF EXISTS trg_handle_embedding_source_deletion ON civitai_downloads.image;
        
        CREATE OR REPLACE FUNCTION civitai_downloads.handle_embedding_source_deletion()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $func$
        BEGIN
            UPDATE civitai_downloads.image 
            SET embedding_source_id = NULL,
                image_embedding = NULL,
                is_embedding_representative = FALSE
            WHERE embedding_source_id = OLD.id
              AND id != OLD.id;
            RETURN OLD;
        END;
        $func$;
        
        CREATE TRIGGER trg_handle_embedding_source_deletion
            AFTER DELETE ON civitai_downloads.image
            FOR EACH ROW
            EXECUTE FUNCTION civitai_downloads.handle_embedding_source_deletion();
    END IF;
END;
$$;
