using Npgsql;
using Dapper;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Album management operations (create, delete, add/remove images)
/// </summary>
public partial class PostgreSQLDataStore
{
    public IEnumerable<AlbumListItem> GetAlbumsView()
    {
        using var conn = OpenConnection();
        
        return conn.Query<AlbumListItem>(@"
            SELECT 
                a.id AS Id,
                a.name AS Name,
                a.order AS Order,
                a.last_updated AS LastUpdated,
                (SELECT COUNT(1) FROM album_image ai WHERE a.id = ai.album_id) AS ImageCount
            FROM album a");
    }

    public AlbumListItem GetAlbumView(int id)
    {
        using var conn = OpenConnection();
        
        return conn.QuerySingle<AlbumListItem>(@"
            SELECT 
                a.id AS Id,
                a.name AS Name,
                a.order AS Order,
                a.last_updated AS LastUpdated,
                (SELECT COUNT(1) FROM album_image ai WHERE a.id = ai.album_id) AS ImageCount
            FROM album a
            WHERE a.id = @id",
            new { id });
    }

    public IEnumerable<Album> GetAlbums()
    {
        using var conn = OpenConnection();
        
        return conn.Query<Album>(@"
            SELECT 
                id AS Id,
                name AS Name,
                ""order"" AS Order,
                last_updated AS LastUpdated
            FROM album");
    }

    public IEnumerable<Album> GetAlbumsByLastUpdated(int limit)
    {
        using var conn = OpenConnection();
        
        return conn.Query<Album>(@"
            SELECT 
                id AS Id,
                name AS Name,
                ""order"" AS Order,
                last_updated AS LastUpdated
            FROM album
            ORDER BY last_updated DESC
            LIMIT @limit",
            new { limit });
    }

    public IEnumerable<Album> GetAlbumsByName()
    {
        using var conn = OpenConnection();
        
        return conn.Query<Album>(@"
            SELECT 
                id AS Id,
                name AS Name,
                ""order"" AS Order,
                last_updated AS LastUpdated
            FROM album
            ORDER BY name");
    }

    public void RenameAlbum(int id, string name)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE album SET name = @name WHERE id = @id",
                new { name, id });
        }
    }

    public Album? GetAlbum(int id)
    {
        using var conn = OpenConnection();
        
        return conn.QuerySingleOrDefault<Album>(@"
            SELECT 
                id AS Id,
                name AS Name,
                ""order"" AS Order,
                last_updated AS LastUpdated
            FROM album
            WHERE id = @id",
            new { id });
    }

    public Album CreateAlbum(Album album)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            album.Id = conn.QuerySingle<int>(@"
                INSERT INTO album (name, last_updated)
                VALUES (@Name, NOW())
                RETURNING id",
                album);
            
            return album;
        }
    }

    public void RemoveAlbum(int id)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            using var transaction = conn.BeginTransaction();
            
            try
            {
                conn.Execute("DELETE FROM album_image WHERE album_id = @id", new { id }, transaction);
                conn.Execute("DELETE FROM album WHERE id = @id", new { id }, transaction);
                
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public bool AddImagesToAlbum(int albumId, IEnumerable<int> imageIds)
    {
        // Verify album exists
        if (GetAlbum(albumId) == null)
            return false;

        lock (_lock)
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();
            
            try
            {
                var idList = imageIds.ToArray();
                
                // Use PostgreSQL's ON CONFLICT for upsert behavior
                conn.Execute(@"
                    INSERT INTO album_image (album_id, image_id)
                    SELECT @albumId, id
                    FROM unnest(@imageIds) AS id
                    ON CONFLICT (album_id, image_id) DO NOTHING",
                    new { albumId, imageIds = idList },
                    transaction);
                
                conn.Execute(
                    "UPDATE album SET last_updated = NOW() WHERE id = @albumId",
                    new { albumId },
                    transaction);
                
                transaction.Commit();
                return true;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public int RemoveImagesFromAlbum(int albumId, IEnumerable<int> imageIds)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var idList = imageIds.ToArray();
            
            return conn.Execute(
                "DELETE FROM album_image WHERE album_id = @albumId AND image_id = ANY(@imageIds)",
                new { albumId, imageIds = idList });
        }
    }

    public IEnumerable<Image> GetAlbumImages(int albumId, int pageSize, int offset)
    {
        using var conn = OpenConnection();
        
        return conn.Query<Image>(@"
            SELECT 
                i.id AS Id,
                i.root_folder_id AS RootFolderId,
                i.folder_id AS FolderId,
                i.path AS Path,
                i.file_name AS FileName,
                i.prompt AS Prompt,
                i.negative_prompt AS NegativePrompt,
                i.steps AS Steps,
                i.sampler AS Sampler,
                i.cfg_scale AS CfgScale,
                i.seed AS Seed,
                i.width AS Width,
                i.height AS Height,
                i.model_hash AS ModelHash,
                i.model AS Model,
                i.favorite AS Favorite,
                i.rating AS Rating,
                i.nsfw AS NSFW,
                i.for_deletion AS ForDeletion,
                i.created_date AS CreatedDate,
                i.modified_date AS ModifiedDate,
                i.aesthetic_score AS AestheticScore,
                i.hyper_network AS HyperNetwork,
                i.hyper_network_strength AS HyperNetworkStrength,
                i.clip_skip AS ClipSkip,
                i.batch_size AS BatchSize,
                i.batch_pos AS BatchPos,
                i.ensd AS Ensd,
                i.file_size AS FileSize,
                i.no_metadata AS NoMetadata,
                i.workflow AS Workflow,
                i.workflow_id AS WorkflowId,
                i.has_error AS HasError,
                i.hash AS Hash,
                i.unavailable AS Unavailable,
                i.viewed_date AS ViewedDate,
                i.touched_date AS TouchedDate,
                i.custom_tags AS CustomTags
            FROM image i
            INNER JOIN album_image ai ON i.id = ai.image_id
            WHERE ai.album_id = @albumId
            ORDER BY i.created_date DESC
            LIMIT @pageSize OFFSET @offset",
            new { albumId, pageSize, offset });
    }

    public void UpdateAlbumsOrder(IEnumerable<Album> albums)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();
            
            try
            {
                foreach (var album in albums)
                {
                    var orderValue = album.Order;
                    var idValue = album.Id;
                    conn.Execute(
                        "UPDATE album SET \"order\" = @order WHERE id = @id",
                        new { order = orderValue, id = idValue },
                        transaction);
                }
                
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
