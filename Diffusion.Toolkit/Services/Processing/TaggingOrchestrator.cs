using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Tagging.Services;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Tagging model pool - holds JoyTag and WD models for a specific GPU.
/// Thread-safe: ONNX Runtime with CUDA EP supports concurrent Run() calls.
/// Multiple workers can share one pool.
/// </summary>
public class TaggingModelPool : IModelPool
{
    private readonly Settings _settings;
    private JoyTagService? _joyTagService;
    private WDTagService? _wdTagService;
    private bool _isReady;
    
    public int GpuId { get; }
    public bool IsReady => _isReady;
    public double VramUsageGb => 2.6; // JoyTag (~1.5GB) + WD (~1.1GB)
    
    public JoyTagService? JoyTagService => _joyTagService;
    public WDTagService? WDTagService => _wdTagService;
    
    public TaggingModelPool(int gpuId, Settings settings)
    {
        GpuId = gpuId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isReady) return;
        
        Logger.Log($"TaggingModelPool GPU{GpuId}: Initializing models...");
        
        // Initialize JoyTag if enabled
        if (_settings.EnableJoyTag && !string.IsNullOrEmpty(_settings.JoyTagModelPath))
        {
            var modelPath = _settings.JoyTagModelPath;
            var tagsPath = _settings.JoyTagTagsPath;
            
            if (System.IO.File.Exists(modelPath) && System.IO.File.Exists(tagsPath))
            {
                _joyTagService = new JoyTagService(
                    modelPath, 
                    tagsPath, 
                    _settings.JoyTagThreshold, 
                    GpuId);
                await _joyTagService.InitializeAsync();
                Logger.Log($"TaggingModelPool GPU{GpuId}: JoyTag loaded (threshold: {_settings.JoyTagThreshold})");
            }
            else
            {
                Logger.Log($"TaggingModelPool GPU{GpuId}: JoyTag model files not found");
            }
        }
        
        // Initialize WD if enabled
        if (_settings.EnableWDTag && !string.IsNullOrEmpty(_settings.WDTagModelPath))
        {
            var modelPath = _settings.WDTagModelPath;
            var tagsPath = _settings.WDTagTagsPath;
            
            if (System.IO.File.Exists(modelPath) && System.IO.File.Exists(tagsPath))
            {
                _wdTagService = new WDTagService(
                    modelPath, 
                    tagsPath, 
                    _settings.WDTagThreshold, 
                    GpuId);
                await _wdTagService.InitializeAsync();
                Logger.Log($"TaggingModelPool GPU{GpuId}: WDTag loaded (threshold: {_settings.WDTagThreshold})");
            }
            else
            {
                Logger.Log($"TaggingModelPool GPU{GpuId}: WDTag model files not found");
            }
        }
        
        _isReady = _joyTagService != null || _wdTagService != null;
        
        if (_isReady)
        {
            Logger.Log($"TaggingModelPool GPU{GpuId}: Ready (VRAM: ~{VramUsageGb:F1}GB)");
        }
        else
        {
            Logger.Log($"TaggingModelPool GPU{GpuId}: No tagging models available!");
        }
    }
    
    public Task ShutdownAsync()
    {
        Logger.Log($"TaggingModelPool GPU{GpuId}: Shutting down...");
        _joyTagService?.Dispose();
        _wdTagService?.Dispose();
        _joyTagService = null;
        _wdTagService = null;
        _isReady = false;
        Logger.Log($"TaggingModelPool GPU{GpuId}: Shutdown complete, VRAM freed");
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}

/// <summary>
/// Tagging service orchestrator - handles JoyTag and WD tagging.
/// Owns model pools (one per GPU), spawns lightweight workers that share the pools.
/// </summary>
public class TaggingOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    private readonly Dictionary<int, TaggingModelPool> _modelPools = new();
    
    public override ProcessingType ProcessingType => ProcessingType.Tagging;
    public override string Name => "Tagging";
    
    public TaggingOrchestrator(PostgreSQLDataStore dataStore, Settings settings) 
        : base(dataStore)
    {
        _settings = settings;
    }
    
    protected override async Task<int> CountItemsNeedingProcessingAsync()
    {
        return await DataStore.CountImagesNeedingTagging();
    }
    
    protected override async Task<List<int>> GetItemsNeedingProcessingAsync(int batchSize, int lastId)
    {
        return await DataStore.GetImagesNeedingTagging(batchSize, lastId);
    }
    
    protected override async Task<ProcessingJob?> GetJobForImageAsync(int imageId)
    {
        var image = DataStore.GetImage(imageId);
        if (image == null || !System.IO.File.Exists(image.Path))
        {
            // Mark as processed to skip in future
            await DataStore.SetNeedsTagging(new List<int> { imageId }, false);
            return null;
        }
        
        return new ProcessingJob
        {
            ImageId = imageId,
            ImagePath = image.Path
        };
    }
    
    protected override async Task WriteResultAsync(ProcessingResult result)
    {
        if (result.Success && result.Data is List<(string Tag, float Confidence)> tags && tags.Count > 0)
        {
            // Determine tagger source based on what was enabled
            var source = (_settings.EnableJoyTag && _settings.EnableWDTag) 
                ? "joytag+wdv3large" 
                : (_settings.EnableJoyTag ? "joytag" : "wdv3large");
            
            await DataStore.StoreImageTagsAsync(result.ImageId, tags, source);
            Logger.Log($"Tagging: Saved {tags.Count} tags for image {result.ImageId} (source: {source})");
        }
        else if (!result.Success)
        {
            Logger.Log($"Tagging: No tags generated for image {result.ImageId}: {result.ErrorMessage ?? "unknown error"}");
        }
        
        // Clear the flag regardless of success
        await DataStore.SetNeedsTagging(new List<int> { result.ImageId }, false);
    }
    
    /// <summary>
    /// Initialize model pools for all GPUs in the allocation.
    /// Called before workers are created.
    /// </summary>
    protected override async Task InitializeModelsAsync(ServiceAllocation allocation, CancellationToken ct)
    {
        foreach (var gpuAlloc in allocation.GpuAllocations)
        {
            if (gpuAlloc.ModelCount > 0 && !_modelPools.ContainsKey(gpuAlloc.GpuId))
            {
                var pool = new TaggingModelPool(gpuAlloc.GpuId, _settings);
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
    
    /// <summary>
    /// Get the model pool for a specific GPU.
    /// </summary>
    protected TaggingModelPool? GetModelPool(int gpuId)
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
        return new TaggingWorker(gpuId, workerId, pool);
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
/// Lightweight tagging worker - uses shared model pool from orchestrator.
/// Does NOT own models - just processes images using provided pool.
/// Thread-safe: ONNX CUDA EP allows concurrent Run() calls.
/// </summary>
public class TaggingWorker : IProcessingWorker
{
    private readonly TaggingModelPool _modelPool;
    private bool _isBusy;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsBusy => _isBusy;
    
    public TaggingWorker(int gpuId, int workerId, TaggingModelPool modelPool)
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
            if (!_modelPool.IsReady)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Tagging model pool not ready"
                };
            }
            
            var allTags = new List<(string Tag, float Confidence)>();
            
            // Run both taggers in parallel if both are available
            // Thread-safe: ONNX CUDA EP supports concurrent calls
            if (_modelPool.JoyTagService != null && _modelPool.WDTagService != null)
            {
                var joyTask = _modelPool.JoyTagService.TagImageAsync(job.ImagePath);
                var wdTask = _modelPool.WDTagService.TagImageAsync(job.ImagePath);
                await Task.WhenAll(joyTask, wdTask);
                
                var joyTags = await joyTask;
                var wdTags = await wdTask;
                
                if (joyTags != null)
                    allTags.AddRange(joyTags.Select(t => (t.Tag, t.Confidence)));
                if (wdTags != null)
                    allTags.AddRange(wdTags.Select(t => (t.Tag, t.Confidence)));
            }
            else if (_modelPool.JoyTagService != null)
            {
                var tags = await _modelPool.JoyTagService.TagImageAsync(job.ImagePath);
                if (tags != null)
                    allTags.AddRange(tags.Select(t => (t.Tag, t.Confidence)));
            }
            else if (_modelPool.WDTagService != null)
            {
                var tags = await _modelPool.WDTagService.TagImageAsync(job.ImagePath);
                if (tags != null)
                    allTags.AddRange(tags.Select(t => (t.Tag, t.Confidence)));
            }
            
            return new ProcessingResult
            {
                ImageId = job.ImageId,
                Success = allTags.Count > 0,
                Data = allTags
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"TaggingWorker {WorkerId}: Error processing {job.ImagePath}: {ex.Message}");
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
