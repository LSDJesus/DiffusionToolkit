using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Diffusion.Toolkit.Thumbnails;
using SQLite;
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
                // Refresh the current view to show new tags
                if (ServiceLocator.ToastService != null)
                {
                    ServiceLocator.ToastService.Toast(
                        $"Successfully processed {imageIds.Count} image(s) in folder '{folder.Name}'", 
                        "Tagging Complete");
                }
                
                // Refresh the search results
                ServiceLocator.SearchService.RefreshResults();
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