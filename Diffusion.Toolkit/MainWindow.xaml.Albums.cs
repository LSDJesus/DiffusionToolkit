using Diffusion.Common.Query;
using System;
using System.Windows.Controls;
using System.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Diffusion.Database.PostgreSQL.Models;
using PgAlbum = Diffusion.Database.PostgreSQL.Models.Album;

namespace Diffusion.Toolkit
{
    public partial class MainWindow
    {
        private void InitAlbums()
        {
            if (_model != null)
            {
                _model.AddSelectedImagesToAlbum = AddSelectedImagesToAlbum;
            }


            if (_model != null)
            {
                _model.CreateAlbumCommand = new AsyncCommand<object>(async (o) =>
                {
                    var title = GetLocalizedText("Actions.Albums.Create.Title");

                    if (ServiceLocator.MessageService == null)
                    {
                        MessageBox.Show("MessageService is not available.", "Error");
                        return;
                    }

                    var inputResult = await ServiceLocator.MessageService.ShowInput(GetLocalizedText("Actions.Albums.Create.Message"), title);

                    var (result, name) = inputResult;

                    name = name?.Trim() ?? string.Empty;

                    if (result == PopupResult.OK)
                    {
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            await ServiceLocator.MessageService.Show(GetLocalizedText("Actions.Albums.CannotBeEmpty.Message"), title, PopupButtons.OK);
                            return;
                        }

                        await CreateAlbum(name);
                    }
                });
            }

            if (_model != null)
            {
                _model.AddAlbumCommand = new AsyncCommand<object>(async (o) =>
                {
                    var title = GetLocalizedText("Actions.Albums.Create.Title");

                    if (ServiceLocator.MessageService == null)
                    {
                        MessageBox.Show("MessageService is not available.", "Error");
                        return;
                    }

                    var (result, name) = await ServiceLocator.MessageService.ShowInput(GetLocalizedText("Actions.Albums.Create.Message"), title);

                    name = name?.Trim() ?? string.Empty;

                    if (result == PopupResult.OK)
                    {
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            await ServiceLocator.MessageService.Show(GetLocalizedText("Actions.Albums.CannotBeEmpty.Message"), title, PopupButtons.OK);
                            return;
                        }

                        await CreateAlbum(name, _model.SelectedImages);
                    }
                });
            }

            if (_model != null)
            {
                _model.AddToAlbumCommand = new RelayCommand<object>((o) =>
                {
                    var album = (Album)((MenuItem)o).Tag;
                    if (album == null)
                    {
                        MessageBox.Show("Album not found, please refresh and try again", "No Album");
                        return;
                    }
                    var images = (_model.SelectedImages ?? Enumerable.Empty<ImageEntry>()).ToList();

                    if (_dataStore != null && _dataStore.AddImagesToAlbum(album.Id, images.Select(x => x.Id)))
                    {
                        if (ServiceLocator.ToastService != null)
                        {
                            ServiceLocator.ToastService.Toast($"{images.Count} image{(images.Count == 1 ? "" : "s")} added to \"{album.Name} \".", "Add to Album");
                        }

                        UpdateAlbumCount(album.Id);

                        ServiceLocator.AlbumService.UpdateSelectedImageAlbums();

                        UpdateImageAlbums();

                        //_search.ReloadMatches(null);
                    }
                    else
                        MessageBox.Show("Album not found, please refresh and try again", "No Album");
                });
            }

            if (_model != null)
            {
                _model.RenameAlbumCommand = new AsyncCommand<AlbumModel>(async (album) =>
                {
                    var title = GetLocalizedText("Actions.Albums.Rename.Title");

                    if (ServiceLocator.MessageService == null)
                    {
                        MessageBox.Show("MessageService is not available.", "Error");
                        return;
                    }

                    if (album == null)
                    {
                        MessageBox.Show("Album not found, please refresh and try again", "No Album");
                        return;
                    }

                    var (result, name) = await ServiceLocator.MessageService.ShowInput(GetLocalizedText("Actions.Albums.Rename.Message"), title, album.Name);

                    name = name?.Trim() ?? string.Empty;

                    if (result == PopupResult.OK)
                    {
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            await ServiceLocator.MessageService.Show(GetLocalizedText("Actions.Albums.CannotBeEmpty.Message"), title, PopupButtons.OK);
                            return;
                        }

                        if (_dataStore != null)
                        {
                            _dataStore.RenameAlbum(album.Id, name);

                            UpdateAlbumName(album.Id, name);

                            UpdateImageAlbums();
                            //UpdateAlbums();
                            //SearchImages(null);
                            //LoadAlbums();
                        }
                        else
                        {
                            MessageBox.Show("DataStore is not available.", "Error");
                        }
                    }
                });
            }

            if (_model != null)
            {
                _model.RemoveFromAlbumCommand = new RelayCommand<object>((o) =>
                {
                    var album = ((MenuItem)o).Tag as Album;
                    if (album == null)
                    {
                        MessageBox.Show("Album not found, please refresh and try again", "No Album");
                        return;
                    }
                    var images = (_model.SelectedImages ?? Enumerable.Empty<ImageEntry>()).ToList();
                    int count = 0;
                    if (_dataStore != null)
                    {
                        count = _dataStore.RemoveImagesFromAlbum(album.Id, images.Select(x => x.Id));
                    }
                    else
                    {
                        MessageBox.Show("DataStore is not available.", "Error");
                        return;
                    }

                    var message = GetLocalizedText("Actions.Albums.RemoveImages.Toast")
                        .Replace("{images}", $"{count}")
                        .Replace("{album}", $"{album.Name}");

                    if (ServiceLocator.ToastService != null)
                    {
                        if (ServiceLocator.ToastService != null)
                        {
                            ServiceLocator.ToastService.Toast(message, "");
                        }
                    }

                    UpdateAlbumCount(album.Id);

                    UpdateImageAlbums();

                    ServiceLocator.AlbumService.UpdateSelectedImageAlbums();
                });
            }

            if (_model != null)
            {
                _model.RemoveAlbumCommand = new AsyncCommand<AlbumModel>(async (album) =>
                {
                    var title = GetLocalizedText("Actions.Albums.Remove.Title");

                    if (album == null)
                    {
                        MessageBox.Show("Album not found, please refresh and try again", "No Album");
                        return;
                    }

                    if (_messagePopupManager == null)
                    {
                        MessageBox.Show("MessagePopupManager is not available.", "Error");
                        return;
                    }

                    var result = await _messagePopupManager.Show(GetLocalizedText("Actions.Albums.Remove.Message").Replace("{album}", album.Name), title, PopupButtons.YesNo);

                    if (result == PopupResult.Yes)
                    {
                        if (_dataStore != null)
                        {
                            _dataStore.RemoveAlbum(album.Id);
                        }
                        else
                        {
                            MessageBox.Show("DataStore is not available.", "Error");
                            return;
                        }

                        if (_search != null && _search.QueryOptions != null && _search.QueryOptions.AlbumIds != null && _search.QueryOptions.AlbumIds.Count > 0)
                        {
                            if (_search.QueryOptions.AlbumIds.Contains(album.Id))
                            {
                                _search.QueryOptions.AlbumIds = _search.QueryOptions.AlbumIds.Except(new[] { album.Id }).ToList();
                            }
                        }

                        LoadAlbums();

                        UpdateImageAlbums();

                        // TODO: Detect whether we need to refresh?
                        if (_search != null)
                        {
                            _search.ReloadMatches(null);
                        }
                    }
                });
            }


        }

        public void UpdateImageAlbums()
        {
            if (_search != null)
            {
                _search.UpdateImagesInPlace();
            }
        }

        public void UpdateAlbumCount(int id)
        {
            if (_model?.Albums == null)
            {
                return;
            }

            var albumModel = _model.Albums.FirstOrDefault(d => d.Id == id);
            var albumView = _dataStore != null ? _dataStore.GetAlbumView(id) : null;

            if (albumModel != null && albumView != null)
            {
                albumModel.ImageCount = albumView.ImageCount;
            }
        }

        public void UpdateAlbumName(int id, string name)
        {
            if (_model?.Albums == null)
            {
                return;
            }

            var album = _model.Albums.FirstOrDefault(d => d.Id == id);
            if (album != null)
            {
                album.Name = name;
            }
        }

        private void LoadAlbums()
        {
            // Fire and forget - non-blocking album loading
            _ = LoadAlbumsAsync();
        }

        private async Task LoadAlbumsAsync()
        {
            // Run database query on background thread
            var (currentAlbums, albums) = await Task.Run(() =>
            {
                var current = (_model != null && _model.Albums != null) ? _model.Albums.ToList() : Enumerable.Empty<AlbumModel>();
                var albumList = _dataStore != null
                    ? _dataStore.GetAlbumsView().Select(a => new AlbumModel()
                    {
                        Id = a.Id,
                        Name = a.Name,
                        LastUpdated = a.LastUpdated,
                        ImageCount = a.ImageCount,
                        Order = a.Order,
                    }).ToList()
                    : new List<AlbumModel>();

                return (current, albumList);
            });

            // Back on UI thread - update ObservableCollection
            foreach (var album in albums)
            {
                var prevAlbum = currentAlbums.FirstOrDefault(d => d.Id == album.Id);

                if (prevAlbum != null)
                {
                    album.IsTicked = prevAlbum.IsTicked;
                }

                album.PropertyChanged += Album_PropertyChanged;
            }

            if (_model != null)
            {
                switch (_settings?.SortAlbumsBy ?? "Name")
                {
                    case "Name":
                        _model.Albums = new ObservableCollection<AlbumModel>((albums ?? Enumerable.Empty<AlbumModel>()).OrderBy(a => a.Name));
                        break;
                    case "Date":
                        _model.Albums = new ObservableCollection<AlbumModel>(albums?.OrderBy(a => a.LastUpdated) ?? Enumerable.Empty<AlbumModel>());
                        break;
                    case "Custom":
                        _model.Albums = new ObservableCollection<AlbumModel>(albums?.OrderBy(a => a.Order) ?? Enumerable.Empty<AlbumModel>());
                        break;
                    default:
                        _model.Albums = new ObservableCollection<AlbumModel>(albums?.OrderBy(a => a.Name) ?? Enumerable.Empty<AlbumModel>());
                        break;
                }
            }

            ServiceLocator.AlbumService.UpdateSelectedImageAlbums();

        }

        private void Album_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AlbumModel.IsTicked))
            {
                //var selectedAlbums = ServiceLocator.MainModel.Albums.Where(d => d.IsTicked).ToList();
                //ServiceLocator.MainModel.SelectedAlbumsCount = selectedAlbums.Count;
                //ServiceLocator.MainModel.HasSelectedAlbums = selectedAlbums.Any();

                //ServiceLocator.SearchService.ExecuteSearch();
            }
        }

        private async Task CreateAlbum(string name, IEnumerable<ImageEntry>? imageEntries = null)
        {
            var title = GetLocalizedText("Actions.Albums.Create.Title");

            try
            {
                if (_dataStore == null)
                {
                    if (ServiceLocator.MessageService != null)
                    {
                        await ServiceLocator.MessageService.Show("DataStore is not available.", title, PopupButtons.OK);
                    }
                    else
                    {
                        MessageBox.Show("DataStore is not available.", title);
                    }
                    return;
                }

                var album = _dataStore.CreateAlbum(new PgAlbum() { Name = name });

                var images = (imageEntries ?? Enumerable.Empty<ImageEntry>()).ToList();

                if (images.Any())
                {
                    _dataStore.AddImagesToAlbum(album.Id, images.Select(i => i.Id));

                    foreach (var imageEntry in images)
                    {
                        imageEntry.AlbumCount++;
                    }
                }

                if (ServiceLocator.ToastService != null)
                {
                    ServiceLocator.ToastService.Toast(GetLocalizedText("Actions.Albums.Created.Toast").Replace("{album}", album.Name), title);
                }

                LoadAlbums();
            }
            catch (Exception)
            {
                if (ServiceLocator.MessageService != null)
                {
                    await ServiceLocator.MessageService.Show($"Album {name} already exists!\r\n Please use another name.", "New Album", PopupButtons.OK);
                }
                else
                {
                    MessageBox.Show($"Album {name} already exists!\r\n Please use another name.", "New Album");
                }
            }
        }

        private void AddSelectedImagesToAlbum(IAlbumInfo album)
        {
            if (_model != null && _model.SelectedImages != null)
            {
                var images = _model.SelectedImages.Select(x => x.Id).ToList();
                if (_dataStore != null && _dataStore.AddImagesToAlbum(album.Id, images))
                {
                    var message = GetLocalizedText("Actions.Albums.AddImages.Toast")
                        .Replace("{images}", $"{images.Count}")
                        .Replace("{album}", $"{album.Name}");

                    if (ServiceLocator.ToastService != null)
                    {
                        ServiceLocator.ToastService.Toast(message, "");
                    }

                    UpdateAlbumCount(album.Id);

                    UpdateImageAlbums();

                    ServiceLocator.AlbumService.UpdateSelectedImageAlbums();
                    //_search.ReloadMatches(null);
                }
                else
                    MessageBox.Show("Album not found, please refresh and try again", "No Album");
            }
        }

        private void MoveSelectedImagesToFolder(FolderViewModel folder)
        {
            if (_model != null && _model.SelectedImages != null)
            {
                var imagePaths = _model.SelectedImages.Select(d => new ImagePath() { Id = d.Id, Path = d.Path }).ToList();
                ServiceLocator.FileService.MoveFiles(imagePaths, folder.Path, false).ContinueWith(d =>
                {
                    if (_search != null)
                    {
                        _search.SearchImages(null);
                    }
                });
            }
        }

    }
}