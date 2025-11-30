using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Windows;

/// <summary>
/// View model for a duplicate pair in the UI
/// </summary>
public class DuplicatePairViewModel : INotifyPropertyChanged
{
    private bool _isSelected = true;
    
    public int OriginalId { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public long OriginalFileSize { get; set; }
    
    public int FinalId { get; set; }
    public string FinalPath { get; set; } = string.Empty;
    public int FinalWidth { get; set; }
    public int FinalHeight { get; set; }
    public long FinalFileSize { get; set; }
    
    public long Seed { get; set; }
    public string Model { get; set; } = string.Empty;
    public double UpscaleFactor { get; set; }
    
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }
    
    public string DisplayName => Path.GetFileNameWithoutExtension(FinalPath);
    public string OriginalResolution => $"{OriginalWidth}×{OriginalHeight}";
    public string FinalResolution => $"{FinalWidth}×{FinalHeight}";
    public long Savings => OriginalFileSize;
    public string SavingsText => FormatBytes(Savings);
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Window for detecting and managing duplicate images
/// </summary>
public partial class DuplicateDetectionWindow : Window, INotifyPropertyChanged
{
    private readonly PostgreSQLDataStore? _dataStore;
    private CancellationTokenSource? _cancellationTokenSource;
    
    private bool _isProcessing;
    private string _processingMessage = "";
    private int _totalPairs;
    private string _totalSavings = "0 B";
    private string _progressText = "—";
    
    public ObservableCollection<DuplicatePairViewModel> Pairs { get; } = new();
    
    public event PropertyChangedEventHandler? PropertyChanged;

    public DuplicateDetectionWindow()
    {
        InitializeComponent();
        DataContext = this;
        
        _dataStore = ServiceLocator.DataStore;
        
        if (_dataStore == null)
        {
            MessageBox.Show("PostgreSQL database not available.", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    #region Properties
    
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            _isProcessing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanScan));
        }
    }
    
    public bool CanScan => !IsProcessing;
    
    public string ProcessingMessage
    {
        get => _processingMessage;
        set
        {
            _processingMessage = value;
            OnPropertyChanged();
        }
    }
    
    public int TotalPairs
    {
        get => _totalPairs;
        set
        {
            _totalPairs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPairs));
            OnPropertyChanged(nameof(HasNoPairs));
        }
    }
    
    public bool HasPairs => TotalPairs > 0;
    public bool HasNoPairs => TotalPairs == 0 && !IsProcessing;
    
    public int SelectedCount => Pairs.Count(p => p.IsSelected);
    
    public bool HasSelectedPairs => SelectedCount > 0;
    
    public string TotalSavings
    {
        get => _totalSavings;
        set
        {
            _totalSavings = value;
            OnPropertyChanged();
        }
    }
    
    public string ProgressText
    {
        get => _progressText;
        set
        {
            _progressText = value;
            OnPropertyChanged();
        }
    }
    
    #endregion

    #region Event Handlers
    
    private void DetectionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SimilarityPanel == null) return;
        
        var selectedItem = DetectionModeComboBox.SelectedItem as ComboBoxItem;
        var mode = selectedItem?.Tag?.ToString();
        
        SimilarityPanel.Visibility = mode == "visual" ? Visibility.Visible : Visibility.Collapsed;
    }
    
    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataStore == null) return;
        
        IsProcessing = true;
        ProcessingMessage = "Scanning for duplicates...";
        Pairs.Clear();
        
        _cancellationTokenSource = new CancellationTokenSource();
        
        try
        {
            var selectedItem = DetectionModeComboBox.SelectedItem as ComboBoxItem;
            var mode = selectedItem?.Tag?.ToString() ?? "filename";
            
            await Task.Run(async () =>
            {
                List<DuplicatePairViewModel> results = new();
                
                switch (mode)
                {
                    case "filename":
                        var filenamePairs = await _dataStore.FindOrigFinalPairsByFilenameAsync(
                            limit: 500, 
                            cancellationToken: _cancellationTokenSource.Token).ConfigureAwait(false);
                        
                        results = filenamePairs.Select(p => new DuplicatePairViewModel
                        {
                            OriginalId = p.OriginalId,
                            OriginalPath = p.OriginalPath,
                            OriginalWidth = p.OriginalWidth,
                            OriginalHeight = p.OriginalHeight,
                            OriginalFileSize = p.OriginalFileSize,
                            FinalId = p.FinalId,
                            FinalPath = p.FinalPath,
                            FinalWidth = p.FinalWidth,
                            FinalHeight = p.FinalHeight,
                            FinalFileSize = p.FinalFileSize,
                            Seed = p.Seed,
                            Model = p.Model,
                            UpscaleFactor = p.UpscaleFactor
                        }).ToList();
                        break;
                        
                    case "workflow":
                        var variantGroups = await _dataStore.FindWorkflowVariantGroupsAsync(
                            limit: 500, 
                            cancellationToken: _cancellationTokenSource.Token).ConfigureAwait(false);
                        
                        foreach (var group in variantGroups.Where(g => g.VariantCount == 2))
                        {
                            // Get the two images in this group
                            var images = await _dataStore.FindWorkflowVariantsAsync(
                                group.ImageIds[0], 
                                _cancellationTokenSource.Token).ConfigureAwait(false);
                            
                            if (images.Count == 2)
                            {
                                // Determine which is original (smaller) and which is final (larger)
                                var sorted = images.OrderBy(i => i.Width * i.Height).ToList();
                                var orig = sorted[0];
                                var final = sorted[1];
                                
                                results.Add(new DuplicatePairViewModel
                                {
                                    OriginalId = orig.Id,
                                    OriginalPath = orig.Path,
                                    OriginalWidth = orig.Width,
                                    OriginalHeight = orig.Height,
                                    OriginalFileSize = orig.FileSize,
                                    FinalId = final.Id,
                                    FinalPath = final.Path,
                                    FinalWidth = final.Width,
                                    FinalHeight = final.Height,
                                    FinalFileSize = final.FileSize,
                                    Seed = orig.Seed,
                                    Model = orig.Model ?? "",
                                    UpscaleFactor = orig.Width > 0 ? (double)final.Width / orig.Width : 1.0
                                });
                            }
                        }
                        break;
                        
                    case "visual":
                        double threshold = 0.95;
                        Dispatcher.Invoke(() => threshold = SimilaritySlider.Value);
                        
                        var clusters = await _dataStore.FindVisualDuplicatesAsync(
                            (float)threshold, 
                            limit: 500, 
                            cancellationToken: _cancellationTokenSource.Token).ConfigureAwait(false);
                        
                        foreach (var cluster in clusters.Where(c => c.Count == 2))
                        {
                            var id1 = cluster.ImageIds[0];
                            var id2 = cluster.ImageIds[1];
                            
                            var img1 = _dataStore.GetImage(id1);
                            var img2 = _dataStore.GetImage(id2);
                            
                            if (img1 != null && img2 != null)
                            {
                                // Larger resolution is "final"
                                var isImg1Larger = (img1.Width * img1.Height) >= (img2.Width * img2.Height);
                                var orig = isImg1Larger ? img2 : img1;
                                var final = isImg1Larger ? img1 : img2;
                                
                                results.Add(new DuplicatePairViewModel
                                {
                                    OriginalId = orig.Id,
                                    OriginalPath = orig.Path,
                                    OriginalWidth = orig.Width,
                                    OriginalHeight = orig.Height,
                                    OriginalFileSize = orig.FileSize,
                                    FinalId = final.Id,
                                    FinalPath = final.Path,
                                    FinalWidth = final.Width,
                                    FinalHeight = final.Height,
                                    FinalFileSize = final.FileSize,
                                    Seed = orig.Seed,
                                    Model = orig.Model ?? "",
                                    UpscaleFactor = orig.Width > 0 ? (double)final.Width / orig.Width : 1.0
                                });
                            }
                        }
                        break;
                }
                
                Dispatcher.Invoke(() =>
                {
                    foreach (var pair in results)
                    {
                        pair.PropertyChanged += Pair_PropertyChanged;
                        Pairs.Add(pair);
                    }
                    
                    TotalPairs = Pairs.Count;
                    UpdateStatistics();
                });
                
            }, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            ProcessingMessage = "Scan cancelled";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during scan: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
        }
    }
    
    private void Pair_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DuplicatePairViewModel.IsSelected))
        {
            UpdateStatistics();
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelectedPairs));
        }
    }
    
    private void PairsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PairsListView.SelectedItem is DuplicatePairViewModel pair)
        {
            LoadPreviewImages(pair);
        }
    }
    
    private void LoadPreviewImages(DuplicatePairViewModel pair)
    {
        try
        {
            // Load original image
            if (File.Exists(pair.OriginalPath))
            {
                var origBitmap = new BitmapImage();
                origBitmap.BeginInit();
                origBitmap.UriSource = new Uri(pair.OriginalPath);
                origBitmap.DecodePixelWidth = 400;
                origBitmap.CacheOption = BitmapCacheOption.OnLoad;
                origBitmap.EndInit();
                origBitmap.Freeze();
                OriginalImage.Source = origBitmap;
                OriginalInfoText.Text = $"{pair.OriginalWidth}×{pair.OriginalHeight} • {FormatBytes(pair.OriginalFileSize)}";
            }
            else
            {
                OriginalImage.Source = null;
                OriginalInfoText.Text = "File not found";
            }
            
            // Load final image
            if (File.Exists(pair.FinalPath))
            {
                var finalBitmap = new BitmapImage();
                finalBitmap.BeginInit();
                finalBitmap.UriSource = new Uri(pair.FinalPath);
                finalBitmap.DecodePixelWidth = 400;
                finalBitmap.CacheOption = BitmapCacheOption.OnLoad;
                finalBitmap.EndInit();
                finalBitmap.Freeze();
                FinalImage.Source = finalBitmap;
                FinalInfoText.Text = $"{pair.FinalWidth}×{pair.FinalHeight} • {FormatBytes(pair.FinalFileSize)}";
            }
            else
            {
                FinalImage.Source = null;
                FinalInfoText.Text = "File not found";
            }
        }
        catch (Exception ex)
        {
            OriginalInfoText.Text = $"Error: {ex.Message}";
            FinalInfoText.Text = "";
        }
    }
    
    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pair in Pairs)
        {
            pair.IsSelected = true;
        }
    }
    
    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pair in Pairs)
        {
            pair.IsSelected = false;
        }
    }
    
    private async void MarkForDeletionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataStore == null) return;
        
        var selectedPairs = Pairs.Where(p => p.IsSelected).ToList();
        if (selectedPairs.Count == 0) return;
        
        var result = MessageBox.Show(
            $"Mark {selectedPairs.Count} original image(s) for deletion?\n\n" +
            "This will keep the FINAL (upscaled) versions and mark the ORIG (base) versions for deletion.\n\n" +
            "You can review and permanently delete them later from Tools > Empty Trash.",
            "Confirm Mark for Deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result != MessageBoxResult.Yes) return;
        
        IsProcessing = true;
        ProcessingMessage = "Marking images for deletion...";
        
        try
        {
            var ids = selectedPairs.Select(p => p.OriginalId).ToList();
            
            await Task.Run(() =>
            {
                _dataStore.SetDeleted(ids, true);
            });
            
            // Remove processed pairs from the list
            foreach (var pair in selectedPairs.ToList())
            {
                pair.PropertyChanged -= Pair_PropertyChanged;
                Pairs.Remove(pair);
            }
            
            TotalPairs = Pairs.Count;
            UpdateStatistics();
            
            ServiceLocator.ToastService?.Toast(
                $"Marked {selectedPairs.Count} image(s) for deletion",
                "Duplicate Detection");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error marking files: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        Close();
    }
    
    #endregion

    #region Helpers
    
    private void UpdateStatistics()
    {
        var selectedPairs = Pairs.Where(p => p.IsSelected).ToList();
        var totalSavingsBytes = selectedPairs.Sum(p => p.Savings);
        
        TotalSavings = FormatBytes(totalSavingsBytes);
        ProgressText = Pairs.Count > 0 ? $"{SelectedCount}/{Pairs.Count}" : "—";
        
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelectedPairs));
    }
    
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    #endregion
}
