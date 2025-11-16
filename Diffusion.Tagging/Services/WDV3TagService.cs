namespace Diffusion.Tagging.Services;

/// <summary>
/// Service for auto-tagging images using WD v3 tagger ONNX model
/// Inherits all functionality from WDTagService - models are architecture-compatible
/// </summary>
public class WDV3TagService : WDTagService
{
    public WDV3TagService(string modelPath, string tagsPath) 
        : base(modelPath, tagsPath)
    {
    }
}
