using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Global orchestrator that coordinates all processing services.
/// Responsibilities:
/// - Decides which services to start based on UI settings
/// - Determines processing mode (Solo vs Concurrent)
/// - Reads VRAM allocation settings and passes to service orchestrators
/// - Aggregates progress from all services for UI display
/// - Handles global start/stop/pause commands
/// </summary>
public class GlobalProcessingOrchestrator : IDisposable
{
    private readonly PostgreSQLDataStore _dataStore;
    private readonly Settings _settings;
    
    // Service orchestrators
    private readonly Dictionary<ProcessingType, IServiceOrchestrator> _orchestrators = new();
    private CancellationTokenSource? _globalCts;
    
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
    /// Start all enabled services with GPU allocation (async wrapper)
    /// </summary>
    public async Task StartAllAsync(Dictionary<int, int> gpuAllocation, ProcessingMode mode, CancellationToken ct = default)
    {
        _currentMode = mode;
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;
        
        try
        {
            // Ensure all orchestrators are created
            EnsureOrchestratorsCreated();
            
            // Start all with the given allocation
            var startTasks = new List<Task>();
            foreach (var (type, orchestrator) in _orchestrators)
            {
                var allocation = BuildServiceAllocation(gpuAllocation, type);
                var task = orchestrator.StartAsync(allocation, _globalCts.Token);
                startTasks.Add(task);
            }
            
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
    
    private ServiceAllocation BuildServiceAllocation(Dictionary<int, int> gpuAllocation, ProcessingType type)
    {
        var gpuAllocations = gpuAllocation
            .Where(kvp => kvp.Value > 0)
            .Select(kvp => new GpuAllocation
            {
                GpuId = kvp.Key,
                ModelCount = kvp.Value,
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
    
    #endregion
    
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
