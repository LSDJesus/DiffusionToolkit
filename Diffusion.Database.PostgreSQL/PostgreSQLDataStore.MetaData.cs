using Npgsql;
using Dapper;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Metadata operations (favorite, rating, NSFW, deletion marking, custom tags, etc.)
/// </summary>
public partial class PostgreSQLDataStore
{
    public void SetDeleted(int id, bool forDeletion)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET for_deletion = @forDeletion, touched_date = NOW() WHERE id = @id",
                new { forDeletion, id });
        }
    }

    public void SetDeleted(IEnumerable<int> ids, bool forDeletion)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var parameters = new DynamicParameters();
            parameters.Add("@forDeletion", forDeletion);
            parameters.Add("@ids", ids.ToArray());

            conn.Execute(
                "UPDATE image SET for_deletion = @forDeletion, touched_date = NOW() WHERE id = ANY(@ids)",
                parameters);
        }
    }

    public void SetFavorite(int id, bool favorite)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET favorite = @favorite, touched_date = NOW() WHERE id = @id",
                new { favorite, id });
        }
    }

    public void SetFavorite(IEnumerable<int> ids, bool favorite)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();

            var parameters = new DynamicParameters();
            parameters.Add("@favorite", favorite);
            parameters.Add("@ids", ids.ToArray());

            conn.Execute(
                "UPDATE image SET favorite = @favorite, touched_date = NOW() WHERE id = ANY(@ids)",
                parameters);
        }
    }

    public void SetNSFW(int id, bool nsfw)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET nsfw = @nsfw, touched_date = NOW() WHERE id = @id",
                new { nsfw, id });
        }
    }

    public void SetNSFW(IEnumerable<int> ids, bool nsfw, bool preserve = false)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var parameters = new DynamicParameters();
            parameters.Add("@nsfw", nsfw);
            parameters.Add("@ids", ids.ToArray());

            var update = preserve 
                ? "UPDATE image SET nsfw = nsfw OR @nsfw, touched_date = NOW() WHERE id = ANY(@ids)"
                : "UPDATE image SET nsfw = @nsfw, touched_date = NOW() WHERE id = ANY(@ids)";
            
            conn.Execute(update, parameters);
        }
    }

    public void SetRating(int id, int? rating)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET rating = @rating, touched_date = NOW() WHERE id = @id",
                new { rating, id });
        }
    }

    public void SetRating(IEnumerable<int> ids, int? rating)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var parameters = new DynamicParameters();
            parameters.Add("@rating", rating);
            parameters.Add("@ids", ids.ToArray());

            conn.Execute(
                "UPDATE image SET rating = @rating, touched_date = NOW() WHERE id = ANY(@ids)",
                parameters);
        }
    }

    public void SetCustomTags(int id, string? customTags)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET custom_tags = @customTags, touched_date = NOW() WHERE id = @id",
                new { customTags, id });
        }
    }

    public void SetCustomTags(IEnumerable<int> ids, string? customTags)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var parameters = new DynamicParameters();
            parameters.Add("@customTags", customTags);
            parameters.Add("@ids", ids.ToArray());

            conn.Execute(
                "UPDATE image SET custom_tags = @customTags, touched_date = NOW() WHERE id = ANY(@ids)",
                parameters);
        }
    }

    public void SetUnavailable(int id, bool unavailable)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET unavailable = @unavailable, touched_date = NOW() WHERE id = @id",
                new { unavailable, id });
        }
    }

    public void SetUnavailable(IEnumerable<int> ids, bool unavailable)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var parameters = new DynamicParameters();
            parameters.Add("@unavailable", unavailable);
            parameters.Add("@ids", ids.ToArray());

            conn.Execute(
                "UPDATE image SET unavailable = @unavailable, touched_date = NOW() WHERE id = ANY(@ids)",
                parameters);
        }
    }

    public void SetViewed(int id)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET viewed_date = NOW() WHERE id = @id",
                new { id });
        }
    }

    public void SetViewed(IEnumerable<int> ids)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            conn.Execute(
                "UPDATE image SET viewed_date = NOW() WHERE id = ANY(@ids)",
                new { ids = ids.ToArray() });
        }
    }

    public void DeleteImages(IEnumerable<int> ids)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            
            var idList = ids.ToArray();
            
            // Delete associated records first (cascade should handle this, but being explicit)
            conn.Execute("DELETE FROM album_image WHERE image_id = ANY(@ids)", new { ids = idList });
            conn.Execute("DELETE FROM node WHERE image_id = ANY(@ids)", new { ids = idList });
            conn.Execute("DELETE FROM image WHERE id = ANY(@ids)", new { ids = idList });
        }
    }

    public IEnumerable<ImagePath> GetUnavailable(bool unavailable)
    {
        using var conn = OpenConnection();
        
        return conn.Query<ImagePath>(
            "SELECT id AS Id, path AS Path FROM image WHERE unavailable = @unavailable",
            new { unavailable });
    }
}
