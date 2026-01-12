using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// VRAM usage constants for each service type (in GB)
/// </summary>
public static class ServiceVramUsage
{
    public const double Tagging = 2.6;        // JoyTag + WD (fixed per GPU)
    public const double FaceDetection = 0.8;  // YOLO + ArcFace (fixed per GPU)
    public const double Embedding = 7.6;      // BGE + CLIP (fixed per GPU)
    public const double Captioning = 5.6;     // JoyCaption Q4 (per model instance)
    
    public static double GetVramUsage(ProcessingType type, int modelCount = 1)
    {
        return type switch
        {
            ProcessingType.Tagging => Tagging,       // Fixed
            ProcessingType.FaceDetection => FaceDetection, // Fixed
            ProcessingType.Embedding => Embedding,    // Fixed
            ProcessingType.Captioning => Captioning * modelCount, // Scales
            _ => 0
        };
    }
    
    public static int GetPriority(ProcessingType type)
    {
        // Lower = higher priority (loads first)
        return type switch
        {
            ProcessingType.Tagging => 1,
            ProcessingType.FaceDetection => 1,
            ProcessingType.Embedding => 2,
            ProcessingType.Captioning => 3,
            _ => 99
        };
    }
    
    public static bool IsOnnxService(ProcessingType type)
    {
        return type is ProcessingType.Tagging 
                    or ProcessingType.Embedding 
                    or ProcessingType.FaceDetection;
    }
}

/// <summary>
/// Tracks VRAM usage per GPU for dynamic allocation
/// </summary>
public class GpuVramTracker
{
    private readonly double[] _vramCapacity;
    private readonly double[] _vramUsed;
    private readonly double _maxUsagePercent;
    private readonly object _lock = new();
    
    public int GpuCount => _vramCapacity.Length;
    
    public GpuVramTracker(double[] vramCapacity, double maxUsagePercent)
    {
        _vramCapacity = vramCapacity;
        _vramUsed = new double[vramCapacity.Length];
        _maxUsagePercent = maxUsagePercent / 100.0;
    }
    
    public double GetAvailableVram(int gpuId)
    {
        lock (_lock)
        {
            if (gpuId >= _vramCapacity.Length) return 0;
            return (_vramCapacity[gpuId] * _maxUsagePercent) - _vramUsed[gpuId];
        }
    }
    
    public double GetUsedVram(int gpuId)
    {
        lock (_lock)
        {
            return gpuId < _vramUsed.Length ? _vramUsed[gpuId] : 0;
        }
    }
    
    public bool TryAllocate(int gpuId, double vramGb)
    {
        lock (_lock)
        {
            if (gpuId >= _vramCapacity.Length) return false;
            var available = (_vramCapacity[gpuId] * _maxUsagePercent) - _vramUsed[gpuId];
            if (vramGb > available) return false;
            _vramUsed[gpuId] += vramGb;
            return true;
        }
    }
    
    public void Release(int gpuId, double vramGb)
    {
        lock (_lock)
        {
            if (gpuId < _vramUsed.Length)
            {
                _vramUsed[gpuId] = Math.Max(0, _vramUsed[gpuId] - vramGb);
            }
        }
    }
    
    public int CalculateMaxCaptioningInstances(int gpuId)
    {
        var available = GetAvailableVram(gpuId);
        return (int)(available / ServiceVramUsage.Captioning);
    }
    
    public string GetStatusString()
    {
        lock (_lock)
        {
            var parts = new List<string>();
            for (int i = 0; i < _vramCapacity.Length; i++)
            {
                var max = _vramCapacity[i] * _maxUsagePercent;
                parts.Add($"GPU{i}: {_vramUsed[i]:F1}/{max:F1}GB");
            }
            return string.Join(" | ", parts);
        }
    }
}

/// <summary>
/// Global orchestrator that coordinates all processing services.
/// Responsibilities:
/// - Decides which services to start based on UI settings
/// - Determines processing mode (Solo vs Concurrent)
/// - Reads VRAM allocation settings and passes to service orchestrators
/// - Aggregates progress from all services for UI display
/// - Handles global start/stop/pause commands
/// - Dynamic reallocation when queues complete (priority-based)
/// </summary>
public class GlobalProcessingOrchestrator : IDisposable
{
    private readonly PostgreSQLDataStore _dataStore;
    private readonly Settings _settings;
    
    // Service orchestrators
    private readonly Dictionary<ProcessingType, IServiceOrchestrator> _orchestrators = new();
    private CancellationTokenSource? _globalCts;
    
    // Dynamic allocation tracking
    private GpuVramTracker? _vramTracker;
    private readonly ConcurrentDictionary<ProcessingType, double[]> _serviceVramPerGpu = new();
    private readonly SemaphoreSlim _reallocationLock = new(1, 1);
    private bool _enableDynamicReallocation = true;
    
    // State
    private bool _isRunning;
    private ProcessingMode _currentMode;
    
    // Events for UI binding
    public event EventHandler<ProcessingProgressEventArgs>? ProgressChanged;
    public event EventHandler<ProcessingStatusEventArgs>? StatusChanged;
    public event EventHandler<ProcessingType>? ServiceCompleted;
    public event EventHandler? AllServicesCompleted;
    
    // Status properties
    public bool IsRunning => _isRunning;
    public ProcessingMode CurrentMode => _currentMode;
    public IReadOnlyDictionary<ProcessingType, IServiceOrchestrator> Orchestrators => _orchestrators;
    
    public GlobalProcessingOrchestrator(PostgreSQLDataStore dataStore, Settings settings)
    {
        _dataStore = dataStore;
        _settings = settings;
    }
    
    /// <summary>
    /// Start processing with the specified services
    /// </summary>
    /// <param name="enableTagging">Enable tagging service</param>
    /// <param name="enableCaptioning">Enable captioning service</param>
    /// <param name="enableEmbedding">Enable embedding service</param>
    /// <param name="enableFaceDetection">Enable face detection service</param>
    public async Task StartAsync(
        bool enableTagging = false,
        bool enableCaptioning = false,
        bool enableEmbedding = false,
        bool enableFaceDetection = false)
    {
        if (_isRunning)
        {
            Logger.Log("GlobalOrchestrator: Already running");
            return;
        }
        
        var enabledServices = new List<ProcessingType>();
        if (enableTagging) enabledServices.Add(ProcessingType.Tagging);
        if (enableCaptioning) enabledServices.Add(ProcessingType.Captioning);
        if (enableEmbedding) enabledServices.Add(ProcessingType.Embedding);
        if (enableFaceDetection) enabledServices.Add(ProcessingType.FaceDetection);
        
        if (enabledServices.Count == 0)
        {
            Logger.Log("GlobalOrchestrator: No services enabled");
            return;
        }
        
        _isRunning = true;
        _globalCts = new CancellationTokenSource();
        
        // Determine mode: Solo if only 1 service, Concurrent if 2+
        _currentMode = enabledServices.Count == 1 ? ProcessingMode.Solo : ProcessingMode.Concurrent;
        Logger.Log($"GlobalOrchestrator: Starting {enabledServices.Count} services in {_currentMode} mode");
        
        try
        {
            // Create and start orchestrators for each enabled service
            var startTasks = new List<Task>();
            
            foreach (var serviceType in enabledServices)
            {
                var orchestrator = CreateOrchestrator(serviceType);
                _orchestrators[serviceType] = orchestrator;
                
                // Subscribe to events
                orchestrator.ProgressChanged += OnServiceProgressChanged;
                orchestrator.StatusChanged += OnServiceStatusChanged;
                orchestrator.Completed += (s, e) => OnServiceCompleted(serviceType);
                
                // Get allocation for this service
                var allocation = GetAllocation(serviceType, _currentMode);
                
                // Start the service
                var task = orchestrator.StartAsync(allocation, _globalCts.Token);
                startTasks.Add(task);
            }
            
            // Wait for all services to complete
            await Task.WhenAll(startTasks);
            
            Logger.Log("GlobalOrchestrator: All services completed");
            AllServicesCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Logger.Log("GlobalOrchestrator: Cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GlobalOrchestrator: Error - {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            _isRunning = false;
        }
    }
    
    /// <summary>
    /// Stop all processing
    /// </summary>
    public async Task StopAsync()
    {
        Logger.Log("GlobalOrchestrator: Stopping all services...");
        _globalCts?.Cancel();
        
        var stopTasks = _orchestrators.Values.Select(o => o.StopAsync()).ToArray();
        await Task.WhenAll(stopTasks);
        
        _isRunning = false;
        Logger.Log("GlobalOrchestrator: All services stopped");
    }
    
    /// <summary>
    /// Pause all processing
    /// </summary>
    public void PauseAll()
    {
        foreach (var orchestrator in _orchestrators.Values)
        {
            orchestrator.Pause();
        }
        Logger.Log("GlobalOrchestrator: All services paused");
    }
    
    /// <summary>
    /// Resume all processing
    /// </summary>
    public void ResumeAll()
    {
        foreach (var orchestrator in _orchestrators.Values)
        {
            orchestrator.Resume();
        }
        Logger.Log("GlobalOrchestrator: All services resumed");
    }
    
    /// <summary>
    /// Pause a specific service
    /// </summary>
    public void Pause(ProcessingType type)
    {
        if (_orchestrators.TryGetValue(type, out var orchestrator))
        {
            orchestrator.Pause();
        }
    }
    
    /// <summary>
    /// Resume a specific service
    /// </summary>
    public void Resume(ProcessingType type)
    {
        if (_orchestrators.TryGetValue(type, out var orchestrator))
        {
            orchestrator.Resume();
        }
    }
    
    #region UI-friendly async methods
    
    /// <summary>
    /// Check if any service is paused
    /// </summary>
    public bool IsAnyPaused => _orchestrators.Values.Any(o => o.IsPaused);
    
    /// <summary>
    /// Start specific services with their individual GPU allocations
    /// </summary>
    public async Task StartServicesAsync(Dictionary<ProcessingType, Dictionary<int, int>> serviceAllocations, ProcessingMode mode, CancellationToken ct = default)
    {
        _currentMode = mode;
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;
        
        try
        {
            // Start only the services in the dictionary (checkbox enabled) AND have non-empty queues
            var startTasks = new List<Task>();
            foreach (var (type, gpuAllocation) in serviceAllocations)
            {
                // Check queue count before creating orchestrator to avoid wasting resources
                int queueCount = await GetQueueCountForType(type);
                if (queueCount == 0)
                {
                    Logger.Log($"GlobalOrchestrator: Skipping {type} (queue empty)");
                    continue;
                }
                
                // Build allocation for this specific service
                var allocation = BuildServiceAllocation(gpuAllocation, type);
                
                // Get or create orchestrator for this service type
                // The orchestrator will validate allocation and decide which GPUs to use
                var orchestrator = GetOrCreateOrchestrator(type);
                
                var task = orchestrator.StartAsync(allocation, _globalCts.Token);
                startTasks.Add(task);
                Logger.Log($"GlobalOrchestrator: Starting {type} | Queue: {queueCount} | Workers: {allocation.TotalWorkers} on GPUs [{string.Join(", ", gpuAllocation.Where(kvp => kvp.Value > 0).Select(kvp => $"{kvp.Key}:{kvp.Value}"))}]");
            }
            
            if (startTasks.Count == 0)
            {
                Logger.Log("GlobalOrchestrator: No services to start (all queues empty)");
                _isRunning = false;
                return;
            }
            
            Logger.Log($"GlobalOrchestrator: Starting {startTasks.Count} service(s)");
            await Task.WhenAll(startTasks);
            
            Logger.Log("GlobalOrchestrator: All services completed");
            AllServicesCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Logger.Log("GlobalOrchestrator: Cancelled");
        }
        finally
        {
            _isRunning = false;
        }
    }
    
    /// <summary>
    /// Start all enabled services with GPU allocation (async wrapper) - LEGACY
    /// </summary>
    [Obsolete("Use StartServicesAsync with per-service allocations instead")]
    public async Task StartAllAsync(Dictionary<int, int> gpuAllocation, ProcessingMode mode, CancellationToken ct = default)
    {
        _currentMode = mode;
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;
        
        try
        {
            // Only start services that have non-zero GPU allocation (indicates they're enabled)
            var startTasks = new List<Task>();
            foreach (ProcessingType type in Enum.GetValues<ProcessingType>())
            {
                // Check if this service has any GPU allocation (i.e., it was enabled in UI)
                if (gpuAllocation.Values.Sum() == 0)
                    continue; // No GPUs allocated at all - skip
                
                // Create orchestrator only for this service type
                var orchestrator = GetOrCreateOrchestrator(type);
                
                var allocation = BuildServiceAllocation(gpuAllocation, type);
                
                // Only start if allocation has actual workers
                if (allocation.TotalWorkers > 0)
                {
                    var task = orchestrator.StartAsync(allocation, _globalCts.Token);
                    startTasks.Add(task);
                    Logger.Log($"GlobalOrchestrator: Starting {type} with {allocation.TotalWorkers} workers");
                }
            }
            
            if (startTasks.Count == 0)
            {
                Logger.Log("GlobalOrchestrator: No services to start (no allocations)");
                _isRunning = false;
                return;
            }
            
            Logger.Log($"GlobalOrchestrator: Starting {startTasks.Count} services");
            await Task.WhenAll(startTasks);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isRunning = false;
        }
    }
    
    /// <summary>
    /// Start a single service with GPU allocation
    /// </summary>
    public async Task StartServiceAsync(ProcessingType type, Dictionary<int, int> gpuAllocation, CancellationToken ct = default)
    {
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;
        _currentMode = ProcessingMode.Solo;
        
        try
        {
            var orchestrator = CreateOrchestrator(type);
            _orchestrators[type] = orchestrator;
            
            // Subscribe to events
            orchestrator.ProgressChanged += OnServiceProgressChanged;
            orchestrator.StatusChanged += OnServiceStatusChanged;
            orchestrator.Completed += (s, e) => OnServiceCompleted(type);
            
            var allocation = BuildServiceAllocation(gpuAllocation, type);
            await orchestrator.StartAsync(allocation, _globalCts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isRunning = false;
        }
    }
    
    /// <summary>
    /// Stop a single service
    /// </summary>
    public async Task StopServiceAsync(ProcessingType type)
    {
        if (_orchestrators.TryGetValue(type, out var orchestrator))
        {
            await orchestrator.StopAsync();
            _orchestrators.Remove(type);
        }
        
        if (_orchestrators.Count == 0)
        {
            _isRunning = false;
        }
    }
    
    #endregion
    
    #region Priority-Based Dynamic Allocation
    
    /// <summary>
    /// Start services with priority-based loading and dynamic reallocation.
    /// 
    /// Priority order (loads first → last):
    /// 1. Tagging (2.6GB) + FaceDetection (0.8GB) - fast, low VRAM
    /// 2. Embedding (7.6GB) - medium speed, medium VRAM
    /// 3. Captioning (5.6GB per instance) - slow, fills remaining VRAM
    /// 
    /// When a queue completes:
    /// - Its VRAM is freed
    /// - Additional captioning instances are loaded in the freed space
    /// </summary>
    public async Task StartWithDynamicAllocationAsync(
        bool enableTagging,
        bool enableCaptioning,
        bool enableEmbedding,
        bool enableFaceDetection,
        int defaultWorkersPerOnnxService = 8,
        CancellationToken ct = default)
    {
        if (_isRunning)
        {
            Logger.Log("GlobalOrchestrator: Already running");
            return;
        }
        
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;
        _currentMode = ProcessingMode.Concurrent;
        _enableDynamicReallocation = true;
        
        // Initialize VRAM tracker
        var vramCapacities = _settings.GpuVramCapacity
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, out var v) ? v : 32.0)
            .ToArray();
        
        _vramTracker = new GpuVramTracker(vramCapacities, _settings.MaxVramUsagePercent);
        
        var gpuIds = _settings.GpuDevices
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .ToArray();
        
        Logger.Log($"GlobalOrchestrator: Dynamic allocation starting");
        Logger.Log($"  GPUs: {string.Join(", ", gpuIds.Select((id, i) => $"GPU{id}={vramCapacities.ElementAtOrDefault(i):F0}GB"))}");
        Logger.Log($"  Max usage: {_settings.MaxVramUsagePercent}%");
        
        try
        {
            // Build list of enabled services with queue counts
            var enabledServices = new List<(ProcessingType Type, int QueueCount, int Priority)>();
            
            if (enableTagging)
            {
                var count = await GetQueueCountForType(ProcessingType.Tagging);
                if (count > 0) enabledServices.Add((ProcessingType.Tagging, count, ServiceVramUsage.GetPriority(ProcessingType.Tagging)));
            }
            if (enableFaceDetection)
            {
                var count = await GetQueueCountForType(ProcessingType.FaceDetection);
                if (count > 0) enabledServices.Add((ProcessingType.FaceDetection, count, ServiceVramUsage.GetPriority(ProcessingType.FaceDetection)));
            }
            if (enableEmbedding)
            {
                var count = await GetQueueCountForType(ProcessingType.Embedding);
                if (count > 0) enabledServices.Add((ProcessingType.Embedding, count, ServiceVramUsage.GetPriority(ProcessingType.Embedding)));
            }
            if (enableCaptioning)
            {
                var count = await GetQueueCountForType(ProcessingType.Captioning);
                if (count > 0) enabledServices.Add((ProcessingType.Captioning, count, ServiceVramUsage.GetPriority(ProcessingType.Captioning)));
            }
            
            if (enabledServices.Count == 0)
            {
                Logger.Log("GlobalOrchestrator: No services have items to process");
                _isRunning = false;
                return;
            }
            
            // Sort by priority (lower = higher priority)
            enabledServices = enabledServices.OrderBy(s => s.Priority).ToList();
            
            Logger.Log($"GlobalOrchestrator: Services to process: {string.Join(", ", enabledServices.Select(s => $"{s.Type}({s.QueueCount})"))}");
            
            // Phase 1: Load services by priority, respecting VRAM
            var serviceTasks = new Dictionary<ProcessingType, Task>();
            
            foreach (var (type, queueCount, priority) in enabledServices)
            {
                var allocation = CalculateDynamicAllocation(type, gpuIds, defaultWorkersPerOnnxService);
                
                if (allocation.TotalWorkers == 0)
                {
                    Logger.Log($"GlobalOrchestrator: {type} - no VRAM available, will load when space frees");
                    continue;
                }
                
                // Track VRAM usage per GPU for this service
                TrackVramAllocation(type, allocation);
                
                var orchestrator = GetOrCreateOrchestrator(type);
                
                // Subscribe to completion for dynamic reallocation
                orchestrator.Completed += async (s, e) => await OnServiceCompletedWithReallocation(type);
                
                var task = orchestrator.StartAsync(allocation, _globalCts.Token);
                serviceTasks[type] = task;
                
                Logger.Log($"GlobalOrchestrator: Started {type} | Queue: {queueCount} | Workers: {allocation.TotalWorkers} | VRAM: {_vramTracker.GetStatusString()}");
            }
            
            if (serviceTasks.Count == 0)
            {
                Logger.Log("GlobalOrchestrator: No services could be started (insufficient VRAM)");
                _isRunning = false;
                return;
            }
            
            // Wait for all services to complete
            await Task.WhenAll(serviceTasks.Values);
            
            Logger.Log("GlobalOrchestrator: All services completed");
            AllServicesCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Logger.Log("GlobalOrchestrator: Cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GlobalOrchestrator: Error - {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            _isRunning = false;
            _vramTracker = null;
            _serviceVramPerGpu.Clear();
        }
    }
    
    /// <summary>
    /// Calculate allocation for a service based on current VRAM availability.
    /// </summary>
    private ServiceAllocation CalculateDynamicAllocation(ProcessingType type, int[] gpuIds, int defaultWorkers)
    {
        if (_vramTracker == null)
            throw new InvalidOperationException("VRAM tracker not initialized");
        
        var gpuAllocations = new List<GpuAllocation>();
        var isOnnx = ServiceVramUsage.IsOnnxService(type);
        var vramNeeded = ServiceVramUsage.GetVramUsage(type);
        
        for (int i = 0; i < gpuIds.Length; i++)
        {
            var gpuId = gpuIds[i];
            var available = _vramTracker.GetAvailableVram(i);
            
            if (isOnnx)
            {
                // ONNX services: need fixed VRAM, can have multiple workers
                if (available >= vramNeeded)
                {
                    if (_vramTracker.TryAllocate(i, vramNeeded))
                    {
                        gpuAllocations.Add(new GpuAllocation
                        {
                            GpuId = gpuId,
                            WorkerCount = defaultWorkers,
                            ModelCount = 1,
                            VramCapacityGb = available + vramNeeded,
                            MaxUsagePercent = _settings.MaxVramUsagePercent
                        });
                    }
                }
            }
            else
            {
                // Captioning: each instance needs VRAM, 1 worker per instance
                var instanceCount = (int)(available / vramNeeded);
                if (instanceCount > 0)
                {
                    var vramToAllocate = instanceCount * vramNeeded;
                    if (_vramTracker.TryAllocate(i, vramToAllocate))
                    {
                        gpuAllocations.Add(new GpuAllocation
                        {
                            GpuId = gpuId,
                            WorkerCount = instanceCount,
                            ModelCount = instanceCount,
                            VramCapacityGb = available + vramToAllocate,
                            MaxUsagePercent = _settings.MaxVramUsagePercent
                        });
                    }
                }
            }
        }
        
        return new ServiceAllocation
        {
            ProcessingType = type,
            Mode = ProcessingMode.Concurrent,
            GpuAllocations = gpuAllocations.ToArray()
        };
    }
    
    /// <summary>
    /// Track VRAM allocation for a service (for later release)
    /// </summary>
    private void TrackVramAllocation(ProcessingType type, ServiceAllocation allocation)
    {
        if (_vramTracker == null) return;
        
        var vramPerGpu = new double[_vramTracker.GpuCount];
        var isOnnx = ServiceVramUsage.IsOnnxService(type);
        var vramPerModel = ServiceVramUsage.GetVramUsage(type);
        
        foreach (var gpuAlloc in allocation.GpuAllocations)
        {
            var gpuIndex = gpuAlloc.GpuId; // Assuming GPU ID = index for now
            if (gpuIndex < vramPerGpu.Length)
            {
                if (isOnnx)
                {
                    vramPerGpu[gpuIndex] = vramPerModel; // Fixed
                }
                else
                {
                    vramPerGpu[gpuIndex] = gpuAlloc.ModelCount * vramPerModel; // Scales
                }
            }
        }
        
        _serviceVramPerGpu[type] = vramPerGpu;
    }
    
    /// <summary>
    /// Handle service completion with dynamic reallocation to captioning.
    /// </summary>
    private async Task OnServiceCompletedWithReallocation(ProcessingType completedType)
    {
        Logger.Log($"GlobalOrchestrator: {completedType} completed, checking for reallocation");
        
        // Don't reallocate if disabled or if captioning is what completed
        if (!_enableDynamicReallocation || completedType == ProcessingType.Captioning)
        {
            OnServiceCompleted(completedType);
            return;
        }
        
        await _reallocationLock.WaitAsync();
        try
        {
            // Release VRAM from completed service
            if (_serviceVramPerGpu.TryRemove(completedType, out var releasedVram) && _vramTracker != null)
            {
                for (int i = 0; i < releasedVram.Length; i++)
                {
                    if (releasedVram[i] > 0)
                    {
                        _vramTracker.Release(i, releasedVram[i]);
                        Logger.Log($"GlobalOrchestrator: Released {releasedVram[i]:F1}GB from GPU{i} ({completedType})");
                    }
                }
            }
            
            // Check if captioning is still running and has items remaining
            if (_orchestrators.TryGetValue(ProcessingType.Captioning, out var captioningOrchestrator))
            {
                if (captioningOrchestrator.QueueRemaining > 0)
                {
                    // Try to load additional captioning instances on freed GPUs
                    await TryExpandCaptioningAsync();
                }
            }
            else if (_vramTracker != null)
            {
                // Captioning wasn't started initially (no VRAM), try starting it now
                var queueCount = await GetQueueCountForType(ProcessingType.Captioning);
                if (queueCount > 0)
                {
                    Logger.Log($"GlobalOrchestrator: Starting captioning now that VRAM is available (queue: {queueCount})");
                    await TryStartCaptioningAsync(queueCount);
                }
            }
        }
        finally
        {
            _reallocationLock.Release();
            OnServiceCompleted(completedType);
        }
    }
    
    /// <summary>
    /// Try to expand captioning with additional instances on freed VRAM.
    /// </summary>
    private async Task TryExpandCaptioningAsync()
    {
        if (_vramTracker == null || _globalCts == null) return;
        
        var gpuIds = _settings.GpuDevices
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .ToArray();
        
        // Calculate how many new instances we can add
        var newInstances = new Dictionary<int, int>();
        for (int i = 0; i < gpuIds.Length && i < _vramTracker.GpuCount; i++)
        {
            var available = _vramTracker.GetAvailableVram(i);
            var count = (int)(available / ServiceVramUsage.Captioning);
            if (count > 0)
            {
                newInstances[gpuIds[i]] = count;
                _vramTracker.TryAllocate(i, count * ServiceVramUsage.Captioning);
                Logger.Log($"GlobalOrchestrator: Can add {count} captioning instance(s) on GPU{gpuIds[i]} ({available:F1}GB available)");
            }
        }
        
        if (newInstances.Count == 0)
        {
            Logger.Log("GlobalOrchestrator: No additional VRAM available for captioning expansion");
            return;
        }
        
        // TODO: Implement hot-adding workers to running orchestrator
        // For now, log what we could do
        Logger.Log($"GlobalOrchestrator: Could expand captioning by {newInstances.Values.Sum()} instances (hot-add not yet implemented)");
        
        // Future: Call orchestrator.AddWorkersAsync(newInstances) to dynamically add workers
    }
    
    /// <summary>
    /// Try to start captioning service that was deferred due to VRAM constraints.
    /// </summary>
    private async Task TryStartCaptioningAsync(int queueCount)
    {
        if (_vramTracker == null || _globalCts == null) return;
        
        var gpuIds = _settings.GpuDevices
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .ToArray();
        
        var allocation = CalculateDynamicAllocation(ProcessingType.Captioning, gpuIds, 1);
        
        if (allocation.TotalWorkers == 0)
        {
            Logger.Log("GlobalOrchestrator: Still insufficient VRAM for captioning");
            return;
        }
        
        TrackVramAllocation(ProcessingType.Captioning, allocation);
        
        var orchestrator = GetOrCreateOrchestrator(ProcessingType.Captioning);
        orchestrator.Completed += async (s, e) => await OnServiceCompletedWithReallocation(ProcessingType.Captioning);
        
        Logger.Log($"GlobalOrchestrator: Starting deferred captioning | Queue: {queueCount} | Workers: {allocation.TotalWorkers}");
        
        // Start but don't await - let it run alongside any remaining services
        _ = orchestrator.StartAsync(allocation, _globalCts.Token);
    }
    
    #endregion
    
    /// <summary>
    /// Stop all services (async)
    /// </summary>
    public async Task StopAllAsync()
    {
        await StopAsync();
    }
    
    /// <summary>
    /// Pause all services (async)
    /// </summary>
    public Task PauseAllAsync()
    {
        PauseAll();
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Resume all services (async)
    /// </summary>
    public Task ResumeAllAsync()
    {
        ResumeAll();
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Pause a specific service (async)
    /// </summary>
    public Task PauseServiceAsync(ProcessingType type)
    {
        Pause(type);
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Resume a specific service (async)
    /// </summary>
    public Task ResumeServiceAsync(ProcessingType type)
    {
        Resume(type);
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Get orchestrator by type
    /// </summary>
    public IServiceOrchestrator? GetOrchestrator(ProcessingType type)
    {
        return _orchestrators.TryGetValue(type, out var orch) ? orch : null;
    }
    
    /// <summary>
    /// Get or create orchestrator for a specific type
    /// </summary>
    private IServiceOrchestrator GetOrCreateOrchestrator(ProcessingType type)
    {
        if (!_orchestrators.ContainsKey(type))
        {
            var orchestrator = CreateOrchestrator(type);
            _orchestrators[type] = orchestrator;
            orchestrator.ProgressChanged += OnServiceProgressChanged;
            orchestrator.StatusChanged += OnServiceStatusChanged;
            orchestrator.Completed += (s, e) => OnServiceCompleted(type);
        }
        return _orchestrators[type];
    }
    
    private void EnsureOrchestratorsCreated()
    {
        foreach (ProcessingType type in Enum.GetValues<ProcessingType>())
        {
            if (!_orchestrators.ContainsKey(type))
            {
                var orchestrator = CreateOrchestrator(type);
                _orchestrators[type] = orchestrator;
                orchestrator.ProgressChanged += OnServiceProgressChanged;
                orchestrator.StatusChanged += OnServiceStatusChanged;
                orchestrator.Completed += (s, e) => OnServiceCompleted(type);
            }
        }
    }
    
    /// <summary>
    /// Get queue count for a specific processing type
    /// </summary>
    private async Task<int> GetQueueCountForType(ProcessingType type)
    {
        return type switch
        {
            ProcessingType.Tagging => await _dataStore.CountImagesNeedingTagging(),
            ProcessingType.Captioning => await _dataStore.CountImagesNeedingCaptioning(),
            ProcessingType.Embedding => await _dataStore.CountImagesNeedingEmbedding(),
            ProcessingType.FaceDetection => await _dataStore.CountImagesNeedingFaceDetection(),
            _ => 0
        };
    }
    
    private ServiceAllocation BuildServiceAllocation(Dictionary<int, int> gpuAllocation, ProcessingType type)
    {
        // For ONNX services (Tagging, Embedding, FaceDetection):
        //   - Allocation value = worker count (all share 1 model)
        //   - ModelCount = 1 per GPU with any workers
        // For LLM services (Captioning):
        //   - Allocation value = model count (1 worker per model)
        //   - ModelCount = WorkerCount (1:1 ratio)
        
        var isOnnxService = type is ProcessingType.Tagging 
                                 or ProcessingType.Embedding 
                                 or ProcessingType.FaceDetection;
        
        var gpuAllocations = gpuAllocation
            .Where(kvp => kvp.Value > 0)
            .Select(kvp => new GpuAllocation
            {
                GpuId = kvp.Key,
                WorkerCount = kvp.Value,
                // ONNX: 1 model shared by N workers. Captioning: N models for N workers.
                ModelCount = isOnnxService ? 1 : kvp.Value,
                VramCapacityGb = GetVramForGpu(kvp.Key),
                MaxUsagePercent = _settings.MaxVramUsagePercent
            })
            .ToArray();
        
        return new ServiceAllocation
        {
            ProcessingType = type,
            Mode = _currentMode,
            GpuAllocations = gpuAllocations
        };
    }
    
    private double GetVramForGpu(int gpuId)
    {
        var vramValues = _settings.GpuVramCapacity
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, out var v) ? v : 32.0)
            .ToArray();
        
        return gpuId < vramValues.Length ? vramValues[gpuId] : 32.0;
    }
    
    /// <summary>
    /// Get aggregated progress across all services
    /// </summary>
    public (int TotalProgress, int TotalItems, int TotalRemaining) GetAggregatedProgress()
    {
        int totalProgress = 0;
        int totalItems = 0;
        int totalRemaining = 0;
        
        foreach (var orchestrator in _orchestrators.Values)
        {
            totalProgress += orchestrator.Progress;
            totalItems += orchestrator.Total;
            totalRemaining += orchestrator.QueueRemaining;
        }
        
        return (totalProgress, totalItems, totalRemaining);
    }
    
    #region Allocation Parsing
    
    /// <summary>
    /// Get VRAM allocation for a service based on mode and settings
    /// </summary>
    private ServiceAllocation GetAllocation(ProcessingType type, ProcessingMode mode)
    {
        // Parse GPU devices
        var gpuIds = _settings.GpuDevices
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .ToArray();
        
        // Parse VRAM capacities
        var vramCapacities = _settings.GpuVramCapacity
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, out var v) ? v : 32.0)
            .ToArray();
        
        // Get allocation string based on service type and mode
        var allocationString = GetAllocationString(type, mode);
        
        // Parse model counts per GPU
        var modelCounts = allocationString
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var c) ? c : 0)
            .ToArray();
        
        // Build GPU allocations
        var gpuAllocations = new List<GpuAllocation>();
        for (int i = 0; i < gpuIds.Length; i++)
        {
            var modelCount = i < modelCounts.Length ? modelCounts[i] : 0;
            if (modelCount > 0)
            {
                gpuAllocations.Add(new GpuAllocation
                {
                    GpuId = gpuIds[i],
                    ModelCount = modelCount,
                    VramCapacityGb = i < vramCapacities.Length ? vramCapacities[i] : 32.0,
                    MaxUsagePercent = _settings.MaxVramUsagePercent
                });
            }
        }
        
        return new ServiceAllocation
        {
            ProcessingType = type,
            Mode = mode,
            GpuAllocations = gpuAllocations.ToArray()
        };
    }
    
    /// <summary>
    /// Get the allocation string from settings for a given service type and mode
    /// </summary>
    private string GetAllocationString(ProcessingType type, ProcessingMode mode)
    {
        return (type, mode) switch
        {
            (ProcessingType.Tagging, ProcessingMode.Concurrent) => _settings.ConcurrentTaggingAllocation,
            (ProcessingType.Tagging, ProcessingMode.Solo) => _settings.SoloTaggingAllocation,
            (ProcessingType.Captioning, ProcessingMode.Concurrent) => _settings.ConcurrentCaptioningAllocation,
            (ProcessingType.Captioning, ProcessingMode.Solo) => _settings.SoloCaptioningAllocation,
            (ProcessingType.Embedding, ProcessingMode.Concurrent) => _settings.ConcurrentEmbeddingAllocation,
            (ProcessingType.Embedding, ProcessingMode.Solo) => _settings.SoloEmbeddingAllocation,
            (ProcessingType.FaceDetection, ProcessingMode.Concurrent) => _settings.ConcurrentFaceDetectionAllocation,
            (ProcessingType.FaceDetection, ProcessingMode.Solo) => _settings.SoloFaceDetectionAllocation,
            _ => "1,0"
        };
    }
    
    #endregion
    
    #region Factory
    
    /// <summary>
    /// Create the appropriate orchestrator for a service type
    /// </summary>
    private IServiceOrchestrator CreateOrchestrator(ProcessingType type)
    {
        return type switch
        {
            ProcessingType.Tagging => new TaggingOrchestrator(_dataStore, _settings),
            ProcessingType.Captioning => new CaptioningOrchestrator(_dataStore, _settings),
            ProcessingType.Embedding => new EmbeddingOrchestrator(_dataStore, _settings),
            ProcessingType.FaceDetection => new FaceDetectionOrchestrator(_dataStore, _settings),
            _ => throw new ArgumentException($"Unknown processing type: {type}")
        };
    }
    
    #endregion
    
    #region Event Handlers
    
    private void OnServiceProgressChanged(object? sender, ProcessingProgressEventArgs e)
    {
        // Forward to subscribers
        ProgressChanged?.Invoke(this, e);
    }
    
    private void OnServiceStatusChanged(object? sender, ProcessingStatusEventArgs e)
    {
        // Forward to subscribers
        StatusChanged?.Invoke(this, e);
    }
    
    private void OnServiceCompleted(ProcessingType type)
    {
        Logger.Log($"GlobalOrchestrator: {type} completed");
        ServiceCompleted?.Invoke(this, type);
        
        // Remove from active orchestrators
        if (_orchestrators.TryGetValue(type, out var orchestrator))
        {
            orchestrator.ProgressChanged -= OnServiceProgressChanged;
            orchestrator.StatusChanged -= OnServiceStatusChanged;
            orchestrator.Dispose();
            _orchestrators.Remove(type);
        }
        
        // Check if all done
        if (_orchestrators.Count == 0)
        {
            AllServicesCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
    
    #endregion
    
    #region IDisposable
    
    public void Dispose()
    {
        StopAsync().Wait(TimeSpan.FromSeconds(10));
        
        foreach (var orchestrator in _orchestrators.Values)
        {
            orchestrator.Dispose();
        }
        _orchestrators.Clear();
        
        _globalCts?.Dispose();
        Logger.Log("GlobalOrchestrator: Disposed");
    }
    
    #endregion
}
