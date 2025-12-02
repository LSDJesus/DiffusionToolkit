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
using Diffusion.Database.Models;
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
        JoyCaptionAvailable = ServiceLocator.CaptionService != null;

        JoyTagStatus = JoyTagAvailable 
            ? $"Ready (Threshold: {ServiceLocator.Settings?.JoyTagThreshold ?? 0.5f:F2})" 
            : "Model not found";
        WDTagStatus = WDTagAvailable 
            ? $"Ready (Threshold: {ServiceLocator.Settings?.WDTagThreshold ?? 0.5f:F2})" 
            : "Model not found";
        JoyCaptionStatus = JoyCaptionAvailable 
            ? "Ready" 
            : "Service not available";

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
            
            // Set mode-specific UI state
            if (taggingOnly)
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

            foreach (var imageId in _imageIds)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                var image = dataStore.GetImage(imageId);
                if (image == null) continue;

                CurrentImageName = System.IO.Path.GetFileName(image.Path);
                ProgressMessage = "Processing...";

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
                        
                        // Write tags to image metadata if enabled
                        Logger.Log($"AutoWriteMetadata={ServiceLocator.Settings?.AutoWriteMetadata}, WriteTagsToMetadata={ServiceLocator.Settings?.WriteTagsToMetadata}");
                        if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                            ServiceLocator.Settings?.WriteTagsToMetadata == true)
                        {
                            // Get deduplicated tags from database for metadata write
                            var dbTags = await dataStore.GetImageTagsAsync(imageId, "joytag+wdv3large");
                            var request = new Scanner.MetadataWriteRequest
                            {
                                Tags = dbTags.Select(t => new Scanner.TagWithConfidence 
                                { 
                                    Tag = t.Tag, 
                                    Confidence = t.Confidence 
                                }).ToList(),
                                CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                            };
                            Scanner.MetadataWriter.WriteMetadata(image.Path, request);
                        }
                    }, _cancellationTokenSource.Token);
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
                            Logger.Log($"AutoWriteMetadata={ServiceLocator.Settings?.AutoWriteMetadata}, WriteTagsToMetadata={ServiceLocator.Settings?.WriteTagsToMetadata}");
                            if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                                ServiceLocator.Settings?.WriteTagsToMetadata == true)
                            {
                                var request = new Scanner.MetadataWriteRequest
                                {
                                    Tags = tags.Select(t => new Scanner.TagWithConfidence 
                                    { 
                                        Tag = t.Tag, 
                                        Confidence = t.Confidence 
                                    }).ToList(),
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
                            
                            // Write tags to image metadata if enabled
                            if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                                ServiceLocator.Settings?.WriteTagsToMetadata == true)
                            {
                                var request = new Scanner.MetadataWriteRequest
                                {
                                    Tags = tags.Select(t => new Scanner.TagWithConfidence 
                                    { 
                                        Tag = t.Tag, 
                                        Confidence = t.Confidence 
                                    }).ToList(),
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
                            
                            string finalPrompt = promptText;
                            string? finalCaption = null;

                            switch (handlingMode)
                            {
                                case Configuration.CaptionHandlingMode.Refine:
                                    if (existingCaption != null && !string.IsNullOrWhiteSpace(existingCaption.Caption))
                                    {
                                        finalPrompt = $"{promptText}\n\nExisting caption to refine/expand/correct: \"{existingCaption.Caption}\"";
                                    }
                                    break;

                                case Configuration.CaptionHandlingMode.Append:
                                    break;

                                case Configuration.CaptionHandlingMode.Overwrite:
                                default:
                                    break;
                            }
                            
                                var result = await ServiceLocator.CaptionService!.CaptionImageAsync(
                                image.Path, 
                                finalPrompt);

                            // Handle append mode
                            if (handlingMode == Configuration.CaptionHandlingMode.Append && 
                                existingCaption != null && 
                                !string.IsNullOrWhiteSpace(existingCaption.Caption))
                            {
                                finalCaption = $"{existingCaption.Caption} {result.Caption}";
                            }
                            else
                            {
                                finalCaption = result.Caption;
                            }
                            
                            var captionSource = ServiceLocator.Settings?.CaptionProvider == Diffusion.Toolkit.Configuration.CaptionProviderType.LocalJoyCaption ? "joycaption" : "openai";
                            await dataStore.StoreCaptionAsync(imageId, finalCaption, captionSource, 
                                result.PromptUsed, result.TokenCount, (float)result.GenerationTimeMs);
                            
                            // Write caption to image metadata if enabled
                            if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                                ServiceLocator.Settings?.WriteCaptionsToMetadata == true)
                            {
                                var request = new Scanner.MetadataWriteRequest
                                {
                                    Caption = finalCaption,
                                    CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                                };
                                Scanner.MetadataWriter.WriteMetadata(image.Path, request);
                            }
                        }, _cancellationTokenSource.Token));
                    }

                    await Task.WhenAll(tasks);
                }

                // Handle caption separately if only captioning (no tagging)
                if (runJoyCaption && !runJoyTag && !runWDTag)
                {
                    await Task.Run(async () =>
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
                        
                        string finalPrompt = promptText;
                        string? finalCaption = null;

                        switch (handlingMode)
                        {
                            case Configuration.CaptionHandlingMode.Refine:
                                if (existingCaption != null && !string.IsNullOrWhiteSpace(existingCaption.Caption))
                                {
                                    // Use existing caption as context for refinement
                                    finalPrompt = $"{promptText}\n\nExisting caption to refine/expand/correct: \"{existingCaption.Caption}\"";
                                }
                                break;

                            case Configuration.CaptionHandlingMode.Append:
                                // Generate new caption, will append after
                                break;

                            case Configuration.CaptionHandlingMode.Overwrite:
                            default:
                                // Generate new caption to replace
                                break;
                        }
                        
                            var result = await ServiceLocator.CaptionService!.CaptionImageAsync(
                            image.Path, 
                            finalPrompt);

                        // Handle append mode
                        if (handlingMode == Configuration.CaptionHandlingMode.Append && 
                            existingCaption != null && 
                            !string.IsNullOrWhiteSpace(existingCaption.Caption))
                        {
                            finalCaption = $"{existingCaption.Caption} {result.Caption}";
                        }
                        else
                        {
                            finalCaption = result.Caption;
                        }
                        
                        var captionSource2 = ServiceLocator.Settings?.CaptionProvider == Diffusion.Toolkit.Configuration.CaptionProviderType.LocalJoyCaption ? "joycaption" : "openai";
                        await dataStore.StoreCaptionAsync(imageId, finalCaption, captionSource2, 
                            result.PromptUsed, result.TokenCount, (float)result.GenerationTimeMs);
                        
                        // Write caption to image metadata if enabled
                        if (ServiceLocator.Settings?.AutoWriteMetadata == true && 
                            ServiceLocator.Settings?.WriteCaptionsToMetadata == true)
                        {
                            var request = new Scanner.MetadataWriteRequest
                            {
                                Caption = finalCaption,
                                CreateBackup = ServiceLocator.Settings?.CreateMetadataBackup ?? true
                            };
                            Scanner.MetadataWriter.WriteMetadata(image.Path, request);
                        }
                    }, _cancellationTokenSource.Token);
                }

                ProcessedImages++;
            }

            ProgressMessage = "Complete!";
            MessageBox.Show($"Successfully tagged {ProcessedImages} images!", "Success", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancelled";
            MessageBox.Show("Tagging cancelled by user.", "Cancelled", 
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

    private void JoyCaptionCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        // Trigger CanStart update
        OnPropertyChanged(nameof(CanStart));
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
