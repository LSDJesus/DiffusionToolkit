using Npgsql;
using Dapper;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Database.PostgreSQL;

/// <summary>
/// Vector similarity search using pgvector
/// Supports prompt similarity, image similarity, and cross-modal search
/// </summary>
public partial class PostgreSQLDataStore
{
    /// <summary>
    /// Find images with similar prompts using semantic similarity
    /// </summary>
    public async Task<List<ImageEntity>> SearchByPromptSimilarityAsync(
        float[] promptEmbedding, 
        float threshold = 0.85f, 
        int limit = 50)
    {
        await using var conn = await OpenConnectionAsync();

        var vector = $"[{string.Join(",", promptEmbedding)}]";
        var query = @"
            SELECT * FROM image
            WHERE prompt_embedding IS NOT NULL
            AND 1 - (prompt_embedding <=> @embedding::vector) >= @threshold
            ORDER BY prompt_embedding <=> @embedding::vector ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query, 
            new { embedding = vector, threshold, limit });

        return images.ToList();
    }

    /// <summary>
    /// Find images with similar negative prompts
    /// </summary>
    public async Task<List<ImageEntity>> SearchByNegativePromptSimilarityAsync(
        float[] negativePromptEmbedding,
        float threshold = 0.85f,
        int limit = 50)
    {
        await using var conn = await OpenConnectionAsync();

        var vector = $"[{string.Join(",", negativePromptEmbedding)}]";
        var query = @"
            SELECT * FROM image
            WHERE negative_prompt_embedding IS NOT NULL
            AND 1 - (negative_prompt_embedding <=> @embedding::vector) >= @threshold
            ORDER BY negative_prompt_embedding <=> @embedding::vector ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query,
            new { embedding = vector, threshold, limit });

        return images.ToList();
    }

    /// <summary>
    /// Find images with similar visual content using CLIP embeddings
    /// </summary>
    public async Task<List<ImageEntity>> SearchSimilarImagesAsync(
        int imageId,
        float threshold = 0.75f,
        int limit = 50)
    {
        await using var conn = await OpenConnectionAsync();

        var query = @"
            SELECT i2.* FROM image i1
            JOIN image i2 ON i1.id != i2.id
            WHERE i1.id = @imageId
            AND i1.image_embedding IS NOT NULL
            AND i2.image_embedding IS NOT NULL
            AND 1 - (i1.image_embedding <=> i2.image_embedding) >= @threshold
            ORDER BY i1.image_embedding <=> i2.image_embedding ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query,
            new { imageId, threshold, limit });

        return images.ToList();
    }

    /// <summary>
    /// Cross-modal search: find images similar to a text query using visual embeddings
    /// </summary>
    public async Task<List<ImageEntity>> CrossModalSearchAsync(
        float[] textEmbedding,
        float threshold = 0.70f,
        int limit = 50)
    {
        await using var conn = await OpenConnectionAsync();

        var vector = $"[{string.Join(",", textEmbedding)}]";
        var query = @"
            SELECT * FROM image
            WHERE image_embedding IS NOT NULL
            AND 1 - (image_embedding <=> @embedding::vector) >= @threshold
            ORDER BY image_embedding <=> @embedding::vector ASC
            LIMIT @limit;";

        var images = await conn.QueryAsync<ImageEntity>(query,
            new { embedding = vector, threshold, limit });

        return images.ToList();
    }

    /// <summary>
    /// Batch similarity search: find similar images to a set of images
    /// Used for auto-tagging and clustering
    /// </summary>
    public async Task<List<(int ImageId, int SimilarImageId, float Similarity)>> BatchSimilaritySearchAsync(
        List<int> imageIds,
        float threshold = 0.75f,
        int topKPerImage = 10)
    {
        await using var conn = await OpenConnectionAsync();

        var results = new List<(int, int, float)>();

        foreach (var imageId in imageIds)
        {
            var query = @"
                SELECT i1.id AS ImageId,
                       i2.id AS SimilarImageId,
                       (1 - (i1.image_embedding <=> i2.image_embedding)) AS Similarity
                FROM image i1
                JOIN image i2 ON i1.id != i2.id
                WHERE i1.id = @imageId
                AND i1.image_embedding IS NOT NULL
                AND i2.image_embedding IS NOT NULL
                AND 1 - (i1.image_embedding <=> i2.image_embedding) >= @threshold
                ORDER BY Similarity DESC
                LIMIT @topK;";

            var rows = await conn.QueryAsync<(int, int, float)>(query,
                new { imageId, threshold, topK = topKPerImage });

            results.AddRange(rows);
        }

        return results;
    }

    /// <summary>
    /// Suggest tags based on similar image captions/custom tags
    /// </summary>
    public async Task<List<(int ImageId, string SuggestedTag, float Similarity)>> AutoTagBySimilarityAsync(
        int imageId,
        float threshold = 0.75f,
        int topK = 5)
    {
        await using var conn = await OpenConnectionAsync();

        var query = @"
            SELECT i2.id AS ImageId,
                   i2.custom_tags AS SuggestedTag,
                   (1 - (i1.prompt_embedding <=> i2.prompt_embedding)) AS Similarity
            FROM image i1
            JOIN image i2 ON i1.id != i2.id
            WHERE i1.id = @imageId
            AND i1.prompt_embedding IS NOT NULL
            AND i2.prompt_embedding IS NOT NULL
            AND i2.custom_tags IS NOT NULL
            AND 1 - (i1.prompt_embedding <=> i2.prompt_embedding) >= @threshold
            ORDER BY Similarity DESC
            LIMIT @topK;";

        var suggestions = await conn.QueryAsync<(int, string, float)>(query,
            new { imageId, threshold, topK });

        return suggestions.ToList();
    }

    /// <summary>
    /// Get embedding coverage statistics
    /// </summary>
    public async Task<EmbeddingCoverageStats> GetEmbeddingCoverageAsync()
    {
        await using var conn = await OpenConnectionAsync();

        var query = @"
            SELECT 
                COUNT(*) AS TotalImages,
                COUNT(CASE WHEN prompt_embedding IS NOT NULL THEN 1 END) AS PromptEmbeddings,
                COUNT(CASE WHEN negative_prompt_embedding IS NOT NULL THEN 1 END) AS NegativePromptEmbeddings,
                COUNT(CASE WHEN image_embedding IS NOT NULL THEN 1 END) AS ImageEmbeddings
            FROM image;";

        var stats = await conn.QueryFirstAsync<EmbeddingCoverageStats>(query);
        return stats;
    }

    /// <summary>
    /// Update image embeddings
    /// </summary>
    public async Task UpdateImageEmbeddingsAsync(
        int imageId,
        float[] promptEmbedding,
        float[] negativePromptEmbedding,
        float[] imageEmbedding)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();

            var promptVector = promptEmbedding != null ? $"[{string.Join(",", promptEmbedding)}]" : null;
            var negPromptVector = negativePromptEmbedding != null ? $"[{string.Join(",", negativePromptEmbedding)}]" : null;
            var imageVector = imageEmbedding != null ? $"[{string.Join(",", imageEmbedding)}]" : null;

            var query = @"
                UPDATE image
                SET prompt_embedding = @promptEmbedding::vector,
                    negative_prompt_embedding = @negPromptEmbedding::vector,
                    image_embedding = @imageEmbedding::vector,
                    touched_date = NOW()
                WHERE id = @imageId;";

            conn.Execute(query, new
            {
                imageId,
                promptEmbedding = promptVector,
                negPromptEmbedding = negPromptVector,
                imageEmbedding = imageVector
            });
        }
    }

    /// <summary>
    /// Batch update embeddings for performance
    /// </summary>
    public async Task UpdateImageEmbeddingsBatchAsync(
        List<(int ImageId, float[] Prompt, float[] NegativePrompt, float[] Image)> embeddings)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();

            foreach (var (imageId, promptEmb, negPromptEmb, imageEmb) in embeddings)
            {
                var promptVector = promptEmb != null ? $"[{string.Join(",", promptEmb)}]" : null;
                var negPromptVector = negPromptEmb != null ? $"[{string.Join(",", negPromptEmb)}]" : null;
                var imageVector = imageEmb != null ? $"[{string.Join(",", imageEmb)}]" : null;

                var query = @"
                    UPDATE image
                    SET prompt_embedding = @promptEmbedding::vector,
                        negative_prompt_embedding = @negPromptEmbedding::vector,
                        image_embedding = @imageEmbedding::vector,
                        touched_date = NOW()
                    WHERE id = @imageId;";

                conn.Execute(query, new
                {
                    imageId,
                    promptEmbedding = promptVector,
                    negPromptEmbedding = negPromptVector,
                    imageEmbedding = imageVector
                });
            }
        }
    }
}

public class EmbeddingCoverageStats
{
    public long TotalImages { get; set; }
    public long PromptEmbeddings { get; set; }
    public long NegativePromptEmbeddings { get; set; }
    public long ImageEmbeddings { get; set; }

    public float PromptCoverage => TotalImages > 0 ? (float)PromptEmbeddings / TotalImages : 0f;
    public float NegativePromptCoverage => TotalImages > 0 ? (float)NegativePromptEmbeddings / TotalImages : 0f;
    public float ImageCoverage => TotalImages > 0 ? (float)ImageEmbeddings / TotalImages : 0f;
}
