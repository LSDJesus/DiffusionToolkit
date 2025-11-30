using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Pages;

/// <summary>
/// Search page partial - Model Library functionality
/// </summary>
public partial class Search
{
    /// <summary>
    /// Initialize the Model Library navigation section
    /// </summary>
    private async Task InitializeModelLibraryAsync()
    {
        try
        {
            _model.MainModel.ModelLibraryFolders = new ObservableCollection<ModelLibraryFolderViewModel>();
            _model.MainModel.RefreshModelLibraryCommand = new RelayCommand<object>(o => _ = RefreshModelLibraryAsync());
            _model.MainModel.ScanModelsCommand = new RelayCommand<object>(o => _ = ScanModelsAsync());
            
            await RefreshModelLibraryAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize Model Library: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Refresh the Model Library folder list
    /// </summary>
    private async Task RefreshModelLibraryAsync()
    {
        try
        {
            if (ServiceLocator.DataStore == null) return;
            
            var folders = await ServiceLocator.DataStore.GetModelFoldersAsync();
            
            var viewModels = new ObservableCollection<ModelLibraryFolderViewModel>();
            
            // Group by resource type
            var grouped = folders
                .Where(f => f.Enabled)
                .GroupBy(f => f.ResourceType)
                .OrderBy(g => GetResourceTypeOrder(g.Key));
            
            foreach (var group in grouped)
            {
                var resourceType = group.Key;
                var displayName = GetResourceTypeDisplayName(resourceType);
                
                // Get count of models for this type
                var models = await ServiceLocator.DataStore.GetModelResourcesByTypeAsync(resourceType);
                var modelCount = models.Count;
                
                var folderVm = new ModelLibraryFolderViewModel
                {
                    Name = displayName,
                    ResourceType = resourceType,
                    Path = group.First().Path,
                    ModelCount = modelCount,
                    Depth = 0
                };
                
                viewModels.Add(folderVm);
            }
            
            await Dispatcher.InvokeAsync(() =>
            {
                _model.MainModel.ModelLibraryFolders = viewModels;
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to refresh Model Library: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Scan for new models
    /// </summary>
    private async Task ScanModelsAsync()
    {
        try
        {
            if (ServiceLocator.ModelResourceService == null) return;
            
            await ServiceLocator.ModelResourceService.ScanAllFoldersAsync(CancellationToken.None);
            await RefreshModelLibraryAsync();
            
            ServiceLocator.ToastService?.Toast("Model scan complete", "");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to scan models: {ex.Message}");
            MessageBox.Show($"Failed to scan models: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    /// <summary>
    /// Handle click on a Model Library folder
    /// </summary>
    private async void ModelLibraryFolder_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var button = sender as FrameworkElement;
            var folderVm = button?.Tag as ModelLibraryFolderViewModel;
            
            if (folderVm == null) return;
            
            // Clear previous selection
            if (_model.MainModel.ModelLibraryFolders != null)
            {
                foreach (var f in _model.MainModel.ModelLibraryFolders)
                {
                    f.IsSelected = false;
                }
            }
            
            folderVm.IsSelected = true;
            _model.MainModel.SelectedModelLibraryFolder = folderVm;
            
            // Load models of this type into the browser
            await LoadModelsIntoThumbnailViewAsync(folderVm.ResourceType);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to handle Model Library folder click: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Load models of a specific type into the thumbnail view
    /// </summary>
    private async Task LoadModelsIntoThumbnailViewAsync(string resourceType)
    {
        try
        {
            if (ServiceLocator.DataStore == null) return;
            
            _model.IsBusy = true;
            
            var models = await ServiceLocator.DataStore.GetModelResourcesByTypeAsync(resourceType, 500, 0);
            
            // Start a new thumbnail batch
            ServiceLocator.ThumbnailService.StopCurrentBatch();
            var batchId = ServiceLocator.ThumbnailService.StartBatch();
            
            // Convert to ImageEntry for display in the existing thumbnail view
            var entries = models.Select(m => new ImageEntry(batchId)
            {
                Id = m.Id,
                Name = !string.IsNullOrEmpty(m.CivitaiName) ? m.CivitaiName : m.FileName,
                FileName = m.FileName,
                Path = m.FilePath,
                EntryType = EntryType.Model,
                Dispatcher = Dispatcher,
                // Use file size as a rough proxy for dimensions (models don't have width/height)
                Width = 256,
                Height = 256,
            }).ToList();
            
            Logger.Log($"Loading {entries.Count} models of type {resourceType}");
            
            // Update the model's Images collection on the UI thread
            await Dispatcher.InvokeAsync(() =>
            {
                // Update header/status
                _model.TotalFiles = entries.Count;
                _model.Results = $"{entries.Count} models";
                _model.Page = 1;
                _model.Pages = 1;
                
                // Set the images
                _model.Images = new ObservableCollection<ImageEntry>(entries);
                
                // Reset selection
                if (entries.Count > 0)
                {
                    _model.SelectedImageEntry = entries[0];
                }
                else
                {
                    _model.SelectedImageEntry = null;
                }
            });
            
            // Trigger thumbnail loading
            ThumbnailListView.ResetView(null);
            ThumbnailListView.ReloadThumbnailsView();
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to load models: {ex.Message}");
        }
        finally
        {
            _model.IsBusy = false;
        }
    }
    
    /// <summary>
    /// Get display name for resource type
    /// </summary>
    private static string GetResourceTypeDisplayName(string resourceType)
    {
        return resourceType switch
        {
            ResourceTypes.Lora => "LoRAs",
            ResourceTypes.Embedding => "Embeddings",
            ResourceTypes.Checkpoint => "Checkpoints",
            ResourceTypes.DiffusionModel => "Diffusion Models",
            ResourceTypes.Vae => "VAEs",
            ResourceTypes.ControlNet => "ControlNets",
            ResourceTypes.Upscaler => "Upscalers",
            ResourceTypes.Unet => "UNets",
            ResourceTypes.Clip => "CLIP Models",
            _ => resourceType
        };
    }
    
    /// <summary>
    /// Get sort order for resource types
    /// </summary>
    private static int GetResourceTypeOrder(string resourceType)
    {
        return resourceType switch
        {
            ResourceTypes.Checkpoint => 0,
            ResourceTypes.DiffusionModel => 1,
            ResourceTypes.Lora => 2,
            ResourceTypes.Embedding => 3,
            ResourceTypes.Vae => 4,
            ResourceTypes.ControlNet => 5,
            ResourceTypes.Upscaler => 6,
            ResourceTypes.Unet => 7,
            ResourceTypes.Clip => 8,
            _ => 99
        };
    }
}
