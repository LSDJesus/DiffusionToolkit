using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Embeddings;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Request for embedding a single image with BGE text and CLIP-Vision embeddings
/// </summary>
public class EmbeddingRequest
{
    public int ImageId { get; init; }
    public required string Prompt { get; init; }
    public string? NegativePrompt { get; init; }
    public required string ImagePath { get; init; }
    public TaskCompletionSource<EmbeddingResult?> Completion { get; } = new();
}

/// <summary>
/// Internal work item for a single encoder
/// </summary>
internal class EncoderWorkItem
{
    public int ImageId { get; init; }
    public required string Text { get; init; }
    public string? ImagePath { get; init; }  // Only for vision encoder
    public required Action<float[]?> OnComplete { get; init; }
}

/// <summary>
/// GPU-specific embedding orchestrator that loads models once and shares them across sub-workers.
/// 
/// Architecture:
/// - 1 orchestrator per GPU
/// - 2 model instances (BGE text, CLIP-Vision) loaded once
/// - N sub-workers per model sharing the same ONNX session
/// - Coordinates both embeddings per image before returning result
/// 
/// Embedding types:
/// - BGE-large-en-v1.5 (1024D) - Semantic text similarity for prompts, tags, captions
/// - CLIP-ViT-H (1280D) - Visual image similarity
/// 
/// Note: CLIP-L/G text encoders removed - their embeddings are effectively just
/// tokenized prompts that need transformer inference anyway. For ComfyUI integration,
/// conditioning will be generated on-demand.
/// </summary>
public class EmbeddingPooledOrchestrator : IDisposable
{
    private readonly int _gpuId;
    private readonly int _workersPerModel;
    
    // Shared model instances (loaded once per GPU)
    private BGETextEncoder? _bgeEncoder;
    private CLIPVisionEncoder? _clipVisionEncoder;
    
    // Work queues for each encoder
    private Channel<EncoderWorkItem>? _bgeQueue;
    private Channel<EncoderWorkItem>? _visionQueue;
    
    // Worker tasks
    private readonly List<Task> _workerTasks = new();
    private CancellationTokenSource? _cts;
    
    // Pending image results (tracks both embeddings per image)
    private readonly ConcurrentDictionary<int, PendingEmbeddingResult> _pendingResults = new();
    
    private bool _isInitialized;
    private bool _disposed;

    public bool IsInitialized => _isInitialized;
    
    /// <summary>
    /// VRAM usage: BGE (~0.5GB) + CLIP-Vision (~2.6GB) = ~3.1GB
    /// (Reduced from 7.6GB after removing CLIP-L and CLIP-G text encoders)
    /// </summary>
    public const double VramUsageGb = 3.1;
    
    public EmbeddingPooledOrchestrator(int gpuId, int workersPerModel = 3)
    {
        _gpuId = gpuId;
        _workersPerModel = workersPerModel;
    }
    
    /// <summary>
    /// Initialize all models on this GPU. Call once before processing.
    /// </summary>
    public async Task InitializeAsync(EmbeddingConfig config, CancellationToken ct = default)
    {
        if (_isInitialized)
            return;
            
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Initializing 2 embedding models (BGE + CLIP-Vision)...");
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        // Override GPU assignment for all models to this GPU
        config.BgeGpuDevice = _gpuId;
        config.ClipVisionGpuDevice = _gpuId;
        
        try
        {
            // Load models sequentially to avoid VRAM contention
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Loading BGE encoder (~0.5GB)...");
            _bgeEncoder = new BGETextEncoder(config.BgeModelPath, config.BgeVocabPath, _gpuId);
            
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Loading CLIP-Vision encoder (~2.6GB)...");
            _clipVisionEncoder = new CLIPVisionEncoder(config.ClipVisionModelPath, _gpuId);
            
            // Create work queues
            _bgeQueue = Channel.CreateUnbounded<EncoderWorkItem>();
            _visionQueue = Channel.CreateUnbounded<EncoderWorkItem>();
            
            // Start sub-workers for each encoder
            for (int i = 0; i < _workersPerModel; i++)
            {
                _workerTasks.Add(Task.Run(() => RunBgeWorker(i, _cts.Token)));
                _workerTasks.Add(Task.Run(() => RunVisionWorker(i, _cts.Token)));
            }
            
            _isInitialized = true;
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Initialized with {_workersPerModel} sub-workers per model ({_workersPerModel * 2} total workers)");
        }
        catch (Exception ex)
        {
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Initialization failed: {ex.Message}");
            Dispose();
            throw;
        }
    }
    
    /// <summary>
    /// Generate all embeddings for an image. Distributes work across both encoders
    /// and waits for both to complete before returning.
    /// </summary>
    public async Task<EmbeddingResult?> GenerateEmbeddingsAsync(
        int imageId,
        string prompt,
        string? negativePrompt,
        string imagePath,
        CancellationToken ct = default)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Orchestrator not initialized");
        
        // Create pending result tracker for this image
        var pending = new PendingEmbeddingResult(imageId);
        _pendingResults[imageId] = pending;
        
        try
        {
            // Build the text to encode (prompt + negative if available)
            var textToEncode = string.IsNullOrEmpty(negativePrompt) 
                ? prompt 
                : $"{prompt} [SEP] {negativePrompt}";
            
            // Queue work items to both encoders
            await _bgeQueue!.Writer.WriteAsync(new EncoderWorkItem
            {
                ImageId = imageId,
                Text = textToEncode,
                OnComplete = emb => pending.SetBge(emb)
            }, ct);
            
            await _visionQueue!.Writer.WriteAsync(new EncoderWorkItem
            {
                ImageId = imageId,
                Text = prompt,  // Not used, but required
                ImagePath = imagePath,
                OnComplete = emb => pending.SetVision(emb)
            }, ct);
            
            // Wait for both embeddings to complete
            var result = await pending.WaitForCompletionAsync(ct);
            
            return result;
        }
        finally
        {
            _pendingResults.TryRemove(imageId, out _);
        }
    }
    
    private async Task RunBgeWorker(int workerId, CancellationToken ct)
    {
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] BGE sub-worker {workerId} started");
        
        try
        {
            await foreach (var item in _bgeQueue!.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var embedding = await _bgeEncoder!.EncodeAsync(item.Text);
                    item.OnComplete(embedding);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] BGE worker {workerId} error: {ex.Message}");
                    item.OnComplete(null);
                }
            }
        }
        catch (OperationCanceledException) { }
        
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] BGE sub-worker {workerId} exiting");
    }
    
    private async Task RunVisionWorker(int workerId, CancellationToken ct)
    {
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Vision sub-worker {workerId} started");
        
        try
        {
            await foreach (var item in _visionQueue!.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (string.IsNullOrEmpty(item.ImagePath))
                    {
                        item.OnComplete(null);
                        continue;
                    }
                    
                    var embedding = await _clipVisionEncoder!.EncodeAsync(item.ImagePath);
                    item.OnComplete(embedding);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Vision worker {workerId} error: {ex.Message}");
                    item.OnComplete(null);
                }
            }
        }
        catch (OperationCanceledException) { }
        
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Vision sub-worker {workerId} exiting");
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Disposing...");
        
        _cts?.Cancel();
        
        // Complete all queues
        _bgeQueue?.Writer.TryComplete();
        _visionQueue?.Writer.TryComplete();
        
        // Wait for workers to finish
        try
        {
            Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch { }
        
        // Dispose encoders
        _bgeEncoder?.Dispose();
        _clipVisionEncoder?.Dispose();
        
        _cts?.Dispose();
        
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Disposed");
    }
}

/// <summary>
/// Tracks the completion of both embeddings for a single image
/// </summary>
internal class PendingEmbeddingResult
{
    private readonly int _imageId;
    private float[]? _bgeEmbedding;
    private float[]? _visionEmbedding;
    
    private int _completedCount;
    private readonly TaskCompletionSource<EmbeddingResult?> _tcs = new();
    
    public PendingEmbeddingResult(int imageId)
    {
        _imageId = imageId;
    }
    
    public void SetBge(float[]? embedding)
    {
        _bgeEmbedding = embedding;
        CheckComplete();
    }
    
    public void SetVision(float[]? embedding)
    {
        _visionEmbedding = embedding;
        CheckComplete();
    }
    
    private void CheckComplete()
    {
        var count = Interlocked.Increment(ref _completedCount);
        if (count == 2)
        {
            // Both embeddings complete
            if (_bgeEmbedding != null && _visionEmbedding != null)
            {
                _tcs.TrySetResult(new EmbeddingResult
                {
                    BgeEmbedding = _bgeEmbedding,
                    ImageEmbedding = _visionEmbedding
                });
            }
            else
            {
                // At least one embedding failed
                Logger.Log($"Embedding incomplete for image {_imageId}: BGE={_bgeEmbedding != null}, Vision={_visionEmbedding != null}");
                _tcs.TrySetResult(null);
            }
        }
    }
    
    public Task<EmbeddingResult?> WaitForCompletionAsync(CancellationToken ct = default)
    {
        ct.Register(() => _tcs.TrySetCanceled());
        return _tcs.Task;
    }
}
