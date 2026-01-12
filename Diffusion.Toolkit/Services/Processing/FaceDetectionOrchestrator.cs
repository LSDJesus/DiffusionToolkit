using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Common.Models;
using Diffusion.Database.PostgreSQL;
using Diffusion.FaceDetection.Services;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Face detection model pool - holds YOLO and ArcFace models for a specific GPU.
/// Thread-safe: ONNX Runtime with CUDA EP supports concurrent Run() calls.
/// Multiple workers can share one pool.
/// </summary>
public class FaceDetectionModelPool : IModelPool
{
    private readonly Settings _settings;
    private FaceDetectionService? _detectionService;
    private bool _isReady;
    
    public int GpuId { get; }
    public bool IsReady => _isReady;
    public double VramUsageGb => 0.8; // YOLO (~0.3GB) + ArcFace (~0.5GB)
    
    public FaceDetectionService? DetectionService => _detectionService;
    
    public FaceDetectionModelPool(int gpuId, Settings settings)
    {
        GpuId = gpuId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isReady) return;
        
        Logger.Log($"FaceDetectionModelPool GPU{GpuId}: Initializing models...");
        
        try
        {
            var baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            
            // Create config
            var config = FaceDetectionConfig.CreateDefault(baseDir);
            config.GpuDeviceId = GpuId;
            config.ConfidenceThreshold = _settings.FaceDetectionConfidenceThreshold;
            
            // Validate model files exist
            if (!File.Exists(config.YoloModelPath))
            {
                Logger.Log($"FaceDetectionModelPool GPU{GpuId}: YOLO model not found: {config.YoloModelPath}");
                return;
            }
            
            if (!File.Exists(config.ArcFaceModelPath))
            {
                Logger.Log($"FaceDetectionModelPool GPU{GpuId}: ArcFace model not found: {config.ArcFaceModelPath}");
                // ArcFace is optional - we can still detect faces without embeddings
            }
            
            _detectionService = new FaceDetectionService(config);
            _isReady = true;
            
            Logger.Log($"FaceDetectionModelPool GPU{GpuId}: Ready (threshold: {config.ConfidenceThreshold}, VRAM: ~{VramUsageGb:F1}GB)");
        }
        catch (Exception ex)
        {
            Logger.Log($"FaceDetectionModelPool GPU{GpuId}: Initialization failed - {ex.Message}");
            _isReady = false;
        }
        
        await Task.CompletedTask;
    }
    
    public Task ShutdownAsync()
    {
        Logger.Log($"FaceDetectionModelPool GPU{GpuId}: Shutting down...");
        _detectionService?.Dispose();
        _detectionService = null;
        _isReady = false;
        Logger.Log($"FaceDetectionModelPool GPU{GpuId}: Shutdown complete, VRAM freed");
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}

/// <summary>
/// Face detection service orchestrator - handles YOLO-based face detection.
/// Owns model pools (one per GPU), spawns lightweight workers that share the pools.
/// </summary>
public class FaceDetectionOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    private readonly Dictionary<int, FaceDetectionModelPool> _modelPools = new();
    
    public override ProcessingType ProcessingType => ProcessingType.FaceDetection;
    public override string Name => "Face Detection";
    
    public FaceDetectionOrchestrator(PostgreSQLDataStore dataStore, Settings settings) 
        : base(dataStore)
    {
        _settings = settings;
    }
    
    protected override async Task<int> CountItemsNeedingProcessingAsync()
    {
        return await DataStore.CountImagesNeedingFaceDetection();
    }
    
    protected override async Task<List<int>> GetItemsNeedingProcessingAsync(int batchSize, int lastId)
    {
        return await DataStore.GetImagesNeedingFaceDetection(batchSize, lastId);
    }
    
    protected override async Task<ProcessingJob?> GetJobForImageAsync(int imageId)
    {
        var image = DataStore.GetImage(imageId);
        if (image == null || !File.Exists(image.Path))
        {
            await DataStore.SetNeedsFaceDetection(new List<int> { imageId }, false);
            return null;
        }
        
        return new ProcessingJob
        {
            ImageId = imageId,
            ImagePath = image.Path
        };
    }
    
    protected override async Task WriteResultAsync(ProcessingResult result)
    {
        if (result.Success && result.Data is FaceDetectionResultData faceData && faceData.Faces.Count > 0)
        {
            // Store each detected face
            foreach (var face in faceData.Faces)
            {
                await DataStore.StoreFaceDetectionAsync(
                    result.ImageId,
                    face.X, face.Y, face.Width, face.Height,
                    face.FaceCrop, face.CropWidth, face.CropHeight,
                    face.ArcFaceEmbedding,
                    face.DetectionModel,
                    face.Confidence, face.QualityScore, face.SharpnessScore,
                    face.PoseYaw, face.PosePitch, face.PoseRoll,
                    face.Landmarks != null ? System.Text.Json.JsonSerializer.Serialize(face.Landmarks) : null);
            }
        }
        
        await DataStore.SetNeedsFaceDetection(new List<int> { result.ImageId }, false);
    }
    
    /// <summary>
    /// Initialize model pools for all GPUs in the allocation.
    /// </summary>
    protected override async Task InitializeModelsAsync(ServiceAllocation allocation, CancellationToken ct)
    {
        foreach (var gpuAlloc in allocation.GpuAllocations)
        {
            if (gpuAlloc.ModelCount > 0 && !_modelPools.ContainsKey(gpuAlloc.GpuId))
            {
                var pool = new FaceDetectionModelPool(gpuAlloc.GpuId, _settings);
                await pool.InitializeAsync(ct);
                _modelPools[gpuAlloc.GpuId] = pool;
            }
        }
    }
    
    /// <summary>
    /// Shutdown all model pools and free VRAM.
    /// </summary>
    protected override async Task ShutdownModelsAsync()
    {
        foreach (var pool in _modelPools.Values)
        {
            await pool.ShutdownAsync();
        }
        _modelPools.Clear();
    }
    
    protected FaceDetectionModelPool? GetModelPool(int gpuId)
    {
        return _modelPools.TryGetValue(gpuId, out var pool) ? pool : null;
    }
    
    protected override IProcessingWorker CreateWorker(int gpuId, int workerId)
    {
        var pool = GetModelPool(gpuId);
        if (pool == null)
        {
            throw new InvalidOperationException($"No model pool for GPU {gpuId}. Call InitializeModelsAsync first.");
        }
        return new FaceDetectionWorker(gpuId, workerId, pool);
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ShutdownModelsAsync().Wait();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Result data from face detection
/// </summary>
public class FaceDetectionResultData
{
    public List<FaceDetectionResult> Faces { get; init; } = new();
}

/// <summary>
/// Lightweight face detection worker - uses shared model pool from orchestrator.
/// Thread-safe: ONNX CUDA EP allows concurrent Run() calls.
/// </summary>
public class FaceDetectionWorker : IProcessingWorker
{
    private readonly FaceDetectionModelPool _modelPool;
    private bool _isBusy;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsBusy => _isBusy;
    
    public FaceDetectionWorker(int gpuId, int workerId, FaceDetectionModelPool modelPool)
    {
        GpuId = gpuId;
        WorkerId = workerId;
        _modelPool = modelPool;
    }
    
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        
        try
        {
            if (!_modelPool.IsReady || _modelPool.DetectionService == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Face detection model pool not ready"
                };
            }
            
            // Process image using shared model pool
            var results = await _modelPool.DetectionService.ProcessImageAsync(job.ImagePath);
            
            if (!string.IsNullOrEmpty(results.ErrorMessage))
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = results.ErrorMessage
                };
            }
            
            return new ProcessingResult
            {
                ImageId = job.ImageId,
                Success = true,
                Data = new FaceDetectionResultData { Faces = results.Faces }
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"FaceDetectionWorker {WorkerId}: Error processing {job.ImagePath}: {ex.Message}");
            return new ProcessingResult
            {
                ImageId = job.ImageId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _isBusy = false;
        }
    }
    
    public void Dispose()
    {
        // Worker doesn't own the model pool - nothing to dispose
    }
}
