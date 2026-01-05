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

    private void RaiseFaceDetectionProgress(int current, int total)
    {
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            FaceDetectionProgressChanged?.Invoke(this, new ProgressEventArgs(current, total));
        });
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
    /// Start background face detection with multi-GPU worker pool
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
            
            var gpuDevices = ServiceLocator.Settings?.FaceDetectionGpuDevices ?? "0";
            var totalWorkers = ServiceLocator.Settings?.FaceDetectionConcurrentWorkers ?? 4;
            
            // Parse GPU device IDs
            var deviceIds = gpuDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(d => int.TryParse(d, out var id) ? id : 0)
                                       .Distinct()
                                       .ToList();
            
            if (deviceIds.Count == 0) deviceIds.Add(0);
            
            // Distribute workers across GPUs
            var workersPerGpu = totalWorkers / deviceIds.Count;
            var extraWorkers = totalWorkers % deviceIds.Count;
            
            // Create unbounded channel for shared work queue
            _faceDetectionQueue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });
            
            // Queue population task
            Task.Run(async () =>
            {
                try
                {
                    _faceDetectionTotal = await _dataStore.CountImagesNeedingFaceDetection();
                    _faceDetectionQueueRemaining = _faceDetectionTotal;
                    Logger.Log($"Found {_faceDetectionTotal} images for face detection");
                    
                    const int batchSize = 500;
                    while (!_faceDetectionCts.Token.IsCancellationRequested)
                    {
                        var batch = await _dataStore.GetImagesNeedingFaceDetection(batchSize);
                        if (batch.Count == 0) break;
                        
                        foreach (var imageId in batch)
                        {
                            await _faceDetectionQueue.Writer.WriteAsync(imageId, _faceDetectionCts.Token);
                        }
                        
                        if (batch.Count < batchSize) break;
                    }
                    
                    _faceDetectionQueue.Writer.Complete();
                    Logger.Log("Face detection queue populated");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Face detection queue population error: {ex.Message}");
                }
            });
            
            // Create workers
            _faceDetectionOrchestrators = new List<Task>();
            int workerIndex = 0;
            
            for (int i = 0; i < deviceIds.Count; i++)
            {
                int gpuId = deviceIds[i];
                int workersOnGpu = workersPerGpu + (i < extraWorkers ? 1 : 0);
                
                Logger.Log($"GPU {gpuId}: Creating {workersOnGpu} face detection workers");
                
                for (int w = 0; w < workersOnGpu; w++)
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
            
            Logger.Log($"Started {totalWorkers} face detection workers across {deviceIds.Count} GPU(s)");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start face detection: {ex.Message}\n{ex.StackTrace}");
            _isFaceDetectionRunning = false;
        }
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
                // Store face detection
                var faceId = await _dataStore.StoreFaceDetectionAsync(
                    imageId,
                    face.X, face.Y, face.Width, face.Height,
                    storeFaceCrops ? face.FaceCrop : null,
                    face.CropWidth, face.CropHeight,
                    face.ArcFaceEmbedding,
                    face.DetectionModel,
                    face.Confidence, face.QualityScore, face.SharpnessScore,
                    face.PoseYaw, face.PosePitch, face.PoseRoll,
                    face.Landmarks != null ? System.Text.Json.JsonSerializer.Serialize(face.Landmarks) : null);
                
                Interlocked.Increment(ref _totalFacesDetected);
                
                // Auto-cluster if enabled
                if (autoCluster && face.ArcFaceEmbedding != null)
                {
                    var similarFaces = await _dataStore.FindSimilarFaces(
                        face.ArcFaceEmbedding, 
                        clusterThreshold, 
                        1);
                    
                    if (similarFaces.Count > 0)
                    {
                        var existingFace = await _dataStore.GetFacesForImage(similarFaces[0].imageId);
                        var clusterId = existingFace.FirstOrDefault(f => f.FaceClusterId.HasValue)?.FaceClusterId;
                        
                        if (clusterId.HasValue)
                        {
                            await _dataStore.AssignFaceToCluster(faceId, clusterId.Value);
                            var cluster = (await _dataStore.GetAllClusters()).FirstOrDefault(c => c.Id == clusterId.Value);
                            if (cluster?.Label != null)
                            {
                                detectedCharacters.Add(cluster.Label);
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
