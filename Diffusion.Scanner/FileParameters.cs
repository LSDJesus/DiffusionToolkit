namespace Diffusion.IO;

public class FileParameters
{
    public string Path { get; set; }
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public int Steps { get; set; }
    public string? Sampler { get; set; }
    public decimal CFGScale { get; set; }
    public long Seed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? ModelHash { get; set; }
    public string? Model { get; set; }
    public int BatchSize { get; set; }
    public int BatchPos { get; set; }
    public string? OtherParameters { get; set; }
    public string Parameters { get; set; }
    public decimal? AestheticScore { get; set; }
    public string? HyperNetwork { get; set; }
    public decimal? HyperNetworkStrength { get; set; }
    public int? ClipSkip { get; set; }
    public int? ENSD { get; set; }
    public decimal? PromptStrength { get; set; }
    public long FileSize { get; set; }
    public bool NoMetadata { get; set; }
    public string? Workflow { get; set; }
    public string? WorkflowId { get; set; }
    public bool HasError { get; set; }
    public string ErrorMessage { get; set; }

    public IReadOnlyCollection<Node>? Nodes { get; set; }
    public string? Hash { get; set; }
    
    // Sidecar .txt file content (AI-generated tags)
    public string? GeneratedTags { get; set; }
    
    // Extracted searchable metadata
    public List<LoraInfo>? Loras { get; set; }
    public List<EmbeddingInfo>? Embeddings { get; set; }
    // Computed after extraction for storage
    public List<(string Name, decimal Weight, bool IsImplicit)>? ProcessedEmbeddings { get; set; }
    public string? Vae { get; set; }
    public string? RefinerModel { get; set; }
    public decimal? RefinerSwitch { get; set; }
    public string? Upscaler { get; set; }
    public decimal? UpscaleFactor { get; set; }
    public int? HiresSteps { get; set; }
    public string? HiresUpscaler { get; set; }
    public decimal? HiresUpscale { get; set; }
    public decimal? DenoisingStrength { get; set; }
    public List<ControlNetInfo>? ControlNets { get; set; }
    public string? IpAdapter { get; set; }
    public decimal? IpAdapterStrength { get; set; }
    public List<string>? WildcardsUsed { get; set; }
    public decimal? GenerationTimeSeconds { get; set; }
    public string? Scheduler { get; set; }
    
    // Video-specific metadata
    public int? DurationMs { get; set; }              // Video duration in milliseconds
    public string? VideoCodec { get; set; }           // Video codec (h264, vp9, av1, etc.)
    public string? AudioCodec { get; set; }           // Audio codec (aac, opus, mp3, etc.)
    public decimal? FrameRate { get; set; }           // Frames per second
    public int? Bitrate { get; set; }                 // Total bitrate in kbps
    public bool IsVideo { get; set; }                 // True if this is a video file
}

/// <summary>
/// LoRA/LyCORIS information
/// </summary>
public class LoraInfo
{
    public string Name { get; set; } = string.Empty;
    public decimal Strength { get; set; }
}

/// <summary>
/// Embedding (Textual Inversion) information
/// </summary>
public class EmbeddingInfo
{
    public string Name { get; set; } = string.Empty;
    public decimal Weight { get; set; } = 1.0m;
}

/// <summary>
/// ControlNet configuration
/// </summary>
public class ControlNetInfo
{
    public int Index { get; set; }
    public string Preprocessor { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public decimal Weight { get; set; }
    public decimal? GuidanceStart { get; set; }
    public decimal? GuidanceEnd { get; set; }
}