using System;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Type of processing operation
/// </summary>
public enum ProcessingType
{
    Tagging,
    Captioning,
    Embedding,
    FaceDetection
}

/// <summary>
/// Processing mode - affects VRAM allocation strategy
/// </summary>
public enum ProcessingMode
{
    /// <summary>
    /// Single process running alone - can use more VRAM
    /// </summary>
    Solo,
    
    /// <summary>
    /// Multiple processes running together - must share VRAM
    /// </summary>
    Concurrent
}

/// <summary>
/// VRAM allocation for a single GPU.
/// For ONNX services (Tagging, Embedding, FaceDetection): WorkerCount = number of parallel workers sharing 1 model.
/// For LLM services (Captioning): WorkerCount = number of model instances (1 worker per model).
/// </summary>
public class GpuAllocation
{
    public int GpuId { get; init; }
    
    /// <summary>
    /// For ONNX: Number of workers sharing one model on this GPU.
    /// For Captioning: Number of model instances (each gets 1 worker).
    /// </summary>
    public int WorkerCount { get; init; }
    
    /// <summary>
    /// Number of model instances on this GPU.
    /// For ONNX services: Always 1 (workers share).
    /// For Captioning: Same as WorkerCount (1:1 ratio).
    /// </summary>
    public int ModelCount { get; init; }
    
    public double VramCapacityGb { get; init; }
    public double MaxUsagePercent { get; init; }
    
    public double AvailableVramGb => VramCapacityGb * (MaxUsagePercent / 100.0);
}

/// <summary>
/// Complete VRAM allocation for a processing service
/// </summary>
public class ServiceAllocation
{
    public ProcessingType ProcessingType { get; init; }
    public ProcessingMode Mode { get; init; }
    public GpuAllocation[] GpuAllocations { get; init; } = Array.Empty<GpuAllocation>();
    
    public int TotalWorkers => GpuAllocations.Length > 0 
        ? System.Linq.Enumerable.Sum(GpuAllocations, g => g.WorkerCount) 
        : 0;
    
    public int TotalModels => GpuAllocations.Length > 0 
        ? System.Linq.Enumerable.Sum(GpuAllocations, g => g.ModelCount) 
        : 0;
}

/// <summary>
/// Result from a worker processing an image
/// </summary>
public class ProcessingResult
{
    public int ImageId { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public object? Data { get; init; }  // Service-specific result data
}

/// <summary>
/// Job for a worker to process
/// </summary>
public class ProcessingJob
{
    public int ImageId { get; init; }
    public string ImagePath { get; init; } = string.Empty;
    public object? AdditionalData { get; init; }  // Service-specific input data (e.g., prompt for embedding)
}

/// <summary>
/// Progress event arguments
/// </summary>
public class ProcessingProgressEventArgs : EventArgs
{
    public ProcessingType ProcessingType { get; init; }
    public int Current { get; init; }
    public int Total { get; init; }
    public int Remaining { get; init; }
    public int Skipped { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    
    public double Percentage => Total > 0 ? (double)Current / Total * 100 : 0;
}

/// <summary>
/// Status change event arguments
/// </summary>
public class ProcessingStatusEventArgs : EventArgs
{
    public ProcessingType ProcessingType { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public bool IsPaused { get; init; }
}
