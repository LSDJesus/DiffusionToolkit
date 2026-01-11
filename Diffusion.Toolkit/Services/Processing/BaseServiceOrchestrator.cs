using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Base class for service orchestrators with shared functionality:
/// - Queue management with cursor-based pagination
/// - Worker spawning and lifecycle
/// - Progress tracking and ETA calculation
/// - Pause/Resume functionality
/// - Event raising
/// </summary>
public abstract class BaseServiceOrchestrator : IServiceOrchestrator
{
    protected readonly PostgreSQLDataStore DataStore;
    
    // Queue management
    protected Channel<ProcessingJob>? JobQueue;
    protected CancellationTokenSource? Cts;
    protected List<Task>? WorkerTasks;
    protected List<IProcessingWorker>? Workers;
    
    // State
    private bool _isRunning;
    private bool _isPaused;
    private int _queueRemaining;
    private int _progress;
    private int _total;
    private int _skipped;
    private DateTime _startTime;
    
    // Thread-safe state access
    public bool IsRunning => _isRunning;
    public bool IsPaused => _isPaused;
    public int QueueRemaining => _queueRemaining;
    public int Progress => _progress;
    public int Total => _total;
    public int Skipped => _skipped;
    
    // Abstract properties for subclasses
    public abstract ProcessingType ProcessingType { get; }
    public abstract string Name { get; }
    
    // Events
    public event EventHandler<ProcessingProgressEventArgs>? ProgressChanged;
    public event EventHandler<ProcessingStatusEventArgs>? StatusChanged;
    public event EventHandler? Completed;
    
    protected BaseServiceOrchestrator(PostgreSQLDataStore dataStore)
    {
        DataStore = dataStore;
    }
    
    #region Abstract Methods - Subclasses must implement
    
    /// <summary>
    /// Count total items needing processing
    /// </summary>
    protected abstract Task<int> CountItemsNeedingProcessingAsync();
    
    /// <summary>
    /// Get batch of items needing processing using cursor pagination
    /// </summary>
    /// <param name="batchSize">Number of items to fetch</param>
    /// <param name="lastId">Last ID from previous batch (0 for first batch)</param>
    protected abstract Task<List<int>> GetItemsNeedingProcessingAsync(int batchSize, int lastId);
    
    /// <summary>
    /// Get the processing job for an image ID (load path and any additional data needed)
    /// </summary>
    protected abstract Task<ProcessingJob?> GetJobForImageAsync(int imageId);
    
    /// <summary>
    /// Write the processing result to the database and clear the needs_* flag
    /// </summary>
    protected abstract Task WriteResultAsync(ProcessingResult result);
    
    /// <summary>
    /// Create a worker for the specified GPU
    /// </summary>
    protected abstract IProcessingWorker CreateWorker(int gpuId, int workerId);
    
    #endregion
    
    #region Public Methods
    
    public virtual async Task StartAsync(ServiceAllocation allocation, CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            Logger.Log($"{Name} already running");
            return;
        }
        
        try
        {
            _isRunning = true;
            _isPaused = false;
            _progress = 0;
            _skipped = 0;
            _startTime = DateTime.Now;
            
            Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            
            // Count total items
            _total = await CountItemsNeedingProcessingAsync();
            _queueRemaining = _total;
            
            if (_total == 0)
            {
                Logger.Log($"{Name}: No items to process");
                _isRunning = false;
                RaiseCompleted();
                return;
            }
            
            Logger.Log($"{Name}: Found {_total} items to process");
            RaiseStatusChanged($"{Name}: Starting with {_total} items");
            
            // Create job queue
            JobQueue = Channel.CreateBounded<ProcessingJob>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });
            
            // Start queue population task
            var populateTask = Task.Run(() => PopulateQueueAsync(Cts.Token), Cts.Token);
            
            // Start workers based on allocation
            Workers = new List<IProcessingWorker>();
            WorkerTasks = new List<Task>();
            
            int workerId = 0;
            foreach (var gpuAlloc in allocation.GpuAllocations)
            {
                for (int i = 0; i < gpuAlloc.ModelCount; i++)
                {
                    var worker = CreateWorker(gpuAlloc.GpuId, workerId++);
                    Workers.Add(worker);
                    
                    var workerTask = Task.Run(async () =>
                    {
                        await RunWorkerLoopAsync(worker, Cts.Token);
                    }, Cts.Token);
                    
                    WorkerTasks.Add(workerTask);
                }
            }
            
            Logger.Log($"{Name}: Started {Workers.Count} workers across {allocation.GpuAllocations.Length} GPUs");
            
            // Wait for all workers to complete
            await Task.WhenAll(WorkerTasks);
            
            // Ensure population task also completes
            await populateTask;
            
            Logger.Log($"{Name}: All workers completed. Processed {_progress}, Skipped {_skipped}");
            _isRunning = false;
            RaiseCompleted();
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"{Name}: Cancelled");
            _isRunning = false;
        }
        catch (Exception ex)
        {
            Logger.Log($"{Name}: Error - {ex.Message}\n{ex.StackTrace}");
            _isRunning = false;
            throw;
        }
    }
    
    public virtual async Task StopAsync()
    {
        Logger.Log($"{Name}: Stopping...");
        _isRunning = false;
        
        Cts?.Cancel();
        
        // Complete the queue to signal workers to exit
        try
        {
            JobQueue?.Writer.Complete();
        }
        catch (ChannelClosedException)
        {
            // Already closed
        }
        
        // Wait for workers to finish (with timeout)
        if (WorkerTasks != null && WorkerTasks.Count > 0)
        {
            await Task.WhenAny(
                Task.WhenAll(WorkerTasks),
                Task.Delay(TimeSpan.FromSeconds(10))
            );
        }
        
        // Shutdown workers
        if (Workers != null)
        {
            foreach (var worker in Workers)
            {
                try
                {
                    await worker.ShutdownAsync();
                    worker.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log($"{Name}: Error shutting down worker {worker.WorkerId}: {ex.Message}");
                }
            }
        }
        
        Cts?.Dispose();
        Cts = null;
        
        RaiseStatusChanged($"{Name}: Stopped");
        Logger.Log($"{Name}: Stopped");
    }
    
    public void Pause()
    {
        _isPaused = true;
        RaiseStatusChanged($"{Name}: Paused");
        Logger.Log($"{Name}: Paused");
    }
    
    public void Resume()
    {
        _isPaused = false;
        RaiseStatusChanged($"{Name}: Resumed");
        Logger.Log($"{Name}: Resumed");
    }
    
    #endregion
    
    #region Queue Population
    
    /// <summary>
    /// Populate the job queue using cursor-based pagination
    /// </summary>
    protected virtual async Task PopulateQueueAsync(CancellationToken ct)
    {
        const int batchSize = 1000;
        int lastId = 0;
        int totalQueued = 0;
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await GetItemsNeedingProcessingAsync(batchSize, lastId);
                
                if (batch.Count == 0)
                    break;
                
                foreach (var imageId in batch)
                {
                    if (ct.IsCancellationRequested)
                        break;
                    
                    var job = await GetJobForImageAsync(imageId);
                    if (job != null)
                    {
                        await JobQueue!.Writer.WriteAsync(job, ct);
                        totalQueued++;
                    }
                    else
                    {
                        // Couldn't create job (missing file, etc.) - mark as skipped
                        Interlocked.Increment(ref _skipped);
                        Interlocked.Decrement(ref _queueRemaining);
                    }
                }
                
                // Update cursor for next batch
                lastId = batch[^1];
                
                // If we got less than batch size, we're done
                if (batch.Count < batchSize)
                    break;
            }
            
            // Signal no more jobs
            JobQueue?.Writer.Complete();
            Logger.Log($"{Name}: Queue populated with {totalQueued} jobs");
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"{Name}: Queue population cancelled");
            try { JobQueue?.Writer.Complete(); } catch { }
        }
        catch (Exception ex)
        {
            Logger.Log($"{Name}: Queue population error - {ex.Message}");
            try { JobQueue?.Writer.Complete(ex); } catch { }
        }
    }
    
    #endregion
    
    #region Worker Loop
    
    /// <summary>
    /// Main worker loop - initialize, process jobs, shutdown
    /// </summary>
    protected virtual async Task RunWorkerLoopAsync(IProcessingWorker worker, CancellationToken ct)
    {
        try
        {
            // Initialize worker (load model)
            await worker.InitializeAsync(ct);
            Logger.Log($"{Name}: Worker {worker.WorkerId} initialized on GPU {worker.GpuId}");
            
            // Process jobs from queue
            await foreach (var job in JobQueue!.Reader.ReadAllAsync(ct))
            {
                // Wait if paused
                while (_isPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                
                if (ct.IsCancellationRequested)
                    break;
                
                try
                {
                    // Process the image
                    var result = await worker.ProcessAsync(job, ct);
                    
                    // Write result to database (orchestrator's responsibility)
                    await WriteResultAsync(result);
                    
                    // Update progress
                    var progress = Interlocked.Increment(ref _progress);
                    var remaining = Interlocked.Decrement(ref _queueRemaining);
                    
                    if (!result.Success)
                    {
                        Interlocked.Increment(ref _skipped);
                    }
                    
                    // Raise progress event (throttled)
                    if (progress % 10 == 0 || progress <= 5)
                    {
                        RaiseProgress(progress, remaining);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"{Name}: Worker {worker.WorkerId} error processing image {job.ImageId}: {ex.Message}");
                    Interlocked.Increment(ref _skipped);
                    Interlocked.Decrement(ref _queueRemaining);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"{Name}: Worker {worker.WorkerId} cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"{Name}: Worker {worker.WorkerId} fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            try
            {
                await worker.ShutdownAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"{Name}: Worker {worker.WorkerId} shutdown error: {ex.Message}");
            }
            Logger.Log($"{Name}: Worker {worker.WorkerId} exited");
        }
    }
    
    #endregion
    
    #region Event Raising
    
    protected void RaiseProgress(int current, int remaining)
    {
        var elapsed = DateTime.Now - _startTime;
        TimeSpan? eta = null;
        
        if (current >= 5 && remaining > 0)
        {
            var avgTimePerItem = elapsed.TotalSeconds / current;
            eta = TimeSpan.FromSeconds(avgTimePerItem * remaining);
        }
        
        var args = new ProcessingProgressEventArgs
        {
            ProcessingType = ProcessingType,
            Current = current,
            Total = _total,
            Remaining = remaining,
            Skipped = _skipped,
            EstimatedTimeRemaining = eta
        };
        
        try
        {
            ProgressChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            Logger.Log($"{Name}: Error raising ProgressChanged: {ex.Message}");
        }
    }
    
    protected void RaiseStatusChanged(string status)
    {
        var args = new ProcessingStatusEventArgs
        {
            ProcessingType = ProcessingType,
            Status = status,
            IsRunning = _isRunning,
            IsPaused = _isPaused
        };
        
        try
        {
            StatusChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            Logger.Log($"{Name}: Error raising StatusChanged: {ex.Message}");
        }
    }
    
    protected void RaiseCompleted()
    {
        try
        {
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Log($"{Name}: Error raising Completed: {ex.Message}");
        }
    }
    
    #endregion
    
    #region IDisposable
    
    private bool _disposed;
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            StopAsync().Wait(TimeSpan.FromSeconds(5));
            Cts?.Dispose();
            
            if (Workers != null)
            {
                foreach (var worker in Workers)
                {
                    worker.Dispose();
                }
            }
        }
        
        _disposed = true;
        Logger.Log($"{Name}: Disposed");
    }
    
    #endregion
}
