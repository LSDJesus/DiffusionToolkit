using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Diffusion.Database.PostgreSQL;
using Diffusion.Embeddings;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Background worker service for user-controlled embedding generation.
/// Manages state machine: Idle → Running → Paused → Stopped
/// Pause: stops workers, keeps models in VRAM
/// Stop: stops workers, unloads models from VRAM
/// </summary>
public class EmbeddingWorkerService : IDisposable
{
    private readonly PostgreSQLDataStore _dataStore;
    private readonly EmbeddingService? _embeddingService;
    private readonly EmbeddingProcessingService? _processingService;
    
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    
    // State
    private WorkerStatus _status = WorkerStatus.Stopped;
    private bool _modelsLoaded = false;
    private int _batchSize = 32;
    
    // Statistics (updated in real-time)
    public int TotalProcessed { get; private set; }
    public int TotalFailed { get; private set; }
    public int QueueSize { get; private set; }
    public int HighPriorityCount { get; private set; }
    public double CacheHitRate { get; private set; }
    public DateTime? LastProcessedAt { get; private set; }
    public string? LastError { get; private set; }
    
    // Events for UI updates
    public event EventHandler<WorkerStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<WorkerProgressEventArgs>? ProgressUpdated;
    public event EventHandler<string>? ErrorOccurred;
    
    public WorkerStatus Status => _status;
    public bool ModelsLoaded => _modelsLoaded;
    public int BatchSize
    {
        get => _batchSize;
        set => _batchSize = Math.Clamp(value, 8, 128);
    }
    
    public EmbeddingWorkerService(
        PostgreSQLDataStore dataStore,
        EmbeddingService? embeddingService = null)
    {
        _dataStore = dataStore;
        _embeddingService = embeddingService;
        
        if (embeddingService != null)
        {
            _processingService = new EmbeddingProcessingService(embeddingService, dataStore);
        }
    }
    
    /// <summary>
    /// Start embedding worker (loads models if needed, begins processing)
    /// </summary>
    public async Task StartAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_status == WorkerStatus.Running)
                return; // Already running
            
            // Load models if not already loaded
            if (!_modelsLoaded)
            {
                await LoadModelsAsync();
            }
            
            // Start worker task
            _cancellationTokenSource = new CancellationTokenSource();
            _workerTask = Task.Run(() => ProcessQueueAsync(_cancellationTokenSource.Token));
            
            _status = WorkerStatus.Running;
            await _dataStore.UpdateWorkerStateAsync("running", _modelsLoaded);
            
            OnStatusChanged(new WorkerStatusChangedEventArgs
            {
                NewStatus = _status,
                ModelsLoaded = _modelsLoaded
            });
        }
        finally
        {
            _stateLock.Release();
        }
    }
    
    /// <summary>
    /// Pause embedding worker (stops processing, keeps models in VRAM)
    /// </summary>
    public async Task PauseAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_status != WorkerStatus.Running)
                return; // Not running
            
            // Cancel worker task
            _cancellationTokenSource?.Cancel();
            if (_workerTask != null)
            {
                try
                {
                    await _workerTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
            
            _status = WorkerStatus.Paused;
            await _dataStore.UpdateWorkerStateAsync("paused", _modelsLoaded);
            
            OnStatusChanged(new WorkerStatusChangedEventArgs
            {
                NewStatus = _status,
                ModelsLoaded = _modelsLoaded
            });
        }
        finally
        {
            _stateLock.Release();
        }
    }
    
    /// <summary>
    /// Stop embedding worker (stops processing, unloads models from VRAM)
    /// Queue is NOT cleared
    /// </summary>
    public async Task StopAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (_status == WorkerStatus.Stopped)
                return; // Already stopped
            
            // Cancel worker task
            _cancellationTokenSource?.Cancel();
            if (_workerTask != null)
            {
                try
                {
                    await _workerTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
            
            // Unload models from VRAM
            if (_modelsLoaded)
            {
                UnloadModels();
            }
            
            _status = WorkerStatus.Stopped;
            await _dataStore.UpdateWorkerStateAsync("stopped", _modelsLoaded);
            
            OnStatusChanged(new WorkerStatusChangedEventArgs
            {
                NewStatus = _status,
                ModelsLoaded = _modelsLoaded
            });
        }
        finally
        {
            _stateLock.Release();
        }
    }
    
    /// <summary>
    /// Clear the entire queue (separate from Stop)
    /// </summary>
    public async Task ClearQueueAsync()
    {
        await _dataStore.ClearAllQueueItemsAsync();
        QueueSize = 0;
        HighPriorityCount = 0;
        
        OnProgressUpdated(new WorkerProgressEventArgs
        {
            QueueSize = 0,
            HighPriorityCount = 0
        });
    }
    
    /// <summary>
    /// Restore state from database (call on app startup)
    /// </summary>
    public async Task RestoreStateAsync()
    {
        var state = await _dataStore.GetWorkerStateAsync();
        
        _status = state.Status switch
        {
            "running" => WorkerStatus.Paused, // Don't auto-start, but mark as paused
            "paused" => WorkerStatus.Paused,
            _ => WorkerStatus.Stopped
        };
        
        _modelsLoaded = state.ModelsLoaded && _status != WorkerStatus.Stopped;
        TotalProcessed = state.TotalProcessed;
        TotalFailed = state.TotalFailed;
        LastError = state.LastError;
        
        // Update queue statistics
        await UpdateStatisticsAsync();
    }
    
    private async Task LoadModelsAsync()
    {
        if (_embeddingService == null)
        {
            throw new InvalidOperationException("EmbeddingService not initialized");
        }
        
        // Models are automatically loaded on first use by EmbeddingService
        // This just sets the flag
        _modelsLoaded = true;
        await _dataStore.UpdateWorkerStateAsync(_status.ToString().ToLowerInvariant(), true);
        
        OnStatusChanged(new WorkerStatusChangedEventArgs
        {
            NewStatus = _status,
            ModelsLoaded = true
        });
    }
    
    private void UnloadModels()
    {
        // Dispose EmbeddingService to free VRAM
        _processingService?.Dispose();
        _embeddingService?.Dispose();
        
        // Force garbage collection to free VRAM immediately
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        _modelsLoaded = false;
    }
    
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Get next batch from queue
                var batch = await _dataStore.GetNextEmbeddingBatchAsync(_batchSize, cancellationToken);
                
                if (batch.Count == 0)
                {
                    // Queue empty, wait and check again
                    await Task.Delay(1000, cancellationToken);
                    await UpdateStatisticsAsync();
                    continue;
                }
                
                // Process batch
                var processed = 0;
                var failed = 0;
                var stopwatch = Stopwatch.StartNew();
                
                foreach (var item in batch)
                {
                    try
                    {
                        // Get image data
                        var image = await _dataStore.GetImageByIdAsync(item.ImageId);
                        if (image == null)
                        {
                            await _dataStore.FailEmbeddingQueueItemAsync(item.Id, "Image not found");
                            failed++;
                            continue;
                        }
                        
                        // Generate embeddings (uses EmbeddingProcessingService)
                        if (_processingService != null)
                        {
                            await _processingService.QueueImageAsync(new ImageEmbeddingRequest
                            {
                                ImageId = item.ImageId,
                                FilePath = image.Path,
                                Prompt = image.Prompt,
                                NegativePrompt = image.NegativePrompt
                            }, cancellationToken);
                        }
                        
                        // Mark as completed
                        await _dataStore.CompleteEmbeddingQueueItemAsync(item.Id, cancellationToken);
                        processed++;
                        LastProcessedAt = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        await _dataStore.FailEmbeddingQueueItemAsync(item.Id, ex.Message);
                        failed++;
                        LastError = ex.Message;
                        OnErrorOccurred(ex.Message);
                    }
                }
                
                stopwatch.Stop();
                
                // Update counters
                TotalProcessed += processed;
                TotalFailed += failed;
                await _dataStore.IncrementWorkerCountersAsync(processed, failed, failed > 0 ? LastError : null);
                
                // Update statistics
                await UpdateStatisticsAsync();
                
                // Calculate speed
                var imagesPerSecond = processed / stopwatch.Elapsed.TotalSeconds;
                
                OnProgressUpdated(new WorkerProgressEventArgs
                {
                    Processed = processed,
                    Failed = failed,
                    TotalProcessed = TotalProcessed,
                    TotalFailed = TotalFailed,
                    QueueSize = QueueSize,
                    HighPriorityCount = HighPriorityCount,
                    ImagesPerSecond = imagesPerSecond,
                    CacheHitRate = _processingService?.GetStatistics().CacheHitRate ?? 0.0
                });
            }
            catch (OperationCanceledException)
            {
                // Worker paused or stopped
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                OnErrorOccurred(ex.Message);
                await Task.Delay(5000, cancellationToken); // Wait before retry
            }
        }
    }
    
    private async Task UpdateStatisticsAsync()
    {
        var stats = await _dataStore.GetQueueStatisticsAsync();
        QueueSize = stats.TotalPending;
        HighPriorityCount = stats.HighPriorityCount;
        
        if (_processingService != null)
        {
            var embStats = _processingService.GetStatistics();
            CacheHitRate = embStats.CacheHitRate;
        }
    }
    
    private void OnStatusChanged(WorkerStatusChangedEventArgs e) =>
        StatusChanged?.Invoke(this, e);
    
    private void OnProgressUpdated(WorkerProgressEventArgs e) =>
        ProgressUpdated?.Invoke(this, e);
    
    private void OnErrorOccurred(string error) =>
        ErrorOccurred?.Invoke(this, error);
    
    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _processingService?.Dispose();
        _stateLock?.Dispose();
    }
}

public enum WorkerStatus
{
    Stopped,  // Models unloaded, not processing
    Running,  // Models loaded, processing queue
    Paused    // Models loaded, not processing
}

public class WorkerStatusChangedEventArgs : EventArgs
{
    public WorkerStatus NewStatus { get; set; }
    public bool ModelsLoaded { get; set; }
}

public class WorkerProgressEventArgs : EventArgs
{
    public int Processed { get; set; }
    public int Failed { get; set; }
    public int TotalProcessed { get; set; }
    public int TotalFailed { get; set; }
    public int QueueSize { get; set; }
    public int HighPriorityCount { get; set; }
    public double ImagesPerSecond { get; set; }
    public double CacheHitRate { get; set; }
}
