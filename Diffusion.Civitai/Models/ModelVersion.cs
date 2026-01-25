namespace Diffusion.Civitai.Models;

public class ModelVersion
{
    public int Id { get; set; }
    public int ModelId { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string DownloadUrl { get; set; }
    public List<string> TrainedWords { get; set; }
    public List<ModelFile> Files { get; set; }
    public List<ModelImage> Images { get; set; }
    public Stats Stats { get; set; }
    public string BaseModel { get; set; }
    public string? BaseModelType { get; set; }
    
    // Recommended settings (if provided by creator)
    public decimal? ClipWeight { get; set; }
    public decimal? Weight { get; set; }
    
    // Additional properties from API
    public int? VaeId { get; set; }
    public int? EarlyAccessTimeFrame { get; set; }
}

/// <summary>
/// Used by model-versions api (by-hash endpoint)
/// </summary>
public class ModelVersion2 : ModelVersion
{
    public ModelVersionModel? Model { get; set; }
}


public class ModelVersionModel
{
    public string Name { get; set; }
    public ModelType Type { get; set; }
    public bool Nsfw { get; set; }
    public bool Poi { get; set; }
    public ModelMode? Mode { get; set; }
    
    // Creator info
    public Creator? Creator { get; set; }
    
    // Tags
    public List<string>? Tags { get; set; }
}
