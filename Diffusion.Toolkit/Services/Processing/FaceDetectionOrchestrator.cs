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
/// Face detection service orchestrator - handles YOLO-based face detection
/// </summary>
public class FaceDetectionOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    
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
    
    protected override IProcessingWorker CreateWorker(int gpuId, int workerId)
    {
        return new FaceDetectionWorker(gpuId, workerId, _settings);
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
/// Face detection worker - loads YOLO and ArcFace models
/// </summary>
public class FaceDetectionWorker : IProcessingWorker
{
    private readonly Settings _settings;
    private FaceDetectionService? _detectionService;
    private bool _isReady;
    private bool _isBusy;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsReady => _isReady;
    public bool IsBusy => _isBusy;
    
    public FaceDetectionWorker(int gpuId, int workerId, Settings settings)
    {
        GpuId = gpuId;
        WorkerId = workerId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Logger.Log($"FaceDetectionWorker {WorkerId}: Initializing on GPU {GpuId}");
        
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
                Logger.Log($"FaceDetectionWorker {WorkerId}: YOLO model not found: {config.YoloModelPath}");
                return;
            }
            
            if (!File.Exists(config.ArcFaceModelPath))
            {
                Logger.Log($"FaceDetectionWorker {WorkerId}: ArcFace model not found: {config.ArcFaceModelPath}");
                // ArcFace is optional - we can still detect faces without embeddings
            }
            
            _detectionService = new FaceDetectionService(config);
            _isReady = true;
            
            Logger.Log($"FaceDetectionWorker {WorkerId}: Initialized (threshold: {config.ConfidenceThreshold})");
        }
        catch (Exception ex)
        {
            Logger.Log($"FaceDetectionWorker {WorkerId}: Initialization failed - {ex.Message}");
            _isReady = false;
        }
        
        await Task.CompletedTask;
    }
    
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        
        try
        {
            if (!_isReady || _detectionService == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Face detection service not initialized"
                };
            }
            
            // Process image
            var results = await _detectionService.ProcessImageAsync(job.ImagePath);
            
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
    
    public Task ShutdownAsync()
    {
        Logger.Log($"FaceDetectionWorker {WorkerId}: Shutting down");
        _detectionService?.Dispose();
        _detectionService = null;
        _isReady = false;
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}
