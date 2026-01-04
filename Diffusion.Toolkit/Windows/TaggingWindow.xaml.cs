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
        // Caption service is initialized on-demand when captioning starts, so always allow it if configured
        JoyCaptionAvailable = ServiceLocator.Settings?.CaptionProvider == Configuration.CaptionProviderType.LocalJoyCaption 
            && !string.IsNullOrEmpty(ServiceLocator.Settings?.JoyCaptionModelPath);

        JoyTagStatus = JoyTagAvailable 
            ? $"Ready (Threshold: {ServiceLocator.Settings?.JoyTagThreshold ?? 0.5f:F2})" 
            : "Model not found";
        WDTagStatus = WDTagAvailable 
            ? $"Ready (Threshold: {ServiceLocator.Settings?.WDTagThreshold ?? 0.5f:F2})" 
            : "Model not found";
        JoyCaptionStatus = JoyCaptionAvailable 
            ? "Ready (will initialize on start)" 
            : "Not configured";

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
                JoyTagCheckBox.IsChecked = ServiceLocator.Settings?.TagDialogJoyTagEnabled ?? true;
                WDTagCheckBox.IsChecked = ServiceLocator.Settings?.TagDialogWDTagEnabled ?? true;
                JoyCaptionCheckBox.IsChecked = ServiceLocator.Settings?.TagDialogCaptionEnabled ?? false;
            }
            else if (taggingOnly)
            {
                Title = "Auto-Tag Images";
                JoyCaptionCheckBox.IsChecked = false;
                JoyCaptionCheckBox.IsEnabled = false;
            }
            else if (captioningOnly)
            {
                Title = "Caption Images";
                JoyTagCheckBox.IsChecked = false;
                JoyTagCheckBox.IsEnabled = false;
                WDTagCheckBox.IsChecked = false;
                WDTagCheckBox.IsEnabled = false;
                JoyCaptionCheckBox.IsChecked = true;
            }
        };
    }

    public bool JoyTagAvailable { get; }
    public bool WDTagAvailable { get; }
    public bool JoyCaptionAvailable { get; }
    
    public string JoyTagStatus { get; }
    public string WDTagStatus { get; }
    public string JoyCaptionStatus { get; }

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

    public bool CanStart => !IsProcessing && (JoyTagCheckBox.IsChecked == true || 
                                              WDTagCheckBox.IsChecked == true || 
                                              JoyCaptionCheckBox.IsChecked == true);

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

            var runJoyTag = JoyTagCheckBox.IsChecked == true && ServiceLocator.JoyTagService != null;
            var runWDTag = WDTagCheckBox.IsChecked == true && ServiceLocator.WDTagService != null;
            var runJoyCaption = JoyCaptionCheckBox.IsChecked == true && ServiceLocator.CaptionService != null;
            var storeConfidence = ServiceLocator.Settings?.StoreTagConfidence ?? false;
            var maxConcurrency = ServiceLocator.Settings?.TaggingConcurrentWorkers ?? 8;
            var skipAlreadyTagged = ServiceLocator.Settings?.SkipAlreadyTaggedImages ?? true;

            Logger.Log($"TaggingWindow: runJoyTag={runJoyTag}, runWDTag={runWDTag}, runJoyCaption={runJoyCaption}");
            Logger.Log($"  JoyTagCheckBox.IsChecked={JoyTagCheckBox.IsChecked}, JoyTagService={ServiceLocator.JoyTagService != null}");
            Logger.Log($"  WDTagCheckBox.IsChecked={WDTagCheckBox.IsChecked}, WDTagService={ServiceLocator.WDTagService != null}");
            Logger.Log($"  JoyCaptionCheckBox.IsChecked={JoyCaptionCheckBox.IsChecked}, CaptionService={ServiceLocator.CaptionService != null}");
            Logger.Log($"  Parallel processing with {maxConcurrency} concurrent workers, skipAlreadyTagged={skipAlreadyTagged}");

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
                if (skipAlreadyTagged && (runJoyTag || runWDTag))
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

                // When both taggers are enabled, we need to deduplicate and average confidence
                if (runJoyTag && runWDTag)
                {
                    await Task.Run(async () =>
                    {
                        // Run both taggers
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
                        if (false) // Auto-write disabled
                        {
                            // Get deduplicated tags from database for metadata write
                            var dbTags = await dataStore.GetImageTagsAsync(imageId, "joytag+wdv3large");
                            
                            // Get latest caption to preserve it
                            var latestCaption = await dataStore.GetLatestCaptionAsync(imageId);
                            
                            // Preserve ALL existing metadata
                            var request = new Scanner.MetadataWriteRequest
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
                            Scanner.MetadataWriter.WriteMetadata(image.Path, request);
                        }
                    }, _cancellationTokenSource.Token);
                    
                    // Run caption if enabled (alongside both taggers)
                    if (runJoyCaption)
                    {
                        await RunCaptionAsync(dataStore, image, imageId);
                    }
                }
                else
                {
                    // Run taggers separately
                    var tasks = new List<Task>();

                    if (runJoyTag)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            var tags = await ServiceLocator.JoyTagService!.TagImageAsync(image.Path);
                            Logger.Log($"JoyTag: {tags.Count} tags for image {imageId}");
                            var tagTuples = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                            await dataStore.StoreImageTagsAsync(imageId, tagTuples, "joytag");
                            
                            // Write tags to image metadata if enabled
                            // Auto-write disabled - use context menu instead
                            if (false)
                            {
                                var latestCaption = await dataStore.GetLatestCaptionAsync(imageId);
                                var request = new Scanner.MetadataWriteRequest
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
                                    Tags = tags.Select(t => new Scanner.TagWithConfidence 
                                    { 
                                        Tag = t.Tag, 
                                        Confidence = t.Confidence 
                                    }).ToList(),
                                    Caption = latestCaption?.Caption,
                                    AestheticScore = image.AestheticScore > 0 ? (decimal?)image.AestheticScore : null,
                                    CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                                };
                                Scanner.MetadataWriter.WriteMetadata(image.Path, request);
                            }
                        }, _cancellationTokenSource.Token));
                    }

                    if (runWDTag)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            var tags = await ServiceLocator.WDTagService!.TagImageAsync(image.Path);
                            var tagTuples = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                            await dataStore.StoreImageTagsAsync(imageId, tagTuples, "wdv3large");
                            
                            // Auto-write disabled - use context menu instead
                            if (false)
                            {
                                var latestCaption = await dataStore.GetLatestCaptionAsync(imageId);
                                var request = new Scanner.MetadataWriteRequest
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
                                    Tags = tags.Select(t => new Scanner.TagWithConfidence 
                                    { 
                                        Tag = t.Tag, 
                                        Confidence = t.Confidence 
                                    }).ToList(),
                                    Caption = latestCaption?.Caption,
                                    AestheticScore = image.AestheticScore > 0 ? (decimal?)image.AestheticScore : null,
                                    CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                                };
                                Scanner.MetadataWriter.WriteMetadata(image.Path, request);
                            }
                        }, _cancellationTokenSource.Token));
                    }

                    if (runJoyCaption)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            await RunCaptionAsync(dataStore, image, imageId);
                        }, _cancellationTokenSource.Token));
                    }

                    await Task.WhenAll(tasks);
                }

                // NOTE: Caption-only path removed - captioning is now handled in the parallel tasks above
                // The duplicate code was causing double caption generation

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

    private void JoyTagCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (ServiceLocator.Settings != null)
        {
            ServiceLocator.Settings.TagDialogJoyTagEnabled = JoyTagCheckBox.IsChecked ?? false;
        }
        OnPropertyChanged(nameof(CanStart));
    }

    private void WDTagCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (ServiceLocator.Settings != null)
        {
            ServiceLocator.Settings.TagDialogWDTagEnabled = WDTagCheckBox.IsChecked ?? false;
        }
        OnPropertyChanged(nameof(CanStart));
    }

    private void JoyCaptionCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (ServiceLocator.Settings != null)
        {
            ServiceLocator.Settings.TagDialogCaptionEnabled = JoyCaptionCheckBox.IsChecked ?? false;
        }
        // Trigger CanStart update
        OnPropertyChanged(nameof(CanStart));
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
        var handlingMode = ServiceLocator.Settings?.CaptionHandlingMode ?? Configuration.CaptionHandlingMode.Overwrite;
        var existingCaption = await dataStore.GetLatestCaptionAsync(imageId);
        
        Logger.Log($"Captioning image {imageId}: mode={handlingMode}, existing caption length={existingCaption?.Caption?.Length ?? 0}");
        
        string finalPrompt = promptText;
        string? finalCaption = null;

        switch (handlingMode)
        {
            case Configuration.CaptionHandlingMode.Refine:
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

            case Configuration.CaptionHandlingMode.Append:
                break;

            case Configuration.CaptionHandlingMode.Overwrite:
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
        if (handlingMode == Configuration.CaptionHandlingMode.Append && 
            existingCaption != null && 
            !string.IsNullOrWhiteSpace(existingCaption.Caption))
        {
            finalCaption = $"{existingCaption.Caption} {result.Caption}";
        }
        else if (handlingMode == Configuration.CaptionHandlingMode.Refine)
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
        
        var captionSource = ServiceLocator.Settings?.CaptionProvider == Diffusion.Toolkit.Configuration.CaptionProviderType.LocalJoyCaption ? "joycaption" : "openai";
        
        Logger.Log($"Storing caption in database for image {imageId}, source={captionSource}");
        await dataStore.StoreCaptionAsync(imageId, finalCaption, captionSource, 
            result.PromptUsed, result.TokenCount, (float)result.GenerationTimeMs);
        Logger.Log($"Caption stored successfully");
        
        // Write caption to image metadata if enabled
        // Auto-write disabled - use context menu instead
        if (false)
        {
            Logger.Log($"Writing caption to metadata file: {image.Path}");
            // Preserve ALL existing metadata
            var request = new Scanner.MetadataWriteRequest
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
        }
    }
}
