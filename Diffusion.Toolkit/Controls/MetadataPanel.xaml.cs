using Diffusion.Toolkit.Models;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Configuration;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Windows.Media.Imaging;
using Diffusion.Toolkit.Services;
using System;

namespace Diffusion.Toolkit.Controls
{
    /// <summary>
    /// Interaction logic for MetadataPanel.xaml
    /// </summary>
    public partial class MetadataPanel : UserControl
    {
        public static readonly DependencyProperty CurrentImageProperty = DependencyProperty.Register(
            nameof(CurrentImage),
            typeof(ImageViewModel),
            typeof(MetadataPanel),
            new PropertyMetadata(default(ImageEntry), OnCurrentImageChanged)
        );

        public ImageViewModel CurrentImage
        {
            get => (ImageViewModel)GetValue(CurrentImageProperty);
            set => SetValue(CurrentImageProperty, value);
        }

        private static async void OnCurrentImageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MetadataPanel panel && e.NewValue is ImageViewModel imageViewModel)
            {
                await panel.LoadFacesAsync(imageViewModel);
            }
        }

        public static readonly DependencyProperty MetadataSectionProperty = DependencyProperty.Register(
            nameof(MetadataSection),
            typeof(MetadataSection),
            typeof(MetadataPanel),
            new PropertyMetadata(default(ImageEntry))
        );

        public MetadataSection MetadataSection
        {
            get => (MetadataSection)GetValue(MetadataSectionProperty);
            set => SetValue(MetadataSectionProperty, value);
        }

        public MetadataPanel()
        {
            InitializeComponent();
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            SetMetadataState(AccordionState.Collapsed);
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            SetMetadataState(AccordionState.Expanded);
        }

        private void SetMetadataState(AccordionState state)
        {
            PromptMetadata.State = state;
            NegativePromptMetadata.State = state;
            SeedMetadata.State = state;
            SamplerMetadata.State = state;
            OtherMetadata.State = state;
            ModelMetadata.State = state;
            PathMetadata.State = state;
            AlbumMetadata.State = state;
            DateMetadata.State = state;
        }

        private void AlbumName_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var album = ((Album)((TextBox)sender).DataContext);
            CurrentImage.OpenAlbumCommand?.Execute(album);
        }

        private void UIElement_OnGotFocus(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
        }

        private async void FaceThumbnail_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is Border border && border.DataContext is Models.FaceViewModel face)
            {
                // Double-click: Find similar faces and navigate to gallery
                await NavigateToSimilarFacesAsync(face);
            }
        }

        private async void FaceName_OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is Models.FaceViewModel face)
            {
                try
                {
                    // If face has cluster, update cluster name
                    if (face.ClusterId.HasValue)
                    {
                        await ServiceLocator.DataStore.UpdateClusterLabel(face.ClusterId.Value, face.ClusterLabel);
                    }
                    else if (!string.IsNullOrWhiteSpace(face.ClusterLabel) && face.ClusterLabel != "Unknown")
                    {
                        // Create new cluster for this face with the given name
                        var clusterId = await ServiceLocator.DataStore.CreateFaceClusterAsync(face.ClusterLabel, isManual: true);
                        await ServiceLocator.DataStore.AssignFaceToCluster(face.Id, clusterId);
                        face.ClusterId = clusterId;
                        face.ClusterFaceCount = 1;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error saving face name: {ex.Message}");
                }
            }
        }

        private async Task NavigateToSimilarFacesAsync(Models.FaceViewModel face)
        {
            try
            {
                // Get face data with embedding
                var faceEntity = await ServiceLocator.DataStore.GetFaceWithEmbedding(face.Id);
                if (faceEntity?.ArcFaceEmbedding == null)
                {
                    Logger.Log($"Face {face.Id} has no embedding");
                    return;
                }

                // Find similar faces using threshold from settings
                var threshold = ServiceLocator.Settings.FaceRecognitionThreshold;
                var similarFaces = await ServiceLocator.DataStore.FindSimilarFaces(
                    faceEntity.ArcFaceEmbedding, threshold, limit: 100);

                if (similarFaces.Count <= 1) // Only found itself
                {
                    Logger.Log($"No similar faces found for threshold {threshold:P0}");
                    return;
                }

                // If face already has a cluster, navigate to that cluster's gallery
                if (face.ClusterId.HasValue)
                {
                    ServiceLocator.NavigatorService.NavigateToFaceGallery(face.ClusterId.Value);
                }
                else
                {
                    // Create a new temporary cluster and add similar faces
                    var clusterName = string.IsNullOrWhiteSpace(face.ClusterLabel) || face.ClusterLabel == "Unknown" 
                        ? $"Group {DateTime.Now:yyyyMMddHHmmss}" 
                        : face.ClusterLabel;
                    
                    var clusterId = await ServiceLocator.DataStore.CreateFaceClusterAsync(clusterName, isManual: true);
                    
                    // Add the source face and all similar faces to the cluster
                    foreach (var (faceId, _, _) in similarFaces)
                    {
                        await ServiceLocator.DataStore.AssignFaceToCluster(faceId, clusterId);
                    }
                    
                    // Update the face view model
                    face.ClusterId = clusterId;
                    face.ClusterFaceCount = similarFaces.Count;
                    
                    // Navigate to the new cluster gallery
                    ServiceLocator.NavigatorService.NavigateToFaceGallery(clusterId);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error navigating to similar faces: {ex.Message}");
            }
        }

        private async Task LoadFacesAsync(ImageViewModel imageViewModel)
        {
            try
            {
                // Get faces from database
                var faces = await ServiceLocator.DataStore.GetFacesForImage(imageViewModel.Id);
                
                // Convert to view models
                var faceViewModels = new System.Collections.Generic.List<Models.FaceViewModel>();
                
                foreach (var face in faces)
                {
                    var faceViewModel = new Models.FaceViewModel
                    {
                        Id = face.Id,
                        ImageId = face.ImageId,
                        ClusterId = face.FaceGroupId,
                        Confidence = face.Confidence,
                        QualityScore = face.QualityScore
                    };
                    
                    // Load face group info if available
                    if (face.FaceGroupId.HasValue)
                    {
                        var group = await ServiceLocator.DataStore.GetFaceGroupAsync(face.FaceGroupId.Value);
                        if (group != null)
                        {
                            faceViewModel.ClusterLabel = group.Name ?? "Unknown";
                            faceViewModel.ClusterFaceCount = group.FaceCount;
                        }
                    }
                    
                    // Load face crop image from database blob
                    if (face.FaceCrop != null && face.FaceCrop.Length > 0)
                    {
                        try
                        {
                            using var ms = new MemoryStream(face.FaceCrop);
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = ms;
                            bitmap.DecodePixelWidth = 120; // Match the UI width
                            bitmap.EndInit();
                            bitmap.Freeze();
                            
                            faceViewModel.FaceCropImage = bitmap;
                        }
                        catch
                        {
                            // If face crop fails to load, skip it
                        }
                    }
                    
                    faceViewModels.Add(faceViewModel);
                }
                
                // Update the image view model on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    imageViewModel.DetectedFaces = faceViewModels;
                });
            }
            catch (System.Exception ex)
            {
                Logger.Log($"Error loading faces for image {imageViewModel.Id}: {ex.Message}");
            }
        }
    }
}
