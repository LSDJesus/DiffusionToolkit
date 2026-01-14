using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.FaceDetection.Services;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Background service for face detection using persistent worker pools
/// </summary>
public class BackgroundFaceDetectionService : IDisposable
{
    private readonly PostgreSQLDataStore _dataStore;
    
    // Face detection orchestrator
    private Channel<int>? _faceDetectionQueue;
    private CancellationTokenSource? _faceDetectionCts;
    private List<Task>? _faceDetectionOrchestrators;
    
    private int _faceDetectionProgress;
    private int _faceDetectionTotal;
    private int _faceDetectionSkipped;
    private int _totalFacesDetected;
    private bool _isFaceDetectionRunning;
    private bool _isFaceDetectionPaused;
    
    // ETA tracking
    private DateTime _faceDetectionStartTime;
    private int _faceDetectionQueueRemaining;

    public int FaceDetectionQueueRemaining => _faceDetectionQueueRemaining;

    public event EventHandler<ProgressEventArgs>? FaceDetectionProgressChanged;
    public event EventHandler? FaceDetectionCompleted;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? QueueCountChanged;

    public bool IsFaceDetectionRunning => _isFaceDetectionRunning;
    public bool IsFaceDetectionPaused => _isFaceDetectionPaused;
    public int FaceDetectionProgress => _faceDetectionProgress;
    public int FaceDetectionTotal => _faceDetectionTotal;
    public int FaceDetectionSkipped => _faceDetectionSkipped;
    public int TotalFacesDetected => _totalFacesDetected;

    public BackgroundFaceDetectionService(PostgreSQLDataStore dataStore)
    {
        _dataStore = dataStore;
        
        // Register cleanup on app exit
        Application.Current.Exit += OnApplicationExit;
    }

    private void OnApplicationExit(object sender, ExitEventArgs e)
    {
        StopFaceDetection();
    }

    /// <summary>
    /// Refresh face detection queue count from the database and notify UI
    /// </summary>
    public async Task RefreshQueueCountAsync()
    {
        try
        {
            var (_, _, _, faceDetection) = await _dataStore.GetPendingCountsAsync();
            _faceDetectionQueueRemaining = faceDetection;
            RaiseQueueCountChanged();
            Logger.Log($"Face detection queue count refreshed: {faceDetection}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error refreshing face detection queue count: {ex.Message}");
        }
    }

    private void RaiseFaceDetectionProgress(int current, int total)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            FaceDetectionProgressChanged?.Invoke(this, new ProgressEventArgs(current, total));
        });
    }

    private void RaiseQueueCountChanged()
    {
        try
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                try
                {
                    QueueCountChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error in QueueCountChanged event: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Error raising QueueCountChanged: {ex.Message}");
        }
    }

    private void RaiseFaceDetectionCompleted()
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            FaceDetectionCompleted?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RaiseStatusChanged(string status)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            StatusChanged?.Invoke(this, status);
        });
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
            return new List<(int, int)> { (0, 2) };
        }
        
        // Get optimal plan from orchestrator
        var fullPlan = orchestrator.GetOptimalAllocationPlan();
        
        // Filter to this process type and aggregate workers per GPU
        var filteredPlan = fullPlan
            .Where(p => p.Priority == processType)
            .GroupBy(p => p.GpuId)
            .Select(g => (GpuId: g.Key, WorkerCount: g.Sum(x => x.WorkerCount)))
            .ToList();
        
        if (filteredPlan.Count == 0)
        {
            // No allocation available - try to get at least one
            return new List<(int, int)> { (0, 2) };
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

    /// <summary>
    /// Start background face detection with multi-GPU worker pool - uses GPU orchestrator
    /// </summary>
    public void StartFaceDetection()
    {
        if (_isFaceDetectionRunning)
        {
            Logger.Log("Face detection already running");
            return;
        }

        try
        {
            _faceDetectionCts = new CancellationTokenSource();
            _isFaceDetectionRunning = true;
            _isFaceDetectionPaused = false;
            _faceDetectionProgress = 0;
            _faceDetectionSkipped = 0;
            _totalFacesDetected = 0;
            _faceDetectionStartTime = DateTime.Now;
            _faceDetectionQueueRemaining = 0;
            
            // Get allocation plan from GPU orchestrator
            var allocationPlan = GetAllocationPlan(ProcessPriority.FaceDetection);
            
            if (allocationPlan.Count == 0)
            {
                Logger.Log("No GPU allocation available for face detection");
                _isFaceDetectionRunning = false;
                return;
            }
            
            var totalWorkers = allocationPlan.Sum(a => a.WorkerCount);
            
            Logger.Log($"Face detection allocation plan: {string.Join(", ", allocationPlan.Select(a => $"GPU{a.GpuId}={a.WorkerCount} workers"))}");
            
            // Create unbounded channel for shared work queue
            _faceDetectionQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });
            
            // Queue population task using cursor-based pagination
            Task.Run(async () =>
            {
                try
                {
                    _faceDetectionTotal = await _dataStore.CountImagesNeedingFaceDetection();
                    _faceDetectionQueueRemaining = _faceDetectionTotal;
                    RaiseQueueCountChanged();
                    Logger.Log($"Found {_faceDetectionTotal} images for face detection");
                    
                    // Update orchestrator with queue status
                    UpdateOrchestratorStatus(ProcessPriority.FaceDetection, _faceDetectionTotal, 0, totalWorkers, true);
                    
                    const int batchSize = 500;
                    int lastId = 0;  // Cursor for pagination
                    int totalQueued = 0;
                    
                    while (!_faceDetectionCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingFaceDetection(batchSize, lastId);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _faceDetectionQueue.Writer.WriteAsync(imageId, _faceDetectionCts.Token);
                            totalQueued++;
                        }
                        
                        // Move cursor to last ID in batch
                        lastId = batch[batch.Count - 1];
                        
                        if (batch.Count < batchSize) break;  // Last batch
                    }
                    
                    _faceDetectionQueue.Writer.Complete();
                    Logger.Log($"Face detection queue populated with {totalQueued} images");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Face detection queue population error: {ex.Message}");
                }
            });
            
            // Create workers based on allocation plan
            _faceDetectionOrchestrators = new List<Task>();
            int workerIndex = 0;
            
            foreach (var (gpuId, workerCount) in allocationPlan)
            {
                Logger.Log($"GPU {gpuId}: Creating {workerCount} face detection workers");
                
                for (int w = 0; w < workerCount; w++)
                {
                    int currentWorkerIndex = workerIndex++;
                    var workerTask = Task.Run(async () =>
                    {
                        try
                        {
                            await RunFaceDetectionWorker(gpuId, currentWorkerIndex, _faceDetectionCts.Token);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"GPU {gpuId} face detection worker {currentWorkerIndex} error: {ex.Message}\n{ex.StackTrace}");
                        }
                    });
                    
                    _faceDetectionOrchestrators.Add(workerTask);
                }
            }
            
            Logger.Log($"Started {totalWorkers} face detection workers via GPU orchestrator");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start face detection: {ex.Message}\n{ex.StackTrace}");
            _isFaceDetectionRunning = false;            UpdateOrchestratorStatus(ProcessPriority.FaceDetection, 0, 0, 0, false);        }
    }

    public void PauseFaceDetection()
    {
        _isFaceDetectionPaused = true;
        Logger.Log("Paused face detection");
    }

    public void ResumeFaceDetection()
    {
        _isFaceDetectionPaused = false;
        Logger.Log("Resumed face detection");
    }

    public void StopFaceDetection()
    {
        Logger.Log("Stopping face detection...");
        _isFaceDetectionRunning = false;
        _faceDetectionCts?.Cancel();
        
        try
        {
            _faceDetectionQueue?.Writer.Complete();
        }
        catch (ChannelClosedException)
        {
            // Already closed, ignore
        }
        
        try
        {
            if (_faceDetectionOrchestrators != null)
            {
                Task.WaitAll(_faceDetectionOrchestrators.ToArray(), TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception while stopping face detection: {ex.Message}");
        }
        
        _faceDetectionOrchestrators?.Clear();
        
        // Notify orchestrator that face detection is stopped
        UpdateOrchestratorStatus(ProcessPriority.FaceDetection, _faceDetectionTotal, _faceDetectionProgress, 0, false);
        ServiceLocator.GpuOrchestrator?.MarkQueueCompleted(ProcessPriority.FaceDetection);
        
        Logger.Log("Face detection stopped");
    }

    /// <summary>
    /// Face detection worker: Creates its own FaceDetectionService for exclusive use
    /// </summary>
    private async Task RunFaceDetectionWorker(int gpuId, int workerId, CancellationToken ct)
    {
        Logger.Log($"Face detection worker {workerId} started on GPU {gpuId}");
        
        FaceDetectionService? faceService = null;
        
        try
        {
            // Create face detection service for this worker
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var config = FaceDetectionConfig.CreateDefault(baseDir);
            config.GpuDeviceId = gpuId;
            config.ConfidenceThreshold = ServiceLocator.Settings?.FaceDetectionConfidenceThreshold ?? 0.5f;
            
            // Check if models exist
            if (!System.IO.File.Exists(config.YoloModelPath))
            {
                Logger.Log($"YOLO11 model not found: {config.YoloModelPath}");
                return;
            }
            if (!System.IO.File.Exists(config.ArcFaceModelPath))
            {
                Logger.Log($"ArcFace model not found: {config.ArcFaceModelPath}");
                return;
            }
            
            faceService = FaceDetectionService.FromConfig(config);
            Logger.Log($"GPU {gpuId} face detection worker {workerId}: Service initialized");
            
            // Process images from queue
            await foreach (var imageId in _faceDetectionQueue!.Reader.ReadAllAsync(ct))
            {
                // Check if paused
                while (_isFaceDetectionPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                
                if (ct.IsCancellationRequested)
                    break;
                
                try
                {
                    await ProcessImageFaceDetection(imageId, faceService);
                    
                    var progress = Interlocked.Increment(ref _faceDetectionProgress);
                    var remaining = Interlocked.Decrement(ref _faceDetectionQueueRemaining);
                    
                    RaiseFaceDetectionProgress(progress, _faceDetectionTotal);
                    
                    // Calculate ETA and update status
                    string status;
                    if (progress >= 3 && remaining > 0)
                    {
                        var elapsed = DateTime.Now - _faceDetectionStartTime;
                        var avgTimePerImage = elapsed.TotalSeconds / progress;
                        var etaSeconds = avgTimePerImage * remaining;
                        var eta = TimeSpan.FromSeconds(etaSeconds);
                        var imgPerSec = progress / elapsed.TotalSeconds;
                        
                        status = $"Faces: {progress}/{_faceDetectionTotal} | Found: {_totalFacesDetected} | {imgPerSec:F1} img/s | ETA: {eta:hh\\:mm\\:ss}";
                    }
                    else
                    {
                        status = $"Faces: {progress}/{_faceDetectionTotal} | Found: {_totalFacesDetected}";
                    }
                    
                    RaiseStatusChanged(status);
                }
                catch (Exception ex)
                {
                    Logger.Log($"GPU {gpuId} face detection worker {workerId} error processing image {imageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"GPU {gpuId} face detection worker {workerId} cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"GPU {gpuId} face detection worker {workerId} fatal error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            faceService?.Dispose();
            Logger.Log($"GPU {gpuId} face detection worker {workerId} exiting");
            RaiseFaceDetectionCompleted();
        }
    }

    private async Task ProcessImageFaceDetection(int imageId, FaceDetectionService faceService)
    {
        try
        {
            var image = _dataStore.GetImage(imageId);
            if (image == null)
            {
                Logger.Log($"Image {imageId} not found, skipping face detection");
                Interlocked.Increment(ref _faceDetectionSkipped);
                await _dataStore.SetNeedsFaceDetection(new List<int> { imageId }, false);
                return;
            }

            if (!System.IO.File.Exists(image.Path))
            {
                Logger.Log($"Image file not found: {image.Path}, skipping");
                Interlocked.Increment(ref _faceDetectionSkipped);
                await _dataStore.SetNeedsFaceDetection(new List<int> { imageId }, false);
                return;
            }

            // Detect faces
            var results = await faceService.ProcessImageAsync(image.Path);
            
            if (results.ErrorMessage != null)
            {
                Logger.Log($"Face detection error for {image.Path}: {results.ErrorMessage}");
                Interlocked.Increment(ref _faceDetectionSkipped);
                await _dataStore.SetNeedsFaceDetection(new List<int> { imageId }, false);
                return;
            }

            // Store each detected face
            var storeFaceCrops = ServiceLocator.Settings?.StoreFaceCrops ?? true;
            var autoCluster = ServiceLocator.Settings?.AutoClusterFaces ?? true;
            var clusterThreshold = ServiceLocator.Settings?.FaceClusterThreshold ?? 0.6f;
            
            var detectedCharacters = new List<string>();
            
            foreach (var face in results.Faces)
            {
                // Store face detection with multi-model embeddings
                var faceId = await _dataStore.StoreFaceDetectionAsync(
                    imageId,
                    face.X, face.Y, face.Width, face.Height,
                    storeFaceCrops ? face.FaceCrop : null,
                    face.CropWidth, face.CropHeight,
                    face.ArcFaceEmbedding,
                    face.ClipFaceEmbedding,
                    face.StyleType,
                    face.DetectionModel,
                    face.Confidence, face.QualityScore, face.SharpnessScore,
                    face.PoseYaw, face.PosePitch, face.PoseRoll,
                    face.Landmarks != null ? System.Text.Json.JsonSerializer.Serialize(face.Landmarks) : null);
                
                Interlocked.Increment(ref _totalFacesDetected);
                
                // Auto-cluster if enabled - use style-aware search
                if (autoCluster && (face.ArcFaceEmbedding != null || face.ClipFaceEmbedding != null))
                {
                    var similarFaces = await _dataStore.FindSimilarFacesStyleAware(
                        face.ArcFaceEmbedding,
                        face.ClipFaceEmbedding,
                        face.StyleType,
                        clusterThreshold, 
                        1);
                    
                    if (similarFaces.Count > 0)
                    {
                        var existingFace = await _dataStore.GetFacesForImage(similarFaces[0].faceId);
                        var clusterId = existingFace.FirstOrDefault(f => f.FaceGroupId.HasValue)?.FaceGroupId;
                        
                        if (clusterId.HasValue)
                        {
                            await _dataStore.AssignFaceToCluster(faceId, clusterId.Value);
                            var cluster = (await _dataStore.GetAllClusters()).FirstOrDefault(c => c.Id == clusterId.Value);
                            if (cluster?.Name != null)
                            {
                                detectedCharacters.Add(cluster.Name);
                            }
                        }
                    }
                }
            }
            
            // Update image with face info
            var primaryCharacter = detectedCharacters.FirstOrDefault();
            var uniqueCharacters = detectedCharacters.Distinct().ToArray();
            
            await _dataStore.UpdateImageFaceInfo(
                imageId, 
                results.Faces.Count, 
                primaryCharacter, 
                uniqueCharacters.Length > 0 ? uniqueCharacters : null);
            
            Logger.Log($"Face detection for image {imageId}: {results.Faces.Count} faces in {results.ProcessingTimeMs:F0}ms");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error in face detection for image {imageId}: {ex.Message}");
            Interlocked.Increment(ref _faceDetectionSkipped);
        }
    }

    public void Dispose()
    {
        StopFaceDetection();
        Application.Current.Exit -= OnApplicationExit;
    }
}
