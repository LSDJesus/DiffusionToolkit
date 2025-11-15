using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Embeddings;

/// <summary>
/// Intelligent embedding processing service with deduplication and representative image selection.
/// Optimized for datasets with ORIG/FINAL pairs and reused prompts (400K+ images from 5K prompts).
/// </summary>
public class EmbeddingProcessingService : IDisposable
{
    private readonly EmbeddingService _embeddingService;
    private readonly PostgreSQLDataStore _dataStore;
    private readonly Channel<EmbeddingWorkItem> _workQueue;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _processingTask;
    
    // In-memory cache for prompt embeddings (SHA256 -> TextEmbeddings)
    private readonly ConcurrentDictionary<string, CachedPromptEmbeddings> _promptCache;
    
    // Statistics
    private int _promptCacheHits;
    private int _promptCacheMisses;
    private int _imagesEmbedded;
    private int _imagesSkipped;
    
    public EmbeddingProcessingService(EmbeddingService embeddingService, PostgreSQLDataStore dataStore)
    {
        _embeddingService = embeddingService;
        _dataStore = dataStore;
        _promptCache = new ConcurrentDictionary<string, CachedPromptEmbeddings>();
        
        // Unbounded channel for work queue
        _workQueue = Channel.CreateUnbounded<EmbeddingWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        
        _cancellationTokenSource = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessQueueAsync(_cancellationTokenSource.Token));
    }
    
    /// <summary>
    /// Get cache statistics
    /// </summary>
    public EmbeddingStatistics GetStatistics()
    {
        return new EmbeddingStatistics
        {
            PromptCacheHits = _promptCacheHits,
            PromptCacheMisses = _promptCacheMisses,
            ImagesEmbedded = _imagesEmbedded,
            ImagesSkipped = _imagesSkipped,
            PromptCacheSize = _promptCache.Count,
            QueueLength = _workQueue.Reader.Count
        };
    }
    
    /// <summary>
    /// Pre-load unique prompts from database into cache.
    /// Call this before bulk processing to maximize cache hits.
    /// For 5K batch prompts, this takes ~24 minutes but saves 32+ hours during processing.
    /// </summary>
    public async Task PreloadPromptsAsync(
        int? limit = null, 
        IProgress<PreloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Get unique prompt combinations from database
        var uniquePrompts = await _dataStore.GetUniquePromptsAsync(limit, cancellationToken);
        
        var total = uniquePrompts.Count;
        var processed = 0;
        
        progress?.Report(new PreloadProgress
        {
            Stage = "Preloading prompt embeddings",
            Current = 0,
            Total = total
        });
        
        // Process in batches for better performance
        var batchSize = 16; // Conservative for memory
        var batches = uniquePrompts
            .Select((prompt, index) => new { prompt, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.prompt).ToList());
        
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Generate embeddings for batch in parallel
            var tasks = batch.Select(async promptPair =>
            {
                var (prompt, negativePrompt) = promptPair;
                var hash = ComputePromptHash(prompt, negativePrompt);
                
                // Skip if already cached
                if (_promptCache.ContainsKey(hash))
                {
                    Interlocked.Increment(ref _promptCacheHits);
                    return;
                }
                
                // Generate text embeddings (BGE + CLIP-L + CLIP-G)
                var (bgeEmb, clipLEmb, clipGEmb) = await _embeddingService.GenerateTextEmbeddingsAsync(
                    prompt ?? string.Empty);
                
                // Cache result
                _promptCache[hash] = new CachedPromptEmbeddings
                {
                    PromptHash = hash,
                    BgeEmbedding = bgeEmb,
                    ClipLEmbedding = clipLEmb,
                    ClipGEmbedding = clipGEmb,
                    ReferenceCount = 0,
                    CreatedAt = DateTime.UtcNow
                };
                
                Interlocked.Increment(ref _promptCacheMisses);
            });
            
            await Task.WhenAll(tasks);
            
            processed += batch.Count;
            progress?.Report(new PreloadProgress
            {
                Stage = "Preloading prompt embeddings",
                Current = processed,
                Total = total
            });
        }
    }
    
    /// <summary>
    /// Process all images with intelligent deduplication strategy:
    /// 1. Compute metadata hashes for all images (SHA256 of prompt+model+seed+params)
    /// 2. Group by metadata_hash and select representative (largest file_size = FINAL version)
    /// 3. Generate embeddings for representatives only (~425K instead of 773K images)
    /// 4. Copy embeddings to non-representatives via embedding_source_id reference
    /// 
    /// Estimated time: ~22-23 hours vs 59 hours naive approach (60% time savings!)
    /// </summary>
    public async Task ProcessAllImagesWithDeduplicationAsync(
        int batchSize = 32, 
        IProgress<ProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var totalImages = await _dataStore.GetTotalImageCountAsync(cancellationToken);
        
        // Step 1: Compute metadata hashes for all images
        progress?.Report(new ProcessingProgress
        {
            Stage = "Computing metadata hashes",
            Current = 0,
            Total = totalImages,
            Message = "Identifying duplicate generation parameters..."
        });
        
        await _dataStore.ComputeAllMetadataHashesAsync(cancellationToken);
        
        // Step 2: Group images by metadata hash and select representatives (largest file = FINAL)
        progress?.Report(new ProcessingProgress
        {
            Stage = "Selecting representatives",
            Current = 0,
            Total = totalImages,
            Message = "Choosing FINAL images over ORIG for each metadata group..."
        });
        
        var representatives = await _dataStore.SelectRepresentativeImagesAsync(cancellationToken);
        var repCount = representatives.Count;
        
        progress?.Report(new ProcessingProgress
        {
            Stage = "Representatives selected",
            Current = 0,
            Total = repCount,
            Message = $"Will embed {repCount:N0} representatives (saved {totalImages - repCount:N0} duplicates)"
        });
        
        // Step 3: Process representatives in batches
        var processed = 0;
        var batches = representatives
            .Select((rep, index) => new { rep, index })
            .GroupBy(x => x.index / batchSize)
            .Select(g => g.Select(x => x.rep).ToList());
        
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Process batch in parallel
            var tasks = batch.Select(rep => ProcessRepresentativeImageAsync(rep, cancellationToken));
            await Task.WhenAll(tasks);
            
            processed += batch.Count;
            var stats = GetStatistics();
            
            progress?.Report(new ProcessingProgress
            {
                Stage = "Generating embeddings",
                Current = processed,
                Total = repCount,
                Message = $"Embedded: {stats.ImagesEmbedded:N0} | Cache hits: {stats.PromptCacheHits:N0} ({stats.CacheHitRate:P1})"
            });
        }
        
        // Step 4: Copy embeddings to non-representatives
        progress?.Report(new ProcessingProgress
        {
            Stage = "Copying to duplicates",
            Current = 0,
            Total = totalImages - repCount,
            Message = "Copying embeddings to ORIG images and other duplicates..."
        });
        
        await _dataStore.CopyEmbeddingsToAllNonRepresentativesAsync(cancellationToken);
        
        progress?.Report(new ProcessingProgress
        {
            Stage = "Complete",
            Current = totalImages,
            Total = totalImages,
            Message = $"Processing complete! Generated {_imagesEmbedded:N0} embeddings, reused for {totalImages - _imagesEmbedded:N0} duplicates."
        });
    }
    
    private async Task ProcessRepresentativeImageAsync(RepresentativeImage rep, CancellationToken cancellationToken)
    {
        try
        {
            // Check prompt cache first
            var promptHash = ComputePromptHash(rep.Prompt, rep.NegativePrompt);
            CachedPromptEmbeddings? promptEmbeddings;
            
            if (!_promptCache.TryGetValue(promptHash, out promptEmbeddings))
            {
                // Generate text embeddings (BGE + CLIP-L + CLIP-G)
                var (bgeEmb, clipLEmb, clipGEmb) = await _embeddingService.GenerateTextEmbeddingsAsync(
                    rep.Prompt ?? string.Empty);
                
                promptEmbeddings = new CachedPromptEmbeddings
                {
                    PromptHash = promptHash,
                    BgeEmbedding = bgeEmb,
                    ClipLEmbedding = clipLEmb,
                    ClipGEmbedding = clipGEmb,
                    ReferenceCount = 0,
                    CreatedAt = DateTime.UtcNow
                };
                
                _promptCache[promptHash] = promptEmbeddings;
                Interlocked.Increment(ref _promptCacheMisses);
            }
            else
            {
                Interlocked.Increment(ref _promptCacheHits);
            }
            
            promptEmbeddings.ReferenceCount++;
            
            // Generate image embedding (CLIP-H vision)
            var imageEmbedding = await _embeddingService.GenerateImageEmbeddingAsync(
                rep.FilePath);
            
            // Store embeddings with representative flag
            await _dataStore.StoreImageEmbeddingsAsync(
                imageId: rep.ImageId,
                bgeEmbedding: promptEmbeddings.BgeEmbedding,
                clipLEmbedding: promptEmbeddings.ClipLEmbedding,
                clipGEmbedding: promptEmbeddings.ClipGEmbedding,
                imageEmbedding: imageEmbedding,
                isRepresentative: true,
                cancellationToken: cancellationToken);
            
            Interlocked.Increment(ref _imagesEmbedded);
        }
        catch (Exception ex)
        {
            // Log error but continue processing
            Console.WriteLine($"Error processing image {rep.ImageId} ({rep.FilePath}): {ex.Message}");
        }
    }
    
    /// <summary>
    /// Queue a single image for embedding generation (legacy API for incremental processing)
    /// </summary>
    public async Task QueueImageAsync(ImageEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var workItem = new EmbeddingWorkItem
        {
            Request = request,
            QueuedAt = DateTime.UtcNow
        };
        
        await _workQueue.Writer.WriteAsync(workItem, cancellationToken);
    }
    
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var workItem in _workQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessWorkItemAsync(workItem, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing work item: {ex.Message}");
            }
        }
    }
    
    private async Task ProcessWorkItemAsync(EmbeddingWorkItem workItem, CancellationToken cancellationToken)
    {
        var request = workItem.Request;
        
        // Check if this image needs embedding (not already processed)
        var needsEmbedding = await _dataStore.ImageNeedsEmbeddingAsync(request.ImageId, cancellationToken);
        if (!needsEmbedding)
        {
            Interlocked.Increment(ref _imagesSkipped);
            return;
        }
        
        // Check prompt cache
        var promptHash = ComputePromptHash(request.Prompt, request.NegativePrompt);
        CachedPromptEmbeddings? promptEmbeddings;
        
        if (!_promptCache.TryGetValue(promptHash, out promptEmbeddings))
        {
            // Generate text embeddings
            var (bgeEmb, clipLEmb, clipGEmb) = await _embeddingService.GenerateTextEmbeddingsAsync(
                request.Prompt ?? string.Empty);
            
            promptEmbeddings = new CachedPromptEmbeddings
            {
                PromptHash = promptHash,
                BgeEmbedding = bgeEmb,
                ClipLEmbedding = clipLEmb,
                ClipGEmbedding = clipGEmb,
                ReferenceCount = 0,
                CreatedAt = DateTime.UtcNow
            };
            
            _promptCache[promptHash] = promptEmbeddings;
            Interlocked.Increment(ref _promptCacheMisses);
        }
        else
        {
            Interlocked.Increment(ref _promptCacheHits);
        }
        
        promptEmbeddings.ReferenceCount++;
        
        // Generate image embedding
        var imageEmbedding = await _embeddingService.GenerateImageEmbeddingAsync(
            request.FilePath);
        
        // Store embeddings
        await _dataStore.StoreImageEmbeddingsAsync(
            request.ImageId,
            promptEmbeddings.BgeEmbedding,
            promptEmbeddings.ClipLEmbedding,
            promptEmbeddings.ClipGEmbedding,
            imageEmbedding,
            isRepresentative: false,
            cancellationToken);
        
        Interlocked.Increment(ref _imagesEmbedded);
    }
    
    private static string ComputePromptHash(string? prompt, string? negativePrompt)
    {
        var combined = $"{prompt ?? string.Empty}|{negativePrompt ?? string.Empty}";
        var bytes = Encoding.UTF8.GetBytes(combined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _workQueue.Writer.Complete();
        
        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Ignore cancellation exceptions
        }
        
        _cancellationTokenSource.Dispose();
    }
}

/// <summary>
/// Request to generate embeddings for an image
/// </summary>
public class ImageEmbeddingRequest
{
    public int ImageId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? MetadataHash { get; set; }
}

/// <summary>
/// Work item in the embedding queue
/// </summary>
internal class EmbeddingWorkItem
{
    public ImageEmbeddingRequest Request { get; set; } = null!;
    public DateTime QueuedAt { get; set; }
}

/// <summary>
/// Cached prompt embeddings (in-memory cache for deduplication)
/// </summary>
internal class CachedPromptEmbeddings
{
    public string PromptHash { get; set; } = string.Empty;
    public float[] BgeEmbedding { get; set; } = Array.Empty<float>();
    public float[] ClipLEmbedding { get; set; } = Array.Empty<float>();
    public float[] ClipGEmbedding { get; set; } = Array.Empty<float>();
    public int ReferenceCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Cache and processing statistics
/// </summary>
public class EmbeddingStatistics
{
    public int PromptCacheHits { get; set; }
    public int PromptCacheMisses { get; set; }
    public int ImagesEmbedded { get; set; }
    public int ImagesSkipped { get; set; }
    public int PromptCacheSize { get; set; }
    public int QueueLength { get; set; }
    
    public double CacheHitRate => PromptCacheHits + PromptCacheMisses > 0 
        ? (double)PromptCacheHits / (PromptCacheHits + PromptCacheMisses) 
        : 0.0;
}

/// <summary>
/// Progress report for prompt preloading
/// </summary>
public class PreloadProgress
{
    public string Stage { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public double Percentage => Total > 0 ? (double)Current / Total * 100.0 : 0.0;
}

/// <summary>
/// Progress report for image processing
/// </summary>
public class ProcessingProgress
{
    public string Stage { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = string.Empty;
    public double Percentage => Total > 0 ? (double)Current / Total * 100.0 : 0.0;
}
