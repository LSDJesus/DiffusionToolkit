using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Background service for tagging and captioning images without blocking the UI
/// </summary>
public class BackgroundTaggingService
{
    private readonly PostgreSQLDataStore _dataStore;
    private CancellationTokenSource? _taggingCts;
    private CancellationTokenSource? _captioningCts;
    private Task? _taggingTask;
    private Task? _captioningTask;
    
    private int _taggingProgress;
    private int _taggingTotal;
    private int _captioningProgress;
    private int _captioningTotal;
    private int _taggingSkipped;
    private int _captioningSkipped;
    private bool _isTaggingRunning;
    private bool _isCaptioningRunning;
    private bool _isTaggingPaused;
    private bool _isCaptioningPaused;
    private DateTime _lastCaptioningUseTime;
    private Timer? _captioningTTLTimer;

    public event EventHandler<ProgressEventArgs>? TaggingProgressChanged;
    public event EventHandler<ProgressEventArgs>? CaptioningProgressChanged;
    public event EventHandler? TaggingCompleted;
    public event EventHandler? CaptioningCompleted;
    public event EventHandler<string>? StatusChanged;

    public bool IsTaggingRunning => _isTaggingRunning;
    public bool IsCaptioningRunning => _isCaptioningRunning;
    public bool IsTaggingPaused => _isTaggingPaused;
    public bool IsCaptioningPaused => _isCaptioningPaused;
    
    public int TaggingProgress => _taggingProgress;
    public int TaggingTotal => _taggingTotal;
    public int CaptioningProgress => _captioningProgress;
    public int CaptioningTotal => _captioningTotal;
    public int TaggingSkipped => _taggingSkipped;
    public int CaptioningSkipped => _captioningSkipped;

    public BackgroundTaggingService(PostgreSQLDataStore dataStore)
    {
        _dataStore = dataStore;
    }

    /// <summary>
    /// Queue images for tagging
    /// </summary>
    public async Task QueueImagesForTagging(List<int> imageIds)
    {
        await _dataStore.SetNeedsTagging(imageIds, true);
        Logger.Log($"Queued {imageIds.Count} images for tagging");
        
        // Start tagging if not already running
        if (!_isTaggingRunning)
        {
            StartTagging();
        }
    }

    /// <summary>
    /// Queue images for captioning
    /// </summary>
    public async Task QueueImagesForCaptioning(List<int> imageIds)
    {
        await _dataStore.SetNeedsCaptioning(imageIds, true);
        Logger.Log($"Queued {imageIds.Count} images for captioning");
        
        // Start captioning if not already running
        if (!_isCaptioningRunning)
        {
            StartCaptioning();
        }
    }

    /// <summary>
    /// Start background tagging worker
    /// </summary>
    public void StartTagging()
    {
        if (_isTaggingRunning) return;

        _taggingCts = new CancellationTokenSource();
        _isTaggingRunning = true;
        _isTaggingPaused = false;
        
        var maxWorkers = ServiceLocator.Settings?.TaggingConcurrentWorkers ?? 8;
        
        _taggingTask = Task.Run(async () => await RunTaggingWorkers(maxWorkers, _taggingCts.Token));
        
        Logger.Log($"Started background tagging with {maxWorkers} workers");
    }

    /// <summary>
    /// Start background captioning worker
    /// </summary>
    public void StartCaptioning()
    {
        if (_isCaptioningRunning) return;

        _captioningCts = new CancellationTokenSource();
        _isCaptioningRunning = true;
        _isCaptioningPaused = false;
        
        var maxWorkers = 2; // Captioning uses 1-2 workers due to large models
        
        _captioningTask = Task.Run(async () => await RunCaptioningWorkers(maxWorkers, _captioningCts.Token));
        
        Logger.Log($"Started background captioning with {maxWorkers} workers");
    }

    public void PauseTagging()
    {
        _isTaggingPaused = true;
        Logger.Log("Paused tagging");
    }

    public void ResumeTagging()
    {
        _isTaggingPaused = false;
        Logger.Log("Resumed tagging");
    }

    public void PauseCaptioning()
    {
        _isCaptioningPaused = true;
        Logger.Log("Paused captioning");
    }

    public void ResumeCaptioning()
    {
        _isCaptioningPaused = false;
        Logger.Log("Resumed captioning");
    }

    public void StopTagging()
    {
        _taggingCts?.Cancel();
        _isTaggingRunning = false;
        _isTaggingPaused = false;
        Logger.Log("Stopped tagging");
    }

    public void StopCaptioning()
    {
        _captioningCts?.Cancel();
        _isCaptioningRunning = false;
        _isCaptioningPaused = false;
        Logger.Log("Stopped captioning");
    }

    private async Task RunTaggingWorkers(int maxWorkers, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait if paused
                while (_isTaggingPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }

                // Get images that need tagging
                var imagesToTag = await _dataStore.GetImagesNeedingTagging(maxWorkers * 10);
                
                if (imagesToTag.Count == 0)
                {
                    // No more images to tag
                    _isTaggingRunning = false;
                    TaggingCompleted?.Invoke(this, EventArgs.Empty);
                    Logger.Log("Tagging queue empty - stopping");
                    break;
                }

                _taggingTotal = await _dataStore.CountImagesNeedingTagging();
                
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxWorkers,
                    CancellationToken = ct
                };

                await Parallel.ForEachAsync(imagesToTag, parallelOptions, async (imageId, token) =>
                {
                    if (token.IsCancellationRequested || _isTaggingPaused)
                        return;

                    await ProcessImageTagging(imageId);
                    
                    Interlocked.Increment(ref _taggingProgress);
                    TaggingProgressChanged?.Invoke(this, new ProgressEventArgs(_taggingProgress, _taggingTotal));
                });
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Tagging cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"Tagging error: {ex.Message}");
        }
        finally
        {
            _isTaggingRunning = false;
        }
    }

    private async Task RunCaptioningWorkers(int maxWorkers, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait if paused
                while (_isCaptioningPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }

                // Get images that need captioning
                var imagesToCaption = await _dataStore.GetImagesNeedingCaptioning(maxWorkers * 5);
                
                if (imagesToCaption.Count == 0)
                {
                    // No more images to caption
                    _isCaptioningRunning = false;
                    CaptioningCompleted?.Invoke(this, EventArgs.Empty);
                    Logger.Log("Captioning queue empty - stopping");
                    break;
                }

                _captioningTotal = await _dataStore.CountImagesNeedingCaptioning();
                
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxWorkers,
                    CancellationToken = ct
                };

                await Parallel.ForEachAsync(imagesToCaption, parallelOptions, async (imageId, token) =>
                {
                    if (token.IsCancellationRequested || _isCaptioningPaused)
                        return;

                    await ProcessImageCaptioning(imageId);
                    
                    Interlocked.Increment(ref _captioningProgress);
                    CaptioningProgressChanged?.Invoke(this, new ProgressEventArgs(_captioningProgress, _captioningTotal));
                });
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Captioning cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"Captioning error: {ex.Message}");
        }
        finally
        {
            _isCaptioningRunning = false;
        }
    }

    private async Task ProcessImageTagging(int imageId)
    {
        try
        {
            var image = _dataStore.GetImage(imageId);
            if (image == null)
            {
                await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
                return;
            }

            // Check if both taggers are available
            var joyTagService = ServiceLocator.JoyTagService;
            var wdTagService = ServiceLocator.WDTagService;
            
            if (joyTagService == null && wdTagService == null)
            {
                Logger.Log($"No tagging services available, skipping image {imageId}");
                Interlocked.Increment(ref _taggingSkipped);
                await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
                return;
            }

            StatusChanged?.Invoke(this, $"Tagging: {System.IO.Path.GetFileName(image.Path)}");

            List<(string Tag, float Confidence)> allTags = new();

            // Run both taggers if available
            if (joyTagService != null && wdTagService != null)
            {
                var joyTagsTask = joyTagService.TagImageAsync(image.Path);
                var wdTagsTask = wdTagService.TagImageAsync(image.Path);
                await Task.WhenAll(joyTagsTask, wdTagsTask);

                var joyTags = await joyTagsTask;
                var wdTags = await wdTagsTask;

                allTags = joyTags.Concat(wdTags).Select(t => (t.Tag, t.Confidence)).ToList();
                await _dataStore.StoreImageTagsAsync(imageId, allTags, "joytag+wdv3large");
            }
            else if (joyTagService != null)
            {
                var tags = await joyTagService.TagImageAsync(image.Path);
                allTags = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                await _dataStore.StoreImageTagsAsync(imageId, allTags, "joytag");
            }
            else if (wdTagService != null)
            {
                var tags = await wdTagService.TagImageAsync(image.Path);
                allTags = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                await _dataStore.StoreImageTagsAsync(imageId, allTags, "wdv3large");
            }

            Logger.Log($"Tagged image {imageId} with {allTags.Count} tags");

            // Write tags to metadata if enabled
            if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                ServiceLocator.Settings?.WriteTagsToMetadata == true)
            {
                var dbTags = await _dataStore.GetImageTagsAsync(imageId);
                var latestCaption = await _dataStore.GetLatestCaptionAsync(imageId);

                var request = new Scanner.MetadataWriteRequest
                {
                    Prompt = image.Prompt,
                    NegativePrompt = image.NegativePrompt,
                    Steps = image.Steps,
                    Sampler = image.Sampler,
                    CFGScale = image.CfgScale,
                    Seed = image.Seed,
                    Width = image.Width,
                    Height = image.Height,
                    Model = image.Model,
                    ModelHash = image.ModelHash,
                    Tags = dbTags.Select(t => new Scanner.TagWithConfidence 
                    { 
                        Tag = t.Tag, 
                        Confidence = t.Confidence 
                    }).ToList(),
                    Caption = latestCaption?.Caption,
                    AestheticScore = image.AestheticScore > 0 ? (decimal?)image.AestheticScore : null,
                    CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                };
                Scanner.MetadataWriter.WriteMetadata(image.Path, request);
            }

            // Mark as processed
            await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
            
            // Release small tagging models to free VRAM (they're quick to reload)
            ServiceLocator.JoyTagService?.ReleaseModel();
            ServiceLocator.WDTagService?.ReleaseModel();
        }
        catch (Exception ex)
        {
            Logger.Log($"Error tagging image {imageId}: {ex.Message}");
            // Mark as processed to avoid infinite retry
            await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
        }
    }

    private async Task ProcessImageCaptioning(int imageId)
    {
        try
        {
            var image = _dataStore.GetImage(imageId);
            if (image == null)
            {
                await _dataStore.SetNeedsCaptioning(new List<int> { imageId }, false);
                return;
            }

            var captionService = ServiceLocator.CaptionService;
            if (captionService == null)
            {
                Logger.Log($"No caption service available, skipping image {imageId}");
                Interlocked.Increment(ref _captioningSkipped);
                await _dataStore.SetNeedsCaptioning(new List<int> { imageId }, false);
                return;
            }

            // Update last usage time and restart TTL timer for caption models
            _lastCaptioningUseTime = DateTime.UtcNow;
            RestartCaptioningTTLTimer();
            
            StatusChanged?.Invoke(this, $"Captioning: {System.IO.Path.GetFileName(image.Path)}");

            // Get default prompt
            var promptText = Captioning.Services.JoyCaptionService.PROMPT_DETAILED;
            var defaultPrompt = ServiceLocator.Settings?.JoyCaptionDefaultPrompt ?? "detailed";
            promptText = defaultPrompt switch
            {
                "detailed" => Captioning.Services.JoyCaptionService.PROMPT_DETAILED,
                "short" => Captioning.Services.JoyCaptionService.PROMPT_SHORT,
                "technical" => Captioning.Services.JoyCaptionService.PROMPT_TECHNICAL,
                "precise_colors" => Captioning.Services.JoyCaptionService.PROMPT_PRECISE_COLORS,
                "character" => Captioning.Services.JoyCaptionService.PROMPT_CHARACTER_FOCUS,
                "scene" => Captioning.Services.JoyCaptionService.PROMPT_SCENE_FOCUS,
                "artistic" => Captioning.Services.JoyCaptionService.PROMPT_ARTISTIC_STYLE,
                "booru" => Captioning.Services.JoyCaptionService.PROMPT_BOORU_TAGS,
                _ => Captioning.Services.JoyCaptionService.PROMPT_DETAILED
            };

            var result = await captionService.CaptionImageAsync(image.Path, promptText);
            var finalCaption = result.Caption;

            Logger.Log($"Captioned image {imageId}: {finalCaption?.Substring(0, Math.Min(50, finalCaption?.Length ?? 0))}...");

            var captionSource = ServiceLocator.Settings?.CaptionProvider == Configuration.CaptionProviderType.LocalJoyCaption 
                ? "joycaption" : "openai";
            
            await _dataStore.StoreCaptionAsync(imageId, finalCaption!, captionSource, 
                result.PromptUsed, result.TokenCount, (float)result.GenerationTimeMs);

            // Write caption to metadata if enabled
            if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                ServiceLocator.Settings?.WriteCaptionsToMetadata == true)
            {
                var request = new Scanner.MetadataWriteRequest
                {
                    Prompt = image.Prompt,
                    NegativePrompt = image.NegativePrompt,
                    Steps = image.Steps,
                    Sampler = image.Sampler,
                    CFGScale = image.CfgScale,
                    Seed = image.Seed,
                    Width = image.Width,
                    Height = image.Height,
                    Model = image.Model,
                    ModelHash = image.ModelHash,
                    Caption = finalCaption,
                    AestheticScore = image.AestheticScore > 0 ? (decimal?)image.AestheticScore : null,
                    CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                };
                Scanner.MetadataWriter.WriteMetadata(image.Path, request);
            }

            // Mark as processed
            await _dataStore.SetNeedsCaptioning(new List<int> { imageId }, false);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error captioning image {imageId}: {ex.Message}");
            // Mark as processed to avoid infinite retry
            await _dataStore.SetNeedsCaptioning(new List<int> { imageId }, false);
        }
    }

    /// <summary>
    /// Restart the TTL timer for auto-releasing caption models
    /// </summary>
    private void RestartCaptioningTTLTimer()
    {
        _captioningTTLTimer?.Dispose();
        
        var ttlMinutes = ServiceLocator.Settings?.CaptioningModelTTLMinutes ?? 2;
        
        // -1 means keep loaded indefinitely
        if (ttlMinutes == -1)
        {
            return;
        }
        
        // 0 means release immediately
        if (ttlMinutes == 0)
        {
            ServiceLocator.CaptionService?.ReleaseModel();
            return;
        }
        
        // 1-10: schedule release after TTL
        _captioningTTLTimer = new Timer(_ =>
        {
            var timeSinceLastUse = DateTime.UtcNow - _lastCaptioningUseTime;
            if (timeSinceLastUse.TotalMinutes >= ttlMinutes && !_isCaptioningRunning)
            {
                Logger.Log($"Caption model TTL ({ttlMinutes} min) expired, releasing from VRAM");
                ServiceLocator.CaptionService?.ReleaseModel();
                _captioningTTLTimer?.Dispose();
                _captioningTTLTimer = null;
            }
        }, null, TimeSpan.FromMinutes(ttlMinutes), TimeSpan.FromMinutes(ttlMinutes));
    }
}

public class ProgressEventArgs : EventArgs
{
    public int Current { get; }
    public int Total { get; }
    public double Percentage => Total > 0 ? (double)Current / Total * 100 : 0;

    public ProgressEventArgs(int current, int total)
    {
        Current = current;
        Total = total;
    }
}
