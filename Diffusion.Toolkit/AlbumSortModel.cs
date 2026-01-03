using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Models;
using PgAlbum = Diffusion.Database.PostgreSQL.Models.Album;

namespace Diffusion.Toolkit;

public class AlbumSortModel : BaseNotify
{
    private ObservableCollection<PgAlbum> _albums;
    private ObservableCollection<PgAlbum> _sortedAlbums;
    private PgAlbum? _selectedAlbum;
    private string _sortAlbumsBy;
    public ICommand Escape { get; set; }

    public string SortAlbumsBy
    {
        get => _sortAlbumsBy;
        set => SetField(ref _sortAlbumsBy, value);
    }

    public PgAlbum? SelectedAlbum
    {
        get => _selectedAlbum;
        set => SetField(ref _selectedAlbum, value);
    }

    public ObservableCollection<PgAlbum> Albums
    {
        get => _albums;
        set => SetField(ref _albums, value);
    }

    public ObservableCollection<PgAlbum> SortedAlbums
    {
        get => _sortedAlbums;
        set => SetField(ref _sortedAlbums, value);
    }
    public ICommand MoveUpCommand { get; set; }
    public ICommand MoveDownCommand { get; set; }
}
