using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Pages;

public partial class FaceGallery : NavigationPage, INotifyPropertyChanged
{
    private int _groupId;
    private string _groupName = "";
    private int _faceCount;
    private int _uniqueImageCount;
    private float _avgConfidence;
    private float _avgQualityScore;
    private bool _isLoading;
    private ObservableCollection<FaceGalleryItem> _faces = new();

    public string GroupName
    {
        get => _groupName;
        set { _groupName = value; OnPropertyChanged(); }
    }

    public int FaceCount
    {
        get => _faceCount;
        set { _faceCount = value; OnPropertyChanged(); }
    }

    public int UniqueImageCount
    {
        get => _uniqueImageCount;
        set { _uniqueImageCount = value; OnPropertyChanged(); }
    }

    public float AvgConfidence
    {
        get => _avgConfidence;
        set { _avgConfidence = value; OnPropertyChanged(); }
    }

    public float AvgQualityScore
    {
        get => _avgQualityScore;
        set { _avgQualityScore = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public ObservableCollection<FaceGalleryItem> Faces
    {
        get => _faces;
        set { _faces = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FaceGallery() : base("facegallery")
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>
    /// Load face group gallery
    /// </summary>
    public async Task LoadGroupAsync(int groupId)
    {
        _groupId = groupId;
        IsLoading = true;

        try
        {
            var dataStore = ServiceLocator.DataStore as PostgreSQLDataStore;
            if (dataStore == null) return;

            // Load group info
            var group = await dataStore.GetFaceGroupAsync(groupId);
            if (group != null)
            {
                GroupName = group.Name ?? $"Group {group.Id}";
                FaceCount = group.FaceCount;
                AvgConfidence = group.AvgConfidence;
                AvgQualityScore = group.AvgQualityScore;
            }

            // Load all faces in group
            var faces = await dataStore.GetFacesInGroupAsync(groupId);
            
            Faces.Clear();
            UniqueImageCount = faces.Select(f => f.ImageId).Distinct().Count();

            foreach (var face in faces)
            {
                var item = new FaceGalleryItem
                {
                    FaceId = face.Id,
                    ImageId = face.ImageId,
                    ImagePath = face.ImagePath,
                    ImageFileName = Path.GetFileName(face.ImagePath),
                    ImageWidth = face.ImageWidth,
                    ImageHeight = face.ImageHeight,
                    Confidence = face.Confidence,
                    QualityScore = face.QualityScore,
                    SimilarityScore = face.SimilarityScore
                };

                // Load face crop
                if (face.FaceCrop != null && face.FaceCrop.Length > 0)
                {
                    item.FaceCropSource = LoadImageFromBytes(face.FaceCrop);
                }

                Faces.Add(item);
            }
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Error loading face gallery: {ex.Message}");
            MessageBox.Show($"Error loading face gallery: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private BitmapImage? LoadImageFromBytes(byte[] imageData)
    {
        try
        {
            using var ms = new MemoryStream(imageData);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // Navigate back to previous page
        ServiceLocator.NavigatorService.GoBack();
    }

    private async void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("Rename Group", "Enter new name:", GroupName);
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var dataStore = ServiceLocator.DataStore as PostgreSQLDataStore;
                if (dataStore == null) return;

                await dataStore.UpdateFaceGroupNameAsync(_groupId, dialog.InputText);
                GroupName = dialog.InputText;
                
                Diffusion.Common.Logger.Log($"Renamed face group {_groupId} to '{GroupName}'");
            }
            catch (Exception ex)
            {
                Diffusion.Common.Logger.Log($"Error renaming group: {ex.Message}");
                MessageBox.Show($"Error renaming group: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            $"Delete face group '{GroupName}'?\n\nThe faces will not be deleted, just ungrouped.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var dataStore = ServiceLocator.DataStore as PostgreSQLDataStore;
                if (dataStore == null) return;

                await dataStore.DeleteFaceGroupAsync(_groupId);
                
                Diffusion.Common.Logger.Log($"Deleted face group {_groupId}");
                
                // Navigate back
                ServiceLocator.NavigatorService.GoBack();
            }
            catch (Exception ex)
            {
                Diffusion.Common.Logger.Log($"Error deleting group: {ex.Message}");
                MessageBox.Show($"Error deleting group: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void FaceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is FaceGalleryItem item)
        {
            // Navigate to the full image in the search page
            ServiceLocator.NavigatorService.NavigateToImage(item.ImageId);
        }
    }

    private async void ConfirmMatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int faceId)
        {
            try
            {
                var dataStore = ServiceLocator.DataStore as PostgreSQLDataStore;
                if (dataStore == null) return;

                // Confirm face belongs to this group
                await dataStore.AssignFaceToCluster(faceId, _groupId);
                
                Diffusion.Common.Logger.Log($"Confirmed face {faceId} in group {_groupId}");
                
                // Refresh the gallery to update stats
                await LoadGroupAsync(_groupId);
            }
            catch (Exception ex)
            {
                Diffusion.Common.Logger.Log($"Error confirming match: {ex.Message}");
            }
        }
    }

    private async void RejectMatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int faceId)
        {
            try
            {
                var dataStore = ServiceLocator.DataStore as PostgreSQLDataStore;
                if (dataStore == null) return;

                // Remove face from this group
                await dataStore.RemoveFaceFromCluster(faceId);
                
                Diffusion.Common.Logger.Log($"Rejected face {faceId} from group {_groupId}");
                
                // Remove from UI
                var face = Faces.FirstOrDefault(f => f.FaceId == faceId);
                if (face != null)
                {
                    Faces.Remove(face);
                    FaceCount--;
                }
            }
            catch (Exception ex)
            {
                Diffusion.Common.Logger.Log($"Error rejecting match: {ex.Message}");
            }
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// View model for a face in the gallery
/// </summary>
public class FaceGalleryItem
{
    public int FaceId { get; set; }
    public int ImageId { get; set; }
    public string ImagePath { get; set; } = "";
    public string ImageFileName { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public float Confidence { get; set; }
    public float QualityScore { get; set; }
    public float SimilarityScore { get; set; }
    public BitmapImage? FaceCropSource { get; set; }
}

/// <summary>
/// Simple text input dialog
/// </summary>
public class TextInputDialog : Window
{
    private TextBox _textBox;

    public string InputText => _textBox.Text;

    public TextInputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 400;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(15) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock { Text = prompt };
        Grid.SetRow(promptText, 0);
        grid.Children.Add(promptText);

        _textBox = new TextBox { Text = defaultValue, Height = 25 };
        _textBox.SelectAll();
        _textBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) DialogResult = true; };
        Grid.SetRow(_textBox, 2);
        grid.Children.Add(_textBox);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttonPanel, 4);

        var okButton = new Button { Content = "OK", Width = 75, Height = 25, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
        okButton.Click += (s, e) => DialogResult = true;
        buttonPanel.Children.Add(okButton);

        var cancelButton = new Button { Content = "Cancel", Width = 75, Height = 25, IsCancel = true };
        buttonPanel.Children.Add(cancelButton);

        grid.Children.Add(buttonPanel);

        Content = grid;
        Loaded += (s, e) => _textBox.Focus();
    }
}
