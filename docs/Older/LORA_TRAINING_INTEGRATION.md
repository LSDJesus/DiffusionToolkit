# LoRA Training Integration for DiffusionToolkit

## Overview

This document describes the LoRA training dataset builder integration for DiffusionToolkit (C#/WPF). The goal is to leverage DT's existing captioning pipeline, embedding system, and image database to streamline LoRA training dataset preparation.

**Target:** DiffusionToolkit (C#/.NET 8, WPF, PostgreSQL + pgvector)  
**Integration Points:** kohya-ss/sd-scripts, OneTrainer, SimpleTuner

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         DiffusionToolkit                                     │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                    LoRA Training Module                                │  │
│  │  ├── DatasetBuilderWindow.xaml (UI)                                   │  │
│  │  ├── LoraDatasetService.cs (business logic)                           │  │
│  │  ├── CaptionReviewService.cs (caption editing/preview)                │  │
│  │  ├── TrainingConfigService.cs (kohya/OneTrainer config generation)   │  │
│  │  └── TrainingLauncherService.cs (optional: shell out to trainer)     │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                    │                                         │
│                         Uses existing services:                              │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  • ImageDataStore (PostgreSQL queries)                                │  │
│  │  • EmbeddingService (CLIP-H similarity for regularization)           │  │
│  │  • CaptionService (JoyTag, WDv3, JoyCaption)                          │  │
│  │  • ClusterService (character grouping)                                │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
                    ┌───────────────────────────────┐
                    │   Training-Ready Dataset      │
                    │   ├── 10_trigger/             │
                    │   │   ├── image001.png        │
                    │   │   ├── image001.txt        │
                    │   │   └── ...                 │
                    │   ├── 1_regularization/       │
                    │   └── training_config.toml    │
                    └───────────────────────────────┘
                                    │
                    ┌───────────────┴───────────────┐
                    ▼                               ▼
            ┌─────────────┐                 ┌─────────────┐
            │  kohya-ss   │                 │ OneTrainer  │
            │  sd-scripts │                 │             │
            └─────────────┘                 └─────────────┘
```

---

## Data Models

### LoraDataset.cs

```csharp
namespace Diffusion.Training.Models;

public class LoraDataset
{
    public string Name { get; set; } = string.Empty;
    public string TriggerWord { get; set; } = string.Empty;
    public TriggerPosition TriggerPosition { get; set; } = TriggerPosition.Prepend;
    
    public List<TrainingImage> Images { get; set; } = new();
    public List<TrainingImage> RegularizationImages { get; set; } = new();
    
    public TrainingConfig Config { get; set; } = new();
    public string OutputPath { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExportedAt { get; set; }
    public DateTime? TrainingStartedAt { get; set; }
    public DateTime? TrainingCompletedAt { get; set; }
}

public enum TriggerPosition
{
    Prepend,
    Append,
    None
}

public class TrainingImage
{
    public long DatabaseId { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string ExportPath { get; set; } = string.Empty;
    
    // Caption sources (from existing DT captioning)
    public string? Wd14Tags { get; set; }
    public string? JoyTagTags { get; set; }
    public string? JoyCaptionText { get; set; }
    public string? OriginalPrompt { get; set; }
    
    // Final caption (user-selected or edited)
    public string FinalCaption { get; set; } = string.Empty;
    public CaptionSource CaptionSource { get; set; } = CaptionSource.Combined;
    public bool IsEdited { get; set; }
    
    // Metadata
    public int Width { get; set; }
    public int Height { get; set; }
    public int? Rating { get; set; }
    public float[]? ClipHEmbedding { get; set; } // For similarity searches
}

public enum CaptionSource
{
    Wd14Tags,
    JoyTagTags,
    JoyCaption,
    OriginalPrompt,
    Combined,      // WD14 + JoyTag deduplicated
    Custom         // User-edited
}
```

### TrainingConfig.cs

```csharp
namespace Diffusion.Training.Models;

public class TrainingConfig
{
    // Model settings
    public string BaseModelPath { get; set; } = string.Empty;
    public string BaseModelName { get; set; } = string.Empty;
    public ModelType ModelType { get; set; } = ModelType.SDXL;
    
    // LoRA settings
    public int NetworkRank { get; set; } = 32;
    public int NetworkAlpha { get; set; } = 16;
    public NetworkType NetworkType { get; set; } = NetworkType.LoRA;
    
    // Training parameters
    public int Resolution { get; set; } = 1024;
    public int BatchSize { get; set; } = 1;
    public int GradientAccumulationSteps { get; set; } = 1;
    public int Epochs { get; set; } = 5;
    public int Repeats { get; set; } = 10;
    public float LearningRate { get; set; } = 1e-4f;
    public string LrScheduler { get; set; } = "cosine";
    public int LrWarmupSteps { get; set; } = 100;
    
    // Optimizer
    public string Optimizer { get; set; } = "AdamW8bit";
    
    // Augmentation
    public bool FlipAugmentation { get; set; } = true;
    public bool ColorJitter { get; set; } = false;
    public bool RandomCrop { get; set; } = false;
    public bool BucketResolution { get; set; } = true;
    
    // Regularization
    public bool UseRegularization { get; set; } = true;
    public int RegularizationRepeats { get; set; } = 1;
    
    // Output
    public TrainerFormat TrainerFormat { get; set; } = TrainerFormat.Kohya;
    public string OutputName { get; set; } = string.Empty;
    public int SaveEveryNEpochs { get; set; } = 1;
    
    // Advanced
    public bool CacheLa latents { get; set; } = true;
    public bool CacheTextEncoder { get; set; } = true;
    public string? VaePath { get; set; }
    public int? Seed { get; set; }
}

public enum ModelType
{
    SD15,
    SDXL,
    Flux,
    Illustrious,
    Pony
}

public enum NetworkType
{
    LoRA,
    LoCon,
    LoHa,
    DoRA
}

public enum TrainerFormat
{
    Kohya,
    OneTrainer,
    SimpleTuner
}
```

---

## Services

### LoraDatasetService.cs

```csharp
namespace Diffusion.Training.Services;

public class LoraDatasetService
{
    private readonly IImageDataStore _dataStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly ICaptionService _captionService;
    
    public LoraDatasetService(
        IImageDataStore dataStore,
        IEmbeddingService embeddingService,
        ICaptionService captionService)
    {
        _dataStore = dataStore;
        _embeddingService = embeddingService;
        _captionService = captionService;
    }
    
    /// <summary>
    /// Get images for a training dataset based on filters
    /// </summary>
    public async Task<List<TrainingImage>> GetImagesForDatasetAsync(
        DatasetFilter filter,
        CancellationToken ct = default)
    {
        var query = new ImageQuery
        {
            ClusterId = filter.ClusterId,
            MinRating = filter.MinRating,
            ExcludeMarkedBad = filter.ExcludeMarkedBad,
            Tags = filter.RequiredTags,
            ExcludeTags = filter.ExcludeTags,
            MinResolution = filter.MinResolution,
            Limit = filter.MaxImages
        };
        
        var images = await _dataStore.QueryImagesAsync(query, ct);
        
        return images.Select(img => new TrainingImage
        {
            DatabaseId = img.Id,
            SourcePath = img.Path,
            Wd14Tags = img.Wd14Tags,
            JoyTagTags = img.JoyTagTags,
            JoyCaptionText = img.JoyCaption,
            OriginalPrompt = img.Prompt,
            Width = img.Width,
            Height = img.Height,
            Rating = img.Rating,
            ClipHEmbedding = img.ImageEmbedding
        }).ToList();
    }
    
    /// <summary>
    /// Auto-select regularization images using CLIP-H similarity
    /// Finds images with similar style but different subject
    /// </summary>
    public async Task<List<TrainingImage>> GetRegularizationImagesAsync(
        List<TrainingImage> trainingImages,
        int count = 200,
        float minSimilarity = 0.5f,
        float maxSimilarity = 0.85f,
        CancellationToken ct = default)
    {
        // Get average CLIP-H embedding of training images
        var avgEmbedding = ComputeAverageEmbedding(
            trainingImages.Where(t => t.ClipHEmbedding != null)
                         .Select(t => t.ClipHEmbedding!)
                         .ToList());
        
        if (avgEmbedding == null)
            return new List<TrainingImage>();
        
        // Query for visually similar but not identical images
        var candidates = await _dataStore.SearchByEmbeddingAsync(
            avgEmbedding,
            topK: count * 3,  // Get more candidates for filtering
            minSimilarity: minSimilarity,
            maxSimilarity: maxSimilarity,
            ct);
        
        // Exclude training images
        var trainingIds = trainingImages.Select(t => t.DatabaseId).ToHashSet();
        var regularization = candidates
            .Where(img => !trainingIds.Contains(img.Id))
            .Take(count)
            .Select(img => new TrainingImage
            {
                DatabaseId = img.Id,
                SourcePath = img.Path,
                Wd14Tags = img.Wd14Tags,
                JoyTagTags = img.JoyTagTags,
                Width = img.Width,
                Height = img.Height
            })
            .ToList();
        
        return regularization;
    }
    
    /// <summary>
    /// Generate final captions for all images in dataset
    /// </summary>
    public void GenerateFinalCaptions(
        LoraDataset dataset,
        CaptionSource source,
        string? triggerWord = null,
        TriggerPosition triggerPosition = TriggerPosition.Prepend)
    {
        foreach (var image in dataset.Images)
        {
            var baseCaption = source switch
            {
                CaptionSource.Wd14Tags => image.Wd14Tags,
                CaptionSource.JoyTagTags => image.JoyTagTags,
                CaptionSource.JoyCaption => image.JoyCaptionText,
                CaptionSource.OriginalPrompt => image.OriginalPrompt,
                CaptionSource.Combined => CombineTags(image.Wd14Tags, image.JoyTagTags),
                CaptionSource.Custom => image.FinalCaption,
                _ => image.Wd14Tags
            };
            
            image.FinalCaption = ApplyTrigger(baseCaption ?? "", triggerWord, triggerPosition);
            image.CaptionSource = source;
        }
        
        // Regularization images don't get trigger word
        foreach (var image in dataset.RegularizationImages)
        {
            image.FinalCaption = source switch
            {
                CaptionSource.Wd14Tags => image.Wd14Tags ?? "",
                CaptionSource.JoyTagTags => image.JoyTagTags ?? "",
                CaptionSource.Combined => CombineTags(image.Wd14Tags, image.JoyTagTags),
                _ => image.Wd14Tags ?? ""
            };
        }
    }
    
    private string CombineTags(string? wd14, string? joytag)
    {
        if (string.IsNullOrEmpty(wd14) && string.IsNullOrEmpty(joytag))
            return "";
        if (string.IsNullOrEmpty(wd14)) return joytag!;
        if (string.IsNullOrEmpty(joytag)) return wd14;
        
        // Use deduplication dictionary (assumes it's injected or accessible)
        var wd14Tags = wd14.Split(',').Select(t => t.Trim()).ToHashSet();
        var joyTags = joytag.Split(',').Select(t => t.Trim());
        
        foreach (var tag in joyTags)
        {
            // Add if not duplicate (use dedup dictionary here)
            if (!wd14Tags.Contains(tag))
                wd14Tags.Add(tag);
        }
        
        return string.Join(", ", wd14Tags);
    }
    
    private string ApplyTrigger(string caption, string? trigger, TriggerPosition position)
    {
        if (string.IsNullOrEmpty(trigger))
            return caption;
        
        return position switch
        {
            TriggerPosition.Prepend => $"{trigger}, {caption}",
            TriggerPosition.Append => $"{caption}, {trigger}",
            TriggerPosition.None => caption,
            _ => caption
        };
    }
    
    private float[]? ComputeAverageEmbedding(List<float[]> embeddings)
    {
        if (embeddings.Count == 0) return null;
        
        var dim = embeddings[0].Length;
        var avg = new float[dim];
        
        foreach (var emb in embeddings)
        {
            for (int i = 0; i < dim; i++)
                avg[i] += emb[i];
        }
        
        for (int i = 0; i < dim; i++)
            avg[i] /= embeddings.Count;
        
        // Normalize
        var norm = (float)Math.Sqrt(avg.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < dim; i++)
                avg[i] /= norm;
        }
        
        return avg;
    }
}

public class DatasetFilter
{
    public int? ClusterId { get; set; }
    public int? MinRating { get; set; }
    public bool ExcludeMarkedBad { get; set; } = true;
    public List<string>? RequiredTags { get; set; }
    public List<string>? ExcludeTags { get; set; }
    public int? MinResolution { get; set; }
    public int? MaxImages { get; set; }
}
```

### DatasetExportService.cs

```csharp
namespace Diffusion.Training.Services;

public class DatasetExportService
{
    private readonly ILogger<DatasetExportService> _logger;
    
    public DatasetExportService(ILogger<DatasetExportService> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Export dataset to training-ready folder structure
    /// </summary>
    public async Task<ExportResult> ExportDatasetAsync(
        LoraDataset dataset,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ExportResult { OutputPath = dataset.OutputPath };
        
        try
        {
            // Create directory structure
            var trainDir = Path.Combine(dataset.OutputPath, $"{dataset.Config.Repeats}_{dataset.TriggerWord}");
            var regDir = Path.Combine(dataset.OutputPath, $"1_regularization");
            
            Directory.CreateDirectory(trainDir);
            if (dataset.RegularizationImages.Any())
                Directory.CreateDirectory(regDir);
            
            // Export training images
            int processed = 0;
            int total = dataset.Images.Count + dataset.RegularizationImages.Count;
            
            foreach (var image in dataset.Images)
            {
                ct.ThrowIfCancellationRequested();
                
                var fileName = $"{processed:D5}";
                var imagePath = Path.Combine(trainDir, $"{fileName}.png");
                var captionPath = Path.Combine(trainDir, $"{fileName}.txt");
                
                // Copy/resize image
                await CopyAndResizeImageAsync(
                    image.SourcePath, 
                    imagePath, 
                    dataset.Config.Resolution,
                    ct);
                
                // Write caption
                await File.WriteAllTextAsync(captionPath, image.FinalCaption, ct);
                
                image.ExportPath = imagePath;
                processed++;
                result.TrainingImagesExported++;
                
                progress?.Report(new ExportProgress
                {
                    Current = processed,
                    Total = total,
                    CurrentFile = image.SourcePath,
                    Phase = "Exporting training images"
                });
            }
            
            // Export regularization images
            int regIndex = 0;
            foreach (var image in dataset.RegularizationImages)
            {
                ct.ThrowIfCancellationRequested();
                
                var fileName = $"reg_{regIndex:D5}";
                var imagePath = Path.Combine(regDir, $"{fileName}.png");
                var captionPath = Path.Combine(regDir, $"{fileName}.txt");
                
                await CopyAndResizeImageAsync(
                    image.SourcePath,
                    imagePath,
                    dataset.Config.Resolution,
                    ct);
                
                await File.WriteAllTextAsync(captionPath, image.FinalCaption, ct);
                
                image.ExportPath = imagePath;
                regIndex++;
                processed++;
                result.RegularizationImagesExported++;
                
                progress?.Report(new ExportProgress
                {
                    Current = processed,
                    Total = total,
                    CurrentFile = image.SourcePath,
                    Phase = "Exporting regularization images"
                });
            }
            
            // Generate training config
            var configPath = await GenerateTrainingConfigAsync(dataset, ct);
            result.ConfigPath = configPath;
            
            result.Success = true;
            dataset.ExportedAt = DateTime.UtcNow;
            
            _logger.LogInformation(
                "Exported dataset: {Training} training, {Reg} regularization images to {Path}",
                result.TrainingImagesExported,
                result.RegularizationImagesExported,
                dataset.OutputPath);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.LogError(ex, "Failed to export dataset to {Path}", dataset.OutputPath);
        }
        
        return result;
    }
    
    private async Task CopyAndResizeImageAsync(
        string sourcePath,
        string destPath,
        int maxResolution,
        CancellationToken ct)
    {
        // Use ImageSharp or SkiaSharp for resizing
        using var image = await Image.LoadAsync(sourcePath, ct);
        
        // Calculate new size maintaining aspect ratio, bucketed to 64px
        int width = image.Width;
        int height = image.Height;
        
        if (width > maxResolution || height > maxResolution)
        {
            float scale = Math.Min(
                (float)maxResolution / width,
                (float)maxResolution / height);
            
            width = (int)(width * scale);
            height = (int)(height * scale);
        }
        
        // Bucket to 64px increments (kohya requirement)
        width = (width / 64) * 64;
        height = (height / 64) * 64;
        
        if (width != image.Width || height != image.Height)
        {
            image.Mutate(x => x.Resize(width, height, KnownResamplers.Lanczos3));
        }
        
        await image.SaveAsPngAsync(destPath, ct);
    }
    
    private async Task<string> GenerateTrainingConfigAsync(
        LoraDataset dataset,
        CancellationToken ct)
    {
        var config = dataset.Config;
        var configPath = config.TrainerFormat switch
        {
            TrainerFormat.Kohya => await GenerateKohyaConfigAsync(dataset, ct),
            TrainerFormat.OneTrainer => await GenerateOneTrainerConfigAsync(dataset, ct),
            TrainerFormat.SimpleTuner => await GenerateSimpleTunerConfigAsync(dataset, ct),
            _ => throw new NotSupportedException($"Trainer format {config.TrainerFormat} not supported")
        };
        
        return configPath;
    }
    
    private async Task<string> GenerateKohyaConfigAsync(LoraDataset dataset, CancellationToken ct)
    {
        var config = dataset.Config;
        var tomlPath = Path.Combine(dataset.OutputPath, "training_config.toml");
        
        var toml = $"""
            [model]
            pretrained_model_name_or_path = "{config.BaseModelPath.Replace("\\", "/")}"
            
            [network]
            network_module = "networks.lora"
            network_dim = {config.NetworkRank}
            network_alpha = {config.NetworkAlpha}
            
            [training]
            output_dir = "{dataset.OutputPath.Replace("\\", "/")}"
            output_name = "{config.OutputName}"
            save_every_n_epochs = {config.SaveEveryNEpochs}
            max_train_epochs = {config.Epochs}
            
            resolution = "{config.Resolution},{config.Resolution}"
            train_batch_size = {config.BatchSize}
            gradient_accumulation_steps = {config.GradientAccumulationSteps}
            
            learning_rate = {config.LearningRate}
            lr_scheduler = "{config.LrScheduler}"
            lr_warmup_steps = {config.LrWarmupSteps}
            
            optimizer_type = "{config.Optimizer}"
            
            cache_latents = {config.CacheLatents.ToString().ToLower()}
            cache_text_encoder_outputs = {config.CacheTextEncoder.ToString().ToLower()}
            
            [augmentation]
            flip_aug = {config.FlipAugmentation.ToString().ToLower()}
            color_aug = {config.ColorJitter.ToString().ToLower()}
            random_crop = {config.RandomCrop.ToString().ToLower()}
            bucket_reso_steps = 64
            enable_bucket = {config.BucketResolution.ToString().ToLower()}
            
            [dataset]
            train_data_dir = "{dataset.OutputPath.Replace("\\", "/")}"
            """;
        
        if (config.Seed.HasValue)
        {
            toml += $"\nseed = {config.Seed.Value}";
        }
        
        await File.WriteAllTextAsync(tomlPath, toml, ct);
        return tomlPath;
    }
    
    private async Task<string> GenerateOneTrainerConfigAsync(LoraDataset dataset, CancellationToken ct)
    {
        // OneTrainer uses JSON config
        var configPath = Path.Combine(dataset.OutputPath, "training_config.json");
        
        var config = new
        {
            training_folder = dataset.OutputPath,
            base_model = dataset.Config.BaseModelPath,
            output_name = dataset.Config.OutputName,
            network_rank = dataset.Config.NetworkRank,
            network_alpha = dataset.Config.NetworkAlpha,
            epochs = dataset.Config.Epochs,
            learning_rate = dataset.Config.LearningRate,
            batch_size = dataset.Config.BatchSize,
            resolution = dataset.Config.Resolution
        };
        
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json, ct);
        return configPath;
    }
    
    private Task<string> GenerateSimpleTunerConfigAsync(LoraDataset dataset, CancellationToken ct)
    {
        // SimpleTuner uses YAML - implement as needed
        throw new NotImplementedException("SimpleTuner config generation not yet implemented");
    }
}

public class ExportResult
{
    public bool Success { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public string? ConfigPath { get; set; }
    public int TrainingImagesExported { get; set; }
    public int RegularizationImagesExported { get; set; }
    public string? Error { get; set; }
}

public class ExportProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public double Percentage => Total > 0 ? (double)Current / Total * 100 : 0;
}
```

### TrainingLauncherService.cs

```csharp
namespace Diffusion.Training.Services;

public class TrainingLauncherService
{
    private readonly ILogger<TrainingLauncherService> _logger;
    private readonly ISettingsService _settings;
    
    public TrainingLauncherService(
        ILogger<TrainingLauncherService> logger,
        ISettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }
    
    /// <summary>
    /// Launch training in external process
    /// </summary>
    public async Task<TrainingSession> LaunchTrainingAsync(
        LoraDataset dataset,
        CancellationToken ct = default)
    {
        var session = new TrainingSession
        {
            DatasetId = dataset.Name,
            StartedAt = DateTime.UtcNow,
            Status = TrainingStatus.Starting
        };
        
        try
        {
            var config = dataset.Config;
            var (executable, arguments) = config.TrainerFormat switch
            {
                TrainerFormat.Kohya => BuildKohyaCommand(dataset),
                TrainerFormat.OneTrainer => BuildOneTrainerCommand(dataset),
                _ => throw new NotSupportedException()
            };
            
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false // Show training window
            };
            
            session.Process = Process.Start(startInfo);
            session.Status = TrainingStatus.Running;
            
            if (session.Process != null)
            {
                // Monitor process in background
                _ = MonitorTrainingAsync(session, ct);
            }
            
            dataset.TrainingStartedAt = DateTime.UtcNow;
            
            _logger.LogInformation("Started training for dataset {Name}", dataset.Name);
        }
        catch (Exception ex)
        {
            session.Status = TrainingStatus.Failed;
            session.Error = ex.Message;
            _logger.LogError(ex, "Failed to launch training for {Name}", dataset.Name);
        }
        
        return session;
    }
    
    private (string executable, string arguments) BuildKohyaCommand(LoraDataset dataset)
    {
        var kohyaPath = _settings.KohyaSdScriptsPath;
        var pythonPath = _settings.KohyaPythonPath ?? "python";
        var scriptPath = Path.Combine(kohyaPath, "sdxl_train_network.py");
        var configPath = Path.Combine(dataset.OutputPath, "training_config.toml");
        
        return (pythonPath, $"\"{scriptPath}\" --config_file \"{configPath}\"");
    }
    
    private (string executable, string arguments) BuildOneTrainerCommand(LoraDataset dataset)
    {
        var oneTrainerPath = _settings.OneTrainerPath;
        var configPath = Path.Combine(dataset.OutputPath, "training_config.json");
        
        return (Path.Combine(oneTrainerPath, "run.bat"), $"--config \"{configPath}\"");
    }
    
    private async Task MonitorTrainingAsync(TrainingSession session, CancellationToken ct)
    {
        if (session.Process == null) return;
        
        try
        {
            await session.Process.WaitForExitAsync(ct);
            session.CompletedAt = DateTime.UtcNow;
            session.Status = session.Process.ExitCode == 0 
                ? TrainingStatus.Completed 
                : TrainingStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            session.Process.Kill();
            session.Status = TrainingStatus.Cancelled;
        }
    }
}

public class TrainingSession
{
    public string DatasetId { get; set; } = string.Empty;
    public Process? Process { get; set; }
    public TrainingStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public enum TrainingStatus
{
    Starting,
    Running,
    Completed,
    Failed,
    Cancelled
}
```

---

## UI Components

### DatasetBuilderWindow.xaml

```xml
<Window x:Class="Diffusion.Toolkit.Windows.DatasetBuilderWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="LoRA Training Dataset Builder" 
        Width="1200" Height="800"
        WindowStartupLocation="CenterScreen">
    
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        
        <!-- Header -->
        <Border Grid.Row="0" Background="{DynamicResource HeaderBackground}" Padding="16">
            <StackPanel>
                <TextBlock Text="LoRA Training Dataset Builder" 
                           FontSize="24" FontWeight="Bold"/>
                <TextBlock Text="Select images, configure captions, and export for training"
                           Opacity="0.7" Margin="0,4,0,0"/>
            </StackPanel>
        </Border>
        
        <!-- Main Content - Steps -->
        <TabControl Grid.Row="1" x:Name="StepsTabControl" Margin="16">
            
            <!-- Step 1: Image Selection -->
            <TabItem Header="1. Select Images">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="300"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    
                    <!-- Filters Panel -->
                    <Border Grid.Column="0" Background="{DynamicResource PanelBackground}" 
                            Padding="12" Margin="0,0,8,0">
                        <StackPanel>
                            <TextBlock Text="FILTERS" FontWeight="Bold" Margin="0,0,0,12"/>
                            
                            <TextBlock Text="Character Cluster"/>
                            <ComboBox x:Name="ClusterComboBox" Margin="0,4,0,12"
                                      ItemsSource="{Binding Clusters}"
                                      DisplayMemberPath="Name"/>
                            
                            <TextBlock Text="Minimum Rating"/>
                            <ComboBox x:Name="RatingComboBox" Margin="0,4,0,12"
                                      SelectedIndex="3">
                                <ComboBoxItem Content="Any"/>
                                <ComboBoxItem Content="★ 1+"/>
                                <ComboBoxItem Content="★★ 2+"/>
                                <ComboBoxItem Content="★★★ 3+"/>
                                <ComboBoxItem Content="★★★★ 4+"/>
                                <ComboBoxItem Content="★★★★★ 5"/>
                            </ComboBox>
                            
                            <CheckBox x:Name="ExcludeBadCheckBox" 
                                      Content="Exclude marked bad" 
                                      IsChecked="True" Margin="0,0,0,12"/>
                            
                            <TextBlock Text="Minimum Resolution"/>
                            <ComboBox x:Name="ResolutionComboBox" Margin="0,4,0,12">
                                <ComboBoxItem Content="Any"/>
                                <ComboBoxItem Content="512px"/>
                                <ComboBoxItem Content="768px"/>
                                <ComboBoxItem Content="1024px" IsSelected="True"/>
                            </ComboBox>
                            
                            <Button Content="Search Images" 
                                    Command="{Binding SearchCommand}"
                                    Style="{DynamicResource PrimaryButton}"
                                    Margin="0,12,0,0"/>
                            
                            <TextBlock x:Name="ResultCountText" 
                                       Text="0 images found"
                                       Margin="0,8,0,0" Opacity="0.7"/>
                        </StackPanel>
                    </Border>
                    
                    <!-- Image Grid -->
                    <Border Grid.Column="1" Background="{DynamicResource PanelBackground}">
                        <ListBox x:Name="ImageListBox"
                                 ItemsSource="{Binding FilteredImages}"
                                 SelectionMode="Extended"
                                 ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                            <ListBox.ItemsPanel>
                                <ItemsPanelTemplate>
                                    <WrapPanel/>
                                </ItemsPanelTemplate>
                            </ListBox.ItemsPanel>
                            <ListBox.ItemTemplate>
                                <DataTemplate>
                                    <Border Width="120" Height="120" Margin="4"
                                            BorderBrush="{Binding IsSelected, Converter={StaticResource BoolToBorderConverter}}"
                                            BorderThickness="2">
                                        <Grid>
                                            <Image Source="{Binding ThumbnailPath}" Stretch="UniformToFill"/>
                                            <Border Background="#80000000" VerticalAlignment="Bottom" Padding="4">
                                                <TextBlock Text="{Binding Rating, StringFormat='★{0}'}" 
                                                           Foreground="Gold" FontSize="10"/>
                                            </Border>
                                        </Grid>
                                    </Border>
                                </DataTemplate>
                            </ListBox.ItemTemplate>
                        </ListBox>
                    </Border>
                </Grid>
            </TabItem>
            
            <!-- Step 2: Caption Configuration -->
            <TabItem Header="2. Captions">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    
                    <!-- Caption Settings -->
                    <Border Grid.Row="0" Background="{DynamicResource PanelBackground}" 
                            Padding="12" Margin="0,0,0,8">
                        <WrapPanel>
                            <StackPanel Orientation="Horizontal" Margin="0,0,24,0">
                                <TextBlock Text="Caption Source:" VerticalAlignment="Center"/>
                                <ComboBox x:Name="CaptionSourceComboBox" Width="150" Margin="8,0,0,0">
                                    <ComboBoxItem Content="WDv3 Tags"/>
                                    <ComboBoxItem Content="JoyTag Tags"/>
                                    <ComboBoxItem Content="JoyCaption"/>
                                    <ComboBoxItem Content="Combined (Deduplicated)" IsSelected="True"/>
                                    <ComboBoxItem Content="Original Prompt"/>
                                </ComboBox>
                            </StackPanel>
                            
                            <StackPanel Orientation="Horizontal" Margin="0,0,24,0">
                                <TextBlock Text="Trigger Word:" VerticalAlignment="Center"/>
                                <TextBox x:Name="TriggerWordTextBox" Width="200" Margin="8,0,0,0"
                                         Text="{Binding TriggerWord}"/>
                            </StackPanel>
                            
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="Position:" VerticalAlignment="Center"/>
                                <ComboBox x:Name="TriggerPositionComboBox" Width="100" Margin="8,0,0,0">
                                    <ComboBoxItem Content="Prepend" IsSelected="True"/>
                                    <ComboBoxItem Content="Append"/>
                                    <ComboBoxItem Content="None"/>
                                </ComboBox>
                            </StackPanel>
                            
                            <Button Content="Apply to All" 
                                    Command="{Binding ApplyCaptionsCommand}"
                                    Margin="24,0,0,0"/>
                        </WrapPanel>
                    </Border>
                    
                    <!-- Caption Preview/Edit Grid -->
                    <DataGrid Grid.Row="1" 
                              ItemsSource="{Binding SelectedImages}"
                              AutoGenerateColumns="False"
                              CanUserAddRows="False">
                        <DataGrid.Columns>
                            <DataGridTemplateColumn Header="Image" Width="80">
                                <DataGridTemplateColumn.CellTemplate>
                                    <DataTemplate>
                                        <Image Source="{Binding ThumbnailPath}" 
                                               Width="60" Height="60"/>
                                    </DataTemplate>
                                </DataGridTemplateColumn.CellTemplate>
                            </DataGridTemplateColumn>
                            
                            <DataGridTextColumn Header="Final Caption" 
                                                Binding="{Binding FinalCaption}"
                                                Width="*"/>
                            
                            <DataGridTemplateColumn Header="Source" Width="100">
                                <DataGridTemplateColumn.CellTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding CaptionSource}"/>
                                    </DataTemplate>
                                </DataGridTemplateColumn.CellTemplate>
                            </DataGridTemplateColumn>
                            
                            <DataGridTemplateColumn Header="Edited" Width="60">
                                <DataGridTemplateColumn.CellTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding IsEdited, Converter={StaticResource BoolToYesNoConverter}}"/>
                                    </DataTemplate>
                                </DataGridTemplateColumn.CellTemplate>
                            </DataGridTemplateColumn>
                        </DataGrid.Columns>
                    </DataGrid>
                </Grid>
            </TabItem>
            
            <!-- Step 3: Training Config -->
            <TabItem Header="3. Training Config">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="12">
                        
                        <!-- Trainer Selection -->
                        <GroupBox Header="Trainer" Margin="0,0,0,12">
                            <StackPanel Margin="8">
                                <StackPanel Orientation="Horizontal">
                                    <RadioButton Content="kohya-ss" IsChecked="True" GroupName="Trainer"/>
                                    <RadioButton Content="OneTrainer" Margin="16,0,0,0" GroupName="Trainer"/>
                                    <RadioButton Content="SimpleTuner" Margin="16,0,0,0" GroupName="Trainer"/>
                                </StackPanel>
                            </StackPanel>
                        </GroupBox>
                        
                        <!-- Model Settings -->
                        <GroupBox Header="Model" Margin="0,0,0,12">
                            <Grid Margin="8">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="150"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="Auto"/>
                                    <RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>
                                
                                <TextBlock Text="Base Model:" VerticalAlignment="Center"/>
                                <ComboBox Grid.Column="1" x:Name="BaseModelComboBox"
                                          ItemsSource="{Binding AvailableModels}"
                                          Margin="0,0,0,8"/>
                                
                                <TextBlock Grid.Row="1" Text="Model Type:" VerticalAlignment="Center"/>
                                <ComboBox Grid.Row="1" Grid.Column="1" x:Name="ModelTypeComboBox">
                                    <ComboBoxItem Content="SDXL"/>
                                    <ComboBoxItem Content="Illustrious" IsSelected="True"/>
                                    <ComboBoxItem Content="Pony"/>
                                    <ComboBoxItem Content="Flux"/>
                                    <ComboBoxItem Content="SD 1.5"/>
                                </ComboBox>
                            </Grid>
                        </GroupBox>
                        
                        <!-- Network Settings -->
                        <GroupBox Header="Network (LoRA)" Margin="0,0,0,12">
                            <UniformGrid Columns="4" Margin="8">
                                <StackPanel Margin="0,0,8,0">
                                    <TextBlock Text="Rank (dim)"/>
                                    <ComboBox SelectedIndex="2">
                                        <ComboBoxItem Content="8"/>
                                        <ComboBoxItem Content="16"/>
                                        <ComboBoxItem Content="32"/>
                                        <ComboBoxItem Content="64"/>
                                        <ComboBoxItem Content="128"/>
                                    </ComboBox>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,0">
                                    <TextBlock Text="Alpha"/>
                                    <ComboBox SelectedIndex="1">
                                        <ComboBoxItem Content="8"/>
                                        <ComboBoxItem Content="16"/>
                                        <ComboBoxItem Content="32"/>
                                    </ComboBox>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,0">
                                    <TextBlock Text="Type"/>
                                    <ComboBox SelectedIndex="0">
                                        <ComboBoxItem Content="LoRA"/>
                                        <ComboBoxItem Content="LoCon"/>
                                        <ComboBoxItem Content="LoHa"/>
                                        <ComboBoxItem Content="DoRA"/>
                                    </ComboBox>
                                </StackPanel>
                            </UniformGrid>
                        </GroupBox>
                        
                        <!-- Training Parameters -->
                        <GroupBox Header="Training" Margin="0,0,0,12">
                            <UniformGrid Columns="4" Margin="8">
                                <StackPanel Margin="0,0,8,8">
                                    <TextBlock Text="Resolution"/>
                                    <ComboBox SelectedIndex="2">
                                        <ComboBoxItem Content="512"/>
                                        <ComboBoxItem Content="768"/>
                                        <ComboBoxItem Content="1024"/>
                                    </ComboBox>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,8">
                                    <TextBlock Text="Repeats"/>
                                    <TextBox Text="10"/>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,8">
                                    <TextBlock Text="Epochs"/>
                                    <TextBox Text="5"/>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,8">
                                    <TextBlock Text="Batch Size"/>
                                    <TextBox Text="1"/>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,8">
                                    <TextBlock Text="Learning Rate"/>
                                    <TextBox Text="1e-4"/>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,8">
                                    <TextBlock Text="LR Scheduler"/>
                                    <ComboBox SelectedIndex="0">
                                        <ComboBoxItem Content="cosine"/>
                                        <ComboBoxItem Content="constant"/>
                                        <ComboBoxItem Content="polynomial"/>
                                    </ComboBox>
                                </StackPanel>
                                <StackPanel Margin="0,0,8,8">
                                    <TextBlock Text="Optimizer"/>
                                    <ComboBox SelectedIndex="0">
                                        <ComboBoxItem Content="AdamW8bit"/>
                                        <ComboBoxItem Content="AdaFactor"/>
                                        <ComboBoxItem Content="Prodigy"/>
                                    </ComboBox>
                                </StackPanel>
                            </UniformGrid>
                        </GroupBox>
                        
                        <!-- Augmentation -->
                        <GroupBox Header="Augmentation" Margin="0,0,0,12">
                            <WrapPanel Margin="8">
                                <CheckBox Content="Flip augmentation" IsChecked="True" Margin="0,0,16,0"/>
                                <CheckBox Content="Color jitter" Margin="0,0,16,0"/>
                                <CheckBox Content="Random crop" Margin="0,0,16,0"/>
                                <CheckBox Content="Bucket resolution" IsChecked="True" Margin="0,0,16,0"/>
                            </WrapPanel>
                        </GroupBox>
                        
                        <!-- Regularization -->
                        <GroupBox Header="Regularization" Margin="0,0,0,12">
                            <StackPanel Margin="8">
                                <CheckBox x:Name="UseRegularizationCheckBox"
                                          Content="Include regularization images (auto-select similar style, different subject)"
                                          IsChecked="True"/>
                                <StackPanel Orientation="Horizontal" Margin="24,8,0,0"
                                            IsEnabled="{Binding IsChecked, ElementName=UseRegularizationCheckBox}">
                                    <TextBlock Text="Count:" VerticalAlignment="Center"/>
                                    <TextBox Text="200" Width="60" Margin="8,0,0,0"/>
                                    <TextBlock Text="Repeats:" VerticalAlignment="Center" Margin="16,0,0,0"/>
                                    <TextBox Text="1" Width="40" Margin="8,0,0,0"/>
                                </StackPanel>
                            </StackPanel>
                        </GroupBox>
                        
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
            
            <!-- Step 4: Export -->
            <TabItem Header="4. Export">
                <Grid Margin="12">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    
                    <!-- Output Path -->
                    <GroupBox Header="Output" Grid.Row="0">
                        <Grid Margin="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <TextBox x:Name="OutputPathTextBox" 
                                     Text="{Binding OutputPath}"
                                     IsReadOnly="True"/>
                            <Button Grid.Column="1" Content="Browse..." 
                                    Command="{Binding BrowseOutputCommand}"
                                    Margin="8,0,0,0"/>
                        </Grid>
                    </GroupBox>
                    
                    <!-- Summary -->
                    <GroupBox Header="Summary" Grid.Row="1" Margin="0,12,0,0">
                        <StackPanel Margin="8">
                            <TextBlock x:Name="SummaryText">
                                <Run Text="Training images: "/><Run Text="{Binding SelectedImages.Count}" FontWeight="Bold"/>
                                <LineBreak/>
                                <Run Text="Regularization images: "/><Run Text="{Binding RegularizationImages.Count}" FontWeight="Bold"/>
                                <LineBreak/>
                                <Run Text="Total training steps: "/><Run Text="{Binding TotalSteps}" FontWeight="Bold"/>
                                <LineBreak/>
                                <Run Text="Estimated time: "/><Run Text="{Binding EstimatedTime}" FontWeight="Bold"/>
                            </TextBlock>
                        </StackPanel>
                    </GroupBox>
                    
                    <!-- Progress -->
                    <GroupBox Header="Export Progress" Grid.Row="2" Margin="0,12,0,0"
                              Visibility="{Binding IsExporting, Converter={StaticResource BoolToVisibilityConverter}}">
                        <StackPanel Margin="8">
                            <ProgressBar Value="{Binding ExportProgress}" Height="20"/>
                            <TextBlock Text="{Binding ExportStatus}" Margin="0,8,0,0"/>
                        </StackPanel>
                    </GroupBox>
                </Grid>
            </TabItem>
        </TabControl>
        
        <!-- Footer -->
        <Border Grid.Row="2" Background="{DynamicResource FooterBackground}" Padding="16">
            <Grid>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Left">
                    <Button Content="← Previous" Command="{Binding PreviousStepCommand}"/>
                </StackPanel>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                    <Button Content="Next →" Command="{Binding NextStepCommand}" Margin="0,0,8,0"/>
                    <Button Content="Export Dataset" 
                            Command="{Binding ExportCommand}"
                            Style="{DynamicResource PrimaryButton}"
                            Margin="0,0,8,0"/>
                    <Button Content="Export & Train" 
                            Command="{Binding ExportAndTrainCommand}"
                            Style="{DynamicResource AccentButton}"/>
                </StackPanel>
            </Grid>
        </Border>
    </Grid>
</Window>
```

---

## Settings Extensions

### Add to Settings.cs

```csharp
// LoRA Training Settings
public string? KohyaSdScriptsPath { get; set; }
public string? KohyaPythonPath { get; set; }
public string? OneTrainerPath { get; set; }
public string? SimpleTunerPath { get; set; }
public string DefaultTrainingOutputPath { get; set; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    "LoRA Training");

// Default training parameters (user preferences)
public int DefaultNetworkRank { get; set; } = 32;
public int DefaultNetworkAlpha { get; set; } = 16;
public int DefaultTrainingResolution { get; set; } = 1024;
public int DefaultRepeats { get; set; } = 10;
public int DefaultEpochs { get; set; } = 5;
public float DefaultLearningRate { get; set; } = 1e-4f;
public bool DefaultUseRegularization { get; set; } = true;
public int DefaultRegularizationCount { get; set; } = 200;
```

---

## Database Extensions

### New Table: lora_datasets

```sql
CREATE TABLE lora_datasets (
    id SERIAL PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    trigger_word VARCHAR(255),
    trigger_position VARCHAR(20) DEFAULT 'prepend',
    
    -- Config JSON (full TrainingConfig serialized)
    config JSONB NOT NULL,
    
    -- Timestamps
    created_at TIMESTAMP DEFAULT NOW(),
    exported_at TIMESTAMP,
    training_started_at TIMESTAMP,
    training_completed_at TIMESTAMP,
    
    -- Paths
    output_path TEXT,
    trained_lora_path TEXT,
    
    -- Stats
    training_image_count INT DEFAULT 0,
    regularization_image_count INT DEFAULT 0,
    
    -- Status
    status VARCHAR(50) DEFAULT 'draft'  -- draft, exported, training, completed, failed
);

CREATE TABLE lora_dataset_images (
    id SERIAL PRIMARY KEY,
    dataset_id INT REFERENCES lora_datasets(id) ON DELETE CASCADE,
    image_id BIGINT REFERENCES image(id),
    
    is_regularization BOOLEAN DEFAULT FALSE,
    
    -- Caption at time of export (may differ from current caption)
    exported_caption TEXT,
    caption_source VARCHAR(50),
    was_edited BOOLEAN DEFAULT FALSE,
    
    -- Export path
    exported_path TEXT
);

CREATE INDEX idx_lora_dataset_images_dataset ON lora_dataset_images(dataset_id);
CREATE INDEX idx_lora_dataset_images_image ON lora_dataset_images(image_id);
```

---

## Integration Points with Existing DT Features

### 1. Cluster View → Training

```csharp
// In ClusterViewModel or similar
public ICommand CreateTrainingDatasetCommand => new RelayCommand(async () =>
{
    var filter = new DatasetFilter
    {
        ClusterId = SelectedCluster.Id,
        MinRating = 4,
        ExcludeMarkedBad = true
    };
    
    var window = new DatasetBuilderWindow(filter);
    window.Show();
});
```

### 2. Search Results → Training

```csharp
// In SearchViewModel
public ICommand CreateDatasetFromSelectionCommand => new RelayCommand(async () =>
{
    var selectedIds = SelectedImages.Select(i => i.Id).ToList();
    var window = new DatasetBuilderWindow(selectedIds);
    window.Show();
});
```

### 3. Caption Pipeline Integration

The existing JoyTag + WDv3 + JoyCaption pipeline feeds directly into training:

```csharp
// CaptionService already exists - just query it
var image = await _dataStore.GetImageAsync(imageId);

var trainingImage = new TrainingImage
{
    Wd14Tags = image.Wd14Tags,          // Already computed
    JoyTagTags = image.JoyTagTags,      // Already computed
    JoyCaptionText = image.JoyCaption,  // Already computed
    // No new computation needed!
};
```

### 4. Deduplication Dictionary

```csharp
// Use existing dedup dictionary for tag merging
var deduplicatedTags = _captionService.MergeAndDeduplicate(
    image.Wd14Tags,
    image.JoyTagTags,
    _settings.DeduplicationDictionary);
```

---

## Future Enhancements

1. **Training Progress Monitor** - Real-time loss graphs, sample previews during training
2. **LoRA Testing Integration** - After training, auto-test in embedded ComfyUI
3. **Dataset Versioning** - Track changes to datasets over time
4. **A/B Testing** - Compare multiple LoRAs trained on different dataset variants
5. **Auto-Regularization Quality** - Use ratings to select best regularization images
6. **Prompt Analysis** - Analyze which caption styles produce best results

---

## Summary

This integration leverages DT's existing strengths:
- ✅ **Captioning already done** - JoyTag + WDv3 + JoyCaption pipeline
- ✅ **Smart filtering** - Clusters, ratings, embeddings
- ✅ **Auto regularization** - CLIP-H similarity for style-matched images
- ✅ **No duplicate work** - Captions stored in DB, reused for training
- ✅ **Seamless workflow** - Filter → Preview → Configure → Export → Train
