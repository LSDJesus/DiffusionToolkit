namespace Diffusion.Common;

public enum CaptionHandlingMode
{
    /// <summary>Replace existing caption with new one</summary>
    Overwrite,
    /// <summary>Append new caption to existing caption</summary>
    Append,
    /// <summary>Use existing caption as context for refinement</summary>
    Refine
}

public enum CaptionProviderType
{
    LocalJoyCaption = 0,
    OpenAICompatible = 1
}
