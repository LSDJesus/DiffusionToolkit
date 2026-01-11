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
/// Request for embedding a single image with all 4 embedding types
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
/// Similar to RunTaggingGpuOrchestrator pattern.
/// 
/// Architecture:
/// - 1 orchestrator per GPU
/// - 4 model instances (BGE, CLIP-L, CLIP-G, CLIP-Vision) loaded once
/// - N sub-workers per model sharing the same ONNX session
/// - Coordinates all 4 embeddings per image before returning result
/// </summary>
public class EmbeddingPooledOrchestrator : IDisposable
{
    private readonly int _gpuId;
    private readonly int _workersPerModel;
    
    // Shared model instances (loaded once per GPU)
    private BGETextEncoder? _bgeEncoder;
    private CLIPTextEncoder? _clipLEncoder;
    private CLIPTextEncoder? _clipGEncoder;
    private CLIPVisionEncoder? _clipVisionEncoder;
    
    // Work queues for each encoder
    private Channel<EncoderWorkItem>? _bgeQueue;
    private Channel<EncoderWorkItem>? _clipLQueue;
    private Channel<EncoderWorkItem>? _clipGQueue;
    private Channel<EncoderWorkItem>? _visionQueue;
    
    // Worker tasks
    private readonly List<Task> _workerTasks = new();
    private CancellationTokenSource? _cts;
    
    // Pending image results (tracks all 4 embeddings per image)
    private readonly ConcurrentDictionary<int, PendingEmbeddingResult> _pendingResults = new();
    
    private bool _isInitialized;
    private bool _disposed;

    public bool IsInitialized => _isInitialized;
    
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
            
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Initializing 4 embedding models...");
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        // Override GPU assignment for all models to this GPU
        config.BgeGpuDevice = _gpuId;
        config.ClipLGpuDevice = _gpuId;
        config.ClipGGpuDevice = _gpuId;
        config.ClipVisionGpuDevice = _gpuId;
        
        try
        {
            // Load models sequentially to avoid VRAM contention
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Loading BGE encoder...");
            _bgeEncoder = new BGETextEncoder(config.BgeModelPath, config.BgeVocabPath, _gpuId);
            
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Loading CLIP-L encoder...");
            _clipLEncoder = new CLIPTextEncoder(
                config.ClipLModelPath, config.ClipLVocabPath, config.ClipLMergesPath, 
                768, "CLIP-L", _gpuId);
            
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Loading CLIP-G encoder...");
            _clipGEncoder = new CLIPTextEncoder(
                config.ClipGModelPath, config.ClipGVocabPath, config.ClipGMergesPath,
                1280, "CLIP-G", _gpuId);
            
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Loading CLIP-Vision encoder...");
            _clipVisionEncoder = new CLIPVisionEncoder(config.ClipVisionModelPath, _gpuId);
            
            // Create work queues
            _bgeQueue = Channel.CreateUnbounded<EncoderWorkItem>();
            _clipLQueue = Channel.CreateUnbounded<EncoderWorkItem>();
            _clipGQueue = Channel.CreateUnbounded<EncoderWorkItem>();
            _visionQueue = Channel.CreateUnbounded<EncoderWorkItem>();
            
            // Start sub-workers for each encoder
            for (int i = 0; i < _workersPerModel; i++)
            {
                _workerTasks.Add(Task.Run(() => RunBgeWorker(i, _cts.Token)));
                _workerTasks.Add(Task.Run(() => RunClipLWorker(i, _cts.Token)));
                _workerTasks.Add(Task.Run(() => RunClipGWorker(i, _cts.Token)));
                _workerTasks.Add(Task.Run(() => RunVisionWorker(i, _cts.Token)));
            }
            
            _isInitialized = true;
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Initialized with {_workersPerModel} sub-workers per model ({_workersPerModel * 4} total workers)");
        }
        catch (Exception ex)
        {
            Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Initialization failed: {ex.Message}");
            Dispose();
            throw;
        }
    }
    
    /// <summary>
    /// Generate all embeddings for an image. Distributes work across all 4 encoders
    /// and waits for all to complete before returning.
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
            
            // Queue work items to all 4 encoders
            await _bgeQueue!.Writer.WriteAsync(new EncoderWorkItem
            {
                ImageId = imageId,
                Text = textToEncode,
                OnComplete = emb => pending.SetBge(emb)
            }, ct);
            
            await _clipLQueue!.Writer.WriteAsync(new EncoderWorkItem
            {
                ImageId = imageId,
                Text = prompt,  // CLIP uses just the prompt
                OnComplete = emb => pending.SetClipL(emb)
            }, ct);
            
            await _clipGQueue!.Writer.WriteAsync(new EncoderWorkItem
            {
                ImageId = imageId,
                Text = prompt,
                OnComplete = emb => pending.SetClipG(emb)
            }, ct);
            
            await _visionQueue!.Writer.WriteAsync(new EncoderWorkItem
            {
                ImageId = imageId,
                Text = prompt,  // Not used, but required
                ImagePath = imagePath,
                OnComplete = emb => pending.SetVision(emb)
            }, ct);
            
            // Wait for all 4 embeddings to complete
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
    
    private async Task RunClipLWorker(int workerId, CancellationToken ct)
    {
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] CLIP-L sub-worker {workerId} started");
        
        try
        {
            await foreach (var item in _clipLQueue!.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var embedding = await _clipLEncoder!.EncodeAsync(item.Text);
                    item.OnComplete(embedding);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] CLIP-L worker {workerId} error: {ex.Message}");
                    item.OnComplete(null);
                }
            }
        }
        catch (OperationCanceledException) { }
        
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] CLIP-L sub-worker {workerId} exiting");
    }
    
    private async Task RunClipGWorker(int workerId, CancellationToken ct)
    {
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] CLIP-G sub-worker {workerId} started");
        
        try
        {
            await foreach (var item in _clipGQueue!.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var embedding = await _clipGEncoder!.EncodeAsync(item.Text);
                    item.OnComplete(embedding);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] CLIP-G worker {workerId} error: {ex.Message}");
                    item.OnComplete(null);
                }
            }
        }
        catch (OperationCanceledException) { }
        
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] CLIP-G sub-worker {workerId} exiting");
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
        _clipLQueue?.Writer.TryComplete();
        _clipGQueue?.Writer.TryComplete();
        _visionQueue?.Writer.TryComplete();
        
        // Wait for workers to finish
        try
        {
            Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch { }
        
        // Dispose encoders
        _bgeEncoder?.Dispose();
        _clipLEncoder?.Dispose();
        _clipGEncoder?.Dispose();
        _clipVisionEncoder?.Dispose();
        
        _cts?.Dispose();
        
        Logger.Log($"[EmbeddingOrchestrator GPU {_gpuId}] Disposed");
    }
}

/// <summary>
/// Tracks the completion of all 4 embeddings for a single image
/// </summary>
internal class PendingEmbeddingResult
{
    private readonly int _imageId;
    private float[]? _bgeEmbedding;
    private float[]? _clipLEmbedding;
    private float[]? _clipGEmbedding;
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
    
    public void SetClipL(float[]? embedding)
    {
        _clipLEmbedding = embedding;
        CheckComplete();
    }
    
    public void SetClipG(float[]? embedding)
    {
        _clipGEmbedding = embedding;
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
        if (count == 4)
        {
            // All 4 embeddings complete
            if (_bgeEmbedding != null && _clipLEmbedding != null && 
                _clipGEmbedding != null && _visionEmbedding != null)
            {
                _tcs.TrySetResult(new EmbeddingResult
                {
                    BgeEmbedding = _bgeEmbedding,
                    ClipLEmbedding = _clipLEmbedding,
                    ClipGEmbedding = _clipGEmbedding,
                    ImageEmbedding = _visionEmbedding
                });
            }
            else
            {
                // At least one embedding failed
                Logger.Log($"Embedding incomplete for image {_imageId}: BGE={_bgeEmbedding != null}, CLIP-L={_clipLEmbedding != null}, CLIP-G={_clipGEmbedding != null}, Vision={_visionEmbedding != null}");
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
