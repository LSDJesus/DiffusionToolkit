using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Windows;

/// <summary>
/// Face Gallery Window for viewing and managing detected faces and clusters
/// </summary>
public partial class FaceGalleryWindow : Window
{
    private readonly PostgreSQLDataStore _dataStore;
    private ObservableCollection<ClusterViewModel> _clusters = new();
    private ObservableCollection<FaceViewModel> _faces = new();
    private ClusterViewModel? _selectedCluster;

    public FaceGalleryWindow()
    {
        InitializeComponent();
        
        _dataStore = ServiceLocator.DataStore!;
        
        Loaded += FaceGalleryWindow_Loaded;
    }

    private async void FaceGalleryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            StatusText.Text = "Loading face data...";
            
            // Load statistics
            var stats = await _dataStore.GetFaceDetectionStats();
            StatsText.Text = $"{stats.TotalFaces:N0} faces | {stats.TotalClusters:N0} clusters | {stats.LabeledClusters:N0} labeled | {stats.UnclusteredFaces:N0} unclustered";
            
            // Load clusters
            await LoadClustersAsync();
            
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            Logger.Log($"Error loading face data: {ex.Message}");
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async Task LoadClustersAsync()
    {
        var clusters = await _dataStore.GetAllClusters();
        
        _clusters.Clear();
        foreach (var cluster in clusters)
        {
            _clusters.Add(new ClusterViewModel
            {
                Id = cluster.Id,
                Label = cluster.Name,
                FaceCount = cluster.FaceCount,
                AvgQualityScore = cluster.AvgQualityScore,
                IsManual = cluster.IsManualGroup
            });
        }
        
        // Add unclustered "cluster"
        var unclusteredCount = (await _dataStore.GetFaceDetectionStats()).UnclusteredFaces;
        if (unclusteredCount > 0)
        {
            _clusters.Insert(0, new ClusterViewModel
            {
                Id = -1,
                Label = "[Unclustered]",
                FaceCount = unclusteredCount,
                IsUnclustered = true
            });
        }
        
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<ClusterViewModel> filtered = _clusters;
        
        if (LabeledRadio.IsChecked == true)
        {
            filtered = _clusters.Where(c => !string.IsNullOrEmpty(c.Label) && !c.IsUnclustered);
        }
        else if (UnlabeledRadio.IsChecked == true)
        {
            filtered = _clusters.Where(c => string.IsNullOrEmpty(c.Label) || c.IsUnclustered);
        }
        
        ClusterList.ItemsSource = filtered.ToList();
    }

    private async void ClusterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClusterList.SelectedItem is ClusterViewModel cluster)
        {
            _selectedCluster = cluster;
            await LoadFacesForClusterAsync(cluster);
            
            ClusterLabel.Text = cluster.DisplayLabel;
            EditLabelButton.Visibility = cluster.IsUnclustered ? Visibility.Collapsed : Visibility.Visible;
            AssignLabelButton.IsEnabled = true;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            _selectedCluster = null;
            _faces.Clear();
            FacesGrid.ItemsSource = null;
            ClusterLabel.Text = "Select a cluster";
            EditLabelButton.Visibility = Visibility.Collapsed;
            AssignLabelButton.IsEnabled = false;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadFacesForClusterAsync(ClusterViewModel cluster)
    {
        try
        {
            StatusText.Text = $"Loading faces for {cluster.DisplayLabel}...";
            
            List<FaceDetectionEntity> faces;
            
            if (cluster.IsUnclustered)
            {
                faces = await _dataStore.GetUnclusteredFaces(500);
            }
            else
            {
                faces = await _dataStore.GetFacesInCluster(cluster.Id, 500);
            }
            
            _faces.Clear();
            
            foreach (var face in faces)
            {
                var viewModel = new FaceViewModel
                {
                    Id = face.Id,
                    ImageId = face.ImageId,
                    Confidence = face.Confidence,
                    QualityScore = face.QualityScore
                    // CharacterLabel not in V5 schema - use FaceGroup.Name instead
                };
                
                // Load thumbnail from face crop
                if (face.FaceCrop != null && face.FaceCrop.Length > 0)
                {
                    viewModel.Thumbnail = LoadThumbnail(face.FaceCrop);
                }
                else
                {
                    // Load from database if not in memory
                    var crop = await _dataStore.GetFaceCrop(face.Id);
                    if (crop != null)
                    {
                        viewModel.Thumbnail = LoadThumbnail(crop);
                    }
                }
                
                _faces.Add(viewModel);
            }
            
            FacesGrid.ItemsSource = _faces;
            StatusText.Text = $"Showing {_faces.Count} faces";
        }
        catch (Exception ex)
        {
            Logger.Log($"Error loading faces: {ex.Message}");
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private BitmapImage? LoadThumbnail(byte[] imageData)
    {
        try
        {
            var image = new BitmapImage();
            using (var ms = new MemoryStream(imageData))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.DecodePixelWidth = 100;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    private void FilterRadio_Checked(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
    }

    private async void EditLabelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCluster == null || _selectedCluster.IsUnclustered) return;
        
        var dialog = new InputDialog("Edit Character Label", 
            "Enter character name:", _selectedCluster.Label ?? "");
        dialog.Owner = this;
        
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            try
            {
                await _dataStore.UpdateClusterLabel(_selectedCluster.Id, dialog.InputText);
                _selectedCluster.Label = dialog.InputText;
                ClusterLabel.Text = dialog.InputText;
                
                // Refresh list
                await LoadClustersAsync();
                
                StatusText.Text = $"Updated label to '{dialog.InputText}'";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating label: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void AssignLabelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCluster == null) return;
        
        var dialog = new InputDialog("Assign Character Label", 
            "Enter character name for this cluster:", _selectedCluster.Label ?? "");
        dialog.Owner = this;
        
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            try
            {
                if (_selectedCluster.IsUnclustered)
                {
                    // Create new cluster for unclustered faces
                    var clusterId = await _dataStore.CreateFaceClusterAsync(dialog.InputText, true);
                    StatusText.Text = $"Created cluster '{dialog.InputText}'";
                }
                else
                {
                    await _dataStore.UpdateClusterLabel(_selectedCluster.Id, dialog.InputText);
                    StatusText.Text = $"Updated label to '{dialog.InputText}'";
                }
                
                await LoadClustersAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error assigning label: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void MergeClustersButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Cluster merging will be implemented in a future update.", 
            "Coming Soon", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void FaceItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is FaceViewModel face)
        {
            // Navigate to the image containing this face
            // TODO: Implement navigation to image viewer
            StatusText.Text = $"Face ID: {face.Id}, Image ID: {face.ImageId}, Confidence: {face.Confidence:P0}";
        }
    }
}

// View Models
public class ClusterViewModel
{
    public int Id { get; set; }
    public string? Label { get; set; }
    public int FaceCount { get; set; }
    public float AvgQualityScore { get; set; }
    public bool IsManual { get; set; }
    public bool IsUnclustered { get; set; }
    
    public string DisplayLabel => string.IsNullOrEmpty(Label) ? $"Cluster #{Id}" : Label;
}

public class FaceViewModel
{
    public int Id { get; set; }
    public int ImageId { get; set; }
    public float Confidence { get; set; }
    public float QualityScore { get; set; }
    public string? CharacterLabel { get; set; }
    public BitmapImage? Thumbnail { get; set; }
    
    public string ConfidenceDisplay => $"{Confidence:P0}";
    public string QualityDisplay => $"Q: {QualityScore:F1}";
}

// Simple input dialog
public class InputDialog : Window
{
    private TextBox _textBox;
    
    public string InputText => _textBox.Text;
    
    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 400;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Margin = new Thickness(15);
        
        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(label, 0);
        
        _textBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 15) };
        Grid.SetRow(_textBox, 1);
        
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        
        var okButton = new Button { Content = "OK", Width = 75, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
        okButton.Click += (s, e) => { DialogResult = true; Close(); };
        
        var cancelButton = new Button { Content = "Cancel", Width = 75, IsCancel = true };
        cancelButton.Click += (s, e) => { DialogResult = false; Close(); };
        
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        Grid.SetRow(buttonPanel, 2);
        
        grid.Children.Add(label);
        grid.Children.Add(_textBox);
        grid.Children.Add(buttonPanel);
        
        Content = grid;
    }
}
