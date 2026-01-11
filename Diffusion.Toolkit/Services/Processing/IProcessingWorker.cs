using System;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Interface for processing workers.
/// Workers are responsible for loading a model on a specific GPU and processing individual images.
/// Workers do NOT access the database - they only receive jobs and return results.
/// </summary>
public interface IProcessingWorker : IDisposable
{
    /// <summary>
    /// The GPU ID this worker is assigned to
    /// </summary>
    int GpuId { get; }
    
    /// <summary>
    /// Unique worker ID within the orchestrator
    /// </summary>
    int WorkerId { get; }
    
    /// <summary>
    /// Whether the worker has its model loaded and is ready to process
    /// </summary>
    bool IsReady { get; }
    
    /// <summary>
    /// Whether the worker is currently processing a job
    /// </summary>
    bool IsBusy { get; }
    
    /// <summary>
    /// Initialize the worker - load model onto GPU
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Process a single image and return the result.
    /// This is the core work method - receives a job, processes it, returns result.
    /// NO DATABASE ACCESS should happen here.
    /// </summary>
    /// <param name="job">The job to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Processing result with success/failure and data</returns>
    Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Shutdown the worker - unload model from GPU
    /// </summary>
    Task ShutdownAsync();
}

/// <summary>
/// Factory interface for creating workers
/// Each service orchestrator will have its own factory implementation
/// </summary>
public interface IProcessingWorkerFactory
{
    /// <summary>
    /// Create a worker for the specified GPU
    /// </summary>
    /// <param name="gpuId">GPU to assign the worker to</param>
    /// <param name="workerId">Unique worker ID</param>
    /// <returns>A new worker instance</returns>
    IProcessingWorker CreateWorker(int gpuId, int workerId);
}
