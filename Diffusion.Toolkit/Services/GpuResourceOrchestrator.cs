using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Diffusion.Common;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Priority order for GPU model loading (largest models first = "rocks before sand")
/// Captioning is special: limited to 1 per GPU when other processes have work
/// </summary>
public enum ProcessPriority
{
    Captioning = 1,    // ~8GB per model - "boulders" (limited when others have work)
    Embedding = 2,     // ~4GB per model set - "rocks"
    Tagging = 3,       // ~2GB per model set - "pebbles"  
    FaceDetection = 4  // ~1GB per model set - "sand"
}

/// <summary>
/// Represents a GPU device with VRAM tracking
/// </summary>
public class GpuDevice
{
    public int DeviceId { get; init; }
    public long TotalVramBytes { get; init; }
    public long ReservedVramBytes { get; private set; }
    public long MaxUsableVramBytes => (long)(TotalVramBytes * MaxUsagePercent);
    public long AvailableVramBytes => MaxUsableVramBytes - ReservedVramBytes;
    public double MaxUsagePercent { get; set; } = 0.85;
    
    private readonly object _lock = new();
    private readonly List<ModelAllocation> _allocations = new();
    
    public bool TryReserve(long bytes, string modelType, out ModelAllocation? allocation)
    {
        lock (_lock)
        {
            if (bytes <= AvailableVramBytes)
            {
                allocation = new ModelAllocation
                {
                    DeviceId = DeviceId,
                    ModelType = modelType,
                    VramBytes = bytes,
                    AllocatedAt = DateTime.Now
                };
                _allocations.Add(allocation);
                ReservedVramBytes += bytes;
                return true;
            }
            allocation = null;
            return false;
        }
    }
    
    public void Release(ModelAllocation allocation)
    {
        lock (_lock)
        {
            if (_allocations.Remove(allocation))
            {
                ReservedVramBytes -= allocation.VramBytes;
            }
        }
    }
    
    public IReadOnlyList<ModelAllocation> GetAllocations()
    {
        lock (_lock)
        {
            return _allocations.ToList();
        }
    }
}

/// <summary>
/// Tracks a VRAM allocation for a model
/// </summary>
public class ModelAllocation
{
    public int DeviceId { get; init; }
    public string ModelType { get; init; } = "";
    public long VramBytes { get; init; }
    public DateTime AllocatedAt { get; init; }
    public int WorkerCount { get; set; } = 1;
}

/// <summary>
/// Estimated VRAM requirements per model type
/// </summary>
public static class ModelVramRequirements
{
    // All values in bytes - MEASURED runtime VRAM usage (not file sizes)
    public const long CaptioningModelBytes = 5939L * 1024 * 1024;     // 5.8GB measured
    public const long TaggingModelSetBytes = 3174L * 1024 * 1024;     // 3.1GB measured
    public const long EmbeddingModelSetBytes = 7680L * 1024 * 1024;   // 7.5GB measured
    public const long FaceDetectionModelSetBytes = 819L * 1024 * 1024; // 0.8GB measured
    
    public const int TaggingWorkersPerModel = 10;
    public const int EmbeddingWorkersPerModel = 3;
    public const int FaceDetectionWorkersPerModel = 2;
    
    public static long GetVramRequirement(ProcessPriority priority) => priority switch
    {
        ProcessPriority.Captioning => CaptioningModelBytes,
        ProcessPriority.Tagging => TaggingModelSetBytes,
        ProcessPriority.Embedding => EmbeddingModelSetBytes,
        ProcessPriority.FaceDetection => FaceDetectionModelSetBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };
    
    public static int GetWorkersPerModel(ProcessPriority priority) => priority switch
    {
        ProcessPriority.Captioning => 1,  // Each caption model = 1 worker
        ProcessPriority.Tagging => TaggingWorkersPerModel,
        ProcessPriority.Embedding => EmbeddingWorkersPerModel,
        ProcessPriority.FaceDetection => FaceDetectionWorkersPerModel,
        _ => 1
    };
}

/// <summary>
/// Request to allocate GPU resources for a process
/// </summary>
public class ResourceRequest
{
    public ProcessPriority Priority { get; init; }
    public int PreferredGpuId { get; init; } = -1; // -1 = any GPU
    public TaskCompletionSource<ModelAllocation?> Completion { get; } = new();
}

/// <summary>
/// Status of a processing queue
/// </summary>
public class QueueStatus
{
    public ProcessPriority ProcessType { get; init; }
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int RemainingItems => TotalItems - ProcessedItems;
    public int ActiveWorkers { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }
}

/// <summary>
/// Central GPU resource orchestrator that manages all processing pipelines
/// Uses "rocks, pebbles, sand" approach to fill VRAM efficiently
/// </summary>
public class GpuResourceOrchestrator : IDisposable
{
    private readonly ConcurrentDictionary<int, GpuDevice> _gpuDevices = new();
    private readonly ConcurrentDictionary<ProcessPriority, QueueStatus> _queueStatuses = new();
    private readonly Channel<ResourceRequest> _resourceRequestChannel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _orchestratorTask;
    private bool _isDisposed;
    
    // Events for UI updates
    public event Action<ProcessPriority, QueueStatus>? QueueStatusChanged;
    public event Action<int, GpuDevice>? GpuStatusChanged;
    public event Action<string>? LogMessage;
    
    public GpuResourceOrchestrator()
    {
        _resourceRequestChannel = Channel.CreateUnbounded<ResourceRequest>();
        
        // Initialize queue statuses
        foreach (ProcessPriority priority in Enum.GetValues<ProcessPriority>())
        {
            _queueStatuses[priority] = new QueueStatus { ProcessType = priority };
        }
    }
    
    /// <summary>
    /// Initialize GPUs from settings
    /// </summary>
    public void Initialize(string gpuDevices, string gpuVramGb, double maxUsagePercent = 0.85)
    {
        var deviceIds = gpuDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var id) ? id : 0)
            .Distinct()
            .ToList();
            
        var vramValues = gpuVramGb.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var gb) ? gb : 32)
            .ToList();
        
        // Pad VRAM values if needed
        while (vramValues.Count < deviceIds.Count)
        {
            vramValues.Add(vramValues.LastOrDefault());
        }
        
        for (int i = 0; i < deviceIds.Count; i++)
        {
            var device = new GpuDevice
            {
                DeviceId = deviceIds[i],
                TotalVramBytes = vramValues[i] * 1024L * 1024L * 1024L,
                MaxUsagePercent = maxUsagePercent
            };
            _gpuDevices[deviceIds[i]] = device;
            
            Log($"GPU {deviceIds[i]}: {vramValues[i]}GB VRAM, max usage {maxUsagePercent:P0}");
        }
        
        // Start orchestrator loop
        _orchestratorTask = Task.Run(OrchestratorLoop);
    }
    
    /// <summary>
    /// Main orchestrator loop - processes resource requests in priority order
    /// </summary>
    private async Task OrchestratorLoop()
    {
        Log("GPU Resource Orchestrator started");
        
        try
        {
            await foreach (var request in _resourceRequestChannel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    var allocation = TryAllocateResources(request);
                    request.Completion.TrySetResult(allocation);
                }
                catch (Exception ex)
                {
                    Log($"Error processing resource request: {ex.Message}");
                    request.Completion.TrySetResult(null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Log($"Orchestrator loop error: {ex.Message}");
        }
        
        Log("GPU Resource Orchestrator stopped");
    }
    
    /// <summary>
    /// Try to allocate GPU resources for a request
    /// </summary>
    private ModelAllocation? TryAllocateResources(ResourceRequest request)
    {
        var requiredVram = ModelVramRequirements.GetVramRequirement(request.Priority);
        var modelType = request.Priority.ToString();
        
        // Try preferred GPU first
        if (request.PreferredGpuId >= 0 && _gpuDevices.TryGetValue(request.PreferredGpuId, out var preferredGpu))
        {
            if (preferredGpu.TryReserve(requiredVram, modelType, out var allocation))
            {
                allocation!.WorkerCount = ModelVramRequirements.GetWorkersPerModel(request.Priority);
                Log($"Allocated {modelType} on GPU {preferredGpu.DeviceId}: {requiredVram / 1024 / 1024}MB ({allocation.WorkerCount} workers)");
                GpuStatusChanged?.Invoke(preferredGpu.DeviceId, preferredGpu);
                return allocation;
            }
        }
        
        // Try any GPU with available VRAM (prefer GPUs with more free VRAM)
        var sortedGpus = _gpuDevices.Values
            .OrderByDescending(g => g.AvailableVramBytes)
            .ToList();
            
        foreach (var gpu in sortedGpus)
        {
            if (gpu.TryReserve(requiredVram, modelType, out var allocation))
            {
                allocation!.WorkerCount = ModelVramRequirements.GetWorkersPerModel(request.Priority);
                Log($"Allocated {modelType} on GPU {gpu.DeviceId}: {requiredVram / 1024 / 1024}MB ({allocation.WorkerCount} workers)");
                GpuStatusChanged?.Invoke(gpu.DeviceId, gpu);
                return allocation;
            }
        }
        
        Log($"Failed to allocate {modelType}: insufficient VRAM on all GPUs");
        return null;
    }
    
    /// <summary>
    /// Request GPU resources for a process (async)
    /// </summary>
    public async Task<ModelAllocation?> RequestResourcesAsync(ProcessPriority priority, int preferredGpuId = -1, CancellationToken ct = default)
    {
        var request = new ResourceRequest
        {
            Priority = priority,
            PreferredGpuId = preferredGpuId
        };
        
        await _resourceRequestChannel.Writer.WriteAsync(request, ct);
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        
        try
        {
            return await request.Completion.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
    
    /// <summary>
    /// Release GPU resources when a worker is done
    /// </summary>
    public void ReleaseResources(ModelAllocation allocation)
    {
        if (_gpuDevices.TryGetValue(allocation.DeviceId, out var gpu))
        {
            gpu.Release(allocation);
            Log($"Released {allocation.ModelType} on GPU {allocation.DeviceId}: {allocation.VramBytes / 1024 / 1024}MB freed");
            GpuStatusChanged?.Invoke(gpu.DeviceId, gpu);
            
            // Check if we should trigger reallocation
            CheckForReallocationOpportunity();
        }
    }
    
    /// <summary>
    /// Event fired when there's an opportunity to load more models
    /// (e.g., when a queue completes and frees VRAM for more captioning)
    /// </summary>
    public event Action<ProcessPriority>? ReallocationOpportunity;
    
    /// <summary>
    /// Check if other processes have completed, allowing more captioning models
    /// </summary>
    private void CheckForReallocationOpportunity()
    {
        // Check if only captioning has work remaining
        var otherProcessesHaveWork = _queueStatuses
            .Where(kvp => kvp.Key != ProcessPriority.Captioning)
            .Any(kvp => kvp.Value.RemainingItems > 0 || kvp.Value.IsRunning);
        
        if (!otherProcessesHaveWork)
        {
            var captionStatus = _queueStatuses.GetValueOrDefault(ProcessPriority.Captioning);
            if (captionStatus != null && (captionStatus.RemainingItems > 0 || captionStatus.IsRunning))
            {
                // Check if there's room for more captioning models
                var vramNeeded = ModelVramRequirements.GetVramRequirement(ProcessPriority.Captioning);
                var hasRoom = _gpuDevices.Values.Any(gpu => gpu.AvailableVramBytes >= vramNeeded);
                
                if (hasRoom)
                {
                    Log("Other processes completed - opportunity to load more captioning models");
                    ReallocationOpportunity?.Invoke(ProcessPriority.Captioning);
                }
            }
        }
    }
    
    /// <summary>
    /// Mark a queue as completed
    /// </summary>
    public void MarkQueueCompleted(ProcessPriority priority)
    {
        UpdateQueueStatus(priority, processed: _queueStatuses[priority].TotalItems, 
            activeWorkers: 0, isRunning: false);
        
        Log($"{priority} queue completed");
        CheckForReallocationOpportunity();
    }
    
    /// <summary>
    /// Update queue status for a process
    /// </summary>
    public void UpdateQueueStatus(ProcessPriority priority, int? total = null, int? processed = null, 
        int? activeWorkers = null, bool? isRunning = null, TimeSpan? eta = null)
    {
        if (_queueStatuses.TryGetValue(priority, out var status))
        {
            if (total.HasValue) status.TotalItems = total.Value;
            if (processed.HasValue) status.ProcessedItems = processed.Value;
            if (activeWorkers.HasValue) status.ActiveWorkers = activeWorkers.Value;
            if (isRunning.HasValue)
            {
                status.IsRunning = isRunning.Value;
                if (isRunning.Value && !status.StartedAt.HasValue)
                    status.StartedAt = DateTime.Now;
                else if (!isRunning.Value)
                    status.StartedAt = null;
            }
            if (eta.HasValue) status.EstimatedTimeRemaining = eta;
            
            QueueStatusChanged?.Invoke(priority, status);
        }
    }
    
    /// <summary>
    /// Get current status of all queues
    /// </summary>
    public IReadOnlyDictionary<ProcessPriority, QueueStatus> GetAllQueueStatuses()
    {
        return _queueStatuses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
    
    /// <summary>
    /// Get status of all GPUs
    /// </summary>
    public IReadOnlyDictionary<int, GpuDevice> GetAllGpuStatuses()
    {
        return _gpuDevices.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
    
    /// <summary>
    /// Get optimal allocation plan for all active processes
    /// SIMPLIFIED: Allocates exactly ONE model set per process type on GPU 0 only
    /// This is for VRAM measurement - will be enhanced after collecting real usage data
    /// </summary>
    public List<(ProcessPriority Priority, int GpuId, int WorkerCount)> GetOptimalAllocationPlan()
    {
        var plan = new List<(ProcessPriority, int, int)>();
        
        // Simple allocation: one model per process type, all on GPU 0
        // Workers are kept minimal for measurement purposes
        const int measurementGpuId = 0;
        
        foreach (ProcessPriority priority in Enum.GetValues<ProcessPriority>())
        {
            // Skip if this queue has no work
            if (!_queueStatuses.TryGetValue(priority, out var status) || 
                (status.RemainingItems <= 0 && !status.IsRunning))
            {
                continue;
            }
            
            // Single model set with minimal workers for measurement
            var workerCount = priority switch
            {
                ProcessPriority.Captioning => 1,      // 1 caption worker
                ProcessPriority.Tagging => 2,         // 2 tagging workers (share models)
                ProcessPriority.Embedding => 1,       // 1 embedding worker
                ProcessPriority.FaceDetection => 1,   // 1 face detection worker
                _ => 1
            };
            
            plan.Add((priority, measurementGpuId, workerCount));
            Log($"Measurement mode: {priority} -> GPU{measurementGpuId} with {workerCount} worker(s)");
        }
        
        return plan;
    }
    
    /// <summary>
    /// Log a message
    /// </summary>
    private void Log(string message)
    {
        Logger.Log($"[GpuOrchestrator] {message}");
        LogMessage?.Invoke(message);
    }
    
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        _cts.Cancel();
        _resourceRequestChannel.Writer.Complete();
        
        try
        {
            _orchestratorTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        
        _cts.Dispose();
    }
}
