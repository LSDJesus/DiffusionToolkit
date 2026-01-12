using System;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Toolkit.Services.Processing;

/// <summary>
/// Interface for processing workers.
/// Workers are LIGHTWEIGHT - they receive a model reference from the orchestrator
/// and process individual images. They do NOT own or manage model lifecycle.
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
    /// Whether the worker is currently processing a job
    /// </summary>
    bool IsBusy { get; }
    
    /// <summary>
    /// Process a single image and return the result.
    /// This is the core work method - receives a job, processes it, returns result.
    /// Uses model reference provided by orchestrator.
    /// NO DATABASE ACCESS should happen here.
    /// NO MODEL LOADING should happen here.
    /// </summary>
    /// <param name="job">The job to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Processing result with success/failure and data</returns>
    Task<ProcessingResult> ProcessAsync(ProcessingJob job, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for model pools managed by orchestrators.
/// Each service type will have its own model pool implementation.
/// </summary>
public interface IModelPool : IDisposable
{
    /// <summary>
    /// GPU ID this pool is assigned to
    /// </summary>
    int GpuId { get; }
    
    /// <summary>
    /// Whether the models are loaded and ready
    /// </summary>
    bool IsReady { get; }
    
    /// <summary>
    /// Estimated VRAM usage in GB
    /// </summary>
    double VramUsageGb { get; }
    
    /// <summary>
    /// Initialize and load models onto GPU
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Shutdown and unload models from GPU
    /// </summary>
    Task ShutdownAsync();
}
