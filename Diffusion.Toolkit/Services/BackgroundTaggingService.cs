using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Background service for tagging and captioning images using persistent worker pools
/// </summary>
public class BackgroundTaggingService : IDisposable
{
    private readonly PostgreSQLDataStore _dataStore;
    
    // Tagging orchestrator
    private Channel<int>? _taggingQueue;
    private CancellationTokenSource? _taggingCts;
    private Task? _taggingOrchestratorTask;
    private List<Task>? _taggingWorkers;
    
    // Captioning orchestrator
    private Channel<int>? _captioningQueue;
    private CancellationTokenSource? _captioningCts;
    private Task? _captioningOrchestratorTask;
    private List<Task>? _captioningWorkers;
    
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
    
    // ETA tracking
    private DateTime _taggingStartTime;
    private DateTime _captioningStartTime;
    private int _taggingQueueRemaining;
    private int _captioningQueueRemaining;

    public event EventHandler<ProgressEventArgs>? TaggingProgressChanged;
    public event EventHandler<ProgressEventArgs>? CaptioningProgressChanged;
    public event EventHandler? TaggingCompleted;
    public event EventHandler? CaptioningCompleted;
    public event EventHandler<string>? StatusChanged;

    private void RaiseTaggingProgress(int current, int total)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            TaggingProgressChanged?.Invoke(this, new ProgressEventArgs(current, total));
        });
    }

    private void RaiseCaptioningProgress(int current, int total)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            CaptioningProgressChanged?.Invoke(this, new ProgressEventArgs(current, total));
        });
    }

    private void RaiseTaggingCompleted()
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            TaggingCompleted?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RaiseCaptioningCompleted()
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            CaptioningCompleted?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RaiseStatusChanged(string status)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            StatusChanged?.Invoke(this, status);
        });
    }

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
        
        // Register cleanup on app exit
        Application.Current.Exit += OnApplicationExit;
    }

    private void OnApplicationExit(object sender, ExitEventArgs e)
    {
        Logger.Log("Application exiting - shutting down background workers");
        Dispose();
    }

    public void Dispose()
    {
        StopTagging();
        StopCaptioning();
        
        _captioningTTLTimer?.Dispose();
        _taggingCts?.Dispose();
        _captioningCts?.Dispose();
        
        Logger.Log("BackgroundTaggingService disposed");
    }

    /// <summary>
    /// Queue images for tagging
    /// </summary>
    public async Task QueueImagesForTagging(List<int> imageIds)
    {
        await _dataStore.SetNeedsTagging(imageIds, true);
        Logger.Log($"Queued {imageIds.Count} images for tagging");
    }

    /// <summary>
    /// Queue images for captioning
    /// </summary>
    public async Task QueueImagesForCaptioning(List<int> imageIds)
    {
        await _dataStore.SetNeedsCaptioning(imageIds, true);
        Logger.Log($"Queued {imageIds.Count} images for captioning");
    }

    /// <summary>
    /// Start background tagging with persistent worker pool
    /// </summary>
    public void StartTagging()
    {
        if (_isTaggingRunning)
        {
            Logger.Log("Tagging already running");
            return;
        }

        try
        {
            _taggingCts = new CancellationTokenSource();
            _isTaggingRunning = true;
            _isTaggingPaused = false;
            _taggingProgress = 0;
            _taggingStartTime = DateTime.Now;
            _taggingQueueRemaining = 0;
            
            var maxWorkers = ServiceLocator.Settings?.TaggingConcurrentWorkers ?? 4;
            
            // Create unbounded channel for work queue
            _taggingQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });
            
            // Start orchestrator task (fills queue and coordinates)
            _taggingOrchestratorTask = Task.Run(async () =>
            {
                try
                {
                    await RunTaggingOrchestrator(_taggingCts.Token);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Tagging orchestrator error: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    _isTaggingRunning = false;
                }
            });
            
            // Start persistent worker pool
            _taggingWorkers = new List<Task>();
            for (int i = 0; i < maxWorkers; i++)
            {
                int workerId = i;
                var workerTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunTaggingWorker(workerId, _taggingCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Tagging worker {workerId} error: {ex.Message}\n{ex.StackTrace}");
                    }
                });
                _taggingWorkers.Add(workerTask);
            }
            
            Logger.Log($"Started tagging orchestrator with {maxWorkers} persistent workers");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start tagging: {ex.Message}\n{ex.StackTrace}");
            _isTaggingRunning = false;
        }
    }

    /// <summary>
    /// Start background captioning with persistent worker pool
    /// </summary>
    public void StartCaptioning()
    {
        if (_isCaptioningRunning)
        {
            Logger.Log("Captioning already running");
            return;
        }

        try
        {
            _captioningCts = new CancellationTokenSource();
            _isCaptioningRunning = true;
            _isCaptioningPaused = false;
            _captioningProgress = 0;
            _captioningStartTime = DateTime.Now;
            _captioningQueueRemaining = 0;
            
            var maxWorkers = 2; // Captioning uses 1-2 workers due to large models
            
            // Create unbounded channel for work queue
            _captioningQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });
            
            // Start orchestrator task
            _captioningOrchestratorTask = Task.Run(async () =>
            {
                try
                {
                    await RunCaptioningOrchestrator(_captioningCts.Token);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Captioning orchestrator error: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    _isCaptioningRunning = false;
                }
            });
            
            // Start persistent worker pool
            _captioningWorkers = new List<Task>();
            for (int i = 0; i < maxWorkers; i++)
            {
                int workerId = i;
                var workerTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunCaptioningWorker(workerId, _captioningCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Captioning worker {workerId} error: {ex.Message}\n{ex.StackTrace}");
                    }
                });
                _captioningWorkers.Add(workerTask);
            }
            
            Logger.Log($"Started captioning orchestrator with {maxWorkers} persistent workers");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start captioning: {ex.Message}\n{ex.StackTrace}");
            _isCaptioningRunning = false;
        }
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
        Logger.Log("Stopping tagging...");
        _isTaggingRunning = false;
        _taggingCts?.Cancel();
        
        // Complete the queue to signal workers to exit
        _taggingQueue?.Writer.Complete();
        
        // Wait for workers to finish (with timeout)
        try
        {
            if (_taggingWorkers != null)
            {
                Task.WaitAll(_taggingWorkers.ToArray(), TimeSpan.FromSeconds(5));
            }
            _taggingOrchestratorTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception while stopping tagging: {ex.Message}");
        }
        
        _taggingWorkers?.Clear();
        Logger.Log("Tagging stopped");
    }

    /// <summary>
    /// Orchestrator: Populates queue with image IDs from database
    /// </summary>
    private async Task RunTaggingOrchestrator(CancellationToken ct)
    {
        try
        {
            Logger.Log("Tagging orchestrator started");
            
            // Get total count for progress tracking
            _taggingTotal = await _dataStore.CountImagesNeedingTagging();
            _taggingQueueRemaining = _taggingTotal;
            Logger.Log($"Found {_taggingTotal} images to tag");
            
            // Populate queue with all pending image IDs
            const int batchSize = 1000;
            int offset = 0;
            
            while (!ct.IsCancellationRequested)
            {
                var batch = await _dataStore.GetImagesNeedingTagging(batchSize);
                if (batch.Count == 0)
                    break;
                
                foreach (var imageId in batch)
                {
                    await _taggingQueue.Writer.WriteAsync(imageId, ct);
                }
                
                offset += batch.Count;
                
                if (batch.Count < batchSize)
                    break; // No more images
            }
            
            // Signal workers that no more work is coming
            _taggingQueue.Writer.Complete();
            Logger.Log($"Tagging orchestrator populated queue with {offset} images");
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Tagging orchestrator cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"Tagging orchestrator error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            RaiseTaggingCompleted();
        }
    }

    /// <summary>
    /// Persistent worker: Pulls images from queue and processes them
    /// </summary>
    private async Task RunTaggingWorker(int workerId, CancellationToken ct)
    {
        Logger.Log($"Tagging worker {workerId} started");
        
        try
        {
            // Keep models loaded for efficiency
            var joyTagService = ServiceLocator.JoyTagService;
            var wdTagService = ServiceLocator.WDTagService;
            
            await foreach (var imageId in _taggingQueue.Reader.ReadAllAsync(ct))
            {
                // Check if paused
                while (_isTaggingPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                
                if (ct.IsCancellationRequested)
                    break;
                
                try
                {
                    await ProcessImageTagging(imageId, joyTagService, wdTagService);
                    
                    var progress = Interlocked.Increment(ref _taggingProgress);
                    var remaining = Interlocked.Decrement(ref _taggingQueueRemaining);
                    
                    // Calculate ETA after 5 images
                    if (progress >= 5)
                    {
                        var elapsed = DateTime.Now - _taggingStartTime;
                        var avgTimePerImage = elapsed.TotalSeconds / progress;
                        var etaSeconds = avgTimePerImage * remaining;
                        var eta = TimeSpan.FromSeconds(etaSeconds);
                        
                        var status = $"Tagging: {progress}/{_taggingTotal} (Queue: {remaining}, ETA: {eta:hh\\:mm\\:ss})";
                        RaiseStatusChanged(status);
                    }
                    else
                    {
                        RaiseStatusChanged($"Tagging: {progress}/{_taggingTotal} (Queue: {remaining})");
                    }
                    
                    RaiseTaggingProgress(progress, _taggingTotal);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Worker {workerId} error processing image {imageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"Tagging worker {workerId} cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"Tagging worker {workerId} fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            Logger.Log($"Tagging worker {workerId} exiting");
        }
    }

    private async Task ProcessImageTagging(int imageId, 
        Tagging.Services.JoyTagService? joyTagService, 
        Tagging.Services.WDTagService? wdTagService)
    {
        try
        {
            var image = _dataStore.GetImage(imageId);
            if (image == null)
            {
                Logger.Log($"Image {imageId} not found, skipping");
                await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
                return;
            }

            if (!System.IO.File.Exists(image.Path))
            {
                Logger.Log($"Image file not found: {image.Path}, skipping");
                await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
                return;
            }

            // Use passed-in services (already loaded by worker)
            if (joyTagService == null && wdTagService == null)
            {
                Logger.Log($"No tagging services available, skipping image {imageId}");
                Interlocked.Increment(ref _taggingSkipped);
                await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
                return;
            }

            RaiseStatusChanged($"Tagging: {System.IO.Path.GetFileName(image.Path)}");

            List<(string Tag, float Confidence)> allTags = new();

            // Run both taggers if available
            if (joyTagService != null && wdTagService != null)
            {
                var joyTagsTask = joyTagService.TagImageAsync(image.Path);
                var wdTagsTask = wdTagService.TagImageAsync(image.Path);
                await Task.WhenAll(joyTagsTask, wdTagsTask);

                var joyTags = await joyTagsTask;
                var wdTags = await wdTagsTask;

                if (joyTags != null && wdTags != null)
                {
                    allTags = joyTags.Concat(wdTags).Select(t => (t.Tag, t.Confidence)).ToList();
                    await _dataStore.StoreImageTagsAsync(imageId, allTags, "joytag+wdv3large");
                }
                else
                {
                    Logger.Log($"Tagger returned null for image {imageId}");
                }
            }
            else if (joyTagService != null)
            {
                var tags = await joyTagService.TagImageAsync(image.Path);
                if (tags != null)
                {
                    allTags = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                    await _dataStore.StoreImageTagsAsync(imageId, allTags, "joytag");
                }
            }
            else if (wdTagService != null)
            {
                var tags = await wdTagService.TagImageAsync(image.Path);
                if (tags != null)
                {
                    allTags = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                    await _dataStore.StoreImageTagsAsync(imageId, allTags, "wdv3large");
                }
            }

            Logger.Log($"Tagged image {imageId} with {allTags.Count} tags");

            // Write tags to metadata if enabled
            if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                ServiceLocator.Settings?.WriteTagsToMetadata == true &&
                allTags.Count > 0)
            {
                var dbTags = await _dataStore.GetImageTagsAsync(imageId);
                var latestCaption = await _dataStore.GetLatestCaptionAsync(imageId);

                if (dbTags != null && dbTags.Any())
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
            }

            // Mark as processed
            await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
            
            // DON'T release models - workers keep them loaded for efficiency
        }
        catch (Exception ex)
        {
            Logger.Log($"Error tagging image {imageId}: {ex.Message}\n{ex.StackTrace}");
            // Mark as processed to avoid infinite retry
            try
            {
                await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
            }
            catch (Exception ex2)
            {
                Logger.Log($"Failed to mark image {imageId} as processed: {ex2.Message}");
            }
        }
    }

    public void StopCaptioning()
    {
        Logger.Log("Stopping captioning...");
        _isCaptioningRunning = false;
        _captioningCts?.Cancel();
        
        // Complete the queue to signal workers to exit
        _captioningQueue?.Writer.Complete();
        
        // Wait for workers to finish (with timeout)
        try
        {
            if (_captioningWorkers != null)
            {
                Task.WaitAll(_captioningWorkers.ToArray(), TimeSpan.FromSeconds(5));
            }
            _captioningOrchestratorTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception while stopping captioning: {ex.Message}");
        }
        
        _captioningWorkers?.Clear();
        Logger.Log("Captioning stopped");
    }

    /// <summary>
    /// Orchestrator: Populates queue with image IDs from database
    /// </summary>
    private async Task RunCaptioningOrchestrator(CancellationToken ct)
    {
        try
        {
            Logger.Log("Captioning orchestrator started");
            
            // Get total count for progress tracking
            _captioningTotal = await _dataStore.CountImagesNeedingCaptioning();
            _captioningQueueRemaining = _captioningTotal;
            Logger.Log($"Found {_captioningTotal} images to caption");
            
            // Populate queue with all pending image IDs
            const int batchSize = 500; // Smaller batches for captioning
            int offset = 0;
            
            while (!ct.IsCancellationRequested)
            {
                var batch = await _dataStore.GetImagesNeedingCaptioning(batchSize);
                if (batch.Count == 0)
                    break;
                
                foreach (var imageId in batch)
                {
                    await _captioningQueue.Writer.WriteAsync(imageId, ct);
                }
                
                offset += batch.Count;
                
                if (batch.Count < batchSize)
                    break; // No more images
            }
            
            // Signal workers that no more work is coming
            _captioningQueue.Writer.Complete();
            Logger.Log($"Captioning orchestrator populated queue with {offset} images");
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Captioning orchestrator cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"Captioning orchestrator error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            RaiseCaptioningCompleted();
        }
    }

    /// <summary>
    /// Persistent worker: Pulls images from queue and processes them
    /// </summary>
    private async Task RunCaptioningWorker(int workerId, CancellationToken ct)
    {
        Logger.Log($"Captioning worker {workerId} started");
        
        try
        {
            // Keep models loaded for efficiency
            var captionService = ServiceLocator.CaptionService;
            
            await foreach (var imageId in _captioningQueue.Reader.ReadAllAsync(ct))
            {
                // Check if paused
                while (_isCaptioningPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                
                if (ct.IsCancellationRequested)
                    break;
                
                try
                {
                    await ProcessImageCaptioning(imageId, captionService as Captioning.Services.JoyCaptionService);
                    
                    var progress = Interlocked.Increment(ref _captioningProgress);
                    var remaining = Interlocked.Decrement(ref _captioningQueueRemaining);
                    
                    // Calculate ETA after 3 images (captioning is slower)
                    if (progress >= 3)
                    {
                        var elapsed = DateTime.Now - _captioningStartTime;
                        var avgTimePerImage = elapsed.TotalSeconds / progress;
                        var etaSeconds = avgTimePerImage * remaining;
                        var eta = TimeSpan.FromSeconds(etaSeconds);
                        
                        var status = $"Captioning: {progress}/{_captioningTotal} (Queue: {remaining}, ETA: {eta:hh\\:mm\\:ss})";
                        RaiseStatusChanged(status);
                    }
                    else
                    {
                        RaiseStatusChanged($"Captioning: {progress}/{_captioningTotal} (Queue: {remaining})");
                    }
                    
                    RaiseCaptioningProgress(progress, _captioningTotal);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Worker {workerId} error processing image {imageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"Captioning worker {workerId} cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"Captioning worker {workerId} fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            Logger.Log($"Captioning worker {workerId} exiting");
        }
    }

    private async Task ProcessImageCaptioning(int imageId, Captioning.Services.JoyCaptionService? captionService)
    {
        try
        {
            var image = _dataStore.GetImage(imageId);
            if (image == null)
            {
                await _dataStore.SetNeedsCaptioning(new List<int> { imageId }, false);
                return;
            }

            // Use passed-in service (already loaded by worker)
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
            
            RaiseStatusChanged($"Captioning: {System.IO.Path.GetFileName(image.Path)}");

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
