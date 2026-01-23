using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Controls;
using Diffusion.Common.Query;

namespace Diffusion.Toolkit.Models;

public class MainModel : BaseNotify
{
    private Page _page;
    private ICommand _rescan;
    private ICommand _closeCommand;
    private ICommand _settingsCommand;
    private ICommand _rebuild;
    private bool _showIcons;
    private bool _hideIcons;
    private ICommand _removeMarked;
    private int _totalProgress;
    private int _currentProgress;
    private string _status;
    private bool _isBusy;
    private ICommand _cancelCommand;
    private ICommand _aboutCommand;
    private ICommand _helpCommand;
    private ICommand _toggleInfoCommand;
    private ICommand _toggleNsfwBlurCommand;
    private ICommand _toggleHideNsfw;
    private ICommand _toggleHideDeleted;
    private bool _hideNsfw;
    private bool _hideDeleted;
    private bool _nsfwBlur;
    private bool _fitToPreview;
    private bool _actualSize;
    private ICommand _toggleFitToPreview;
    private ICommand _toggleActualSize;
    private ICommand _setThumbnailSize;
    private ICommand _poputPreview;
    private ICommand _togglePreview;
    private bool _isPreviewVisible;
    private ICommand _addAllToAlbum;
    private ICommand _markAllForDeletion;
    private ICommand _unmarkAllForDeletion;
    private ICommand _removeMatching;
    private ICommand _autoTagNsfw;
    private ICommand _reloadHashes;
    private ICommand _sortAlbum;
    private ICommand _clearAlbums;
    private ICommand _refresh;
    private ICommand _quickCopy;
    private ICommand _toggleBackgroundPauseCommand;
    private ICommand _stopBackgroundProcessingCommand;
    private ICommand _releaseModelsCommand;
    private ICommand _startTaggingCommand;
    private ICommand _pauseTaggingCommand;
    private ICommand _stopTaggingCommand;
    private ICommand _clearTaggingQueueCommand;
    private ICommand _startCaptioningCommand;
    private ICommand _pauseCaptioningCommand;
    private ICommand _stopCaptioningCommand;
    private ICommand _clearCaptioningQueueCommand;
    private ICommand _startEmbeddingCommand;
    private ICommand _pauseEmbeddingCommand;
    private ICommand _stopEmbeddingCommand;
    private ICommand _clearEmbeddingQueueCommand;
    private ICommand _startFaceDetectionCommand;
    private ICommand _pauseFaceDetectionCommand;
    private ICommand _stopFaceDetectionCommand;
    private ICommand _clearFaceDetectionQueueCommand;
    private int _thumbnailSize;
    private ICommand _escape;
    private ICommand _downloadCivitai;
    private ICommand _enrichModelMetadata;
    private ICommand _writeMetadataToFiles;
    private ICommand _addAlbumCommand;
    private ICommand _addToAlbumCommand;
    private ICommand _removeFromAlbumCommand;
    private ICommand _renameAlbumCommand;
    private ICommand _removeAlbumCommand;
    private ObservableCollection<AlbumModel> _albums;
    private ObservableCollection<ImageEntry>? _selectedImages;
    private AlbumListItem? _selectedAlbum;
    private AlbumModel? _currentAlbum;
    private ICommand _fixFoldersCommand;
    private ICommand _removeExcludedImagesCommand;
    private ICommand _cleanRemovedFoldersCommand;
    private ICommand _showFilterCommand;
    private ICommand _toggleAutoRefresh;
    private bool _autoRefresh;
    private IEnumerable<ModelViewModel>? _imageModels;
    private IEnumerable<string> _imageModelNames;
    private ObservableCollection<FolderViewModel> _folders;


    private FolderViewModel? _currentFolder;
    private ICommand _resetLayout;
    private ICommand _unavailableFilesCommand;
    private bool _hideUnavailable;
    private ICommand _toggleHideUnavailable;
    private bool _autoAdvance;
    private ICommand _toggleAutoAdvance;
    private bool _hasSelectedAlbums;
    private ICommand _clearModelsCommand;
    private bool _hasSelectedModels;
    private int _selectedAlbumsCount;
    private int _selectedModelsCount;
    private string _toastMessage;
    private ICommand _showSettingsCommand;
    private ObservableCollection<QueryModel> _queries;
    private QueryModel? _selectedQuery;
    private ICommand _renameQueryCommand;
    private ICommand _removeQueryCommand;
    
    // Model Library
    private ObservableCollection<ModelLibraryFolderViewModel>? _modelLibraryFolders;
    private ModelLibraryFolderViewModel? _selectedModelLibraryFolder;
    private ICommand? _refreshModelLibraryCommand;
    private ICommand? _scanModelsCommand;

    public MainModel()
    {
        _status = "Ready";
        _isPreviewVisible = true;
        _selectedImages = new ObservableCollection<ImageEntry>();
        _queryOptions = new QueryOptions();
        _folders = new ObservableCollection<FolderViewModel>();
        _databaseStatus = "Connecting...";
        _isDatabaseConnected = false;
        _page = null!;
        _rescan = null!;
        _closeCommand = null!;
        _settingsCommand = null!;
        _rebuild = null!;
        _removeMarked = null!;
        _cancelCommand = null!;
        _aboutCommand = null!;
        _helpCommand = null!;
        _toggleInfoCommand = null!;
        _toggleNsfwBlurCommand = null!;
        _toggleHideNsfw = null!;
        _toggleHideDeleted = null!;
        _toggleFitToPreview = null!;
        _toggleActualSize = null!;
        _setThumbnailSize = null!;
        _poputPreview = null!;
        _togglePreview = null!;
        _addAllToAlbum = null!;
        _markAllForDeletion = null!;
        _unmarkAllForDeletion = null!;
        _removeMatching = null!;
        _autoTagNsfw = null!;
        _reloadHashes = null!;
        _sortAlbum = null!;
        _clearAlbums = null!;
        _refresh = null!;
        _quickCopy = null!;
        _toggleBackgroundPauseCommand = null!;
        _stopBackgroundProcessingCommand = null!;
        _releaseModelsCommand = null!;
        _startTaggingCommand = null!;
        _pauseTaggingCommand = null!;
        _stopTaggingCommand = null!;
        _clearTaggingQueueCommand = null!;
        _startCaptioningCommand = null!;
        _pauseCaptioningCommand = null!;
        _stopCaptioningCommand = null!;
        _clearCaptioningQueueCommand = null!;
        _startEmbeddingCommand = null!;
        _pauseEmbeddingCommand = null!;
        _stopEmbeddingCommand = null!;
        _clearEmbeddingQueueCommand = null!;
        _startFaceDetectionCommand = null!;
        _pauseFaceDetectionCommand = null!;
        _stopFaceDetectionCommand = null!;
        _clearFaceDetectionQueueCommand = null!;
        _escape = null!;
        _downloadCivitai = null!;
        _enrichModelMetadata = null!;
        _writeMetadataToFiles = null!;
        _addAlbumCommand = null!;
        _addToAlbumCommand = null!;
        _removeFromAlbumCommand = null!;
        _renameAlbumCommand = null!;
        _removeAlbumCommand = null!;
        _albums = new ObservableCollection<AlbumModel>();
        _fixFoldersCommand = null!;
        _removeExcludedImagesCommand = null!;
        _cleanRemovedFoldersCommand = null!;
        _showFilterCommand = null!;
        _toggleAutoRefresh = null!;
        _imageModelNames = Array.Empty<string>();
        _resetLayout = null!;
        _unavailableFilesCommand = null!;
        _toggleHideUnavailable = null!;
        _toggleAutoAdvance = null!;
        _clearModelsCommand = null!;
        _toastMessage = string.Empty;
        _queries = new ObservableCollection<QueryModel>();
        _renameQueryCommand = null!;
        _removeQueryCommand = null!;
        _startUnifiedProcessingCommand = null!;
        _pauseUnifiedProcessingCommand = null!;
        _stopUnifiedProcessingCommand = null!;
        _clearAllQueuesCommand = null!;
        RenameFileCommand = null!;
        ReleaseNotesCommand = null!;
        ToggleVisibilityCommand = null!;
        _toggleTags = null!;
        SaveQuery = null!;
        RescanResults = null!;
        _activeView = string.Empty;
        _settings = null!;
        _reloadFoldersCommand = null!;
        _openWithMenuItems = new ObservableCollection<Control>();
        _openWithCommand = null!;
        _gotoUrl = null!;
        _albumMenuItems = new ObservableCollection<Control>();
        _selectionAlbumMenuItems = new ObservableCollection<Control>();
        _toggleFilenamesCommand = null!;
        _currentImageEntry = null!;
        _selectedImageEntry = null!;
        _queryText = string.Empty;
        CreateAlbumCommand = null!;
        AddSelectedImagesToAlbum = _ => { };
        MoveSelectedImagesToFolder = _ => { };
        ScanFolderCommand = null!;
        RescanFolderCommand = null!;
        CreateFolderCommand = null!;
        RenameFolderCommand = null!;
        RemoveFolderCommand = null!;
        DeleteFolderCommand = null!;
        ArchiveFolderCommand = null!;
        ArchiveFolderRecursiveCommand = null!;
        ExcludeFolderCommand = null!;
        ExcludeFolderRecursiveCommand = null!;
        ToggleNavigationPane = null!;
        ShowInExplorerCommand = null!;
        TagFolderWithJoyTagCommand = null!;
        TagFolderWithWDTagCommand = null!;
        TagFolderWithBothCommand = null!;
        CaptionFolderCommand = null!;
        ImportSidecarsCommand = null!;
        ExportMetadataCommand = null!;
        ConvertImagesCommand = null!;
        CurrentQuery = null!;
        ToggleNotificationsCommand = null!;
        NavigateToParentFolderCommand = null!;
        CurrentImage = null!;
        ToggleThumbnailViewModeCommand = null!;
        FocusSearch = null!;
        RefreshFolderCommand = null!;
    }

    // Database connection status
    private string _databaseStatus;
    private bool _isDatabaseConnected;

    public string DatabaseStatus
    {
        get => _databaseStatus;
        set => SetField(ref _databaseStatus, value);
    }

    public bool IsDatabaseConnected
    {
        get => _isDatabaseConnected;
        set => SetField(ref _isDatabaseConnected, value);
    }

    // Background tagging/captioning status
    private bool _isBackgroundProcessingActive;
    private bool _isTaggingActive;
    private bool _isCaptioningActive;
    private bool _isEmbeddingActive;
    private bool _isFaceDetectionActive;
    private string _taggingStatus = "";
    private string _captioningStatus = "";
    private string _embeddingStatus = "";
    private string _faceDetectionStatus = "";
    private int _taggingQueueCount;
    private int _captioningQueueCount;
    private int _embeddingQueueCount;
    private int _faceDetectionQueueCount;
    
    // Unified processing toggles
    private bool _enableTagging = true;
    private bool _enableCaptioning = true;
    private bool _enableEmbedding = true;
    private bool _enableFaceDetection = true;
    private bool _isUnifiedProcessingActive;
    private ICommand _startUnifiedProcessingCommand;
    private ICommand _pauseUnifiedProcessingCommand;
    private ICommand _stopUnifiedProcessingCommand;
    private ICommand _clearAllQueuesCommand;
    private string _estimatedVramUsage = "";
    private string _unifiedProcessingStatus = "";

    public bool IsBackgroundProcessingActive
    {
        get => _isBackgroundProcessingActive;
        set => SetField(ref _isBackgroundProcessingActive, value);
    }

    public bool IsTaggingActive
    {
        get => _isTaggingActive;
        set
        {
            SetField(ref _isTaggingActive, value);
            IsBackgroundProcessingActive = _isTaggingActive || _isCaptioningActive || _isEmbeddingActive;
        }
    }

    public bool IsCaptioningActive
    {
        get => _isCaptioningActive;
        set
        {
            SetField(ref _isCaptioningActive, value);
            IsBackgroundProcessingActive = _isTaggingActive || _isCaptioningActive || _isEmbeddingActive;
        }
    }

    public bool IsEmbeddingActive
    {
        get => _isEmbeddingActive;
        set
        {
            SetField(ref _isEmbeddingActive, value);
            IsBackgroundProcessingActive = _isTaggingActive || _isCaptioningActive || _isEmbeddingActive || _isFaceDetectionActive;
        }
    }

    public bool IsFaceDetectionActive
    {
        get => _isFaceDetectionActive;
        set
        {
            SetField(ref _isFaceDetectionActive, value);
            IsBackgroundProcessingActive = _isTaggingActive || _isCaptioningActive || _isEmbeddingActive || _isFaceDetectionActive;
        }
    }

    public string TaggingStatus
    {
        get => _taggingStatus;
        set => SetField(ref _taggingStatus, value);
    }

    public string CaptioningStatus
    {
        get => _captioningStatus;
        set => SetField(ref _captioningStatus, value);
    }

    public string EmbeddingStatus
    {
        get => _embeddingStatus;
        set => SetField(ref _embeddingStatus, value);
    }

    public string FaceDetectionStatus
    {
        get => _faceDetectionStatus;
        set => SetField(ref _faceDetectionStatus, value);
    }

    public int TaggingQueueCount
    {
        get => _taggingQueueCount;
        set => SetField(ref _taggingQueueCount, value);
    }

    public int CaptioningQueueCount
    {
        get => _captioningQueueCount;
        set => SetField(ref _captioningQueueCount, value);
    }

    public int EmbeddingQueueCount
    {
        get => _embeddingQueueCount;
        set => SetField(ref _embeddingQueueCount, value);
    }

    public int FaceDetectionQueueCount
    {
        get => _faceDetectionQueueCount;
        set => SetField(ref _faceDetectionQueueCount, value);
    }

    // Unified Processing Properties
    public bool EnableTagging
    {
        get => _enableTagging;
        set
        {
            if (SetField(ref _enableTagging, value))
                UpdateEstimatedVramUsage();
        }
    }

    public bool EnableCaptioning
    {
        get => _enableCaptioning;
        set
        {
            if (SetField(ref _enableCaptioning, value))
                UpdateEstimatedVramUsage();
        }
    }

    public bool EnableEmbedding
    {
        get => _enableEmbedding;
        set
        {
            if (SetField(ref _enableEmbedding, value))
                UpdateEstimatedVramUsage();
        }
    }

    public bool EnableFaceDetection
    {
        get => _enableFaceDetection;
        set
        {
            if (SetField(ref _enableFaceDetection, value))
                UpdateEstimatedVramUsage();
        }
    }

    public bool IsUnifiedProcessingActive
    {
        get => _isUnifiedProcessingActive;
        set => SetField(ref _isUnifiedProcessingActive, value);
    }

    public string EstimatedVramUsage
    {
        get => _estimatedVramUsage;
        set => SetField(ref _estimatedVramUsage, value);
    }

    public string UnifiedProcessingStatus
    {
        get => _unifiedProcessingStatus;
        set => SetField(ref _unifiedProcessingStatus, value);
    }

    public ICommand StartUnifiedProcessingCommand
    {
        get => _startUnifiedProcessingCommand;
        set => SetField(ref _startUnifiedProcessingCommand, value);
    }

    public ICommand PauseUnifiedProcessingCommand
    {
        get => _pauseUnifiedProcessingCommand;
        set => SetField(ref _pauseUnifiedProcessingCommand, value);
    }

    public ICommand StopUnifiedProcessingCommand
    {
        get => _stopUnifiedProcessingCommand;
        set => SetField(ref _stopUnifiedProcessingCommand, value);
    }

    public ICommand ClearAllQueuesCommand
    {
        get => _clearAllQueuesCommand;
        set => SetField(ref _clearAllQueuesCommand, value);
    }

    public void UpdateEstimatedVramUsage()
    {
        // Calculate estimated VRAM based on enabled processes (MEASURED runtime values)
        double totalGb = 0;
        if (_enableTagging) totalGb += 3.1;       // Measured: 3.1GB
        if (_enableCaptioning) totalGb += 5.8;    // Measured: 5.8GB
        if (_enableEmbedding) totalGb += 7.5;     // Measured: 7.5GB
        if (_enableFaceDetection) totalGb += 0.8; // Measured: 0.8GB
        
        EstimatedVramUsage = $"~{totalGb:F1}GB VRAM";
    }

    public QueryOptions QueryOptions
    {
        get => _queryOptions;
        set => SetField(ref _queryOptions, value);
    }

    public string QueryText
    {
        get => _queryText;
        set => SetField(ref _queryText, value);
    }

    public bool HasQuery
    {
        get => _hasQuery;
        set => SetField(ref _hasQuery, value);
    }

    public bool HasFilter
    {
        get => _hasFilter;
        set => SetField(ref _hasFilter, value);
    }

    public Page Page
    {
        get => _page;
        set => SetField(ref _page, value);
    }

    public ICommand Rescan
    {
        get => _rescan;
        set => SetField(ref _rescan, value);
    }

    public ICommand RenameFileCommand { get; set; }

    public ICommand OpenWithCommand
    {
        get => _openWithCommand;
        set => SetField(ref _openWithCommand, value);
    }

    public ICommand SettingsCommand
    {
        get => _settingsCommand;
        set => SetField(ref _settingsCommand, value);
    }

    public ICommand CloseCommand
    {
        get => _closeCommand;
        set => SetField(ref _closeCommand, value);
    }

    public ICommand Rebuild
    {
        get => _rebuild;
        set => SetField(ref _rebuild, value);
    }

    public ICommand ReloadHashes
    {
        get => _reloadHashes;
        set => SetField(ref _reloadHashes, value);
    }

    public bool ShowIcons
    {
        get => _showIcons;
        set => SetField(ref _showIcons, value);
    }

    public bool HideIcons
    {
        get => _hideIcons;
        set => SetField(ref _hideIcons, value);
    }

    public ICommand GotoUrl
    {
        get => _gotoUrl;
        set => SetField(ref _gotoUrl, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetField(ref _isBusy, value);
            OnPropertyChanged(nameof(HasPendingTask));
        }
    }

    public bool HasQueued
    {
        get => _hasQueued;
        set
        {
            SetField(ref _hasQueued, value);
            OnPropertyChanged(nameof(HasPendingTask));
        }
    }

    public bool HasPendingTask => IsBusy || HasQueued;

    public int CurrentProgress
    {
        get => _currentProgress;
        set => SetField(ref _currentProgress, value);
    }

    public int TotalProgress
    {
        get => _totalProgress;
        set => SetField(ref _totalProgress, value);
    }
    
    public ICommand CancelCommand
    {
        get => _cancelCommand;
        set => SetField(ref _cancelCommand, value);
    }

    public ICommand ToggleBackgroundPauseCommand
    {
        get => _toggleBackgroundPauseCommand;
        set => SetField(ref _toggleBackgroundPauseCommand, value);
    }

    public ICommand StopBackgroundProcessingCommand
    {
        get => _stopBackgroundProcessingCommand;
        set => SetField(ref _stopBackgroundProcessingCommand, value);
    }

    public ICommand ReleaseModelsCommand
    {
        get => _releaseModelsCommand;
        set => SetField(ref _releaseModelsCommand, value);
    }

    public ICommand StartTaggingCommand
    {
        get => _startTaggingCommand;
        set => SetField(ref _startTaggingCommand, value);
    }

    public ICommand PauseTaggingCommand
    {
        get => _pauseTaggingCommand;
        set => SetField(ref _pauseTaggingCommand, value);
    }

    public ICommand StopTaggingCommand
    {
        get => _stopTaggingCommand;
        set => SetField(ref _stopTaggingCommand, value);
    }

    public ICommand ClearTaggingQueueCommand
    {
        get => _clearTaggingQueueCommand;
        set => SetField(ref _clearTaggingQueueCommand, value);
    }

    public ICommand StartCaptioningCommand
    {
        get => _startCaptioningCommand;
        set => SetField(ref _startCaptioningCommand, value);
    }

    public ICommand PauseCaptioningCommand
    {
        get => _pauseCaptioningCommand;
        set => SetField(ref _pauseCaptioningCommand, value);
    }

    public ICommand StopCaptioningCommand
    {
        get => _stopCaptioningCommand;
        set => SetField(ref _stopCaptioningCommand, value);
    }

    public ICommand ClearCaptioningQueueCommand
    {
        get => _clearCaptioningQueueCommand;
        set => SetField(ref _clearCaptioningQueueCommand, value);
    }

    public ICommand StartEmbeddingCommand
    {
        get => _startEmbeddingCommand;
        set => SetField(ref _startEmbeddingCommand, value);
    }

    public ICommand PauseEmbeddingCommand
    {
        get => _pauseEmbeddingCommand;
        set => SetField(ref _pauseEmbeddingCommand, value);
    }

    public ICommand StopEmbeddingCommand
    {
        get => _stopEmbeddingCommand;
        set => SetField(ref _stopEmbeddingCommand, value);
    }

    public ICommand ClearEmbeddingQueueCommand
    {
        get => _clearEmbeddingQueueCommand;
        set => SetField(ref _clearEmbeddingQueueCommand, value);
    }

    public ICommand StartFaceDetectionCommand
    {
        get => _startFaceDetectionCommand;
        set => SetField(ref _startFaceDetectionCommand, value);
    }

    public ICommand PauseFaceDetectionCommand
    {
        get => _pauseFaceDetectionCommand;
        set => SetField(ref _pauseFaceDetectionCommand, value);
    }

    public ICommand StopFaceDetectionCommand
    {
        get => _stopFaceDetectionCommand;
        set => SetField(ref _stopFaceDetectionCommand, value);
    }

    public ICommand ClearFaceDetectionQueueCommand
    {
        get => _clearFaceDetectionQueueCommand;
        set => SetField(ref _clearFaceDetectionQueueCommand, value);
    }

    public ICommand AboutCommand
    {
        get => _aboutCommand;
        set => SetField(ref _aboutCommand, value);
    }

    public ICommand ReleaseNotesCommand { get; set; }

    public ICommand HelpCommand
    {
        get => _helpCommand;
        set => SetField(ref _helpCommand, value);
    }

    public ICommand ToggleInfoCommand
    {
        get => _toggleInfoCommand;
        set => SetField(ref _toggleInfoCommand, value);
    }

    public ICommand ToggleNSFWBlurCommand
    {
        get => _toggleNsfwBlurCommand;
        set => SetField(ref _toggleNsfwBlurCommand, value);
    }

    public ICommand ToggleVisibilityCommand
    {
        get;
        set;
    }

    private ICommand _toggleTags;

    public ICommand ToggleTagsCommand
    {
        get => _toggleTags;
        set => SetField(ref _toggleTags, value);
    }

    private bool _showTags;

    public bool ShowTags
    {
        get => _showTags;
        set => SetField(ref _showTags, value);
    }

    public bool AutoAdvance
    {
        get => _autoAdvance;
        set => SetField(ref _autoAdvance, value);
    }

    public ICommand ToggleAutoAdvance
    {
        get => _toggleAutoAdvance;
        set => SetField(ref _toggleAutoAdvance, value);
    }

    public ICommand ToggleHideNSFW
    {
        get => _toggleHideNsfw;
        set => SetField(ref _toggleHideNsfw, value);
    }

    public ICommand ToggleHideDeleted
    {
        get => _toggleHideDeleted;
        set => SetField(ref _toggleHideDeleted, value);
    }

    public ICommand ToggleHideUnavailable
    {
        get => _toggleHideUnavailable;
        set => SetField(ref _toggleHideUnavailable, value);
    }

    public ICommand ToggleFilenamesCommand
    {
        get => _toggleFilenamesCommand;
        set => SetField(ref _toggleFilenamesCommand, value);
    }

    public bool NSFWBlur
    {
        get => _nsfwBlur;
        set => SetField(ref _nsfwBlur, value);
    }

    public bool HideNSFW
    {
        get => _hideNsfw;
        set => SetField(ref _hideNsfw, value);
    }

    public bool HideDeleted
    {
        get => _hideDeleted;
        set => SetField(ref _hideDeleted, value);
    }

    public bool HideUnavailable
    {
        get => _hideUnavailable;
        set => SetField(ref _hideUnavailable, value);
    }

    public bool FitToPreview
    {
        get => _fitToPreview;
        set => SetField(ref _fitToPreview, value);
    }
    
    public ICommand ToggleFitToPreview
    {
        get => _toggleFitToPreview;
        set => SetField(ref _toggleFitToPreview, value);
    }

    public bool ActualSize
    {
        get => _actualSize;
        set => SetField(ref _actualSize, value);
    }

    public ICommand ToggleActualSize
    {
        get => _toggleActualSize;
        set => SetField(ref _toggleActualSize, value);
    }

    public ICommand SetThumbnailSize
    {
        get => _setThumbnailSize;
        set => SetField(ref _setThumbnailSize, value);
    }

    public ICommand PoputPreview
    {
        get => _poputPreview;
        set => SetField(ref _poputPreview, value);
    }

    public ICommand ResetLayout
    {
        get => _resetLayout;
        set => SetField(ref _resetLayout, value);
    }

    public ICommand TogglePreview
    {
        get => _togglePreview;
        set => SetField(ref _togglePreview, value);
    }

    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set => SetField(ref _isPreviewVisible, value);
    }

    public ICommand SaveQuery
    {
        get;
        set;
    }

    public ICommand RescanResults
    {
        get;
        set;
    }

    public ICommand AddAllToAlbum
    {
        get => _addAllToAlbum;
        set => SetField(ref _addAllToAlbum, value);
    }

    public ICommand MarkAllForDeletion
    {
        get => _markAllForDeletion;
        set => SetField(ref _markAllForDeletion, value);
    }

    public ICommand UnmarkAllForDeletion
    {
        get => _unmarkAllForDeletion;
        set => SetField(ref _unmarkAllForDeletion, value);
    }

    public ICommand RemoveMatching
    {
        get => _removeMatching;
        set => SetField(ref _removeMatching, value);
    }

    public ICommand AutoTagNSFW
    {
        get => _autoTagNsfw;
        set => SetField(ref _autoTagNsfw, value);
    }

    public ICommand ClearModelsCommand
    {
        get => _clearModelsCommand;
        set => SetField(ref _clearModelsCommand, value);
    }

    public bool HasSelectedModels
    {
        get => _hasSelectedModels;
        set => SetField(ref _hasSelectedModels, value);
    }

    public ICommand ClearAlbumsCommand
    {
        get => _clearAlbums;
        set => SetField(ref _clearAlbums, value);
    }

    public bool HasSelectedAlbums
    {
        get => _hasSelectedAlbums;
        set => SetField(ref _hasSelectedAlbums, value);
    }

    public ICommand SortAlbumCommand
    {
        get => _sortAlbum;
        set => SetField(ref _sortAlbum, value);
    }

    public ICommand Refresh
    {
        get => _refresh;
        set => SetField(ref _refresh, value);
    }

    public ICommand QuickCopy
    {
        get => _quickCopy;
        set => SetField(ref _quickCopy, value);
    }

    public int ThumbnailSize
    {
        get => _thumbnailSize;
        set => SetField(ref _thumbnailSize, value);
    }

    public ICommand Escape
    {
        get => _escape;
        set => SetField(ref _escape, value);
    }

    public ICommand DownloadCivitai
    {
        get => _downloadCivitai;
        set => SetField(ref _downloadCivitai, value);
    }

    public ICommand EnrichModelMetadata
    {
        get => _enrichModelMetadata;
        set => SetField(ref _enrichModelMetadata, value);
    }

    public ICommand WriteMetadataToFiles
    {
        get => _writeMetadataToFiles;
        set => SetField(ref _writeMetadataToFiles, value);
    }
    
    public ObservableCollection<AlbumModel> Albums
    {
        get => _albums;
        set => SetField(ref _albums, value);
    }


    public ObservableCollection<QueryModel> Queries
    {
        get => _queries;
        set => SetField(ref _queries, value);
    }

    public QueryModel? SelectedQuery
    {
        get => _selectedQuery;
        set => SetField(ref _selectedQuery, value);
    }

    public int SelectedAlbumsCount
    {
        get => _selectedAlbumsCount;
        set => SetField(ref _selectedAlbumsCount, value);
    }



    public int SelectedModelsCount
    {
        get => _selectedModelsCount;
        set => SetField(ref _selectedModelsCount, value);
    }

    public ICommand AddAlbumCommand
    {
        get => _addAlbumCommand;
        set => SetField(ref _addAlbumCommand, value);
    }

    public ICommand AddToAlbumCommand
    {
        get => _addToAlbumCommand;
        set => SetField(ref _addToAlbumCommand, value);
    }


    public ICommand RemoveFromAlbumCommand
    {
        get => _removeFromAlbumCommand;
        set => SetField(ref _removeFromAlbumCommand, value);
    }

    public ICommand RenameAlbumCommand
    {
        get => _renameAlbumCommand;
        set => SetField(ref _renameAlbumCommand, value);
    }

    public ICommand RemoveAlbumCommand
    {
        get => _removeAlbumCommand;
        set => SetField(ref _removeAlbumCommand, value);
    }


    public ICommand RenameQueryCommand
    {
        get => _renameQueryCommand;
        set => SetField(ref _renameQueryCommand, value);
    }

    public ICommand RemoveQueryCommand
    {
        get => _removeQueryCommand;
        set => SetField(ref _removeQueryCommand, value);
    }


    public ObservableCollection<ImageEntry>? SelectedImages
    {
        get => _selectedImages;
        set => SetField(ref _selectedImages, value);
    }

    public AlbumListItem? SelectedAlbum
    {
        get => _selectedAlbum;
        set => SetField(ref _selectedAlbum, value);
    }

    public AlbumModel? CurrentAlbum
    {
        get => _currentAlbum;
        set => SetField(ref _currentAlbum, value);
    }

    private ModelViewModel? _currentModel;
    private string _activeView;
    private Settings _settings;
    private ICommand _reloadFoldersCommand;
    private int _progressTarget;
    private bool _isSettingsDirty;
    private ObservableCollection<Control> _openWithMenuItems;
    private ICommand _openWithCommand;
    private ICommand _gotoUrl;
    private bool _foldersBusy;
    private bool _showNotifications;
    private bool _permanentlyDelete;
    private ObservableCollection<Control> _albumMenuItems;
    private ObservableCollection<Control> _selectionAlbumMenuItems;
    private bool _showFilenames;
    private ICommand _toggleFilenamesCommand;
    private ImageEntry _currentImageEntry;
    private ImageEntry _selectedImageEntry;
    private ThumbnailViewMode _thumbnailViewMode;
    private QueryOptions _queryOptions;
    private bool _hasQuery;
    private bool _hasFilter;
    private string _queryText;
    private bool _hasQueued;

    public ModelViewModel? CurrentModel
    {
        get => _currentModel;
        set => SetField(ref _currentModel, value);
    }

    public ICommand CreateAlbumCommand { get; set; }
    public Action<IAlbumInfo> AddSelectedImagesToAlbum { get; set; }


    public ICommand FixFoldersCommand
    {
        get => _fixFoldersCommand;
        set => SetField(ref _fixFoldersCommand, value);
    }

    public ICommand RemoveExcludedImagesCommand
    {
        get => _removeExcludedImagesCommand;
        set => SetField(ref _removeExcludedImagesCommand, value);
    }

    public ICommand CleanRemovedFoldersCommand
    {
        get => _cleanRemovedFoldersCommand;
        set => SetField(ref _cleanRemovedFoldersCommand, value);
    }

    public ICommand UnavailableFilesCommand
    {
        get => _unavailableFilesCommand;
        set => SetField(ref _unavailableFilesCommand, value);
    }


    public ICommand ShowFilterCommand
    {
        get => _showFilterCommand;
        set => SetField(ref _showFilterCommand, value);
    }

    public ICommand ToggleAutoRefresh
    {
        get => _toggleAutoRefresh;
        set => SetField(ref _toggleAutoRefresh, value);
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set => SetField(ref _autoRefresh, value);
    }


    public ObservableCollection<FolderViewModel> Folders
    {
        get => _folders;
        set => SetField(ref _folders, value);
    }

    public IEnumerable<ModelViewModel>? ImageModels
    {
        get => _imageModels;
        set => SetField(ref _imageModels, value);
    }

    public IEnumerable<string> ImageModelNames
    {
        get => _imageModelNames;
        set => SetField(ref _imageModelNames, value);
    }

    public string ActiveView
    {
        get => _activeView;
        set => SetField(ref _activeView, value);
    }

    public Settings Settings
    {
        get => _settings;
        set => SetField(ref _settings, value);
    }

    public Action<FolderViewModel> MoveSelectedImagesToFolder { get; set; }


    public FolderViewModel? CurrentFolder
    {
        get => _currentFolder;
        set => SetField(ref _currentFolder, value);
    }

    public ICommand ReloadFoldersCommand
    {
        get => _reloadFoldersCommand;
        set => SetField(ref _reloadFoldersCommand, value);
    }

    public ICommand ScanFolderCommand { get; set; }
    public ICommand RescanFolderCommand { get; set; }
    public ICommand CreateFolderCommand { get; set; }
    public ICommand RenameFolderCommand { get; set; }
    public ICommand RemoveFolderCommand { get; set; }
    public ICommand DeleteFolderCommand { get; set; }
    public ICommand ArchiveFolderCommand { get; set; }
    public ICommand ArchiveFolderRecursiveCommand { get; set; }
    public ICommand ExcludeFolderCommand { get; set; }
    public ICommand ExcludeFolderRecursiveCommand { get; set; }



    public ICommand ToggleNavigationPane { get; set; }
    public ICommand ShowInExplorerCommand { get; set; }
    public ICommand TagFolderWithJoyTagCommand { get; set; }
    public ICommand TagFolderWithWDTagCommand { get; set; }
    public ICommand TagFolderWithBothCommand { get; set; }
    public ICommand CaptionFolderCommand { get; set; }
    public ICommand ImportSidecarsCommand { get; set; }
    public ICommand ExportMetadataCommand { get; set; }
    public ICommand ConvertImagesCommand { get; set; }
    
    public string ToastMessage
    {
        get => _toastMessage;
        set => SetField(ref _toastMessage, value);
    }

    public QueryModel CurrentQuery { get; set; }

    public bool IsSettingsDirty
    {
        get => _isSettingsDirty;
        set => SetField(ref _isSettingsDirty, value);
    }

    public ObservableCollection<Control> OpenWithMenuItems
    {
        get => _openWithMenuItems;
        set => SetField(ref _openWithMenuItems, value);
    }

    public bool FoldersBusy
    {
        get => _foldersBusy;
        set => SetField(ref _foldersBusy, value);
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => SetField(ref _showNotifications, value);
    }

    public ICommand ToggleNotificationsCommand { get; set; }

    public ICommand NavigateToParentFolderCommand { get; set; }

    public bool PermanentlyDelete
    {
        get => _permanentlyDelete;
        set => SetField(ref _permanentlyDelete, value);
    }

    public ObservableCollection<Control> AlbumMenuItems
    {
        get => _albumMenuItems;
        set => SetField(ref _albumMenuItems, value);
    }

    public ObservableCollection<Control> SelectionAlbumMenuItems
    {
        get => _selectionAlbumMenuItems;
        set => SetField(ref _selectionAlbumMenuItems, value);
    }


    public bool ShowFilenames
    {
        get => _showFilenames;
        set => SetField(ref _showFilenames, value);
    }

    // TODO: Consolidate these
    public ImageEntry SelectedImageEntry
    {
        get => _selectedImageEntry;
        set => SetField(ref _selectedImageEntry, value);
    }

    public ImageEntry CurrentImageEntry
    {
        get => _currentImageEntry;
        set => SetField(ref _currentImageEntry, value);
    }

    public ImageViewModel CurrentImage { get; set; }

    public ThumbnailViewMode ThumbnailViewMode
    {
        get => _thumbnailViewMode;
        set => SetField(ref _thumbnailViewMode, value);
    }

    public ICommand ToggleThumbnailViewModeCommand { get; set;  }
    public ICommand FocusSearch { get; set; }
    public ICommand RefreshFolderCommand { get; set; }

    #region Model Library

    public ObservableCollection<ModelLibraryFolderViewModel>? ModelLibraryFolders
    {
        get => _modelLibraryFolders;
        set => SetField(ref _modelLibraryFolders, value);
    }

    public ModelLibraryFolderViewModel? SelectedModelLibraryFolder
    {
        get => _selectedModelLibraryFolder;
        set => SetField(ref _selectedModelLibraryFolder, value);
    }

    public ICommand? RefreshModelLibraryCommand
    {
        get => _refreshModelLibraryCommand;
        set => SetField(ref _refreshModelLibraryCommand, value);
    }

    public ICommand? ScanModelsCommand
    {
        get => _scanModelsCommand;
        set => SetField(ref _scanModelsCommand, value);
    }

    #endregion
}