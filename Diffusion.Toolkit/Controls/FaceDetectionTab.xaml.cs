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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Controls;

public partial class FaceDetectionTab : UserControl, INotifyPropertyChanged
{
    private int _currentImageId;
    private ObservableCollection<FaceViewModel> _faces = new();
    
    public ObservableCollection<FaceViewModel> Faces
    {
        get => _faces;
        set { _faces = value; OnPropertyChanged(); }
    }

    public int FaceCount => Faces?.Count ?? 0;
    public bool HasFaces => FaceCount > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<int>? FaceGroupClicked;
    public event EventHandler<(int faceId, int x, int y, int width, int height)>? FaceHoverChanged;

    public FaceDetectionTab()
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>
    /// Load faces for an image
    /// </summary>
    public async Task LoadFacesAsync(int imageId)
    {
        _currentImageId = imageId;
        Faces.Clear();

        try
        {
            var dataStore = ServiceLocator.DataStore as PostgreSQLDataStore;
            if (dataStore == null) return;

            var faces = await dataStore.GetFacesForImage(imageId);
            
            foreach (var face in faces)
            {
                var viewModel = new FaceViewModel
                {
                    Id = face.Id,
                    ImageId = face.ImageId,
                    FaceIndex = face.FaceIndex,
                    X = face.X,
                    Y = face.Y,
                    Width = face.Width,
                    Height = face.Height,
                    Confidence = face.Confidence,
                    QualityScore = face.QualityScore,
                    HasEmbedding = face.HasEmbedding,
                    FaceGroupId = face.FaceGroupId,
                    Expression = face.Expression
                };

                // Load thumbnail
                if (face.FaceCrop != null && face.FaceCrop.Length > 0)
                {
                    viewModel.ThumbnailSource = LoadImageFromBytes(face.FaceCrop);
                }

                // Load group info if assigned
                if (face.FaceGroupId.HasValue)
                {
                    var group = await dataStore.GetFaceGroupAsync(face.FaceGroupId.Value);
                    if (group != null)
                    {
                        viewModel.GroupName = group.Name ?? $"Group {group.Id}";
                        viewModel.MatchCount = group.FaceCount;
                    }
                }
                else
                {
                    viewModel.GroupName = "Unknown";
                    viewModel.MatchCount = 1;
                }

                Faces.Add(viewModel);
            }

            OnPropertyChanged(nameof(FaceCount));
            OnPropertyChanged(nameof(HasFaces));
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Error loading faces: {ex.Message}");
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

    private void FaceCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border && border.Tag is FaceViewModel face)
        {
            // Show bounding box on preview
            FaceHoverChanged?.Invoke(this, (face.Id, face.X, face.Y, face.Width, face.Height));
        }
    }

    private void FaceCard_MouseLeave(object sender, MouseEventArgs e)
    {
        // Hide bounding box
        FaceHoverChanged?.Invoke(this, (0, 0, 0, 0, 0));
    }

    private void FaceThumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is FaceViewModel face)
        {
            if (face.FaceGroupId.HasValue)
            {
                // Navigate to face gallery page
                FaceGroupClicked?.Invoke(this, face.FaceGroupId.Value);
            }
        }
    }

    private async void GroupName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Tag is FaceViewModel face)
        {
            await UpdateFaceGroupName(face);
        }
    }

    private async void GroupName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            if (textBox.Tag is FaceViewModel face)
            {
                await UpdateFaceGroupName(face);
                Keyboard.ClearFocus();
            }
        }
    }

    private async Task UpdateFaceGroupName(FaceViewModel face)
    {
        try
        {
            var dataStore = ServiceLocator.DataStore as PostgreSQLDataStore;
            if (dataStore == null) return;

            // If face has a group, update the group name
            if (face.FaceGroupId.HasValue)
            {
                await dataStore.UpdateFaceGroupNameAsync(face.FaceGroupId.Value, face.GroupName);
            }
            else
            {
                // Create new group for this face
                var groupId = await dataStore.CreateFaceGroupAsync(face.GroupName, face.Id);
                await dataStore.AddFaceToGroupAsync(face.Id, groupId, 1.0f);
                face.FaceGroupId = groupId;
                face.MatchCount = 1;
            }

            Diffusion.Common.Logger.Log($"Updated face group name: {face.GroupName}");
        }
        catch (Exception ex)
        {
            Diffusion.Common.Logger.Log($"Error updating face group name: {ex.Message}");
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// View model for a detected face
/// </summary>
public class FaceViewModel : INotifyPropertyChanged
{
    private string _groupName = "Unknown";
    
    public int Id { get; set; }
    public int ImageId { get; set; }
    public int FaceIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public float Confidence { get; set; }
    public float QualityScore { get; set; }
    public bool HasEmbedding { get; set; }
    public int? FaceGroupId { get; set; }
    public int MatchCount { get; set; }
    public string? Expression { get; set; }
    public BitmapImage? ThumbnailSource { get; set; }

    public string GroupName
    {
        get => _groupName;
        set
        {
            _groupName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasGroupName));
        }
    }

    public bool HasGroupName => !string.IsNullOrWhiteSpace(GroupName) && GroupName != "Unknown";
    public bool HasExpression => !string.IsNullOrEmpty(Expression);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
