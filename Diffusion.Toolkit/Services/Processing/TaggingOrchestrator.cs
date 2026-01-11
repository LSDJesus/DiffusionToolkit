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
/// Tagging service orchestrator - handles JoyTag and WD tagging
/// </summary>
public class TaggingOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    
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
        }
        
        // Clear the flag regardless of success
        await DataStore.SetNeedsTagging(new List<int> { result.ImageId }, false);
    }
    
    protected override IProcessingWorker CreateWorker(int gpuId, int workerId)
    {
        return new TaggingWorker(gpuId, workerId, _settings);
    }
}

/// <summary>
/// Tagging worker - loads JoyTag/WD models and processes images
/// </summary>
public class TaggingWorker : IProcessingWorker
{
    private readonly Settings _settings;
    private JoyTagService? _joyTagService;
    private WDTagService? _wdTagService;
    private bool _isReady;
    private bool _isBusy;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsReady => _isReady;
    public bool IsBusy => _isBusy;
    
    public TaggingWorker(int gpuId, int workerId, Settings settings)
    {
        GpuId = gpuId;
        WorkerId = workerId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Logger.Log($"TaggingWorker {WorkerId}: Initializing on GPU {GpuId}");
        
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
                Logger.Log($"TaggingWorker {WorkerId}: JoyTag initialized (threshold: {_settings.JoyTagThreshold})");
            }
            else
            {
                Logger.Log($"TaggingWorker {WorkerId}: JoyTag model files not found");
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
                Logger.Log($"TaggingWorker {WorkerId}: WDTag initialized (threshold: {_settings.WDTagThreshold})");
            }
            else
            {
                Logger.Log($"TaggingWorker {WorkerId}: WDTag model files not found");
            }
        }
        
        _isReady = _joyTagService != null || _wdTagService != null;
        
        if (!_isReady)
        {
            Logger.Log($"TaggingWorker {WorkerId}: No tagging services available!");
        }
    }
    
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        
        try
        {
            if (!_isReady)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Tagging services not initialized"
                };
            }
            
            var allTags = new List<(string Tag, float Confidence)>();
            
            // Run both taggers in parallel if both are available
            if (_joyTagService != null && _wdTagService != null)
            {
                var joyTask = _joyTagService.TagImageAsync(job.ImagePath);
                var wdTask = _wdTagService.TagImageAsync(job.ImagePath);
                await Task.WhenAll(joyTask, wdTask);
                
                var joyTags = await joyTask;
                var wdTags = await wdTask;
                
                if (joyTags != null)
                    allTags.AddRange(joyTags.Select(t => (t.Tag, t.Confidence)));
                if (wdTags != null)
                    allTags.AddRange(wdTags.Select(t => (t.Tag, t.Confidence)));
            }
            else if (_joyTagService != null)
            {
                var tags = await _joyTagService.TagImageAsync(job.ImagePath);
                if (tags != null)
                    allTags.AddRange(tags.Select(t => (t.Tag, t.Confidence)));
            }
            else if (_wdTagService != null)
            {
                var tags = await _wdTagService.TagImageAsync(job.ImagePath);
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
    
    public Task ShutdownAsync()
    {
        Logger.Log($"TaggingWorker {WorkerId}: Shutting down");
        _joyTagService?.Dispose();
        _wdTagService?.Dispose();
        _joyTagService = null;
        _wdTagService = null;
        _isReady = false;
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}
