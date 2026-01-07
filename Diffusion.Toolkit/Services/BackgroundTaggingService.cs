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
    private List<Task>? _taggingOrchestrators;  // Multiple orchestrators for multi-GPU
    
    // Captioning orchestrator
    private Channel<int>? _captioningQueue;
    private CancellationTokenSource? _captioningCts;
    private List<Task>? _captioningOrchestrators;  // Multiple orchestrators for multi-GPU
    
    // Embedding orchestrator
    private Channel<int>? _embeddingQueue;
    private CancellationTokenSource? _embeddingCts;
    private List<Task>? _embeddingOrchestrators;
    
    private int _taggingProgress;
    private int _taggingTotal;
    private int _captioningProgress;
    private int _captioningTotal;
    private int _embeddingProgress;
    private int _embeddingTotal;
    private int _taggingSkipped;
    private int _captioningSkipped;
    private int _embeddingSkipped;
    private bool _isTaggingRunning;
    private bool _isCaptioningRunning;
    private bool _isEmbeddingRunning;
    private bool _isTaggingPaused;
    private bool _isCaptioningPaused;
    private bool _isEmbeddingPaused;
    private DateTime _lastCaptioningUseTime;
    private Timer? _captioningTTLTimer;
    
    // ETA tracking
    private DateTime _taggingStartTime;
    private DateTime _captioningStartTime;
    private DateTime _embeddingStartTime;
    private int _taggingQueueRemaining;
    private int _captioningQueueRemaining;
    private int _embeddingQueueRemaining;

    public int TaggingQueueRemaining => _taggingQueueRemaining;
    public int CaptioningQueueRemaining => _captioningQueueRemaining;
    public int EmbeddingQueueRemaining => _embeddingQueueRemaining;

    public event EventHandler? QueueCountsChanged;

    public event EventHandler<ProgressEventArgs>? TaggingProgressChanged;
    public event EventHandler<ProgressEventArgs>? CaptioningProgressChanged;
    public event EventHandler<ProgressEventArgs>? EmbeddingProgressChanged;
    public event EventHandler? TaggingCompleted;
    public event EventHandler? CaptioningCompleted;
    public event EventHandler? EmbeddingCompleted;
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

    private void RaiseEmbeddingProgress(int current, int total)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            EmbeddingProgressChanged?.Invoke(this, new ProgressEventArgs(current, total));
        });
    }

    private void RaiseEmbeddingCompleted()
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            EmbeddingCompleted?.Invoke(this, EventArgs.Empty);
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
    public bool IsEmbeddingRunning => _isEmbeddingRunning;
    public bool IsTaggingPaused => _isTaggingPaused;
    public bool IsCaptioningPaused => _isCaptioningPaused;
    public bool IsEmbeddingPaused => _isEmbeddingPaused;
    
    public int TaggingProgress => _taggingProgress;
    public int TaggingTotal => _taggingTotal;
    public int CaptioningProgress => _captioningProgress;
    public int CaptioningTotal => _captioningTotal;
    public int EmbeddingProgress => _embeddingProgress;
    public int EmbeddingTotal => _embeddingTotal;
    public int TaggingSkipped => _taggingSkipped;
    public int CaptioningSkipped => _captioningSkipped;
    public int EmbeddingSkipped => _embeddingSkipped;

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
        
        // Update queue count immediately
        _taggingQueueRemaining = await _dataStore.CountImagesNeedingTagging();
        QueueCountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Queue images for captioning
    /// </summary>
    public async Task QueueImagesForCaptioning(List<int> imageIds)
    {
        await _dataStore.SetNeedsCaptioning(imageIds, true);
        Logger.Log($"Queued {imageIds.Count} images for captioning");
        
        // Update queue count immediately
        _captioningQueueRemaining = await _dataStore.CountImagesNeedingCaptioning();
        QueueCountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clear the tagging queue (resets needs_tagging flag without touching has_tags)
    /// </summary>
    public async Task ClearTaggingQueue()
    {
        if (_isTaggingRunning)
        {
            Logger.Log("Cannot clear tagging queue while tagging is running");
            return;
        }
        
        try
        {
            await _dataStore.ClearTaggingQueue();
            Logger.Log("Cleared tagging queue");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error clearing tagging queue: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear the captioning queue (resets needs_captioning flag without touching has_captions)
    /// </summary>
    public async Task ClearCaptioningQueue()
    {
        if (_isCaptioningRunning)
        {
            Logger.Log("Cannot clear captioning queue while captioning is running");
            return;
        }
        
        try
        {
            await _dataStore.ClearCaptioningQueue();
            Logger.Log("Cleared captioning queue");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error clearing captioning queue: {ex.Message}");
        }
    }

    /// <summary>
    /// Start background tagging with persistent worker pool and multi-GPU support
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
            var gpuDevices = ServiceLocator.Settings?.TaggingGpuDevices ?? "0";
            var gpuVramRatios = ServiceLocator.Settings?.TaggingGpuVramRatios ?? "32";
            
            // Parse GPU device IDs
            var deviceIds = gpuDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(d => int.TryParse(d, out var id) ? id : 0)
                                       .Distinct()
                                       .ToList();
            
            // Parse VRAM ratios (in GB)
            var vramRatios = gpuVramRatios.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Select(v => int.TryParse(v, out var gb) ? gb : 32)
                                          .ToList();
            
            // Pad VRAM ratios if needed (use last value for additional GPUs)
            while (vramRatios.Count < deviceIds.Count)
            {
                vramRatios.Add(vramRatios.Last());
            }
            
            // Calculate worker distribution based on VRAM proportions
            var totalVram = vramRatios.Take(deviceIds.Count).Sum();
            var workerDistribution = new List<int>();
            int workersAssigned = 0;
            
            for (int i = 0; i < deviceIds.Count; i++)
            {
                if (i == deviceIds.Count - 1)
                {
                    // Last GPU gets all remaining workers
                    workerDistribution.Add(maxWorkers - workersAssigned);
                }
                else
                {
                    // Proportional allocation based on VRAM
                    var proportion = (double)vramRatios[i] / totalVram;
                    var workers = (int)Math.Max(1, Math.Round(maxWorkers * proportion));
                    workerDistribution.Add(workers);
                    workersAssigned += workers;
                }
            }
            
            // Create unbounded channel for shared work queue
            _taggingQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,  // Multiple orchestrators reading
                SingleWriter = true
            });
            
            // Populate queue with all pending image IDs
            Task.Run(async () =>
            {
                try
                {
                    _taggingTotal = await _dataStore.CountImagesNeedingTagging();
                    _taggingQueueRemaining = _taggingTotal;
                    Logger.Log($"Found {_taggingTotal} images to tag");
                    
                    const int batchSize = 1000;
                    int totalQueued = 0;
                    while (!_taggingCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingTagging(batchSize);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _taggingQueue.Writer.WriteAsync(imageId, _taggingCts.Token);
                            totalQueued++;
                        }
                        
                        if (batch.Count < batchSize) break;
                    }
                    
                    _taggingQueue.Writer.Complete();
                    Logger.Log($"Tagging queue populated with {totalQueued} images (expected {_taggingTotal})");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Queue population error: {ex.Message}");
                }
            });
            
            // Start one orchestrator per GPU device
            _taggingOrchestrators = new List<Task>();
            
            for (int i = 0; i < deviceIds.Count; i++)
            {
                int gpuId = deviceIds[i];
                int orchestratorWorkers = workerDistribution[i];
                
                Logger.Log($"GPU {gpuId}: Assigned {orchestratorWorkers} workers ({vramRatios[i]}GB VRAM)");
                
                var orchestratorTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunTaggingGpuOrchestrator(gpuId, orchestratorWorkers, _taggingCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GPU {gpuId} orchestrator error: {ex.Message}\n{ex.StackTrace}");
                    }
                });
                
                _taggingOrchestrators.Add(orchestratorTask);
            }
            
            Logger.Log($"Started {deviceIds.Count} GPU orchestrators with {maxWorkers} total workers distributed by VRAM: {string.Join(", ", deviceIds.Zip(workerDistribution, (gpu, workers) => $"GPU{gpu}={workers}"))}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start tagging: {ex.Message}\n{ex.StackTrace}");
            _isTaggingRunning = false;
        }
    }

    /// <summary>
    /// Start background captioning with worker pool - each worker gets exclusive JoyCaptionService
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
            
            var gpuDevices = ServiceLocator.Settings?.CaptioningGpuDevices ?? "0";
            var modelsPerDevice = ServiceLocator.Settings?.CaptioningModelsPerDevice ?? "3";
            
            // Parse GPU device IDs
            var deviceIds = gpuDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(d => int.TryParse(d, out var id) ? id : 0)
                                       .Distinct()
                                       .ToList();
            
            // Parse models per device counts
            var modelCounts = modelsPerDevice.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                             .Select(m => int.TryParse(m, out var count) ? count : 3)
                                             .ToList();
            
            // Pad model counts if needed (use last value for additional GPUs)
            while (modelCounts.Count < deviceIds.Count)
            {
                modelCounts.Add(modelCounts.Last());
            }
            
            // Calculate total workers based on models per device
            var totalWorkers = modelCounts.Take(deviceIds.Count).Sum();
            
            // Create unbounded channel for shared work queue
            _captioningQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,  // Multiple workers reading
                SingleWriter = true
            });
            
            // Single global orchestrator to populate queue and handle DB writes
            Task.Run(async () =>
            {
                try
                {
                    _captioningTotal = await _dataStore.CountImagesNeedingCaptioning();
                    _captioningQueueRemaining = _captioningTotal;
                    Logger.Log($"Found {_captioningTotal} images to caption");
                    
                    const int batchSize = 500;
                    while (!_captioningCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingCaptioning(batchSize);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _captioningQueue.Writer.WriteAsync(imageId, _captioningCts.Token);
                        }
                        
                        if (batch.Count < batchSize) break;
                    }
                    
                    _captioningQueue.Writer.Complete();
                    Logger.Log($"Captioning queue populated");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Queue population error: {ex.Message}");
                }
            });
            
            // Create workers, each with its own JoyCaptionService on its assigned GPU
            _captioningOrchestrators = new List<Task>();
            int workerIndex = 0;
            
            for (int i = 0; i < deviceIds.Count; i++)
            {
                int gpuId = deviceIds[i];
                int modelsOnGpu = modelCounts[i];
                
                Logger.Log($"GPU {gpuId}: Creating {modelsOnGpu} caption workers");
                
                // Create workers for this GPU
                for (int w = 0; w < modelsOnGpu; w++)
                {
                    int currentWorkerIndex = workerIndex++;
                    var workerTask = Task.Run(async () =>
                    {
                        try
                        {
                            await RunCaptioningWorker(gpuId, currentWorkerIndex, _captioningCts.Token);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"GPU {gpuId} caption worker {currentWorkerIndex} error: {ex.Message}\n{ex.StackTrace}");
                        }
                    });
                    
                    _captioningOrchestrators.Add(workerTask);
                }
            }
            
            Logger.Log($"Started {totalWorkers} caption workers: {string.Join(", ", deviceIds.Zip(modelCounts, (gpu, models) => $"GPU{gpu}={models}"))}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start captioning: {ex.Message}\n{ex.StackTrace}");
            _isCaptioningRunning = false;
        }
    }

    /// <summary>
    /// Start background embedding with multi-GPU worker pool
    /// </summary>
    public void StartEmbedding()
    {
        if (_isEmbeddingRunning)
        {
            Logger.Log("Embedding already running");
            return;
        }

        try
        {
            _embeddingCts = new CancellationTokenSource();
            _isEmbeddingRunning = true;
            _isEmbeddingPaused = false;
            _embeddingProgress = 0;
            _embeddingSkipped = 0;
            _embeddingStartTime = DateTime.Now;
            _embeddingQueueRemaining = 0;
            
            var gpuDevices = ServiceLocator.Settings?.EmbeddingGpuDevices ?? "0,1";
            var vramRatios = ServiceLocator.Settings?.EmbeddingGpuVramRatios ?? "32,12";
            var totalWorkers = ServiceLocator.Settings?.EmbeddingConcurrentWorkers ?? 9;
            
            // Parse GPU device IDs
            var deviceIds = gpuDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(d => int.TryParse(d, out var id) ? id : 0)
                                       .Distinct()
                                       .ToList();
            
            // Parse VRAM ratios (in GB)
            var vramList = vramRatios.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(v => int.TryParse(v, out var gb) ? gb : 32)
                                      .ToList();
            
            // Pad VRAM list if needed
            while (vramList.Count < deviceIds.Count)
            {
                vramList.Add(vramList.LastOrDefault());
            }
            
            // Calculate worker distribution based on VRAM proportions
            var totalVram = vramList.Take(deviceIds.Count).Sum();
            var workerDistribution = new List<int>();
            int workersAssigned = 0;
            
            for (int i = 0; i < deviceIds.Count; i++)
            {
                if (i == deviceIds.Count - 1)
                {
                    workerDistribution.Add(totalWorkers - workersAssigned);
                }
                else
                {
                    var proportion = (double)vramList[i] / totalVram;
                    var workers = (int)Math.Max(1, Math.Round(totalWorkers * proportion));
                    workerDistribution.Add(workers);
                    workersAssigned += workers;
                }
            }
            
            // Create unbounded channel for shared work queue
            _embeddingQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });
            
            // Queue population task
            Task.Run(async () =>
            {
                try
                {
                    _embeddingTotal = await _dataStore.CountImagesNeedingEmbedding();
                    _embeddingQueueRemaining = _embeddingTotal;
                    Logger.Log($"Found {_embeddingTotal} images to embed");
                    
                    const int batchSize = 1000;
                    while (!_embeddingCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingEmbedding(batchSize);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _embeddingQueue.Writer.WriteAsync(imageId, _embeddingCts.Token);
                        }
                        
                        if (batch.Count < batchSize) break;
                    }
                    
                    _embeddingQueue.Writer.Complete();
                    Logger.Log("Embedding queue populated");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Embedding queue population error: {ex.Message}");
                }
            });
            
            // Create workers
            _embeddingOrchestrators = new List<Task>();
            int workerIndex = 0;
            
            for (int i = 0; i < deviceIds.Count; i++)
            {
                int gpuId = deviceIds[i];
                int workersOnGpu = workerDistribution[i];
                
                Logger.Log($"GPU {gpuId}: Creating {workersOnGpu} embedding workers");
                
                for (int w = 0; w < workersOnGpu; w++)
                {
                    int currentWorkerIndex = workerIndex++;
                    var workerTask = Task.Run(async () =>
                    {
                        try
                        {
                            await RunEmbeddingWorker(gpuId, currentWorkerIndex, _embeddingCts.Token);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"GPU {gpuId} embedding worker {currentWorkerIndex} error: {ex.Message}\n{ex.StackTrace}");
                        }
                    });
                    
                    _embeddingOrchestrators.Add(workerTask);
                }
            }
            
            Logger.Log($"Started {totalWorkers} embedding workers: {string.Join(", ", deviceIds.Zip(workerDistribution, (gpu, workers) => $"GPU{gpu}={workers}"))}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start embedding: {ex.Message}\n{ex.StackTrace}");
            _isEmbeddingRunning = false;
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

    public void PauseEmbedding()
    {
        _isEmbeddingPaused = true;
        Logger.Log("Paused embedding");
    }

    public void ResumeEmbedding()
    {
        _isEmbeddingPaused = false;
        Logger.Log("Resumed embedding");
    }

    public void StopTagging()
    {
        Logger.Log("Stopping tagging...");
        _isTaggingRunning = false;
        _taggingCts?.Cancel();
        
        // Complete the queue to signal workers to exit (only if not already completed)
        try
        {
            _taggingQueue?.Writer.Complete();
        }
        catch (ChannelClosedException)
        {
            // Already closed, ignore
        }
        
        // Wait for orchestrators to finish (with timeout)
        try
        {
            if (_taggingOrchestrators != null)
            {
                Task.WaitAll(_taggingOrchestrators.ToArray(), TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception while stopping tagging: {ex.Message}");
        }
        
        _taggingOrchestrators?.Clear();
        
        // Release GPU resources
        try
        {
            ServiceLocator.JoyTagService?.ReleaseModel();
            ServiceLocator.WDTagService?.ReleaseModel();
            Logger.Log("Released tagging models from VRAM");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error releasing tagging models: {ex.Message}");
        }
        
        Logger.Log("Tagging stopped");
    }

    /// <summary>
    /// GPU-specific orchestrator: Manages workers on a specific GPU device with instance pools
    /// </summary>
    private async Task RunTaggingGpuOrchestrator(int gpuId, int workerCount, CancellationToken ct)
    {
        Logger.Log($"GPU {gpuId} orchestrator started with {workerCount} workers");
        
        var workers = new List<Task>();
        
        // Create instance pools: 10 workers per instance
        const int workersPerInstance = 10;
        int instanceCount = Math.Max(1, (workerCount + workersPerInstance - 1) / workersPerInstance);
        
        var joyTagInstances = new List<Tagging.Services.JoyTagService>();
        var wdTagInstances = new List<Tagging.Services.WDTagService>();
        
        try
        {
            var settings = ServiceLocator.Settings;
            
            // Create instance pools
            for (int pool = 0; pool < instanceCount; pool++)
            {
                // Initialize JoyTag instance for this pool
                if (settings?.EnableJoyTag == true && !string.IsNullOrEmpty(settings.JoyTagModelPath))
                {
                    var joyTagService = new Tagging.Services.JoyTagService(
                        settings.JoyTagModelPath,
                        settings.JoyTagTagsPath ?? string.Empty,
                        settings.JoyTagThreshold,
                        gpuId
                    );
                    await joyTagService.InitializeAsync();
                    joyTagInstances.Add(joyTagService);
                }
                
                // Initialize WDTag instance for this pool
                if (settings?.EnableWDTag == true && !string.IsNullOrEmpty(settings.WDTagModelPath))
                {
                    var wdTagService = new Tagging.Services.WDTagService(
                        settings.WDTagModelPath,
                        settings.WDTagTagsPath ?? string.Empty,
                        settings.WDTagThreshold,
                        gpuId
                    );
                    await wdTagService.InitializeAsync();
                    wdTagInstances.Add(wdTagService);
                }
                
                if (settings?.EnableJoyTag == true || settings?.EnableWDTag == true)
                {
                    Logger.Log($"GPU {gpuId}: Instance pool {pool} initialized");
                }
            }
            
            // Start workers, assigning each to an instance pool
            for (int i = 0; i < workerCount; i++)
            {
                int workerId = i;
                int poolIndex = workerId / workersPerInstance;  // Assign worker to pool
                
                var joyTag = (poolIndex < joyTagInstances.Count) ? joyTagInstances[poolIndex] : null;
                var wdTag = (poolIndex < wdTagInstances.Count) ? wdTagInstances[poolIndex] : null;
                
                var workerTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunTaggingWorker(gpuId, workerId, joyTag, wdTag, ct);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GPU {gpuId} worker {workerId} error: {ex.Message}");
                    }
                });
                workers.Add(workerTask);
            }
            
            Logger.Log($"GPU {gpuId}: Started {workerCount} workers using {instanceCount} instance pools (10 workers/instance)");
            
            // Wait for all workers to complete
            await Task.WhenAll(workers);
            Logger.Log($"GPU {gpuId} orchestrator completed");
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"GPU {gpuId} orchestrator cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GPU {gpuId} orchestrator error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            // Clean up all instance pools
            foreach (var instance in joyTagInstances)
            {
                instance?.Dispose();
            }
            foreach (var instance in wdTagInstances)
            {
                instance?.Dispose();
            }
            
            Logger.Log($"GPU {gpuId}: Released {joyTagInstances.Count + wdTagInstances.Count} model instances from VRAM");
            
            RaiseTaggingCompleted();
        }
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
                    if (_taggingQueue != null)
                        await _taggingQueue.Writer.WriteAsync(imageId, ct);
                }
                
                offset += batch.Count;
                
                if (batch.Count < batchSize)
                    break; // No more images
            }
            
            // Signal workers that no more work is coming
            if (_taggingQueue != null)
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
    /// Persistent worker: Pulls images from queue and processes them on assigned GPU
    /// Uses shared instance from its pool (5 workers per instance)
    /// </summary>
    private async Task RunTaggingWorker(int gpuId, int workerId, 
        Tagging.Services.JoyTagService? joyTagService, 
        Tagging.Services.WDTagService? wdTagService,
        CancellationToken ct)
    {
        Logger.Log($"Tagging worker {workerId} started on GPU {gpuId}");
        
        try
        {
            await foreach (var imageId in _taggingQueue?.Reader.ReadAllAsync(ct) ?? AsyncEnumerable.Empty<int>())
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
                    var remaining = Math.Max(0, Interlocked.Decrement(ref _taggingQueueRemaining));  // Never go below 0
                    
                    // Update progress immediately
                    RaiseTaggingProgress(progress, _taggingTotal);
                    
                    // Calculate ETA and update status (throttle to every 10 images to reduce UI overhead)
                    if (progress % 10 == 0 || progress <= 5)
                    {
                        string status;
                        if (progress >= 5 && remaining > 0)
                        {
                            var elapsed = DateTime.Now - _taggingStartTime;
                            var avgTimePerImage = elapsed.TotalSeconds / progress;
                            var etaSeconds = avgTimePerImage * remaining;
                            var eta = TimeSpan.FromSeconds(etaSeconds);
                            
                            status = $"Tagging: {progress}/{_taggingTotal} (Queue: {remaining}, ETA: {eta:hh\\:mm\\:ss})";
                            
                            // Log for debugging
                            Logger.Log($"TAGGING ETA: progress={progress}, remaining={remaining}, elapsed={elapsed.TotalSeconds:F2}s, avgTime={avgTimePerImage:F3}s/img, eta={eta:hh\\:mm\\:ss}");
                        }
                        else
                        {
                            status = $"Tagging: {progress}/{_taggingTotal} (Queue: {remaining})";
                            Logger.Log($"TAGGING STATUS: progress={progress}, remaining={remaining}");
                        }
                        
                        RaiseStatusChanged(status);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"GPU {gpuId} worker {workerId} error processing image {imageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"GPU {gpuId} worker {workerId} cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GPU {gpuId} worker {workerId} fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            Logger.Log($"GPU {gpuId} worker {workerId} exiting");
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
                    Logger.Log($"Saved {allTags.Count} tags to database for image {imageId}");
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
                    Logger.Log($"Saved {allTags.Count} tags to database for image {imageId}");
                }
            }
            else if (wdTagService != null)
            {
                var tags = await wdTagService.TagImageAsync(image.Path);
                if (tags != null)
                {
                    allTags = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                    await _dataStore.StoreImageTagsAsync(imageId, allTags, "wdv3large");
                    Logger.Log($"Saved {allTags.Count} tags to database for image {imageId}");
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
        
        // Complete the queue to signal workers to exit (only if not already completed)
        try
        {
            _captioningQueue?.Writer.Complete();
        }
        catch (ChannelClosedException)
        {
            // Already closed, ignore
        }
        
        // Wait for orchestrators to finish (with timeout)
        try
        {
            if (_captioningOrchestrators != null)
            {
                Task.WaitAll(_captioningOrchestrators.ToArray(), TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception while stopping captioning: {ex.Message}");
        }
        
        _captioningOrchestrators?.Clear();
        
        // Release GPU resources
        try
        {
            ServiceLocator.CaptionService?.ReleaseModel();
            Logger.Log("Released captioning models from VRAM");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error releasing captioning models: {ex.Message}");
        }
        
        Logger.Log("Captioning stopped");
    }

    public void StopEmbedding()
    {
        Logger.Log("Stopping embedding...");
        _isEmbeddingRunning = false;
        _embeddingCts?.Cancel();
        
        // Complete the queue to signal workers to exit
        try
        {
            _embeddingQueue?.Writer.Complete();
        }
        catch (ChannelClosedException)
        {
            // Already closed, ignore
        }
        
        // Wait for orchestrators to finish (with timeout)
        try
        {
            if (_embeddingOrchestrators != null)
            {
                Task.WaitAll(_embeddingOrchestrators.ToArray(), TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception while stopping embedding: {ex.Message}");
        }
        
        _embeddingOrchestrators?.Clear();
        
        // Release GPU resources - embedding models are disposed in workers
        Logger.Log("Embedding stopped");
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
                    if (_captioningQueue != null)
                        await _captioningQueue.Writer.WriteAsync(imageId, ct);
                }
                
                offset += batch.Count;
                
                if (batch.Count < batchSize)
                    break; // No more images
            }
            
            // Signal workers that no more work is coming
            if (_captioningQueue != null)
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
    /// <summary>
    /// Caption worker: Creates its own JoyCaptionService for exclusive use (no concurrency)
    /// Pulls images from shared queue and processes them sequentially
    /// </summary>
    private async Task RunCaptioningWorker(int gpuId, int workerId, CancellationToken ct)
    {
        Logger.Log($"Caption worker {workerId} started on GPU {gpuId}");
        
        Captioning.Services.JoyCaptionService? captionService = null;
        
        try
        {
            var settings = ServiceLocator.Settings;
            
            if (string.IsNullOrEmpty(settings?.JoyCaptionModelPath) || string.IsNullOrEmpty(settings?.JoyCaptionMMProjPath))
            {
                Logger.Log($"GPU {gpuId} caption worker {workerId}: JoyCaption paths not configured");
                return;
            }
            
            // Each worker creates its own service instance (exclusive access, no concurrency conflicts)
            captionService = new Captioning.Services.JoyCaptionService(
                settings.JoyCaptionModelPath,
                settings.JoyCaptionMMProjPath,
                gpuId  // GPU device ID
            );
            await captionService.InitializeAsync();
            Logger.Log($"GPU {gpuId} caption worker {workerId}: Service initialized");
            
            await foreach (var imageId in _captioningQueue?.Reader.ReadAllAsync(ct) ?? AsyncEnumerable.Empty<int>())
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
                    await ProcessImageCaptioning(imageId, captionService);
                    
                    var progress = Interlocked.Increment(ref _captioningProgress);
                    var remaining = Interlocked.Decrement(ref _captioningQueueRemaining);
                    
                    // Update progress immediately
                    RaiseCaptioningProgress(progress, _captioningTotal);
                    
                    // Calculate ETA and update status
                    string status;
                    if (progress >= 3 && remaining > 0)
                    {
                        var elapsed = DateTime.Now - _captioningStartTime;
                        var avgTimePerImage = elapsed.TotalSeconds / progress;
                        var etaSeconds = avgTimePerImage * remaining;
                        var eta = TimeSpan.FromSeconds(etaSeconds);
                        
                        status = $"Captioning: {progress}/{_captioningTotal} (Queue: {remaining}, ETA: {eta:hh\\:mm\\:ss})";
                        
                        // Log for debugging
                        Logger.Log($"CAPTIONING ETA: progress={progress}, remaining={remaining}, elapsed={elapsed.TotalSeconds:F2}s, avgTime={avgTimePerImage:F3}s/img, eta={eta:hh\\:mm\\:ss}");
                    }
                    else
                    {
                        status = $"Captioning: {progress}/{_captioningTotal} (Queue: {remaining})";
                        Logger.Log($"CAPTIONING STATUS: progress={progress}, remaining={remaining}");
                    }
                    
                    RaiseStatusChanged(status);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GPU {gpuId} caption worker {workerId} error processing image {imageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"GPU {gpuId} caption worker {workerId} cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GPU {gpuId} caption worker {workerId} fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            captionService?.Dispose();
            Logger.Log($"GPU {gpuId} caption worker {workerId} exiting");
            RaiseCaptioningCompleted();
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

            var captionSource = ServiceLocator.Settings?.CaptionProvider == CaptionProviderType.LocalJoyCaption
                ? "joycaption" : "openai";
            
            await _dataStore.StoreCaptionAsync(imageId, finalCaption!, captionSource, 
                result.PromptUsed, result.TokenCount, (float)result.GenerationTimeMs);

            Logger.Log($"Saved caption to database for image {imageId}");

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

    /// <summary>
    /// Embedding worker: Creates its own EmbeddingService for exclusive use
    /// </summary>
    private async Task RunEmbeddingWorker(int gpuId, int workerId, CancellationToken ct)
    {
        Logger.Log($"Embedding worker {workerId} started on GPU {gpuId}");
        
        Embeddings.EmbeddingService? embeddingService = null;
        
        try
        {
            // Create embedding service for this worker
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var config = Embeddings.EmbeddingConfig.CreateDefault(baseDir);
            
            // Override GPU assignment - all models on this worker's GPU
            config.BgeGpuDevice = gpuId;
            config.ClipVisionGpuDevice = gpuId;
            config.ClipLGpuDevice = gpuId;
            config.ClipGGpuDevice = gpuId;
            
            embeddingService = Embeddings.EmbeddingService.FromConfig(config);
            Logger.Log($"GPU {gpuId} embedding worker {workerId}: Service initialized");
            
            // Process images from queue
            await foreach (var imageId in _embeddingQueue!.Reader.ReadAllAsync(ct))
            {
                // Check if paused
                while (_isEmbeddingPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                
                if (ct.IsCancellationRequested)
                    break;
                
                try
                {
                    await ProcessImageEmbedding(imageId, embeddingService);
                    
                    var progress = Interlocked.Increment(ref _embeddingProgress);
                    var remaining = Interlocked.Decrement(ref _embeddingQueueRemaining);
                    
                    RaiseEmbeddingProgress(progress, _embeddingTotal);
                    
                    // Calculate ETA and update status
                    string status;
                    if (progress >= 3 && remaining > 0)
                    {
                        var elapsed = DateTime.Now - _embeddingStartTime;
                        var avgTimePerImage = elapsed.TotalSeconds / progress;
                        var etaSeconds = avgTimePerImage * remaining;
                        var eta = TimeSpan.FromSeconds(etaSeconds);
                        var imgPerSec = progress / elapsed.TotalSeconds;
                        
                        status = $"Embedding: {progress}/{_embeddingTotal} | {imgPerSec:F1} img/s | ETA: {eta:hh\\:mm\\:ss}";
                    }
                    else
                    {
                        status = $"Embedding: {progress}/{_embeddingTotal} (Queue: {remaining})";
                    }
                    
                    RaiseStatusChanged(status);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GPU {gpuId} embedding worker {workerId} error processing image {imageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"GPU {gpuId} embedding worker {workerId} cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GPU {gpuId} embedding worker {workerId} fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            embeddingService?.Dispose();
            Logger.Log($"GPU {gpuId} embedding worker {workerId} exiting");
            RaiseEmbeddingCompleted();
        }
    }

    private async Task ProcessImageEmbedding(int imageId, Embeddings.EmbeddingService embeddingService)
    {
        try
        {
            var image = _dataStore.GetImage(imageId);
            if (image == null)
            {
                Logger.Log($"Image {imageId} not found, skipping embedding");
                Interlocked.Increment(ref _embeddingSkipped);
                await _dataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
                return;
            }

            if (!System.IO.File.Exists(image.Path))
            {
                Logger.Log($"Image file not found: {image.Path}, skipping");
                Interlocked.Increment(ref _embeddingSkipped);
                await _dataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
                return;
            }

            // Generate embeddings
            float[]? promptEmb = null;
            float[]? imageEmb = null;
            
            // Generate prompt embedding (BGE)
            if (!string.IsNullOrEmpty(image.Prompt))
            {
                var (bge, _, _) = await embeddingService.GenerateTextEmbeddingsAsync(image.Prompt);
                promptEmb = bge;
            }
            
            // Generate image embedding (CLIP-ViT-H)
            imageEmb = await embeddingService.GenerateImageEmbeddingAsync(image.Path);
            
            // Store embeddings in database
            await _dataStore.StoreImageEmbeddingsAsync(imageId, promptEmb, imageEmb);
            
            // Mark as processed
            await _dataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
            
            Logger.Log($"Embedded image {imageId}: prompt={promptEmb != null}, image={imageEmb != null}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error embedding image {imageId}: {ex.Message}");
            Interlocked.Increment(ref _embeddingSkipped);
            // Don't mark as processed on error - allow retry
        }
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
