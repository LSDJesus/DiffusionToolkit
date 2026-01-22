using Diffusion.Toolkit.Classes;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Diffusion.Toolkit.Services;
using Node = Diffusion.IO.Node;
using PgAlbum = Diffusion.Database.PostgreSQL.Models.Album;

namespace Diffusion.Toolkit.Models;

public class ImageViewModel : BaseNotify
{
    private ICommand _copyOthersCommand = null!;
    private ICommand _copyNegativePromptCommand = null!;
    private ICommand _copyPathCommand = null!;
    private ICommand _copyPromptCommand = null!;
    private ICommand _copyParametersCommand = null!;
    private ICommand _showInExplorerCommand = null!;
    private ICommand _showInThumbnails = null!;
    private ICommand _deleteCommand = null!;
    private ICommand _favoriteCommand = null!;

    private BitmapSource? _image;

    private string _path = string.Empty;
    private string _prompt = string.Empty;
    private string _negativePrompt = string.Empty;
    private string _otherParameters = string.Empty;
    private string _modelName = string.Empty;
    private string _date = string.Empty;

    private bool _favorite;
    private int? _rating;
    private bool _nsfw;
    private bool _forDeletion;

    private bool _isParametersVisible;
    private long _seed;
    private string _modelHash = string.Empty;
    private ICommand _toggleParameters = null!;
    private string? _aestheticScore;
    private ICommand _searchModelCommand = null!;
    private IEnumerable<PgAlbum> _albums = new List<PgAlbum>();
    private decimal _cfgScale;
    private int _height;
    private int _width;
    private int _steps;
    private string? _sampler;
    private bool _isLoading;
    private bool _isMessageVisible;
    private string _message = string.Empty;
    private ICommand _openAlbumCommand = null!;
    private ICommand _removeFromAlbumCommand = null!;
    private string? _workflow;
    private IReadOnlyCollection<Node> _nodes = new List<Node>();
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private string? _tags;
    private string? _caption;
    private bool _hasTags;
    private ICommand? _copyTagsCommand;
    private ICommand? _copyCaptionCommand;
    private List<FaceViewModel> _detectedFaces = new();
    private int _faceCount;

    public ImageViewModel()
    {
        CopyPathCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyPath);
        CopyPromptCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyPrompt);
        CopyNegativePromptCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyNegative);
        //_model.CurrentImage.CopySeed = new RelayCommand<object>(CopySeed);
        //_model.CurrentImage.CopyHash = new RelayCommand<object>(CopyHash);
        CopyOthersCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyOthers);
        CopyParametersCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyParameters);
        //ShowInExplorerCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.ShowInExplorer);
    }

    public MainModel MainModel => ServiceLocator.MainModel!;

    public int Id { get; set; }

    public bool IsMessageVisible
    {
        get => _isMessageVisible;
        set => SetField(ref _isMessageVisible, value);
    }

    public BitmapSource? Image
    {
        get => _image;
        set => SetField(ref _image, value);
    }

    public string Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }

    public string? Prompt
    {
        get => _prompt;
        set => SetField(ref _prompt, value ?? string.Empty);
    }

    public string? NegativePrompt
    {
        get => _negativePrompt;
        set => SetField(ref _negativePrompt, value ?? string.Empty);
    }

    public string? OtherParameters
    {
        get => _otherParameters;
        set => SetField(ref _otherParameters, value ?? string.Empty);
    }

    public decimal CFGScale
    {
        get => _cfgScale;
        set => SetField(ref _cfgScale, value);
    }

    public int Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    public int Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    public string ModelName
    {
        get => _modelName;
        set => SetField(ref _modelName, value);
    }


    public string Date
    {
        get => _date;
        set => SetField(ref _date, value);
    }

    public ICommand CopyPromptCommand
    {
        get => _copyPromptCommand;
        set => SetField(ref _copyPromptCommand, value);
    }

    public ICommand SearchModelCommand
    {
        get => _searchModelCommand;
        set => SetField(ref _searchModelCommand, value);
    }

    public ICommand CopyPathCommand
    {
        get => _copyPathCommand;
        set => SetField(ref _copyPathCommand, value);
    }

    public ICommand ShowInExplorerCommand
    {
        get => _showInExplorerCommand;
        set => SetField(ref _showInExplorerCommand, value);
    }

    public ICommand DeleteCommand
    {
        get => _deleteCommand;
        set => SetField(ref _deleteCommand, value);
    }

    public ICommand FavoriteCommand
    {
        get => _favoriteCommand;
        set => SetField(ref _favoriteCommand, value);
    }


    public ICommand CopyNegativePromptCommand
    {
        get => _copyNegativePromptCommand;
        set => SetField(ref _copyNegativePromptCommand, value);
    }

    public ICommand CopyOthersCommand
    {
        get => _copyOthersCommand;
        set => SetField(ref _copyOthersCommand, value);
    }

    public ICommand CopyParametersCommand
    {
        get => _copyParametersCommand;
        set => SetField(ref _copyParametersCommand, value);
    }

    public bool Favorite
    {
        get => _favorite;
        set => SetField(ref _favorite, value);
    }

    public int? Rating
    {
        get => _rating;
        set => SetField(ref _rating, value);
    }

    public bool ForDeletion
    {
        get => _forDeletion;
        set => SetField(ref _forDeletion, value);
    }

    public bool NSFW
    {
        get => _nsfw;
        set => SetField(ref _nsfw, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => SetField(ref _hasError, value);
    }

    public ICommand ShowInThumbnails
    {
        get => _showInThumbnails;
        set => SetField(ref _showInThumbnails, value);
    }

    public bool IsParametersVisible
    {
        get => _isParametersVisible;
        set => SetField(ref _isParametersVisible, value);
    }

    public ICommand ToggleParameters
    {
        get => _toggleParameters;
        set => SetField(ref _toggleParameters, value);
    }

    public long Seed
    {
        get => _seed;
        set => SetField(ref _seed, value);
    }

    public string? ModelHash
    {
        get => _modelHash;
        set => SetField(ref _modelHash, value ?? string.Empty);
    }

    public string? AestheticScore
    {
        get => _aestheticScore;
        set => SetField(ref _aestheticScore, value);
    }

    public IEnumerable<PgAlbum> Albums
    {
        get => _albums;
        set => SetField(ref _albums, value);
    }

    public string? Sampler
    {
        get => _sampler;
        set => SetField(ref _sampler, value);
    }

    public int Steps
    {
        get => _steps;
        set => SetField(ref _steps, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public ICommand OpenAlbumCommand
    {
        get => _openAlbumCommand;
        set => SetField(ref _openAlbumCommand, value);
    }

    public ICommand RemoveFromAlbumCommand
    {
        get => _removeFromAlbumCommand;
        set => SetField(ref _removeFromAlbumCommand, value);
    }

    public string? Workflow
    {
        get => _workflow;
        set => SetField(ref _workflow, value);
    }

    public IReadOnlyCollection<Node> Nodes
    {
        get => _nodes;
        set => SetField(ref _nodes, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public string? Tags
    {
        get => _tags;
        set => SetField(ref _tags, value);
    }

    public string? Caption
    {
        get => _caption;
        set => SetField(ref _caption, value);
    }

    public bool HasTags
    {
        get => _hasTags;
        set => SetField(ref _hasTags, value);
    }

    public ICommand? CopyTagsCommand
    {
        get => _copyTagsCommand;
        set => SetField(ref _copyTagsCommand, value);
    }

    public ICommand? CopyCaptionCommand
    {
        get => _copyCaptionCommand;
        set => SetField(ref _copyCaptionCommand, value);
    }
    
    /// <summary>
    /// List of detected faces in this image
    /// </summary>
    public List<FaceViewModel> DetectedFaces
    {
        get => _detectedFaces;
        set
        {
            if (SetField(ref _detectedFaces, value))
            {
                OnPropertyChanged(nameof(HasDetectedFaces));
                OnPropertyChanged(nameof(HasNoFaces));
                FaceCount = value?.Count ?? 0;
            }
        }
    }
    
    /// <summary>
    /// Number of faces detected in this image
    /// </summary>
    public int FaceCount
    {
        get => _faceCount;
        set
        {
            if (SetField(ref _faceCount, value))
            {
                OnPropertyChanged(nameof(HasDetectedFaces));
                OnPropertyChanged(nameof(HasNoFaces));
            }
        }
    }
    
    public bool HasDetectedFaces => FaceCount > 0;
    public bool HasNoFaces => FaceCount == 0;
}