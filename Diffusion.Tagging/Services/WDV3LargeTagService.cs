namespace Diffusion.Tagging.Services;

/// <summary>
/// Service for auto-tagging images using WD v3 Large tagger ONNX model
/// Inherits all functionality from WDTagService - models are architecture-compatible
/// </summary>
public class WDV3LargeTagService : WDTagService
{
    public WDV3LargeTagService(string modelPath, string tagsPath) 
        : base(modelPath, tagsPath)
    {
    }
}
