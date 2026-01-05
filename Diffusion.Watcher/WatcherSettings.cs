using Diffusion.Common;

namespace Diffusion.Watcher;

public class WatcherSettings
{
    public string? ModelRootPath { get; set; }
    
    // Tagging settings
    public int TaggingConcurrentWorkers { get; set; } = 4;
    public string TaggingGpuDevices { get; set; } = "0";
    public string TaggingGpuVramRatios { get; set; } = "32";
    
    // Captioning settings
    public CaptionProviderType CaptionProvider { get; set; }
    public string? JoyCaptionModelPath { get; set; }
    public string? JoyCaptionMMProjPath { get; set; }
    public string? ExternalCaptionBaseUrl { get; set; }
    public string? ExternalCaptionModel { get; set; }
    public string? ExternalCaptionApiKey { get; set; }
    
    // Face detection settings
    public int FaceDetectionConcurrentWorkers { get; set; } = 4;
    public string FaceDetectionGpuDevices { get; set; } = "0";
    public float FaceDetectionConfidenceThreshold { get; set; } = 0.5f;
    
    // Auto-processing on scan
    public bool AutoTagOnScan { get; set; }
    public bool AutoCaptionOnScan { get; set; }
    public bool AutoFaceDetectionOnScan { get; set; }
}
