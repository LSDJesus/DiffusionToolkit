using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Embeddings;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Embedding model pool - holds BGE and CLIP-Vision models for a specific GPU.
/// Thread-safe: ONNX Runtime with CUDA EP supports concurrent Run() calls.
/// Multiple workers can share one pool.
/// 
/// Note: CLIP-L/G text encoders removed - for ComfyUI integration,
/// conditioning will be generated on-demand from prompts.
/// </summary>
public class EmbeddingModelPool : IModelPool
{
    private readonly Settings _settings;
    private EmbeddingPooledOrchestrator? _pooledOrchestrator;
    private EmbeddingConfig? _config;
    private bool _isReady;
    
    public int GpuId { get; }
    public bool IsReady => _isReady;
    public double VramUsageGb => EmbeddingPooledOrchestrator.VramUsageGb; // BGE (~0.5GB) + CLIP-Vision (~2.6GB)
    
    public EmbeddingPooledOrchestrator? PooledOrchestrator => _pooledOrchestrator;
    
    public EmbeddingModelPool(int gpuId, Settings settings)
    {
        GpuId = gpuId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isReady) return;
        
        Logger.Log($"EmbeddingModelPool GPU{GpuId}: Initializing models...");
        
        try
        {
            // Get base directory for model paths
            var baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            
            // Create config with default model paths
            _config = EmbeddingConfig.CreateDefault(baseDir);
            
            // Validate that model files exist
            try
            {
                _config.Validate();
            }
            catch (FileNotFoundException ex)
            {
                Logger.Log($"EmbeddingModelPool GPU{GpuId}: {ex.Message}");
                return;
            }
            
            // Create pooled orchestrator (manages all 4 models on this GPU)
            // Using 1 worker per model internally - our architecture handles parallelism
            _pooledOrchestrator = new EmbeddingPooledOrchestrator(GpuId, workersPerModel: 1);
            await _pooledOrchestrator.InitializeAsync(_config, cancellationToken);
            
            _isReady = _pooledOrchestrator.IsInitialized;
            
            if (_isReady)
            {
                Logger.Log($"EmbeddingModelPool GPU{GpuId}: Ready (VRAM: ~{VramUsageGb:F1}GB)");
            }
            else
            {
                Logger.Log($"EmbeddingModelPool GPU{GpuId}: Initialization failed");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"EmbeddingModelPool GPU{GpuId}: Initialization failed - {ex.Message}");
            _isReady = false;
        }
    }
    
    public Task ShutdownAsync()
    {
        Logger.Log($"EmbeddingModelPool GPU{GpuId}: Shutting down...");
        _pooledOrchestrator?.Dispose();
        _pooledOrchestrator = null;
        _isReady = false;
        Logger.Log($"EmbeddingModelPool GPU{GpuId}: Shutdown complete, VRAM freed");
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}

/// <summary>
/// Embedding service orchestrator - handles BGE text and CLIP image embeddings.
/// Owns model pools (one per GPU), spawns lightweight workers that share the pools.
/// </summary>
public class EmbeddingOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    private readonly Dictionary<int, EmbeddingModelPool> _modelPools = new();
    
    public override ProcessingType ProcessingType => ProcessingType.Embedding;
    public override string Name => "Embedding";
    
    public EmbeddingOrchestrator(PostgreSQLDataStore dataStore, Settings settings) 
        : base(dataStore)
    {
        _settings = settings;
    }
    
    protected override async Task<int> CountItemsNeedingProcessingAsync()
    {
        return await DataStore.CountImagesNeedingEmbedding();
    }
    
    protected override async Task<List<int>> GetItemsNeedingProcessingAsync(int batchSize, int lastId)
    {
        return await DataStore.GetImagesNeedingEmbedding(batchSize, lastId);
    }
    
    protected override async Task<ProcessingJob?> GetJobForImageAsync(int imageId)
    {
        var image = DataStore.GetImage(imageId);
        if (image == null || !File.Exists(image.Path))
        {
            await DataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
            return null;
        }
        
        // Skip if no prompt (need text for text embeddings)
        if (string.IsNullOrWhiteSpace(image.Prompt))
        {
            await DataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
            return null;
        }
        
        return new ProcessingJob
        {
            ImageId = imageId,
            ImagePath = image.Path,
            AdditionalData = new EmbeddingJobData
            {
                Prompt = image.Prompt,
                NegativePrompt = image.NegativePrompt
            }
        };
    }
    
    protected override async Task WriteResultAsync(ProcessingResult result)
    {
        if (result.Success && result.Data is EmbeddingResultData embeddings)
        {
            await DataStore.StoreImageEmbeddingsAsync(
                result.ImageId,
                embeddings.BgeEmbedding,
                embeddings.ImageEmbedding,
                isRepresentative: true);
            
            var embeddingCount = (embeddings.BgeEmbedding != null ? 1 : 0) +
                                 (embeddings.ImageEmbedding != null ? 1 : 0);
            Logger.Log($"Embedding: Saved {embeddingCount} embeddings for image {result.ImageId} (BGE text + CLIP vision)");
        }
        else if (!result.Success)
        {
            Logger.Log($"Embedding: Failed for image {result.ImageId}: {result.ErrorMessage ?? "unknown error"}");
        }
        
        await DataStore.SetNeedsEmbedding(new List<int> { result.ImageId }, false);
    }
    
    /// <summary>
    /// Initialize model pools for all GPUs in the allocation.
    /// </summary>
    protected override async Task InitializeModelsAsync(ServiceAllocation allocation, CancellationToken ct)
    {
        foreach (var gpuAlloc in allocation.GpuAllocations)
        {
            if (gpuAlloc.ModelCount > 0 && !_modelPools.ContainsKey(gpuAlloc.GpuId))
            {
                var pool = new EmbeddingModelPool(gpuAlloc.GpuId, _settings);
                await pool.InitializeAsync(ct);
                _modelPools[gpuAlloc.GpuId] = pool;
            }
        }
    }
    
    /// <summary>
    /// Shutdown all model pools and free VRAM.
    /// </summary>
    protected override async Task ShutdownModelsAsync()
    {
        foreach (var pool in _modelPools.Values)
        {
            await pool.ShutdownAsync();
        }
        _modelPools.Clear();
    }
    
    protected EmbeddingModelPool? GetModelPool(int gpuId)
    {
        return _modelPools.TryGetValue(gpuId, out var pool) ? pool : null;
    }
    
    protected override IProcessingWorker CreateWorker(int gpuId, int workerId)
    {
        var pool = GetModelPool(gpuId);
        if (pool == null)
        {
            throw new InvalidOperationException($"No model pool for GPU {gpuId}. Call InitializeModelsAsync first.");
        }
        return new EmbeddingWorker(gpuId, workerId, pool);
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ShutdownModelsAsync().Wait();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Additional data for embedding jobs
/// </summary>
public class EmbeddingJobData
{
    public string? Prompt { get; init; }
    public string? NegativePrompt { get; init; }
}

/// <summary>
/// Result data from embedding processing.
/// 
/// Contains:
/// - BGE-large-en-v1.5 (1024D) - Semantic text similarity
/// - CLIP-ViT-H (1280D) - Visual image similarity
/// 
/// Note: CLIP-L/G text embeddings removed - conditioning generated on-demand.
/// </summary>
public class EmbeddingResultData
{
    public float[]? BgeEmbedding { get; init; }
    public float[]? ImageEmbedding { get; init; }
}

/// <summary>
/// Lightweight embedding worker - uses shared model pool from orchestrator.
/// Thread-safe: ONNX CUDA EP allows concurrent Run() calls.
/// </summary>
public class EmbeddingWorker : IProcessingWorker
{
    private readonly EmbeddingModelPool _modelPool;
    private bool _isBusy;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsBusy => _isBusy;
    
    public EmbeddingWorker(int gpuId, int workerId, EmbeddingModelPool modelPool)
    {
        GpuId = gpuId;
        WorkerId = workerId;
        _modelPool = modelPool;
    }
    
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        
        try
        {
            if (!_modelPool.IsReady || _modelPool.PooledOrchestrator == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Embedding model pool not ready"
                };
            }
            
            var jobData = job.AdditionalData as EmbeddingJobData;
            var prompt = jobData?.Prompt ?? "";
            var negativePrompt = jobData?.NegativePrompt;
            
            // Use the pooled orchestrator to generate embeddings (BGE + CLIP-Vision)
            var result = await _modelPool.PooledOrchestrator.GenerateEmbeddingsAsync(
                job.ImageId,
                prompt,
                negativePrompt,
                job.ImagePath,
                cancellationToken);
            
            if (result == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Embedding generation returned null"
                };
            }
            
            return new ProcessingResult
            {
                ImageId = job.ImageId,
                Success = true,
                Data = new EmbeddingResultData
                {
                    BgeEmbedding = result.BgeEmbedding,
                    ImageEmbedding = result.ImageEmbedding
                }
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"EmbeddingWorker {WorkerId}: Error processing image {job.ImageId}: {ex.Message}");
            return new ProcessingResult
            {
                ImageId = job.ImageId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _isBusy = false;
        }
    }
    
    public void Dispose()
    {
        // Worker doesn't own the model pool - nothing to dispose
    }
}
