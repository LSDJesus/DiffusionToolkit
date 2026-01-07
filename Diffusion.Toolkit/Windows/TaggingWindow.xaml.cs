using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Windows;

public partial class TaggingWindow : Window, INotifyPropertyChanged
{
    private readonly List<int> _imageIds;
    private CancellationTokenSource? _cancellationTokenSource;

    private bool _isProcessing;
    private int _processedImages;
    private int _totalImages;
    private string _progressMessage = "";
    private string _currentImageName = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public TaggingWindow(List<int> imageIds, bool taggingOnly = false, bool captioningOnly = false)
    {
        InitializeComponent();
        _imageIds = imageIds;
        DataContext = this;

        // Check service availability
        JoyTagAvailable = ServiceLocator.JoyTagService != null;
        WDTagAvailable = ServiceLocator.WDTagService != null;
        TaggingAvailable = JoyTagAvailable && WDTagAvailable; // Both must be available
        
        // Caption service is initialized on-demand when captioning starts, so always allow it if configured
        JoyCaptionAvailable = ServiceLocator.Settings?.CaptionProvider == CaptionProviderType.LocalJoyCaption 
            && !string.IsNullOrEmpty(ServiceLocator.Settings?.JoyCaptionModelPath);

        TaggingStatus = TaggingAvailable 
            ? $"Ready (JoyTag threshold: {ServiceLocator.Settings?.JoyTagThreshold ?? 0.5f:F2}, WD threshold: {ServiceLocator.Settings?.WDTagThreshold ?? 0.5f:F2})" 
            : (JoyTagAvailable ? "WD model not found" : WDTagAvailable ? "JoyTag model not found" : "Models not found");
        JoyCaptionStatus = JoyCaptionAvailable 
            ? "Ready (will initialize on start)" 
            : "Not configured";

        // Embedding is available if ONNX models exist (check for CLIP-ViT-H model)
        var embeddingModelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "onnx", "clip-vit-h", "model.onnx");
        EmbeddingAvailable = System.IO.File.Exists(embeddingModelPath);
        EmbeddingStatus = EmbeddingAvailable 
            ? "Ready (BGE + CLIP-ViT-H)" 
            : "Models not found";
        EmbeddingEnabled = ServiceLocator.Settings?.TagDialogEmbeddingEnabled ?? false;

        // Face detection is available if ONNX models exist
        var retinaFaceModelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "onnx", "retinaface", "det_10g.onnx");
        var arcFaceModelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "onnx", "arcface", "w600k_r50.onnx");
        FaceDetectionAvailable = System.IO.File.Exists(retinaFaceModelPath) && System.IO.File.Exists(arcFaceModelPath);
        FaceDetectionStatus = FaceDetectionAvailable 
            ? "Ready (RetinaFace + ArcFace)" 
            : "Models not found";
        FaceDetectionEnabled = ServiceLocator.Settings?.TagDialogFaceDetectionEnabled ?? false;

        ImageCount = imageIds.Count;
        
        // Set default prompt based on settings after InitializeComponent
        Loaded += (s, e) =>
        {
            var defaultPrompt = ServiceLocator.Settings?.JoyCaptionDefaultPrompt ?? "detailed";
            for (int i = 0; i < CaptionPromptComboBox.Items.Count; i++)
            {
                if (CaptionPromptComboBox.Items[i] is ComboBoxItem item && 
                    item.Tag?.ToString() == defaultPrompt)
                {
                    CaptionPromptComboBox.SelectedIndex = i;
                    break;
                }
            }
            
            // Load saved checkbox states from settings
            if (!taggingOnly && !captioningOnly)
            {
                AutoTagCheckBox.IsChecked = ServiceLocator.Settings?.TagDialogAutoTagEnabled ?? true;
                JoyCaptionCheckBox.IsChecked = ServiceLocator.Settings?.TagDialogCaptionEnabled ?? false;
                EmbeddingCheckBox.IsChecked = ServiceLocator.Settings?.TagDialogEmbeddingEnabled ?? false;
                FaceDetectionCheckBox.IsChecked = ServiceLocator.Settings?.TagDialogFaceDetectionEnabled ?? false;
            }
            else if (taggingOnly)
            {
                Title = "Auto-Tag Images";
                AutoTagCheckBox.IsChecked = true;
                JoyCaptionCheckBox.IsChecked = false;
                JoyCaptionCheckBox.IsEnabled = false;
            }
            else if (captioningOnly)
            {
                Title = "Caption Images";
                AutoTagCheckBox.IsChecked = false;
                AutoTagCheckBox.IsEnabled = false;
                JoyCaptionCheckBox.IsChecked = true;
            }
        };
    }

    public bool JoyTagAvailable { get; }
    public bool WDTagAvailable { get; }
    public bool TaggingAvailable { get; } // Both taggers available
    public bool JoyCaptionAvailable { get; }
    public bool EmbeddingAvailable { get; }
    public bool FaceDetectionAvailable { get; }
    
    public string TaggingStatus { get; } // Combined status
    public string JoyCaptionStatus { get; }
    public string EmbeddingStatus { get; }
    public string FaceDetectionStatus { get; }
    
    public bool EmbeddingEnabled { get; set; }
    public bool FaceDetectionEnabled { get; set; }

    public int ImageCount { get; }

    public enum ProcessMode
    {
        None,
        ProcessNow,  // High priority / front of queue
        AddToQueue   // Background queue
    }

    public ProcessMode SelectedProcessMode { get; private set; } = ProcessMode.None;

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            _isProcessing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public bool CanStart => !IsProcessing && (AutoTagCheckBox.IsChecked == true || 
                                              JoyCaptionCheckBox.IsChecked == true ||
                                              EmbeddingCheckBox.IsChecked == true ||
                                              FaceDetectionCheckBox.IsChecked == true);

    public int ProcessedImages
    {
        get => _processedImages;
        set
        {
            _processedImages = value;
            OnPropertyChanged();
        }
    }

    public int TotalImages
    {
        get => _totalImages;
        set
        {
            _totalImages = value;
            OnPropertyChanged();
        }
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        set
        {
            _progressMessage = value;
            OnPropertyChanged();
        }
    }

    public string CurrentImageName
    {
        get => _currentImageName;
        set
        {
            _currentImageName = value;
            OnPropertyChanged();
        }
    }

    private async void ProcessNow_Click(object sender, RoutedEventArgs e)
    {
        SelectedProcessMode = ProcessMode.ProcessNow;
        DialogResult = true;
        Close();
    }

    private async void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        SelectedProcessMode = ProcessMode.AddToQueue;
        DialogResult = true;
        Close();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        IsProcessing = true;
        _cancellationTokenSource = new CancellationTokenSource();
        TotalImages = _imageIds.Count;
        ProcessedImages = 0;

        try
        {
            var dataStore = ServiceLocator.DataStore;
            if (dataStore == null)
            {
                MessageBox.Show("PostgreSQL database not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var runTagging = AutoTagCheckBox.IsChecked == true && TaggingAvailable;
            var runJoyCaption = JoyCaptionCheckBox.IsChecked == true && ServiceLocator.CaptionService != null;
            var runFaceDetection = FaceDetectionCheckBox?.IsChecked == true && FaceDetectionAvailable;
            var storeConfidence = ServiceLocator.Settings?.StoreTagConfidence ?? false;
            var maxConcurrency = ServiceLocator.Settings?.TaggingConcurrentWorkers ?? 8;
            var skipAlreadyTagged = ServiceLocator.Settings?.SkipAlreadyTaggedImages ?? true;

            Logger.Log($"TaggingWindow: runTagging={runTagging}, runJoyCaption={runJoyCaption}, runFaceDetection={runFaceDetection}");
            Logger.Log($"  AutoTagCheckBox.IsChecked={AutoTagCheckBox.IsChecked}, TaggingAvailable={TaggingAvailable}");
            Logger.Log($"  JoyCaptionCheckBox.IsChecked={JoyCaptionCheckBox.IsChecked}, CaptionService={ServiceLocator.CaptionService != null}");
            Logger.Log($"  FaceDetectionCheckBox.IsChecked={FaceDetectionCheckBox?.IsChecked}, FaceDetectionAvailable={FaceDetectionAvailable}");
            Logger.Log($"  Parallel processing with {maxConcurrency} concurrent workers, skipAlreadyTagged={skipAlreadyTagged}");

            // Queue images for face detection if enabled
            if (runFaceDetection)
            {
                Logger.Log($"Queueing {_imageIds.Count} images for face detection");
                await dataStore.SetNeedsFaceDetection(_imageIds, true);
                
                // Start background face detection service
                var faceService = ServiceLocator.BackgroundFaceDetectionService;
                if (faceService != null && !faceService.IsFaceDetectionRunning)
                {
                    faceService.StartFaceDetection();
                    Logger.Log("Started background face detection service");
                }
            }

            // Use parallel processing for better GPU utilization
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = _cancellationTokenSource.Token
            };

            await Parallel.ForEachAsync(_imageIds, parallelOptions, async (imageId, ct) =>
            {
                if (ct.IsCancellationRequested)
                    return;

                var image = dataStore.GetImage(imageId);
                if (image == null) return;

                // Skip if tags already exist and user preference is set
                if (skipAlreadyTagged && runTagging)
                {
                    var existingTags = await dataStore.GetImageTagsAsync(imageId);
                    if (existingTags.Count > 0)
                    {
                        Logger.Log($"Skipping image {imageId} - already has {existingTags.Count} tags");
                        Interlocked.Increment(ref _processedImages);
                        Dispatcher.Invoke(() => OnPropertyChanged(nameof(ProcessedImages)));
                        return;
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    CurrentImageName = System.IO.Path.GetFileName(image.Path);
                    ProgressMessage = "Processing...";
                });

                // Auto-tag always runs both taggers together
                if (runTagging)
                {
                    await Task.Run(async () =>
                    {
                        // Run both taggers (JoyTag + WD v3 Large)
                        var joyTagsTask = ServiceLocator.JoyTagService!.TagImageAsync(image.Path);
                        var wdTagsTask = ServiceLocator.WDTagService!.TagImageAsync(image.Path);
                        await Task.WhenAll(joyTagsTask, wdTagsTask);

                        var joyTags = await joyTagsTask;
                        var wdTags = await wdTagsTask;

                        // Combine all tags (deduplication handled at database layer)
                        var allTags = joyTags.Concat(wdTags).Select(t => (t.Tag, t.Confidence)).ToList();

                        Logger.Log($"Tagging combined (JoyTag+WDTag): {allTags.Count} tags for image {imageId}");
                        await dataStore.StoreImageTagsAsync(imageId, allTags, "joytag+wdv3large");
                        
                        // Auto-write metadata removed - use context menu "Write Metadata to Files" instead
                        // Logger.Log($"WriteTagsToMetadata={ServiceLocator.Settings?.WriteTagsToMetadata}");
                        //if (false) // Auto-write disabled
                        //{
                            // Get deduplicated tags from database for metadata write
                            //var dbTags = await dataStore.GetImageTagsAsync(imageId, "joytag+wdv3large");
                            
                            // Get latest caption to preserve it
                            //var latestCaption = await dataStore.GetLatestCaptionAsync(imageId);
                            
                            // Preserve ALL existing metadata
                            /*var request = new Scanner.MetadataWriteRequest
                            {
                                Prompt = image.Prompt,
                                NegativePrompt = image.NegativePrompt,
                                Steps = image.Steps,
                                Sampler = image.Sampler,
                                CFGScale = image.CfgScale,
                                Seed = image.Seed,
                                Width = image.Width,
                                Height = image.Height,
                                Model = image.Model,
                                ModelHash = image.ModelHash,
                                Tags = dbTags.Select(t => new Scanner.TagWithConfidence 
                                { 
                                    Tag = t.Tag, 
                                    Confidence = t.Confidence 
                                }).ToList(),
                                Caption = latestCaption?.Caption, // Preserve caption!
                                AestheticScore = image.AestheticScore > 0 ? (decimal?)image.AestheticScore : null,
                                CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                            };
                            Scanner.MetadataWriter.WriteMetadata(image.Path, request);*/
                        //}
                    }, _cancellationTokenSource.Token);
                }
                    
                // Run caption if enabled
                if (runJoyCaption)
                {
                    await RunCaptionAsync(dataStore, image, imageId);
                }

                Interlocked.Increment(ref _processedImages);
                Dispatcher.Invoke(() => OnPropertyChanged(nameof(ProcessedImages)));
            });

            ProgressMessage = "Complete!";
            MessageBox.Show($"Successfully processed {ProcessedImages} images!", "Success", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancelled";
            MessageBox.Show("Processing cancelled by user.", "Cancelled", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error occurred";
            MessageBox.Show($"Error during tagging: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing)
        {
            _cancellationTokenSource?.Cancel();
        }
        else
        {
            DialogResult = false;
            Close();
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Run caption generation for a single image
    /// </summary>
    private async Task RunCaptionAsync(Diffusion.Database.PostgreSQL.PostgreSQLDataStore dataStore, 
        Diffusion.Database.PostgreSQL.Models.ImageEntity image, int imageId)
    {
        // Get selected prompt
        string selectedPrompt = "detailed";
        Dispatcher.Invoke(() =>
        {
            if (CaptionPromptComboBox.SelectedItem is ComboBoxItem selected)
            {
                selectedPrompt = selected.Tag?.ToString() ?? "detailed";
            }
        });
        
        // Map prompt tag to actual prompt string
        var promptText = selectedPrompt switch
        {
            "detailed" => Captioning.Services.JoyCaptionService.PROMPT_DETAILED,
            "short" => Captioning.Services.JoyCaptionService.PROMPT_SHORT,
            "technical" => Captioning.Services.JoyCaptionService.PROMPT_TECHNICAL,
            "precise_colors" => Captioning.Services.JoyCaptionService.PROMPT_PRECISE_COLORS,
            "character" => Captioning.Services.JoyCaptionService.PROMPT_CHARACTER_FOCUS,
            "scene" => Captioning.Services.JoyCaptionService.PROMPT_SCENE_FOCUS,
            "artistic" => Captioning.Services.JoyCaptionService.PROMPT_ARTISTIC_STYLE,
            "booru" => Captioning.Services.JoyCaptionService.PROMPT_BOORU_TAGS,
            _ => Captioning.Services.JoyCaptionService.PROMPT_DETAILED
        };

        // Handle existing captions based on mode
        var handlingMode = ServiceLocator.Settings?.CaptionHandlingMode ?? CaptionHandlingMode.Overwrite;
        var existingCaption = await dataStore.GetLatestCaptionAsync(imageId);
        
        Logger.Log($"Captioning image {imageId}: mode={handlingMode}, existing caption length={existingCaption?.Caption?.Length ?? 0}");
        
        string finalPrompt = promptText;
        string? finalCaption = null;

        switch (handlingMode)
        {
            case CaptionHandlingMode.Refine:
                if (existingCaption != null && !string.IsNullOrWhiteSpace(existingCaption.Caption))
                {
                    finalPrompt = $"{promptText}\n\nExisting caption to refine/expand/correct: \"{existingCaption.Caption}\"";
                    Logger.Log($"Refine mode: using existing caption as context, new prompt length={finalPrompt.Length}");
                }
                else
                {
                    Logger.Log($"Refine mode: no existing caption found, using standard prompt");
                }
                break;

            case CaptionHandlingMode.Append:
                break;

            case CaptionHandlingMode.Overwrite:
            default:
                break;
        }
        
        Logger.Log($"Calling CaptionService with prompt: {finalPrompt.Substring(0, Math.Min(100, finalPrompt.Length))}...");
        
        var result = await ServiceLocator.CaptionService!.CaptionImageAsync(
            image.Path, 
            finalPrompt);

        Logger.Log($"Caption result: {result.Caption?.Substring(0, Math.Min(100, result.Caption?.Length ?? 0))}...");
        Logger.Log($"Result length: {result.Caption?.Length ?? 0}, PromptUsed: {result.PromptUsed}");

        // Handle append mode
        if (handlingMode == CaptionHandlingMode.Append && 
            existingCaption != null && 
            !string.IsNullOrWhiteSpace(existingCaption.Caption))
        {
            finalCaption = $"{existingCaption.Caption} {result.Caption}";
        }
        else if (handlingMode == CaptionHandlingMode.Refine)
        {
            // For refine mode, use the result if it's not empty, otherwise keep existing
            if (!string.IsNullOrWhiteSpace(result.Caption))
            {
                finalCaption = result.Caption;
                Logger.Log($"Refine mode: using new caption result");
            }
            else
            {
                finalCaption = existingCaption?.Caption;
                Logger.Log($"Refine mode: result was empty, keeping existing caption");
            }
        }
        else
        {
            finalCaption = result.Caption;
        }
        
        Logger.Log($"Final caption (len={finalCaption?.Length ?? 0}): {finalCaption?.Substring(0, Math.Min(150, finalCaption?.Length ?? 0))}");
        
        var captionSource = ServiceLocator.Settings?.CaptionProvider == CaptionProviderType.LocalJoyCaption ? "joycaption" : "openai";
        
        Logger.Log($"Storing caption in database for image {imageId}, source={captionSource}");
        await dataStore.StoreCaptionAsync(imageId, finalCaption ?? "", captionSource, 
            result.PromptUsed, result.TokenCount, (float)result.GenerationTimeMs);
        Logger.Log($"Caption stored successfully");
        
        // Write caption to image metadata if enabled
        // Auto-write disabled - use context menu instead
        //if (false)
        //{
            //Logger.Log($"Writing caption to metadata file: {image.Path}");
            // Preserve ALL existing metadata
            /*var request = new Scanner.MetadataWriteRequest
            {
                Prompt = image.Prompt,
                NegativePrompt = image.NegativePrompt,
                Steps = image.Steps,
                Sampler = image.Sampler,
                CFGScale = image.CfgScale,
                Seed = image.Seed,
                Width = image.Width,
                Height = image.Height,
                Model = image.Model,
                ModelHash = image.ModelHash,
                Caption = finalCaption,
                AestheticScore = image.AestheticScore > 0 ? (decimal?)image.AestheticScore : null,
                CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
            };
            var writeResult = Scanner.MetadataWriter.WriteMetadata(image.Path, request);
            Logger.Log($"Metadata write result: {writeResult}");
        }*/
        //}
    }

    private void AutoTagCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(CanStart));
        if (ServiceLocator.Settings != null)
        {
            ServiceLocator.Settings.TagDialogAutoTagEnabled = AutoTagCheckBox.IsChecked == true;
        }
    }

    private void JoyCaptionCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(CanStart));
        if (ServiceLocator.Settings != null)
        {
            ServiceLocator.Settings.TagDialogCaptionEnabled = JoyCaptionCheckBox.IsChecked == true;
        }
    }

    private void EmbeddingCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(CanStart));
        if (ServiceLocator.Settings != null)
        {
            ServiceLocator.Settings.TagDialogEmbeddingEnabled = EmbeddingCheckBox.IsChecked == true;
        }
    }

    private void FaceDetectionCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(CanStart));
        if (ServiceLocator.Settings != null)
        {
            ServiceLocator.Settings.TagDialogFaceDetectionEnabled = FaceDetectionCheckBox.IsChecked == true;
        }
    }
}

