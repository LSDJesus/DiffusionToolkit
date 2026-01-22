using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Common;
using Diffusion.Toolkit.Controls;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Models;

public class SearchModel : BaseNotify
{
    private readonly MainModel _mainModel = null!;
    private ObservableCollection<ImageEntry>? _images;
    private ImageEntry? _selectedImage;
    //public object _rowLock = new object();
    private int _totalFiles;
    private int _currentPosition;

    private ICommand _searchCommand = null!;
    private string _searchText = string.Empty;

    private int _page;
    private bool _isEmpty;
    private int _pages;
    private string _results = string.Empty;
    private string _resultStatus = string.Empty;
    private string _searchHint = string.Empty;
    private ImageViewModel? _currentImage;
    private float _imageOpacity;
    private bool _hideIcons;
    private ObservableCollection<string> _searchHistory = null!;

    private ICommand? _refresh;
    private ICommand? _focusSearch;
    private string? _modeName;
    private ICommand? _showDropDown;
    private ICommand? _hideDropDown;
    private ICommand? _copyFiles;
    private ICommand _showFilter = null!;
    private ICommand _hideFilter = null!;
    private ICommand _clearSearch = null!;
    private bool _isFilterVisible;
    private FilterControlModel _filter = null!;
    private ICommand _filterCommand = null!;
    private ICommand _clearCommand = null!;
    private string _sortBy = string.Empty;
    private string _sortDirection = string.Empty;
    private ICommand _openCommand = null!;
    private ICommand _goHome = null!;
    private ViewMode _currentViewMode;
    private string _folderPath = string.Empty;
    private ICommand _goUp = null!;
    private string _album = string.Empty;
    private ObservableCollection<Album> _albums = null!;
    private ICommand _pageChangedCommand = null!;
    private IEnumerable<OptionValue> _sortOptions = null!;
    private IEnumerable<OptionValue> _sortOrderOptions = null!;

    private NavigationSection _navigationSection = null!;
    private MetadataSection _metadataSection = null!;
    private bool _isBusy;
    private ICommand _showSearchSettings = null!;
    private bool _isSearchSettingsVisible;
    private string _currentMode = string.Empty;
    private bool _isSearchHelpVisible;
    private ICommand _showSearchHelp = null!;
    private Style _searchHelpStyle = null!;
    private string _searchHelpMarkdown = string.Empty;
    private bool _hasNoImagePaths;

    public SearchModel()
    {
        _images = new ObservableCollection<ImageEntry>();
        _searchHistory = new ObservableCollection<string>();
        _currentImage = new ImageViewModel();
        _filter = new FilterControlModel();
        _imageOpacity = 1;
        _isEmpty = true;
        //_resultStatus = "Type anything to begin";
        _searchHint = "Search for the answer to the the question of life, the universe, and everything";
        _sortBy = "Date Created";
        _sortDirection = "Z-A";
        _isFilterVisible = false;
        _searchText = "";
        _refresh = DefaultCommand.Instance;
        MetadataSection = new MetadataSection();
        NavigationSection = new NavigationSection();
        SearchSettings = new SearchSettings();
        PropertyChanged += OnPropertyChanged;
    }
    
    // DefaultCommand stub for ICommand fallback
    public class DefaultCommand : ICommand
    {
        public static DefaultCommand Instance { get; } = new DefaultCommand();
    
        public event System.EventHandler? CanExecuteChanged;
    
        public bool CanExecute(object? parameter) => false;
    
        public void Execute(object? parameter) { }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurrentImage))
        {
            if (ServiceLocator.MainModel != null && CurrentImage != null)
            {
                ServiceLocator.MainModel.CurrentImage = CurrentImage;
            }
        }
        if (e.PropertyName == nameof(SelectedImageEntry))
        {
            if (ServiceLocator.MainModel != null && SelectedImageEntry != null)
            {
                ServiceLocator.MainModel.SelectedImageEntry = SelectedImageEntry;
            }
        }
    }

    //public SearchModel(MainModel mainModel)
    //{
    //    _mainModel = mainModel;
    //    _images = new ObservableCollection<ImageEntry>();
    //    _searchHistory = new ObservableCollection<string>();
    //    _currentImage = new ImageViewModel();
    //    _filter = new FilterControlModel();
    //    _imageOpacity = 1;
    //    _isEmpty = true;
    //    //_resultStatus = "Type anything to begin";
    //    _searchHint = "Search for the answer to the the question of life, the universe, and everything";
    //    _sortBy = "Date Created";
    //    _sortDirection = "Z-A";
    //    _isFilterVisible = false;
    //    MetadataSection = new MetadataSection();
    //    NavigationSection = new NavigationSection();
    //    SearchSettings = new SearchSettings();
    //}

    public MainModel MainModel => ServiceLocator.MainModel ?? throw new System.InvalidOperationException("MainModel is not initialized.");

    public ObservableCollection<ImageEntry>? Images
    {
        get => _images;
        set => SetField(ref _images, value);
    }

    public ImageViewModel? CurrentImage
    {
        get => _currentImage;
        set => SetField(ref _currentImage, value);
    }


    public ImageEntry? SelectedImageEntry
    {
        get => _selectedImage;
        set => SetField(ref _selectedImage, value);
    }


    public int CurrentPosition
    {
        get => _currentPosition;
        set => SetField(ref _currentPosition, value);
    }

    public int TotalFiles
    {
        get => _totalFiles;
        set => SetField(ref _totalFiles, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    public ObservableCollection<string> SearchHistory
    {
        get => _searchHistory;
        set
        {
            SetField(ref _searchHistory, value);
            OnPropertyChanged("ReverseSearchHistory");
        }
    }

    public ICommand SearchCommand
    {
        get => _searchCommand;
        set => SetField(ref _searchCommand, value);
    }

    public int Page
    {
        get => _page;
        set
        {
            if (value > _pages)
            {
                value = _pages;
            }

            if (_pages == 0)
            {
                value = 0;
            }
            else if (value < 1)
            {
                value = 1;
            }

            SetField(ref _page, value);
        }
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetField(ref _isEmpty, value);
    }

    public int Pages
    {
        get => _pages;
        set => SetField(ref _pages, value);
    }

    public string Results
    {
        get => _results;
        set => SetField(ref _results, value);
    }

    public string ResultStatus
    {
        get => _resultStatus;
        set => SetField(ref _resultStatus, value);
    }

    public string SearchHint
    {
        get => _searchHint;
        set => SetField(ref _searchHint, value);
    }

    public float ImageOpacity
    {
        get => _imageOpacity;
        set => SetField(ref _imageOpacity, value);
    }

    public bool HideIcons
    {
        get => _hideIcons;
        set => SetField(ref _hideIcons, value);
    }

    public ICommand Refresh
    {
        get => _refresh ?? DefaultCommand.Instance;
        set => SetField(ref _refresh, value);
    }

    public ICommand FocusSearch
    {
        get => _focusSearch ?? DefaultCommand.Instance;
        set => SetField(ref _focusSearch, value);
    }

    public string ModeName
    {
        get => _modeName ?? string.Empty;
        set => SetField(ref _modeName, value);
    }

    public ICommand ShowDropDown
    {
        get => _showDropDown ?? DefaultCommand.Instance;
        set => SetField(ref _showDropDown, value);
    }

    public ICommand HideDropDown
    {
        get => _hideDropDown ?? DefaultCommand.Instance;
        set => SetField(ref _hideDropDown, value);
    }

    public ICommand CopyFiles
    {
        get => _copyFiles ?? DefaultCommand.Instance;
        set => SetField(ref _copyFiles, value);
    }

    public ICommand ShowSearchHelp
    {
        get => _showSearchHelp;
        set => SetField(ref _showSearchHelp, value);
    }

    public ICommand ShowSearchSettings
    {
        get => _showSearchSettings;
        set => SetField(ref _showSearchSettings, value);
    }

    public ICommand ShowFilter
    {
        get => _showFilter;
        set => SetField(ref _showFilter, value);
    }

    public ICommand HideFilter
    {
        get => _hideFilter;
        set => SetField(ref _hideFilter, value);
    }

    public ICommand ClearSearch
    {
        get => _clearSearch;
        set => SetField(ref _clearSearch, value);
    }

    public bool IsFilterVisible
    {
        get => _isFilterVisible;
        set => SetField(ref _isFilterVisible, value);
    }

    public FilterControlModel Filter
    {
        get => _filter;
        set => SetField(ref _filter, value);
    }

    public ICommand FilterCommand
    {
        get => _filterCommand;
        set => SetField(ref _filterCommand, value);
    }

    public ICommand ClearCommand
    {
        get => _clearCommand;
        set => SetField(ref _clearCommand, value);
    }

    public IEnumerable<OptionValue> SortOptions
    {
        get => _sortOptions;
        set => SetField(ref _sortOptions, value);
    }

    public string SortBy
    {
        get => _sortBy;
        set => SetField(ref _sortBy, value);
    }

    public IEnumerable<OptionValue> SortOrderOptions
    {
        get => _sortOrderOptions;
        set => SetField(ref _sortOrderOptions, value);
    }

    public string SortDirection
    {
        get => _sortDirection;
        set => SetField(ref _sortDirection, value);
    }

    public ICommand OpenCommand
    {
        get => _openCommand;
        set => SetField(ref _openCommand, value);
    }

    public ICommand GoHome
    {
        get => _goHome;
        set => SetField(ref _goHome, value);
    }

    public ICommand GoUp
    {
        get => _goUp;
        set => SetField(ref _goUp, value);
    }

    public ViewMode CurrentViewMode
    {
        get => _currentViewMode;
        set
        {
            SetField(ref _currentViewMode, value);
            OnPropertyChanged(nameof(IsFolderView));
        }
    }

    public bool IsFolderView
    {
        get => _currentViewMode == ViewMode.Folder;
    }


    // TODO: merge this into above
    public string FolderPath
    {
        get => _folderPath;
        set => SetField(ref _folderPath, value);
    }

    public string Album
    {
        get => _album;
        set => SetField(ref _album, value);
    }

    public ICommand PageChangedCommand
    {
        get => _pageChangedCommand;
        set => SetField(ref _pageChangedCommand, value);
    }

    public NavigationSection NavigationSection
    {
        get => _navigationSection;
        set => SetField(ref _navigationSection, value);
    }

    public MetadataSection MetadataSection
    {
        get => _metadataSection;
        set => SetField(ref _metadataSection, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public string CurrentMode
    {
        get => _currentMode;
        set => SetField(ref _currentMode, value);
    }

    public bool IsSearchSettingsVisible
    {
        get => _isSearchSettingsVisible;
        set => SetField(ref _isSearchSettingsVisible, value);
    }

    public SearchSettings SearchSettings { get; set; }

    public bool IsSearchHelpVisible
    {
        get => _isSearchHelpVisible;
        set => SetField(ref _isSearchHelpVisible, value);
    }

    public string SearchHelpMarkdown
    {
        get => _searchHelpMarkdown;
        set => SetField(ref _searchHelpMarkdown, value);
    }

    public Style SearchHelpStyle
    {
        get => _searchHelpStyle;
        set => SetField(ref _searchHelpStyle, value);
    }

    public int Count { get; set; }
    public long Size { get; set; }

    public bool HasNoImagePaths
    {
        get => _hasNoImagePaths;
        set => SetField(ref _hasNoImagePaths, value);
    }
}