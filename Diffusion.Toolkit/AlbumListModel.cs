using System.Collections.Generic;
using System.Windows.Input;
using Diffusion.Database.Models;
using Diffusion.Toolkit.Models;
using PgAlbum = Diffusion.Database.PostgreSQL.Models.Album;

namespace Diffusion.Toolkit;

public class AlbumListModel : BaseNotify
{
    private string _albumName;
    private bool _isNewAlbum;
    private bool _isExistingAlbum;
    private PgAlbum? _selectedAlbum;
    private IEnumerable<PgAlbum> _albums;
    private bool _canClickOk;

    public ICommand Escape { get; set; }

    public bool IsNewAlbum
    {
        get => _isNewAlbum;
        set => SetField(ref _isNewAlbum, value);
    }

    public bool IsExistingAlbum
    {
        get => _isExistingAlbum;
        set => SetField(ref _isExistingAlbum, value);
    }

    public PgAlbum? SelectedAlbum
    {
        get => _selectedAlbum;
        set => SetField(ref _selectedAlbum, value);
    }

    public string AlbumName
    {
        get => _albumName;
        set => SetField(ref _albumName, value);
    }

    public IEnumerable<PgAlbum> Albums
    {
        get => _albums;
        set => SetField(ref _albums, value);
    }

    public bool CanClickOk
    {
        get => _canClickOk;
        set => SetField(ref _canClickOk, value);
    }

}