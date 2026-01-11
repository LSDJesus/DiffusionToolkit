using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Captioning.Services;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Captioning service orchestrator - handles JoyCaption and external API captioning
/// </summary>
public class CaptioningOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    
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
        }
        
        await DataStore.SetNeedsCaptioning(new List<int> { result.ImageId }, false);
    }
    
    protected override IProcessingWorker CreateWorker(int gpuId, int workerId)
    {
        return new CaptioningWorker(gpuId, workerId, _settings);
    }
}

/// <summary>
/// Captioning worker - loads JoyCaption model and processes images
/// </summary>
public class CaptioningWorker : IProcessingWorker
{
    private readonly Settings _settings;
    private JoyCaptionService? _captionService;
    private bool _isReady;
    private bool _isBusy;
    private string _prompt = JoyCaptionService.PROMPT_DETAILED;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsReady => _isReady;
    public bool IsBusy => _isBusy;
    
    public CaptioningWorker(int gpuId, int workerId, Settings settings)
    {
        GpuId = gpuId;
        WorkerId = workerId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Logger.Log($"CaptioningWorker {WorkerId}: Initializing on GPU {GpuId}");
        
        var modelPath = _settings.JoyCaptionModelPath;
        var mmProjPath = _settings.JoyCaptionMMProjPath;
        
        if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(mmProjPath))
        {
            Logger.Log($"CaptioningWorker {WorkerId}: Model paths not configured");
            return;
        }
        
        if (!System.IO.File.Exists(modelPath))
        {
            Logger.Log($"CaptioningWorker {WorkerId}: Model file not found: {modelPath}");
            return;
        }
        
        if (!System.IO.File.Exists(mmProjPath))
        {
            Logger.Log($"CaptioningWorker {WorkerId}: MMProj file not found: {mmProjPath}");
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
        Logger.Log($"CaptioningWorker {WorkerId}: JoyCaption initialized with prompt type: {_settings.JoyCaptionDefaultPrompt}");
    }
    
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        
        try
        {
            if (!_isReady || _captionService == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Caption service not initialized"
                };
            }
            
            var result = await _captionService.CaptionImageAsync(job.ImagePath, _prompt, cancellationToken);
            
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
    
    public Task ShutdownAsync()
    {
        Logger.Log($"CaptioningWorker {WorkerId}: Shutting down");
        _captionService?.Dispose();
        _captionService = null;
        _isReady = false;
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}
