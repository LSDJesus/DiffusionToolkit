using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;

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
                if (o == null)
                {
                    Logger.Log("RemoveFolderCommand: folder parameter is NULL!");
                    return;
                }
                Logger.Log($"RemoveFolderCommand: Removing folder {o.Path}");
                var result = await ServiceLocator.FolderService.ShowRemoveFolderDialog(o);
                Logger.Log($"RemoveFolderCommand: Dialog result = {result}");
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
            var autoTagCheckBox = window.FindName("AutoTagCheckBox") as System.Windows.Controls.CheckBox;
            var joyCaptionCheckBox = window.FindName("JoyCaptionCheckBox") as System.Windows.Controls.CheckBox;
            var embeddingCheckBox = window.FindName("EmbeddingCheckBox") as System.Windows.Controls.CheckBox;
            var faceDetectionCheckBox = window.FindName("FaceDetectionCheckBox") as System.Windows.Controls.CheckBox;

            bool doTagging = autoTagCheckBox?.IsChecked == true;
            bool doCaptioning = joyCaptionCheckBox?.IsChecked == true;
            bool doEmbedding = embeddingCheckBox?.IsChecked == true;
            bool doFaceDetection = faceDetectionCheckBox?.IsChecked == true;

            int taggedQueued = 0;
            int captionQueued = 0;
            int embeddingQueued = 0;
            int faceDetectionQueued = 0;

            // Queue for tagging using smart queue (respects needs_tagging flag state)
            if (doTagging)
            {
                taggedQueued = await dataStore.SmartQueueForTagging(imageIds, settings?.SkipAlreadyTaggedImages ?? true);
            }

            // Queue for captioning using smart queue (respects needs_captioning flag state)
            if (doCaptioning)
            {
                captionQueued = await dataStore.SmartQueueForCaptioning(imageIds, settings?.SkipAlreadyCaptionedImages ?? true);
            }

            // Queue for embedding using smart queue (ALWAYS skips already-processed)
            if (doEmbedding)
            {
                embeddingQueued = await dataStore.SmartQueueForEmbedding(imageIds);
            }

            // Queue for face detection using smart queue (ALWAYS skips already-processed)
            if (doFaceDetection)
            {
                faceDetectionQueued = await dataStore.SmartQueueForFaceDetection(imageIds);
            }

            // Update queue counts in UI directly (faster than re-querying database)
            if (ServiceLocator.MainModel != null)
            {
                ServiceLocator.MainModel.TaggingQueueCount += taggedQueued;
                ServiceLocator.MainModel.CaptioningQueueCount += captionQueued;
                ServiceLocator.MainModel.EmbeddingQueueCount += embeddingQueued;
                ServiceLocator.MainModel.FaceDetectionQueueCount += faceDetectionQueued;
            }

            var priorityText = highPriority ? " (high priority)" : "";
            var parts = new List<string>();
            if (taggedQueued > 0) parts.Add($"{taggedQueued} for tagging");
            if (captionQueued > 0) parts.Add($"{captionQueued} for captioning");
            if (embeddingQueued > 0) parts.Add($"{embeddingQueued} for embedding");
            if (faceDetectionQueued > 0) parts.Add($"{faceDetectionQueued} for face detection");
            var message = parts.Count > 0 ? $"Queued {string.Join(", ", parts)}{priorityText}" : "No images queued";
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
                if (doEmbedding && !bgService.IsEmbeddingRunning)
                {
                    bgService.StartEmbedding();
                }
                if (doFaceDetection)
                {
                    var faceService = ServiceLocator.BackgroundFaceDetectionService;
                    if (faceService != null && !faceService.IsFaceDetectionRunning)
                    {
                        faceService.StartFaceDetection();
                    }
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