using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Embeddings;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Embedding service orchestrator - handles BGE text and CLIP image embeddings
/// Uses EmbeddingPooledOrchestrator for efficient model sharing
/// </summary>
public class EmbeddingOrchestrator : BaseServiceOrchestrator
{
    private readonly Settings _settings;
    
    public override ProcessingType ProcessingType => ProcessingType.Embedding;
    public override string Name => "Embedding";
    
    public EmbeddingOrchestrator(PostgreSQLDataStore dataStore, Settings settings) 
        : base(dataStore)
    {
        _settings = settings;
    }
    
    protected override async Task<int> CountItemsNeedingProcessingAsync()
    {
        return await DataStore.CountImagesNeedingEmbedding();
    }
    
    protected override async Task<List<int>> GetItemsNeedingProcessingAsync(int batchSize, int lastId)
    {
        return await DataStore.GetImagesNeedingEmbedding(batchSize, lastId);
    }
    
    protected override async Task<ProcessingJob?> GetJobForImageAsync(int imageId)
    {
        var image = DataStore.GetImage(imageId);
        if (image == null || !File.Exists(image.Path))
        {
            await DataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
            return null;
        }
        
        // Skip if no prompt (need text for text embeddings)
        if (string.IsNullOrWhiteSpace(image.Prompt))
        {
            await DataStore.SetNeedsEmbedding(new List<int> { imageId }, false);
            return null;
        }
        
        return new ProcessingJob
        {
            ImageId = imageId,
            ImagePath = image.Path,
            AdditionalData = new EmbeddingJobData
            {
                Prompt = image.Prompt,
                NegativePrompt = image.NegativePrompt
            }
        };
    }
    
    protected override async Task WriteResultAsync(ProcessingResult result)
    {
        if (result.Success && result.Data is EmbeddingResultData embeddings)
        {
            await DataStore.StoreImageEmbeddingsAsync(
                result.ImageId,
                embeddings.BgeEmbedding,
                embeddings.ClipLEmbedding,
                embeddings.ClipGEmbedding,
                embeddings.ImageEmbedding,
                isRepresentative: true);
        }
        
        await DataStore.SetNeedsEmbedding(new List<int> { result.ImageId }, false);
    }
    
    protected override IProcessingWorker CreateWorker(int gpuId, int workerId)
    {
        return new EmbeddingWorker(gpuId, workerId, _settings);
    }
}

/// <summary>
/// Additional data for embedding jobs
/// </summary>
public class EmbeddingJobData
{
    public string? Prompt { get; init; }
    public string? NegativePrompt { get; init; }
}

/// <summary>
/// Result data from embedding processing
/// </summary>
public class EmbeddingResultData
{
    public float[] BgeEmbedding { get; init; } = Array.Empty<float>();
    public float[] ClipLEmbedding { get; init; } = Array.Empty<float>();
    public float[] ClipGEmbedding { get; init; } = Array.Empty<float>();
    public float[] ImageEmbedding { get; init; } = Array.Empty<float>();
}

/// <summary>
/// Embedding worker - uses EmbeddingPooledOrchestrator for model management
/// Each worker has its own orchestrator that manages all 4 embedding models on its GPU
/// </summary>
public class EmbeddingWorker : IProcessingWorker
{
    private readonly Settings _settings;
    private EmbeddingPooledOrchestrator? _orchestrator;
    private EmbeddingConfig? _config;
    private bool _isReady;
    private bool _isBusy;
    
    public int GpuId { get; }
    public int WorkerId { get; }
    public bool IsReady => _isReady;
    public bool IsBusy => _isBusy;
    
    public EmbeddingWorker(int gpuId, int workerId, Settings settings)
    {
        GpuId = gpuId;
        WorkerId = workerId;
        _settings = settings;
    }
    
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Logger.Log($"EmbeddingWorker {WorkerId}: Initializing on GPU {GpuId}");
        
        try
        {
            // Get base directory for model paths
            var baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            
            // Create config with default model paths
            _config = EmbeddingConfig.CreateDefault(baseDir);
            
            // Validate that model files exist
            try
            {
                _config.Validate();
            }
            catch (FileNotFoundException ex)
            {
                Logger.Log($"EmbeddingWorker {WorkerId}: {ex.Message}");
                return;
            }
            
            // Create pooled orchestrator (manages all 4 models on this GPU)
            // Note: Using 1 worker per model since the new architecture handles parallelism at the job level
            _orchestrator = new EmbeddingPooledOrchestrator(GpuId, workersPerModel: 1);
            await _orchestrator.InitializeAsync(_config, cancellationToken);
            
            _isReady = _orchestrator.IsInitialized;
            Logger.Log($"EmbeddingWorker {WorkerId}: Initialized = {_isReady}");
        }
        catch (Exception ex)
        {
            Logger.Log($"EmbeddingWorker {WorkerId}: Initialization failed - {ex.Message}");
            _isReady = false;
        }
    }
    
    public async Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        
        try
        {
            if (!_isReady || _orchestrator == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Embedding orchestrator not initialized"
                };
            }
            
            var jobData = job.AdditionalData as EmbeddingJobData;
            var prompt = jobData?.Prompt ?? "";
            var negativePrompt = jobData?.NegativePrompt;
            
            // Use the pooled orchestrator to generate all 4 embeddings
            var result = await _orchestrator.GenerateEmbeddingsAsync(
                job.ImageId,
                prompt,
                negativePrompt,
                job.ImagePath,
                cancellationToken);
            
            if (result == null)
            {
                return new ProcessingResult
                {
                    ImageId = job.ImageId,
                    Success = false,
                    ErrorMessage = "Embedding generation returned null"
                };
            }
            
            return new ProcessingResult
            {
                ImageId = job.ImageId,
                Success = true,
                Data = new EmbeddingResultData
                {
                    BgeEmbedding = result.BgeEmbedding,
                    ClipLEmbedding = result.ClipLEmbedding,
                    ClipGEmbedding = result.ClipGEmbedding,
                    ImageEmbedding = result.ImageEmbedding
                }
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"EmbeddingWorker {WorkerId}: Error processing image {job.ImageId}: {ex.Message}");
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
        Logger.Log($"EmbeddingWorker {WorkerId}: Shutting down");
        _orchestrator?.Dispose();
        _orchestrator = null;
        _isReady = false;
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        ShutdownAsync().Wait();
    }
}
