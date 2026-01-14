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
/// VRAM allocation plan for unified processing
/// </summary>
public class VramAllocationPlan
{
    public double TotalVramGb { get; set; }
    public double AvailableVramGb { get; set; }
    public bool EnableTagging { get; set; }
    public bool EnableCaptioning { get; set; }
    public bool EnableEmbedding { get; set; }
    public bool EnableFaceDetection { get; set; }
}

/// <summary>
/// Background service for tagging and captioning images using persistent worker pools
/// Integrates with GpuResourceOrchestrator for dynamic resource allocation
/// </summary>
public class BackgroundTaggingService : IDisposable
{
    private readonly PostgreSQLDataStore _dataStore;
    
    // Model allocations from GPU orchestrator
    private readonly List<ModelAllocation> _activeAllocations = new();
    
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
    private List<EmbeddingPooledOrchestrator>? _embeddingPooledOrchestrators;  // Pooled orchestrators per GPU
    
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

    private void RaiseQueueCountsChanged()
    {
        try
        {
            var hasSubscribers = QueueCountsChanged != null;
            Logger.Log($"RaiseQueueCountsChanged: hasSubscribers={hasSubscribers}, T={_taggingQueueRemaining}, C={_captioningQueueRemaining}, E={_embeddingQueueRemaining}");
            
            if (!hasSubscribers)
            {
                return;
            }
            
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    Logger.Log($"RaiseQueueCountsChanged: Invoking on dispatcher");
                    QueueCountsChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error in QueueCountsChanged event: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Error raising QueueCountsChanged: {ex.Message}");
        }
    }

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
        
        // Subscribe to orchestrator events for dynamic reallocation
        if (ServiceLocator.GpuOrchestrator != null)
        {
            ServiceLocator.GpuOrchestrator.ReallocationOpportunity += OnReallocationOpportunity;
        }
    }
    
    /// <summary>
    /// Called when the GPU orchestrator detects an opportunity to load more models
    /// </summary>
    private void OnReallocationOpportunity(ProcessPriority priority)
    {
        Logger.Log($"Reallocation opportunity for {priority}");
        
        // If captioning-only mode and captioning is running, try to load more models
        if (priority == ProcessPriority.Captioning && _isCaptioningRunning)
        {
            // TODO: Dynamically add more captioning workers
            Logger.Log("TODO: Dynamic captioning worker addition");
        }
    }
    
    /// <summary>
    /// Get allocation plan from GPU orchestrator
    /// </summary>
    private List<(int GpuId, int WorkerCount)> GetAllocationPlan(ProcessPriority processType)
    {
        var orchestrator = ServiceLocator.GpuOrchestrator;
        if (orchestrator == null)
        {
            // Fallback to default single GPU
            Logger.Log($"GPU Orchestrator not available, using fallback allocation for {processType}");
            var workersPerModel = ModelVramRequirements.GetWorkersPerModel(processType);
            return new List<(int, int)> { (0, workersPerModel) };
        }
        
        // Get optimal plan from orchestrator
        var fullPlan = orchestrator.GetOptimalAllocationPlan();
        
        // Log full plan for debugging
        Logger.Log($"GetAllocationPlan({processType}): Full plan has {fullPlan.Count} entries");
        foreach (var entry in fullPlan)
        {
            Logger.Log($"  Full plan entry: {entry.Priority} -> GPU{entry.GpuId}, {entry.WorkerCount} workers");
        }
        
        // Filter to this process type and aggregate workers per GPU
        var filteredPlan = fullPlan
            .Where(p => p.Priority == processType)
            .GroupBy(p => p.GpuId)
            .Select(g => (GpuId: g.Key, WorkerCount: g.Sum(x => x.WorkerCount)))
            .ToList();
        
        Logger.Log($"GetAllocationPlan({processType}): Filtered plan has {filteredPlan.Count} GPU entries");
        foreach (var entry in filteredPlan)
        {
            Logger.Log($"  Filtered: GPU{entry.GpuId} = {entry.WorkerCount} workers");
        }
        
        if (filteredPlan.Count == 0)
        {
            // No allocation available - try to get at least one
            Logger.Log($"GetAllocationPlan({processType}): No allocation found, using fallback");
            var workersPerModel = ModelVramRequirements.GetWorkersPerModel(processType);
            return new List<(int, int)> { (0, workersPerModel) };
        }
        
        return filteredPlan;
    }
    
    /// <summary>
    /// Update orchestrator with queue status
    /// </summary>
    private void UpdateOrchestratorStatus(ProcessPriority processType, int total, int processed, int activeWorkers, bool isRunning)
    {
        ServiceLocator.GpuOrchestrator?.UpdateQueueStatus(
            processType, 
            total: total, 
            processed: processed, 
            activeWorkers: activeWorkers, 
            isRunning: isRunning);
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
    /// Start selected processors with smart VRAM allocation.
    /// This is the unified entry point that coordinates all enabled processes.
    /// </summary>
    /// <param name="enableTagging">Enable tagging processor</param>
    /// <param name="enableCaptioning">Enable captioning processor</param>
    /// <param name="enableEmbedding">Enable embedding processor</param>
    /// <param name="enableFaceDetection">Enable face detection processor</param>
    public async Task StartSelectedProcessorsAsync(bool enableTagging, bool enableCaptioning, bool enableEmbedding, bool enableFaceDetection)
    {
        var enabledProcesses = new List<string>();
        if (enableTagging) enabledProcesses.Add("Tagging");
        if (enableCaptioning) enabledProcesses.Add("Captioning");
        if (enableEmbedding) enabledProcesses.Add("Embedding");
        if (enableFaceDetection) enabledProcesses.Add("FaceDetection");
        
        if (enabledProcesses.Count == 0)
        {
            Logger.Log("No processes selected for unified start");
            return;
        }
        
        Logger.Log($"Starting unified processing with: {string.Join(", ", enabledProcesses)}");
        
        // Pre-register ALL enabled processes with the orchestrator BEFORE starting any
        // This ensures the orchestrator knows about all processes when calculating allocations
        var orchestrator = ServiceLocator.GpuOrchestrator;
        if (orchestrator != null)
        {
            // Get pending counts for all processes
            var (taggingCount, captioningCount, embeddingCount, faceCount) = await _dataStore.GetPendingCountsAsync();
            
            // Pre-register enabled processes with their queue counts
            if (enableTagging && taggingCount > 0)
            {
                orchestrator.UpdateQueueStatus(ProcessPriority.Tagging, total: taggingCount, processed: 0, isRunning: true);
                Logger.Log($"Pre-registered tagging with {taggingCount} pending items");
            }
            if (enableCaptioning && captioningCount > 0)
            {
                orchestrator.UpdateQueueStatus(ProcessPriority.Captioning, total: captioningCount, processed: 0, isRunning: true);
                Logger.Log($"Pre-registered captioning with {captioningCount} pending items");
            }
            if (enableEmbedding && embeddingCount > 0)
            {
                orchestrator.UpdateQueueStatus(ProcessPriority.Embedding, total: embeddingCount, processed: 0, isRunning: true);
                Logger.Log($"Pre-registered embedding with {embeddingCount} pending items");
            }
            if (enableFaceDetection && faceCount > 0)
            {
                orchestrator.UpdateQueueStatus(ProcessPriority.FaceDetection, total: faceCount, processed: 0, isRunning: true);
                Logger.Log($"Pre-registered face detection with {faceCount} pending items");
            }
            
            // Log the optimal allocation plan BEFORE starting
            var plan = orchestrator.GetOptimalAllocationPlan();
            Logger.Log($"Unified allocation plan ({enabledProcesses.Count} processes):");
            foreach (var group in plan.GroupBy(p => p.Priority))
            {
                var gpuSummary = string.Join(", ", group.Select(p => $"GPU{p.GpuId}:{p.WorkerCount}w"));
                Logger.Log($"  {group.Key}: {gpuSummary}");
            }
        }
        
        // Calculate total VRAM needed and create allocation plan
        var vramPlan = CalculateVramAllocationPlan(enableTagging, enableCaptioning, enableEmbedding, enableFaceDetection);
        Logger.Log($"VRAM allocation plan: {vramPlan.TotalVramGb:F1}GB needed, {vramPlan.AvailableVramGb:F1}GB available");
        
        if (vramPlan.TotalVramGb > vramPlan.AvailableVramGb)
        {
            Logger.Log($"Warning: Estimated VRAM ({vramPlan.TotalVramGb:F1}GB) exceeds available ({vramPlan.AvailableVramGb:F1}GB). Some processes may fail to load.");
        }
        
        // Start processes in priority order (rocks, pebbles, sand approach)
        // 1. Captioning first (largest model ~16GB)
        // 2. Embedding (4 models ~6.6GB total)
        // 3. Tagging (~1.7GB)
        // 4. Face Detection (~1GB)
        
        var tasks = new List<Task>();
        
        if (enableCaptioning && !_isCaptioningRunning)
        {
            Logger.Log("Starting captioning as part of unified processing");
            StartCaptioning();
            await Task.Delay(500); // Allow model to start loading before next
        }
        
        if (enableEmbedding && !_isEmbeddingRunning)
        {
            Logger.Log("Starting embedding as part of unified processing");
            StartEmbedding();
            await Task.Delay(500);
        }
        
        if (enableTagging && !_isTaggingRunning)
        {
            Logger.Log("Starting tagging as part of unified processing");
            StartTagging();
            await Task.Delay(500);
        }
        
        if (enableFaceDetection)
        {
            var faceService = ServiceLocator.BackgroundFaceDetectionService;
            if (faceService != null && !faceService.IsFaceDetectionRunning)
            {
                Logger.Log("Starting face detection as part of unified processing");
                faceService.StartFaceDetection();
            }
        }
        
        StatusChanged?.Invoke(this, $"Started {enabledProcesses.Count} processors");
    }
    
    /// <summary>
    /// Stop all running processors
    /// </summary>
    public void StopAllProcessors()
    {
        Logger.Log("Stopping all processors");
        
        if (_isTaggingRunning) StopTagging();
        if (_isCaptioningRunning) StopCaptioning();
        if (_isEmbeddingRunning) StopEmbedding();
        
        var faceService = ServiceLocator.BackgroundFaceDetectionService;
        if (faceService?.IsFaceDetectionRunning == true)
        {
            faceService.StopFaceDetection();
        }
        
        StatusChanged?.Invoke(this, "All processors stopped");
    }
    
    /// <summary>
    /// Pause all running processors
    /// </summary>
    public void PauseAllProcessors()
    {
        Logger.Log("Pausing all processors");
        
        if (_isTaggingRunning && !_isTaggingPaused) PauseTagging();
        if (_isCaptioningRunning && !_isCaptioningPaused) PauseCaptioning();
        if (_isEmbeddingRunning && !_isEmbeddingPaused) PauseEmbedding();
        
        var faceService = ServiceLocator.BackgroundFaceDetectionService;
        if (faceService?.IsFaceDetectionRunning == true && !faceService.IsFaceDetectionPaused)
        {
            faceService.PauseFaceDetection();
        }
    }
    
    /// <summary>
    /// Resume all paused processors
    /// </summary>
    public void ResumeAllProcessors()
    {
        Logger.Log("Resuming all processors");
        
        if (_isTaggingPaused) ResumeTagging();
        if (_isCaptioningPaused) ResumeCaptioning();
        if (_isEmbeddingPaused) ResumeEmbedding();
        
        var faceService = ServiceLocator.BackgroundFaceDetectionService;
        if (faceService?.IsFaceDetectionPaused == true)
        {
            faceService.ResumeFaceDetection();
        }
    }
    
    /// <summary>
    /// Check if any processor is currently running
    /// </summary>
    public bool IsAnyProcessorRunning => _isTaggingRunning || _isCaptioningRunning || _isEmbeddingRunning || 
                                          (ServiceLocator.BackgroundFaceDetectionService?.IsFaceDetectionRunning == true);
    
    /// <summary>
    /// Check if any processor is paused
    /// </summary>
    public bool IsAnyProcessorPaused => _isTaggingPaused || _isCaptioningPaused || _isEmbeddingPaused ||
                                         (ServiceLocator.BackgroundFaceDetectionService?.IsFaceDetectionPaused == true);
    
    /// <summary>
    /// Calculate VRAM allocation plan for selected processes
    /// </summary>
    private VramAllocationPlan CalculateVramAllocationPlan(bool tagging, bool captioning, bool embedding, bool faceDetection)
    {
        double totalNeeded = 0;
        
        // MEASURED runtime VRAM values
        if (tagging) totalNeeded += 3.1;       // Measured: 3.1GB
        if (captioning) totalNeeded += 5.8;    // Measured: 5.8GB
        if (embedding) totalNeeded += 7.5;     // Measured: 7.5GB
        if (faceDetection) totalNeeded += 0.8; // Measured: 0.8GB
        
        // Get available VRAM from GPU orchestrator
        var orchestrator = ServiceLocator.GpuOrchestrator;
        double availableVram = 32.0; // Default fallback
        
        // TODO: Query actual available VRAM from orchestrator
        
        return new VramAllocationPlan
        {
            TotalVramGb = totalNeeded,
            AvailableVramGb = availableVram,
            EnableTagging = tagging,
            EnableCaptioning = captioning,
            EnableEmbedding = embedding,
            EnableFaceDetection = faceDetection
        };
    }

    /// <summary>
    /// Refresh all queue counts from the database and notify UI
    /// </summary>
    public async Task RefreshQueueCountsAsync()
    {
        try
        {
            var (tagging, captioning, embedding, _) = await _dataStore.GetPendingCountsAsync();
            _taggingQueueRemaining = tagging;
            _captioningQueueRemaining = captioning;
            _embeddingQueueRemaining = embedding;
            RaiseQueueCountsChanged();
            Logger.Log($"Queue counts refreshed: T={tagging}, C={captioning}, E={embedding}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error refreshing queue counts: {ex.Message}");
        }
    }

    /// <summary>
    /// Queue images for tagging (respects skipAlreadyProcessed setting)
    /// </summary>
    public async Task<int> QueueImagesForTagging(List<int> imageIds, bool skipAlreadyProcessed = true)
    {
        var queued = await _dataStore.SmartQueueForTagging(imageIds, skipAlreadyProcessed);
        Logger.Log($"Queued {queued}/{imageIds.Count} images for tagging (skipped: {imageIds.Count - queued})");
        
        // Update queue count immediately
        _taggingQueueRemaining = await _dataStore.CountImagesNeedingTagging();
        QueueCountsChanged?.Invoke(this, EventArgs.Empty);
        
        return queued;
    }

    /// <summary>
    /// Queue images for captioning (respects skipAlreadyProcessed setting)
    /// </summary>
    public async Task<int> QueueImagesForCaptioning(List<int> imageIds, bool skipAlreadyProcessed = true)
    {
        var queued = await _dataStore.SmartQueueForCaptioning(imageIds, skipAlreadyProcessed);
        Logger.Log($"Queued {queued}/{imageIds.Count} images for captioning (skipped: {imageIds.Count - queued})");
        
        // Update queue count immediately
        _captioningQueueRemaining = await _dataStore.CountImagesNeedingCaptioning();
        QueueCountsChanged?.Invoke(this, EventArgs.Empty);
        
        return queued;
    }

    /// <summary>
    /// Queue images for embedding (ALWAYS skips already-processed)
    /// </summary>
    public async Task<int> QueueImagesForEmbedding(List<int> imageIds)
    {
        var queued = await _dataStore.SmartQueueForEmbedding(imageIds);
        Logger.Log($"Queued {queued}/{imageIds.Count} images for embedding (skipped: {imageIds.Count - queued})");
        
        // Update queue count immediately
        QueueCountsChanged?.Invoke(this, EventArgs.Empty);
        
        return queued;
    }

    /// <summary>
    /// Queue images for face detection (ALWAYS skips already-processed)
    /// </summary>
    public async Task<int> QueueImagesForFaceDetection(List<int> imageIds)
    {
        var queued = await _dataStore.SmartQueueForFaceDetection(imageIds);
        Logger.Log($"Queued {queued}/{imageIds.Count} images for face detection (skipped: {imageIds.Count - queued})");
        
        // Update queue count immediately
        QueueCountsChanged?.Invoke(this, EventArgs.Empty);
        
        return queued;
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
    /// Uses GPU orchestrator for resource allocation
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
            
            // Get allocation plan from GPU orchestrator
            var allocationPlan = GetAllocationPlan(ProcessPriority.Tagging);
            
            if (allocationPlan.Count == 0)
            {
                Logger.Log("No GPU allocation available for tagging");
                _isTaggingRunning = false;
                return;
            }
            
            var totalWorkers = allocationPlan.Sum(a => a.WorkerCount);
            
            Logger.Log($"Tagging allocation plan: {string.Join(", ", allocationPlan.Select(a => $"GPU{a.GpuId}={a.WorkerCount} workers"))}");
            
            // Create unbounded channel for shared work queue
            _taggingQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,  // Multiple orchestrators reading
                SingleWriter = true
            });
            
            // Populate queue with all pending image IDs using cursor-based pagination
            Task.Run(async () =>
            {
                try
                {
                    _taggingTotal = await _dataStore.CountImagesNeedingTagging();
                    _taggingQueueRemaining = _taggingTotal;
                    RaiseQueueCountsChanged();
                    Logger.Log($"Found {_taggingTotal} images to tag");
                    
                    const int batchSize = 1000;
                    int totalQueued = 0;
                    int lastId = 0;  // Cursor for pagination
                    
                    while (!_taggingCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingTagging(batchSize, lastId);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _taggingQueue.Writer.WriteAsync(imageId, _taggingCts.Token);
                            totalQueued++;
                        }
                        
                        // Move cursor to last ID in batch
                        lastId = batch[batch.Count - 1];
                        
                        if (batch.Count < batchSize) break;  // Last batch
                    }
                    
                    _taggingQueue.Writer.Complete();
                    Logger.Log($"Tagging queue populated with {totalQueued} images (expected {_taggingTotal})");
                    
                    // Update orchestrator with queue status
                    UpdateOrchestratorStatus(ProcessPriority.Tagging, _taggingTotal, 0, totalWorkers, true);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Queue population error: {ex.Message}");
                }
            });
            
            // Start one orchestrator per GPU based on allocation plan
            _taggingOrchestrators = new List<Task>();
            
            foreach (var (gpuId, workerCount) in allocationPlan)
            {
                Logger.Log($"GPU {gpuId}: Starting {workerCount} tagging workers");
                
                var orchestratorTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunTaggingGpuOrchestrator(gpuId, workerCount, _taggingCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GPU {gpuId} tagging orchestrator error: {ex.Message}\n{ex.StackTrace}");
                    }
                });
                
                _taggingOrchestrators.Add(orchestratorTask);
            }
            
            Logger.Log($"Started tagging with {allocationPlan.Count} GPUs, {totalWorkers} total workers");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start tagging: {ex.Message}\n{ex.StackTrace}");
            _isTaggingRunning = false;
            UpdateOrchestratorStatus(ProcessPriority.Tagging, 0, 0, 0, false);
        }
    }

    /// <summary>
    /// Start background captioning with worker pool - uses GPU orchestrator for allocation
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
            
            // Get allocation plan from GPU orchestrator
            var allocationPlan = GetAllocationPlan(ProcessPriority.Captioning);
            
            if (allocationPlan.Count == 0)
            {
                Logger.Log("No GPU allocation available for captioning");
                _isCaptioningRunning = false;
                return;
            }
            
            // For captioning, worker count = model count (each model is 1 worker)
            var totalWorkers = allocationPlan.Sum(a => a.WorkerCount);
            
            Logger.Log($"Captioning allocation plan: {string.Join(", ", allocationPlan.Select(a => $"GPU{a.GpuId}={a.WorkerCount} models"))}");
            
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
                    RaiseQueueCountsChanged();
                    Logger.Log($"Found {_captioningTotal} images to caption");
                    
                    // Update orchestrator with queue status
                    UpdateOrchestratorStatus(ProcessPriority.Captioning, _captioningTotal, 0, totalWorkers, true);
                    
                    const int batchSize = 500;
                    int lastId = 0;  // Cursor for pagination
                    int totalQueued = 0;
                    
                    while (!_captioningCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingCaptioning(batchSize, lastId);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _captioningQueue.Writer.WriteAsync(imageId, _captioningCts.Token);
                            totalQueued++;
                        }
                        
                        // Move cursor to last ID in batch
                        lastId = batch[batch.Count - 1];
                        
                        if (batch.Count < batchSize) break;  // Last batch
                    }
                    
                    _captioningQueue.Writer.Complete();
                    Logger.Log($"Captioning queue populated with {totalQueued} images");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Queue population error: {ex.Message}");
                }
            });
            
            // Create workers based on allocation plan
            _captioningOrchestrators = new List<Task>();
            int workerIndex = 0;
            
            foreach (var (gpuId, modelCount) in allocationPlan)
            {
                Logger.Log($"GPU {gpuId}: Creating {modelCount} caption workers");
                
                // Create workers for this GPU
                for (int w = 0; w < modelCount; w++)
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
            
            Logger.Log($"Started {totalWorkers} caption workers via GPU orchestrator");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start captioning: {ex.Message}\n{ex.StackTrace}");
            _isCaptioningRunning = false;
            UpdateOrchestratorStatus(ProcessPriority.Captioning, 0, 0, 0, false);
        }
    }

    /// <summary>
    /// Start background embedding with pooled GPU orchestrators - one orchestrator per GPU,
    /// each orchestrator loads 4 models once and spawns sub-workers per model
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
            
            // Get allocation plan from GPU orchestrator
            // For embedding, workerCount = sub-workers per model (not total workers)
            var allocationPlan = GetAllocationPlan(ProcessPriority.Embedding);
            
            if (allocationPlan.Count == 0)
            {
                Logger.Log("No GPU allocation available for embedding");
                _isEmbeddingRunning = false;
                return;
            }
            
            // workerCount per GPU = sub-workers per model (each GPU has 4 models × N sub-workers)
            var totalSubWorkersPerModel = allocationPlan.Sum(a => a.WorkerCount);
            var totalWorkers = totalSubWorkersPerModel * 4; // 4 models per GPU
            
            Logger.Log($"Embedding allocation plan: {string.Join(", ", allocationPlan.Select(a => $"GPU{a.GpuId}={a.WorkerCount} sub-workers/model ({a.WorkerCount * 4} total)"))}");
            
            // Create unbounded channel for shared work queue
            _embeddingQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });
            
            // Queue population task using cursor-based pagination
            Task.Run(async () =>
            {
                try
                {
                    _embeddingTotal = await _dataStore.CountImagesNeedingEmbedding();
                    _embeddingQueueRemaining = _embeddingTotal;
                    RaiseQueueCountsChanged();
                    Logger.Log($"Found {_embeddingTotal} images to embed");
                    
                    // Update orchestrator with queue status
                    UpdateOrchestratorStatus(ProcessPriority.Embedding, _embeddingTotal, 0, totalWorkers, true);
                    
                    const int batchSize = 1000;
                    int lastId = 0;  // Cursor for pagination
                    int totalQueued = 0;
                    
                    while (!_embeddingCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingEmbedding(batchSize, lastId);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _embeddingQueue.Writer.WriteAsync(imageId, _embeddingCts.Token);
                            totalQueued++;
                        }
                        
                        // Move cursor to last ID in batch
                        lastId = batch[batch.Count - 1];
                        
                        if (batch.Count < batchSize) break;  // Last batch
                    }
                    
                    _embeddingQueue.Writer.Complete();
                    Logger.Log($"Embedding queue populated with {totalQueued} images");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Embedding queue population error: {ex.Message}");
                }
            });
            
            // Create one pooled orchestrator per GPU
            _embeddingOrchestrators = new List<Task>();
            _embeddingPooledOrchestrators = new List<EmbeddingPooledOrchestrator>();
            
            foreach (var (gpuId, workersPerModel) in allocationPlan)
            {
                int capturedGpuId = gpuId;
                int capturedWorkersPerModel = workersPerModel;
                
                var orchestratorTask = Task.Run(async () =>
                {
                    try
                    {
                        await RunEmbeddingPooledOrchestrator(capturedGpuId, capturedWorkersPerModel, _embeddingCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"GPU {capturedGpuId} embedding orchestrator error: {ex.Message}\n{ex.StackTrace}");
                    }
                });
                
                _embeddingOrchestrators.Add(orchestratorTask);
            }
            
            Logger.Log($"Started {allocationPlan.Count} embedding orchestrators with {totalWorkers} total sub-workers");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start embedding: {ex.Message}\n{ex.StackTrace}");
            _isEmbeddingRunning = false;
            UpdateOrchestratorStatus(ProcessPriority.Embedding, 0, 0, 0, false);
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
        
        // Notify orchestrator that tagging is stopped
        UpdateOrchestratorStatus(ProcessPriority.Tagging, _taggingTotal, _taggingProgress, 0, false);
        ServiceLocator.GpuOrchestrator?.MarkQueueCompleted(ProcessPriority.Tagging);
        
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
    /// Orchestrator: Populates queue with image IDs from database using cursor-based pagination
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
            
            // Populate queue with all pending image IDs using cursor-based pagination
            const int batchSize = 1000;
            int lastId = 0;  // Cursor for pagination
            int totalQueued = 0;
            
            while (!ct.IsCancellationRequested)
            {
                var batch = await _dataStore.GetImagesNeedingTagging(batchSize, lastId);
                if (batch.Count == 0)
                    break;
                
                foreach (var imageId in batch)
                {
                    if (_taggingQueue != null)
                        await _taggingQueue.Writer.WriteAsync(imageId, ct);
                    totalQueued++;
                }
                
                // Move cursor to last ID in batch
                lastId = batch[batch.Count - 1];
                
                if (batch.Count < batchSize)
                    break; // Last batch
            }
            
            // Signal workers that no more work is coming
            if (_taggingQueue != null)
                _taggingQueue.Writer.Complete();
            Logger.Log($"Tagging orchestrator populated queue with {totalQueued} images");
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
        
        // Notify orchestrator that captioning is stopped
        UpdateOrchestratorStatus(ProcessPriority.Captioning, _captioningTotal, _captioningProgress, 0, false);
        ServiceLocator.GpuOrchestrator?.MarkQueueCompleted(ProcessPriority.Captioning);
        
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
        
        // Wait for orchestrator tasks to finish (with timeout)
        try
        {
            if (_embeddingOrchestrators != null)
            {
                Task.WaitAll(_embeddingOrchestrators.ToArray(), TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception while stopping embedding: {ex.Message}");
        }
        
        _embeddingOrchestrators?.Clear();
        
        // Dispose pooled orchestrators (releases all ONNX models)
        if (_embeddingPooledOrchestrators != null)
        {
            foreach (var orchestrator in _embeddingPooledOrchestrators)
            {
                try
                {
                    orchestrator.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error disposing embedding orchestrator: {ex.Message}");
                }
            }
            _embeddingPooledOrchestrators.Clear();
        }
        
        // Notify orchestrator that embedding is stopped
        UpdateOrchestratorStatus(ProcessPriority.Embedding, _embeddingTotal, _embeddingProgress, 0, false);
        ServiceLocator.GpuOrchestrator?.MarkQueueCompleted(ProcessPriority.Embedding);
        
        Logger.Log("Embedding stopped");
    }

    /// <summary>
    /// Orchestrator: Populates queue with image IDs from database using cursor-based pagination
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
            
            // Populate queue with all pending image IDs using cursor-based pagination
            const int batchSize = 500; // Smaller batches for captioning
            int lastId = 0;  // Cursor for pagination
            int totalQueued = 0;
            
            while (!ct.IsCancellationRequested)
            {
                var batch = await _dataStore.GetImagesNeedingCaptioning(batchSize, lastId);
                if (batch.Count == 0)
                    break;
                
                foreach (var imageId in batch)
                {
                    if (_captioningQueue != null)
                        await _captioningQueue.Writer.WriteAsync(imageId, ct);
                    totalQueued++;
                }
                
                // Move cursor to last ID in batch
                lastId = batch[batch.Count - 1];
                
                if (batch.Count < batchSize)
                    break; // Last batch
            }
            
            // Signal workers that no more work is coming
            if (_captioningQueue != null)
                _captioningQueue.Writer.Complete();
            Logger.Log($"Captioning orchestrator populated queue with {totalQueued} images");
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
    /// Embedding orchestrator: Loads all 4 models once per GPU, spawns sub-workers per model,
    /// and coordinates all 4 embeddings per image before writing to database.
    /// </summary>
    private async Task RunEmbeddingPooledOrchestrator(int gpuId, int workersPerModel, CancellationToken ct)
    {
        Logger.Log($"Embedding orchestrator starting on GPU {gpuId} with {workersPerModel} sub-workers per model");
        
        EmbeddingPooledOrchestrator? orchestrator = null;
        
        try
        {
            // Create configuration
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var config = Embeddings.EmbeddingConfig.CreateDefault(baseDir);
            
            // Create and initialize the pooled orchestrator
            orchestrator = new EmbeddingPooledOrchestrator(gpuId, workersPerModel);
            _embeddingPooledOrchestrators?.Add(orchestrator);
            
            await orchestrator.InitializeAsync(config, ct);
            Logger.Log($"GPU {gpuId} embedding orchestrator initialized with {workersPerModel * 4} total sub-workers");
            
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
                    await ProcessImageEmbeddingPooled(imageId, orchestrator, ct);
                    
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
                    Logger.Log($"GPU {gpuId} embedding orchestrator error processing image {imageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"GPU {gpuId} embedding orchestrator cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GPU {gpuId} embedding orchestrator fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            orchestrator?.Dispose();
            Logger.Log($"GPU {gpuId} embedding orchestrator exiting");
            RaiseEmbeddingCompleted();
        }
    }

    private async Task ProcessImageEmbeddingPooled(int imageId, EmbeddingPooledOrchestrator orchestrator, CancellationToken ct)
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

            // Skip if no prompt (need text for text embeddings)
            if (string.IsNullOrWhiteSpace(image.Prompt))
            {
                Logger.Log($"Image {imageId} has no prompt, skipping");
                Interlocked.Increment(ref _embeddingSkipped);
                await _dataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
                return;
            }

            // Generate all 4 embeddings in parallel using the pooled orchestrator
            var result = await orchestrator.GenerateEmbeddingsAsync(
                imageId,
                image.Prompt,
                image.NegativePrompt,
                image.Path,
                ct);
            
            if (result == null)
            {
                Logger.Log($"Embedding failed for image {imageId}");
                Interlocked.Increment(ref _embeddingSkipped);
                return;
            }
            
            // Store all embeddings in database
            await _dataStore.StoreImageEmbeddingsAsync(
                imageId, 
                result.BgeEmbedding,       // Prompt/semantic embedding
                result.ClipLEmbedding,     // CLIP-L text
                result.ClipGEmbedding,     // CLIP-G text
                result.ImageEmbedding,     // Vision embedding
                isRepresentative: true);   // Mark as representative (has unique embeddings)
            
            // Mark as processed
            await _dataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
            
            Logger.Log($"Embedded image {imageId}: BGE={result.BgeEmbedding.Length}D, CLIP-L={result.ClipLEmbedding.Length}D, CLIP-G={result.ClipGEmbedding.Length}D, Vision={result.ImageEmbedding.Length}D");
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
