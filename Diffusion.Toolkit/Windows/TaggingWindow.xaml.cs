using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

    public TaggingWindow(List<int> imageIds)
    {
        InitializeComponent();
        _imageIds = imageIds;
        DataContext = this;

        // Check service availability
        JoyTagAvailable = ServiceLocator.JoyTagService != null;
        WDTagAvailable = ServiceLocator.WDTagService != null;
        JoyCaptionAvailable = ServiceLocator.JoyCaptionService != null;

        JoyTagStatus = JoyTagAvailable 
            ? $"Ready (Threshold: {ServiceLocator.Settings?.JoyTagThreshold ?? 0.5f})" 
            : "Model not found";
        WDTagStatus = WDTagAvailable 
            ? $"Ready (Threshold: {ServiceLocator.Settings?.WDTagThreshold ?? 0.5f})" 
            : "Model not found";
        JoyCaptionStatus = JoyCaptionAvailable 
            ? "Ready" 
            : "Model not found";

        ImageCount = imageIds.Count;
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
            var dataStore = ServiceLocator.PostgreSQLDataStore;
            if (dataStore == null)
            {
                MessageBox.Show("PostgreSQL database not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var runJoyTag = JoyTagCheckBox.IsChecked == true && ServiceLocator.JoyTagService != null;
            var runWDTag = WDTagCheckBox.IsChecked == true && ServiceLocator.WDTagService != null;
            var runJoyCaption = JoyCaptionCheckBox.IsChecked == true && ServiceLocator.JoyCaptionService != null;

            foreach (var imageId in _imageIds)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                var image = dataStore.GetImage(imageId);
                if (image == null) continue;

                CurrentImageName = System.IO.Path.GetFileName(image.Path);
                ProgressMessage = "Processing...";

                // Run taggers in parallel for efficiency
                var tasks = new List<Task>();

                if (runJoyTag)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var tags = await ServiceLocator.JoyTagService!.TagImageAsync(image.Path);
                        var tagTuples = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                        await dataStore.StoreImageTagsAsync(imageId, tagTuples, "joytag");
                    }, _cancellationTokenSource.Token));
                }

                if (runWDTag)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var tags = await ServiceLocator.WDTagService!.TagImageAsync(image.Path);
                        var tagTuples = tags.Select(t => (t.Tag, t.Confidence)).ToList();
                        await dataStore.StoreImageTagsAsync(imageId, tagTuples, "wdv3large");
                    }, _cancellationTokenSource.Token));
                }

                if (runJoyCaption)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var result = await ServiceLocator.JoyCaptionService!.CaptionImageAsync(
                            image.Path, 
                            Captioning.Services.JoyCaptionService.PROMPT_DETAILED);
                        
                        await dataStore.StoreCaptionAsync(imageId, result.Caption, "joycaption", 
                            result.PromptUsed, result.TokenCount, (float)result.GenerationTimeMs);
                    }, _cancellationTokenSource.Token));
                }

                await Task.WhenAll(tasks);
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

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
