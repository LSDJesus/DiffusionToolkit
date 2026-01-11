using System;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Interface for individual service orchestrators (Tagging, Captioning, Embedding, FaceDetection)
/// Each orchestrator manages its own queue, workers, and DB writes for a single processing type.
/// </summary>
public interface IServiceOrchestrator : IDisposable
{
    /// <summary>
    /// The type of processing this orchestrator handles
    /// </summary>
    ProcessingType ProcessingType { get; }
    
    /// <summary>
    /// Display name for the service
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Whether the service is currently running
    /// </summary>
    bool IsRunning { get; }
    
    /// <summary>
    /// Whether the service is paused
    /// </summary>
    bool IsPaused { get; }
    
    /// <summary>
    /// Number of items remaining in the queue
    /// </summary>
    int QueueRemaining { get; }
    
    /// <summary>
    /// Number of items processed so far
    /// </summary>
    int Progress { get; }
    
    /// <summary>
    /// Total number of items to process
    /// </summary>
    int Total { get; }
    
    /// <summary>
    /// Number of items skipped (errors, missing files, etc.)
    /// </summary>
    int Skipped { get; }
    
    /// <summary>
    /// Start processing with the given allocation
    /// </summary>
    /// <param name="allocation">GPU/VRAM allocation for this service</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StartAsync(ServiceAllocation allocation, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stop processing gracefully
    /// </summary>
    Task StopAsync();
    
    /// <summary>
    /// Pause processing (workers stop taking new jobs but finish current)
    /// </summary>
    void Pause();
    
    /// <summary>
    /// Resume processing after pause
    /// </summary>
    void Resume();
    
    /// <summary>
    /// Fired when progress changes
    /// </summary>
    event EventHandler<ProcessingProgressEventArgs>? ProgressChanged;
    
    /// <summary>
    /// Fired when status changes (started, stopped, paused, etc.)
    /// </summary>
    event EventHandler<ProcessingStatusEventArgs>? StatusChanged;
    
    /// <summary>
    /// Fired when processing completes (queue empty)
    /// </summary>
    event EventHandler? Completed;
}
