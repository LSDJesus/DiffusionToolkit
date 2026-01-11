using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;

namespace Diffusion.Embeddings;

/// <summary>
/// Helper class to create ONNX Runtime sessions with optimized CUDA settings
/// that minimize VRAM overhead while maintaining good performance.
/// </summary>
public static class OnnxSessionHelper
{
    /// <summary>
    /// Create SessionOptions with memory-efficient CUDA provider settings.
    /// Uses kSameAsRequested arena strategy and limited cuDNN workspace to reduce VRAM overhead.
    /// </summary>
    /// <param name="deviceId">CUDA device ID (0 = primary GPU)</param>
    /// <param name="memoryEfficientMode">If true, uses settings that reduce VRAM at slight performance cost</param>
    /// <returns>Configured SessionOptions</returns>
    public static SessionOptions CreateCudaSessionOptions(int deviceId = 0, bool memoryEfficientMode = true)
    {
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            InterOpNumThreads = 2,
            IntraOpNumThreads = 4
        };

        try
        {
            // Use OrtCUDAProviderOptions for fine-grained control
            using var cudaProviderOptions = new OrtCUDAProviderOptions();
            var providerOptionsDict = new Dictionary<string, string>
            {
                ["device_id"] = deviceId.ToString(),
                ["do_copy_in_default_stream"] = "1"
            };

            if (memoryEfficientMode)
            {
                // Memory-efficient settings:
                // - kSameAsRequested: Allocate exactly what's needed instead of powers of 2
                // - cudnn_conv_use_max_workspace = 0: Clamp cuDNN workspace to 32MB (saves ~1-2GB)
                // - cudnn_conv_algo_search = DEFAULT: Use default algo search (faster startup)
                providerOptionsDict["arena_extend_strategy"] = "kSameAsRequested";
                providerOptionsDict["cudnn_conv_use_max_workspace"] = "0";
                providerOptionsDict["cudnn_conv_algo_search"] = "DEFAULT";
            }
            else
            {
                // Performance-optimized settings (uses more VRAM):
                providerOptionsDict["arena_extend_strategy"] = "kNextPowerOfTwo";
                providerOptionsDict["cudnn_conv_use_max_workspace"] = "1";
                providerOptionsDict["cudnn_conv_algo_search"] = "EXHAUSTIVE";
            }

            cudaProviderOptions.UpdateOptions(providerOptionsDict);
            sessionOptions = SessionOptions.MakeSessionOptionWithCudaProvider(cudaProviderOptions);
            
            // Re-apply session-level options that MakeSessionOptionWithCudaProvider may not preserve
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            sessionOptions.ExecutionMode = ExecutionMode.ORT_PARALLEL;
            sessionOptions.InterOpNumThreads = 2;
            sessionOptions.IntraOpNumThreads = 4;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to configure CUDA provider options: {ex.Message}. Using simple CUDA setup.");
            // Fallback to simple CUDA setup
            try
            {
                sessionOptions.AppendExecutionProvider_CUDA(deviceId);
            }
            catch
            {
                Console.WriteLine("Warning: CUDA provider not available, using CPU fallback");
            }
            sessionOptions.AppendExecutionProvider_CPU();
        }

        return sessionOptions;
    }

    /// <summary>
    /// Create SessionOptions with CUDA provider and optional GPU memory limit.
    /// </summary>
    /// <param name="deviceId">CUDA device ID</param>
    /// <param name="gpuMemLimitBytes">Maximum GPU memory to use (0 = no limit)</param>
    /// <returns>Configured SessionOptions</returns>
    public static SessionOptions CreateCudaSessionOptionsWithMemLimit(int deviceId, long gpuMemLimitBytes)
    {
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            InterOpNumThreads = 2,
            IntraOpNumThreads = 4
        };

        try
        {
            using var cudaProviderOptions = new OrtCUDAProviderOptions();
            var providerOptionsDict = new Dictionary<string, string>
            {
                ["device_id"] = deviceId.ToString(),
                ["arena_extend_strategy"] = "kSameAsRequested",
                ["cudnn_conv_use_max_workspace"] = "0",
                ["cudnn_conv_algo_search"] = "DEFAULT",
                ["do_copy_in_default_stream"] = "1"
            };

            if (gpuMemLimitBytes > 0)
            {
                providerOptionsDict["gpu_mem_limit"] = gpuMemLimitBytes.ToString();
            }

            cudaProviderOptions.UpdateOptions(providerOptionsDict);
            sessionOptions = SessionOptions.MakeSessionOptionWithCudaProvider(cudaProviderOptions);
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            sessionOptions.ExecutionMode = ExecutionMode.ORT_PARALLEL;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to configure CUDA provider: {ex.Message}");
            sessionOptions.AppendExecutionProvider_CPU();
        }

        return sessionOptions;
    }
}
