using System.Security.Cryptography;
using System.Text;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Embeddings;

/// <summary>
/// Service for managing embedding cache and deduplication
/// Stores unique embeddings once, reuses for identical content
/// </summary>
public class EmbeddingCacheService
{
    private readonly PostgreSQLDataStore _dataStore;
    private readonly ITextEncoder? _bgeEncoder;
    private readonly ITextEncoder? _clipLEncoder;
    private readonly ITextEncoder? _clipGEncoder;
    private readonly IImageEncoder? _clipHEncoder;

    public EmbeddingCacheService(
        PostgreSQLDataStore dataStore,
        ITextEncoder? bgeEncoder = null,
        ITextEncoder? clipLEncoder = null,
        ITextEncoder? clipGEncoder = null,
        IImageEncoder? clipHEncoder = null)
    {
        _dataStore = dataStore;
        _bgeEncoder = bgeEncoder;
        _clipLEncoder = clipLEncoder;
        _clipGEncoder = clipGEncoder;
        _clipHEncoder = clipHEncoder;
    }

    /// <summary>
    /// Get or create embedding for text content (prompt or negative prompt)
    /// Returns embedding cache ID
    /// </summary>
    public async Task<int> GetOrCreateTextEmbeddingAsync(
        string text,
        string contentType,  // "prompt" or "negative_prompt"
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty", nameof(text));

        if (contentType != "prompt" && contentType != "negative_prompt")
            throw new ArgumentException("Content type must be 'prompt' or 'negative_prompt'", nameof(contentType));

        // 1. Hash the content
        var contentHash = ComputeSHA256(text);

        // 2. Check cache
        var cached = await _dataStore.GetEmbeddingByHashAsync(contentHash, cancellationToken);
        if (cached != null)
        {
            // Cache hit! Increment reference count and return
            await _dataStore.IncrementEmbeddingReferenceCountAsync(cached.Id, cancellationToken);
            return cached.Id;
        }

        // 3. Cache miss - generate new embeddings
        float[]? bgeEmbedding = null;
        float[]? clipLEmbedding = null;
        float[]? clipGEmbedding = null;

        // Generate embeddings in parallel for speed
        var tasks = new List<Task>();

        if (_bgeEncoder != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                bgeEmbedding = await _bgeEncoder.EncodeAsync(text, cancellationToken);
            }, cancellationToken));
        }

        if (_clipLEncoder != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                clipLEmbedding = await _clipLEncoder.EncodeAsync(text, cancellationToken);
            }, cancellationToken));
        }

        if (_clipGEncoder != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                clipGEmbedding = await _clipGEncoder.EncodeAsync(text, cancellationToken);
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        // 4. Store in cache
        var cacheEntry = new EmbeddingCache
        {
            ContentHash = contentHash,
            ContentType = contentType,
            ContentText = text,
            BgeEmbedding = bgeEmbedding,
            ClipLEmbedding = clipLEmbedding,
            ClipGEmbedding = clipGEmbedding,
            ClipHEmbedding = null,  // Only for images
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };

        var cacheId = await _dataStore.InsertEmbeddingCacheAsync(cacheEntry, cancellationToken);
        return cacheId;
    }

    /// <summary>
    /// Get or create visual embedding for image
    /// Returns embedding cache ID
    /// </summary>
    public async Task<int> GetOrCreateVisualEmbeddingAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path cannot be empty", nameof(imagePath));

        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found", imagePath);

        // 1. Hash image content (first 1MB to avoid loading full 4K images)
        var imageHash = await ComputeImageHashAsync(imagePath, cancellationToken);

        // 2. Check cache
        var cached = await _dataStore.GetEmbeddingByHashAsync(imageHash, cancellationToken);
        if (cached != null)
        {
            // Cache hit! Increment reference count and return
            await _dataStore.IncrementEmbeddingReferenceCountAsync(cached.Id, cancellationToken);
            return cached.Id;
        }

        // 3. Cache miss - generate CLIP-H visual embedding
        if (_clipHEncoder == null)
            throw new InvalidOperationException("CLIP-H encoder not configured");

        var clipHEmbedding = await _clipHEncoder.EncodeImageAsync(imagePath, cancellationToken);

        // 4. Store in cache
        var cacheEntry = new EmbeddingCache
        {
            ContentHash = imageHash,
            ContentType = "image",
            ContentText = null,  // No text for images
            BgeEmbedding = null,
            ClipLEmbedding = null,
            ClipGEmbedding = null,
            ClipHEmbedding = clipHEmbedding,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };

        var cacheId = await _dataStore.InsertEmbeddingCacheAsync(cacheEntry, cancellationToken);
        return cacheId;
    }

    /// <summary>
    /// Decrement reference count when image deleted
    /// </summary>
    public async Task DecrementReferenceCountAsync(
        int embeddingCacheId,
        CancellationToken cancellationToken = default)
    {
        await _dataStore.DecrementEmbeddingReferenceCountAsync(embeddingCacheId, cancellationToken);
    }

    /// <summary>
    /// Delete unused embeddings (reference_count = 0)
    /// Run periodically to clean up cache
    /// </summary>
    public async Task<int> CleanupUnusedEmbeddingsAsync(CancellationToken cancellationToken = default)
    {
        return await _dataStore.DeleteUnusedEmbeddingsAsync(cancellationToken);
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public async Task<Database.PostgreSQL.EmbeddingCacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return await _dataStore.GetEmbeddingCacheStatisticsAsync(cancellationToken);
    }

    /// <summary>
    /// Compute SHA256 hash of text
    /// </summary>
    private static string ComputeSHA256(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute hash of image file (first 1MB for performance)
    /// </summary>
    private static async Task<string> ComputeImageHashAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        const int maxBytes = 1024 * 1024; // 1 MB
        
        await using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[Math.Min(maxBytes, stream.Length)];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
        
        var hash = SHA256.HashData(buffer.AsSpan(0, bytesRead));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Interface for text encoders (BGE, CLIP-L, CLIP-G)
/// </summary>
public interface ITextEncoder
{
    Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for image encoders (CLIP-H)
/// </summary>
public interface IImageEncoder
{
    Task<float[]> EncodeImageAsync(string imagePath, CancellationToken cancellationToken = default);
}
