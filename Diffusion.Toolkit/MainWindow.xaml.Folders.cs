using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Common.Query;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Diffusion.Toolkit.Thumbnails;
using Path = System.IO.Path;

namespace Diffusion.Toolkit
{
    public partial class MainWindow
    {
        private async Task InitFolders()
        {
            _model.MoveSelectedImagesToFolder = MoveSelectedImagesToFolder;

            _model.RescanFolderCommand = new RelayCommand<FolderViewModel>((folder) =>
            {
                // Fire-and-forget: intentionally not awaited as it runs in background with progress tracking
                _ = ServiceLocator.ScanningService.ScanFolder(folder, true);
            });

            _model.RefreshFolderCommand = new RelayCommand<FolderViewModel>((folder) =>
            {
                ServiceLocator.FolderService.RefreshFolder(folder);
            });

            _model.ScanFolderCommand = new RelayCommand<FolderViewModel>((folder) =>
            {
                // Fire-and-forget: intentionally not awaited as it runs in background with progress tracking
                _ = ServiceLocator.ScanningService.ScanFolder(folder, false);
            });

            _model.CreateFolderCommand = new AsyncCommand<FolderViewModel>(async (o) =>
            {
                await ServiceLocator.FolderService.ShowCreateFolderDialog(o);
            });


            _model.RenameFolderCommand = new AsyncCommand<FolderViewModel>(async (o) =>
            {
                await ServiceLocator.FolderService.ShowRenameFolderDialog(o.Name, o.Path);
            });

            _model.RemoveFolderCommand = new AsyncCommand<FolderViewModel>(async (o) =>
            {
                await ServiceLocator.FolderService.ShowRemoveFolderDialog(o);
            });

            _model.DeleteFolderCommand = new AsyncCommand<FolderViewModel>(async (o) =>
            {
                var selectedFolders = ServiceLocator.FolderService.SelectedFolders.ToList();

                if (await ServiceLocator.FolderService.ShowDeleteFolderDialog(selectedFolders))
                {
                    _search.OpenFolder(selectedFolders[0].Parent);
                }

            });

            _model.ArchiveFolderCommand = new RelayCommand<bool>((o) =>
            {
                Task.Run(async () =>
                {
                    var folders = ServiceLocator.FolderService.SelectedFolders;

                    foreach (var folder in folders)
                    {
                        ServiceLocator.DataStore.SetFolderArchived(folder.Id, o, false);
                    }

                    await ServiceLocator.FolderService.LoadFolders();
                });
            });

            _model.ArchiveFolderRecursiveCommand = new RelayCommand<bool>((o) =>
            {
                Task.Run(async () =>
                {
                    var folders = ServiceLocator.FolderService.SelectedFolders;

                    foreach (var folder in folders)
                    {
                        ServiceLocator.DataStore.SetFolderArchived(folder.Id, o, true);
                    }

                    await ServiceLocator.FolderService.LoadFolders();
                });
            });

            _model.ExcludeFolderCommand = new RelayCommand<bool>((o) =>
            {
                var folders = ServiceLocator.FolderService.SelectedFolders;

                var folderChanges = folders.Select(d => new FolderChange()
                {
                    Path = d.Path,
                    FolderType = FolderType.Excluded,
                    ChangeType = o ? ChangeType.Add : ChangeType.Remove,
                    Recursive = false
                });

                ServiceLocator.FolderService.ApplyDBFolderChanges(folderChanges);

                _ = ServiceLocator.FolderService.LoadFolders();
                ServiceLocator.SearchService.RefreshResults();
            });

            _model.ExcludeFolderRecursiveCommand = new RelayCommand<bool>((o) =>
            {
                var folders = ServiceLocator.FolderService.SelectedFolders;

                var folderChanges = folders.Select(d => new FolderChange()
                {
                    Path = d.Path,
                    FolderType = FolderType.Excluded,
                    ChangeType = o ? ChangeType.Add : ChangeType.Remove,
                    Recursive = true
                });

                ServiceLocator.FolderService.ApplyDBFolderChanges(folderChanges);

                _ = ServiceLocator.FolderService.LoadFolders();
                ServiceLocator.SearchService.RefreshResults();
            });

            _model.ReloadFoldersCommand = new RelayCommand<object>((o) =>
            {
                _ = ServiceLocator.FolderService.LoadFolders();
            });

            _model.TagFolderWithJoyTagCommand = new AsyncCommand<FolderViewModel>(async (folder) =>
            {
                await TagFolder(folder, tagWithJoyTag: true, tagWithWD: false, caption: false);
            });

            _model.TagFolderWithWDTagCommand = new AsyncCommand<FolderViewModel>(async (folder) =>
            {
                await TagFolder(folder, tagWithJoyTag: false, tagWithWD: true, caption: false);
            });

            _model.TagFolderWithBothCommand = new AsyncCommand<FolderViewModel>(async (folder) =>
            {
                await TagFolder(folder, tagWithJoyTag: true, tagWithWD: true, caption: false);
            });

            _model.CaptionFolderCommand = new AsyncCommand<FolderViewModel>(async (folder) =>
            {
                await TagFolder(folder, tagWithJoyTag: false, tagWithWD: false, caption: true);
            });

            _model.ImportSidecarsCommand = new AsyncCommand<FolderViewModel>(async (folder) =>
            {
                await ImportSidecarsFromFolder(folder);
            });

            _model.ExportMetadataCommand = new AsyncCommand<FolderViewModel>(async (folder) =>
            {
                await ExportMetadataToFiles(folder);
            });

            _model.ConvertImagesCommand = new AsyncCommand<FolderViewModel>(async (folder) =>
            {
                await ConvertImagesToWebP(folder);
            });

            await ServiceLocator.FolderService.LoadFolders();

        }

        /// <summary>
        /// Queue folder images for background tagging/captioning
        /// </summary>
        private async Task QueueFolderForBackgroundProcessing(FolderViewModel folder, bool captionOnly = false)
        {
            if (folder == null) return;

            var dataStore = ServiceLocator.DataStore;
            var bgService = ServiceLocator.BackgroundTaggingService;
            
            if (dataStore == null || bgService == null)
            {
                System.Windows.MessageBox.Show("Background service not available.", 
                    "Error", 
                    System.Windows.MessageBoxButton.OK, 
                    System.Windows.MessageBoxImage.Error);
                return;
            }

            // Queue for tagging (only images without tags)
            int taggedQueued = 0;
            int captionQueued = 0;
            
            if (!captionOnly)
            {
                taggedQueued = await dataStore.QueueFolderForTagging(folder.Id, includeSubfolders: true);
                if (taggedQueued > 0)
                {
                    bgService.StartTagging();
                }
            }

            // Queue for captioning (only images without captions)
            captionQueued = await dataStore.QueueFolderForCaptioning(folder.Id, includeSubfolders: true);
            if (captionQueued > 0)
            {
                bgService.StartCaptioning();
            }

            var message = $"Queued {taggedQueued} images for tagging, {captionQueued} for captioning in '{folder.Name}'";
            ServiceLocator.ToastService?.Toast(message, "Background Processing");
            Logger.Log(message);
        }

        private async Task TagFolder(FolderViewModel folder, bool tagWithJoyTag, bool tagWithWD, bool caption)
        {
            if (folder == null) return;

            // Get all images in the folder recursively
            var images = ServiceLocator.DataStore.GetFolderImages(folder.Id, true);
            var imageIds = images.Select(img => img.Id).ToList();

            if (imageIds.Count == 0)
            {
                System.Windows.MessageBox.Show($"No images found in folder '{folder.Name}'.", 
                    "Auto-Tag Folder", 
                    System.Windows.MessageBoxButton.OK, 
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // Open tagging window with pre-selected options
            var window = new Diffusion.Toolkit.Windows.TaggingWindow(imageIds)
            {
                Owner = this
            };

            // Pre-configure the window based on what was selected
            window.Loaded += (s, e) =>
            {
                // Access the checkboxes by name and set their IsChecked property
                var joyTagCheckBox = window.FindName("JoyTagCheckBox") as System.Windows.Controls.CheckBox;
                var wdTagCheckBox = window.FindName("WDTagCheckBox") as System.Windows.Controls.CheckBox;
                var joyCaptionCheckBox = window.FindName("JoyCaptionCheckBox") as System.Windows.Controls.CheckBox;

                if (joyTagCheckBox != null) joyTagCheckBox.IsChecked = tagWithJoyTag;
                if (wdTagCheckBox != null) wdTagCheckBox.IsChecked = tagWithWD;
                if (joyCaptionCheckBox != null) joyCaptionCheckBox.IsChecked = caption;
            };

            if (window.ShowDialog() == true)
            {
                // Check which button was clicked
                if (window.SelectedProcessMode == Diffusion.Toolkit.Windows.TaggingWindow.ProcessMode.AddToQueue)
                {
                    // Queue for background processing
                    await QueueImagesToBackground(imageIds, window);
                }
                else if (window.SelectedProcessMode == Diffusion.Toolkit.Windows.TaggingWindow.ProcessMode.ProcessNow)
                {
                    // Queue with high priority (front of queue)
                    await QueueImagesToBackground(imageIds, window, highPriority: true);
                }
                
                // Refresh the search results
                ServiceLocator.SearchService.RefreshResults();
            }
        }

        /// <summary>
        /// Queue images to background processing based on tagging window selections
        /// </summary>
        private async Task QueueImagesToBackground(List<int> imageIds, Diffusion.Toolkit.Windows.TaggingWindow window, bool highPriority = false)
        {
            var dataStore = ServiceLocator.DataStore;
            var bgService = ServiceLocator.BackgroundTaggingService;
            var settings = ServiceLocator.Settings;
            
            if (dataStore == null || bgService == null)
            {
                System.Windows.MessageBox.Show("Background service not available.", 
                    "Error", 
                    System.Windows.MessageBoxButton.OK, 
                    System.Windows.MessageBoxImage.Error);
                return;
            }

            // Get checkbox states
            var joyTagCheckBox = window.FindName("JoyTagCheckBox") as System.Windows.Controls.CheckBox;
            var wdTagCheckBox = window.FindName("WDTagCheckBox") as System.Windows.Controls.CheckBox;
            var joyCaptionCheckBox = window.FindName("JoyCaptionCheckBox") as System.Windows.Controls.CheckBox;

            bool doTagging = (joyTagCheckBox?.IsChecked == true) || (wdTagCheckBox?.IsChecked == true);
            bool doCaptioning = joyCaptionCheckBox?.IsChecked == true;

            int taggedQueued = 0;
            int captionQueued = 0;

            // Queue for tagging (filter based on skip settings)
            if (doTagging)
            {
                var imagesToTag = imageIds;
                
                // Check skip already tagged setting
                if (settings?.SkipAlreadyTaggedImages == true)
                {
                    // Filter out images that already have tags
                    var imagesWithTags = new List<int>();
                    foreach (var imageId in imageIds)
                    {
                        var tags = await dataStore.GetImageTagsAsync(imageId);
                        if (tags != null && tags.Any())
                        {
                            imagesWithTags.Add(imageId);
                        }
                    }
                    imagesToTag = imageIds.Except(imagesWithTags).ToList();
                    
                    if (imagesWithTags.Count > 0)
                    {
                        Logger.Log($"Skipped {imagesWithTags.Count} images that already have tags");
                    }
                }
                
                if (imagesToTag.Count > 0)
                {
                    await dataStore.SetNeedsTagging(imagesToTag, true);
                    taggedQueued = imagesToTag.Count;
                }
            }

            // Queue for captioning (filter based on skip settings)
            if (doCaptioning)
            {
                var imagesToCaption = imageIds;
                
                // Check skip already captioned setting
                if (settings?.SkipAlreadyCaptionedImages == true)
                {
                    // Filter out images that already have captions
                    var imagesWithCaptions = new List<int>();
                    foreach (var imageId in imageIds)
                    {
                        var caption = await dataStore.GetLatestCaptionAsync(imageId);
                        if (caption != null && !string.IsNullOrWhiteSpace(caption.Caption))
                        {
                            imagesWithCaptions.Add(imageId);
                        }
                    }
                    imagesToCaption = imageIds.Except(imagesWithCaptions).ToList();
                    
                    if (imagesWithCaptions.Count > 0)
                    {
                        Logger.Log($"Skipped {imagesWithCaptions.Count} images that already have captions");
                    }
                }
                
                if (imagesToCaption.Count > 0)
                {
                    await dataStore.SetNeedsCaptioning(imagesToCaption, true);
                    captionQueued = imagesToCaption.Count;
                }
            }

            var priorityText = highPriority ? " (high priority)" : "";
            var message = $"Queued {taggedQueued} images for tagging, {captionQueued} for captioning{priorityText}";
            ServiceLocator.ToastService?.Toast(message, "Background Processing");
            Logger.Log(message);

            // Ask user if they want to start processing now
            var result = System.Windows.MessageBox.Show(
                $"{message}\n\nDo you want to start background processing now?\n\n" +
                "You can also start/stop processing using the buttons in the status bar.",
                "Start Processing?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                // Start the workers
                if (doTagging && !bgService.IsTaggingRunning)
                {
                    bgService.StartTagging();
                }
                if (doCaptioning && !bgService.IsCaptioningRunning)
                {
                    bgService.StartCaptioning();
                }
            }
        }

        private async Task ImportSidecarsFromFolder(FolderViewModel folder)
        {
            if (folder == null) return;

            // Open sidecar import window
            var window = new Diffusion.Toolkit.Windows.SidecarImportWindow(folder.Path, folder.Id)
            {
                Owner = this
            };

            if (window.ShowDialog() == true)
            {
                // Refresh the search results to show imported tags
                ServiceLocator.SearchService.RefreshResults();
            }
        }

        private async Task ExportMetadataToFiles(FolderViewModel folder)
        {
            if (folder == null) return;

            // Open metadata export window
            var window = new Diffusion.Toolkit.Windows.MetadataExportWindow(folder.Path, folder.Id)
            {
                Owner = this
            };

            window.ShowDialog();
        }

        private async Task ConvertImagesToWebP(FolderViewModel folder)
        {
            if (folder == null) return;

            // Open image conversion window
            var window = new Diffusion.Toolkit.Windows.ImageConversionWindow(folder.Path, folder.Id)
            {
                Owner = this
            };

            window.ShowDialog();

            // Refresh the search results to show updated file paths
            ServiceLocator.SearchService.RefreshResults();
        }
    }
}