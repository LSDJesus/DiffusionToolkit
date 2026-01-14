using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Captioning.Services;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Captioning model instance - holds a single JoyCaptionService for one worker.
/// NOT thread-safe: LlamaSharp requires 1:1 worker:model ratio.
/// Each worker gets its own instance.
/// </summary>
public class CaptioningModelInstance : IModelPool
{
    private readonly Settings _settings;
    private JoyCaptionService? _captionService;
    private bool _isReady;
    private string _prompt = JoyCaptionService.PROMPT_DETAILED;
    
    public int GpuId { get; }
    public int InstanceId { get; }
    public bool IsReady => _isReady;
    public double VramUsageGb => 5.6; // JoyCaption Q4 GGUF
    
    public JoyCaptionService? CaptionService => _captionService;
    public string Prompt => _prompt;
    
    public CaptioningModelInstance(int gpuId, int instanceId, Settings settings)
    {
        GpuId = gpuId;
        InstanceId = instanceId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isReady) return;
        
        Logger.Log($"CaptioningModelInstance GPU{GpuId}#{InstanceId}: Initializing...");
        
        var modelPath = _settings.JoyCaptionModelPath;
        var mmProjPath = _settings.JoyCaptionMMProjPath;
        
        if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(mmProjPath))
        {
            Logger.Log($"CaptioningModelInstance GPU{GpuId}#{InstanceId}: Model paths not configured");
            return;
        }
        
        if (!System.IO.File.Exists(modelPath))
        {
            Logger.Log($"CaptioningModelInstance GPU{GpuId}#{InstanceId}: Model file not found: {modelPath}");
            return;
        }
        
        if (!System.IO.File.Exists(mmProjPath))
        {
            Logger.Log($"CaptioningModelInstance GPU{GpuId}#{InstanceId}: MMProj file not found: {mmProjPath}");
            return;
        }
        
        // Determine prompt from settings
        _prompt = _settings.JoyCaptionDefaultPrompt?.ToLowerInvariant() switch
        {
            "detailed" => JoyCaptionService.PROMPT_DETAILED,
            "short" => JoyCaptionService.PROMPT_SHORT,
            "technical" => JoyCaptionService.PROMPT_TECHNICAL,
            "colors" or "precise_colors" => JoyCaptionService.PROMPT_PRECISE_COLORS,
            "character" or "character_focus" => JoyCaptionService.PROMPT_CHARACTER_FOCUS,
            "scene" or "scene_focus" => JoyCaptionService.PROMPT_SCENE_FOCUS,
            "artistic" or "artistic_style" => JoyCaptionService.PROMPT_ARTISTIC_STYLE,
            "booru" or "booru_tags" => JoyCaptionService.PROMPT_BOORU_TAGS,
            _ => _settings.JoyCaptionDefaultPrompt ?? JoyCaptionService.PROMPT_DETAILED
        };
        
        _captionService = new JoyCaptionService(modelPath, mmProjPath, GpuId);
        await _captionService.InitializeAsync();
        
        _isReady = true;
        Logger.Log($"CaptioningModelInstance GPU{GpuId}#{InstanceId}: Ready (prompt: {_settings.JoyCaptionDefaultPrompt}, VRAM: ~{VramUsageGb:F1}GB)");
    }
    
    public Task ShutdownAsync()
    {
        Logger.Log($"CaptioningModelInstance GPU{GpuId}#{InstanceId}: Shutting down...");
        _captionService?.Dispose();
        _captionService = null;
        _isReady = false;
        Logger.Log($"CaptioningModelInstance GPU{GpuId}#{InstanceId}: Shutdown complete, VRAM freed");
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}

/// <summary>
/// Captioning service orchestrator - handles JoyCaption and external API captioning.
/// DIFFERENT from ONNX services: LlamaSharp is NOT thread-safe.
/// Creates N model instances (1 per worker), not shared pools.
/// </summary>
public class CaptioningOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<int, CaptioningModelInstance> _modelInstances = new();
    private int _nextInstanceId = 0;
    
    public override ProcessingType ProcessingType => ProcessingType.Captioning;
    public override string Name => "Captioning";
    
    public CaptioningOrchestrator(PostgreSQLDataStore dataStore, Settings settings) 
        : base(dataStore)
    {
        _settings = settings;
    }
    
    protected override async Task<int> CountItemsNeedingProcessingAsync()
    {
        return await DataStore.CountImagesNeedingCaptioning();
    }
    
    protected override async Task<List<int>> GetItemsNeedingProcessingAsync(int batchSize, int lastId)
    {
        return await DataStore.GetImagesNeedingCaptioning(batchSize, lastId);
    }
    
    protected override async Task<ProcessingJob?> GetJobForImageAsync(int imageId)
    {
        var image = DataStore.GetImage(imageId);
        if (image == null || !System.IO.File.Exists(image.Path))
        {
            await DataStore.SetNeedsCaptioning(new List<int> { imageId }, false);
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
        if (result.Success && result.Data is string caption && !string.IsNullOrEmpty(caption))
        {
            await DataStore.StoreCaptionAsync(result.ImageId, caption);
            Logger.Log($"Captioning: Saved caption for image {result.ImageId} ({caption.Length} chars)");
        }
        else if (!result.Success)
        {
            Logger.Log($"Captioning: Failed for image {result.ImageId}: {result.ErrorMessage ?? "unknown error"}");
        }
        
        await DataStore.SetNeedsCaptioning(new List<int> { result.ImageId }, false);
    }
    
    /// <summary>
    /// Initialize model instances - one per worker (LlamaSharp is NOT thread-safe).
    /// </summary>
    protected override async Task InitializeModelsAsync(ServiceAllocation allocation, CancellationToken ct)
    {
        foreach (var gpuAlloc in allocation.GpuAllocations)
        {
            // For captioning, ModelCount = number of model instances (1:1 with workers)
            for (int i = 0; i < gpuAlloc.ModelCount; i++)
            {
                var instanceId = Interlocked.Increment(ref _nextInstanceId) - 1;
                var instance = new CaptioningModelInstance(gpuAlloc.GpuId, instanceId, _settings);
                await instance.InitializeAsync(ct);
                _modelInstances[instanceId] = instance;
            }
        }
    }
    
    /// <summary>
    /// Shutdown all model instances and free VRAM.
    /// </summary>
    protected override async Task ShutdownModelsAsync()
    {
        foreach (var instance in _modelInstances.Values)
        {
            await instance.ShutdownAsync();
        }
        _modelInstances.Clear();
    }
    
    /// <summary>
    /// Get model instance for a specific worker.
    /// </summary>
    private CaptioningModelInstance? GetModelInstance(int workerId)
    {
        return _modelInstances.TryGetValue(workerId, out var instance) ? instance : null;
    }
    
    protected override IProcessingWorker CreateWorker(int gpuId, int workerId)
    {
        var instance = GetModelInstance(workerId);
        if (instance == null)
        {
            throw new InvalidOperationException($"No model instance for worker {workerId}. Call InitializeModelsAsync first.");
        }
        return new CaptioningWorker(gpuId, workerId, instance);
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
/// Captioning worker - uses dedicated model instance (1:1 ratio).
/// LlamaSharp is NOT thread-safe, so each worker needs its own model.
/// </summary>
public class CaptioningWorker : IProcessingWorker
{
    private readonly CaptioningModelInstance _modelInstance;
    private bool _isBusy;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsBusy => _isBusy;
    
    public CaptioningWorker(int gpuId, int workerId, CaptioningModelInstance modelInstance)
    {
        GpuId = gpuId;
        WorkerId = workerId;
        _modelInstance = modelInstance;
    }
    
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        
        try
        {
            if (!_modelInstance.IsReady || _modelInstance.CaptionService == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Caption model instance not ready"
                };
            }
            
            var result = await _modelInstance.CaptionService.CaptionImageAsync(
                job.ImagePath, 
                _modelInstance.Prompt, 
                cancellationToken);
            
            return new ProcessingResult
            {
                ImageId = job.ImageId,
                Success = !string.IsNullOrEmpty(result?.Caption),
                Data = result?.Caption ?? ""
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"CaptioningWorker {WorkerId}: Error processing {job.ImagePath}: {ex.Message}");
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
        // Worker doesn't own the model instance - orchestrator handles lifecycle
    }
}
