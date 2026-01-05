using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using Diffusion.Toolkit.Controls;

namespace Diffusion.Toolkit.Configuration;

public class Settings : SettingsContainer, IScanOptions
{
    public static Settings Instance { get; private set; }

    private List<string> _imagePaths;
    private List<string> _excludePaths;
    private string _modelRootPath;
    private string _fileExtensions;
    private int _pageSize;
    private string? _mainGridWidth;
    private string? _mainGridWidth2;
    private string? _navigationThumbnailGridWidth;
    private string? _navigationThumbnailGridWidth2;
    private string? _previewGridHeight;
    private string? _previewGridHeight2;
    private WindowState? _windowState;
    private Size? _windowSize;
    private bool _dontShowWelcomeOnStartup;
    private string _theme;
    //private bool _watchFolders;
    private bool _hideNsfw;
    private bool _hideDeleted;
    private bool _nsfwBlur;
    private bool _scanForNewImagesOnStartup;
    private bool _checkForUpdatesOnStartup;
    private bool _fitToPreview;
    private int _thumbnailSize;
    private bool _autoTagNSFW;
    private List<string> _nsfwTags;
    private string _hashCache;
    private bool _portableMode;
    //private bool? _recurseFolders;
    private bool? _useBuiltInViewer;
    private bool? _openInFullScreen;
    private bool? _useSystemDefault;
    private bool? _useCustomViewer;
    private string _customCommandLine;
    private string _customCommandLineArgs;
    private string _sortAlbumsBy;
    private string _sortBy;
    private string _sortDirection;
    private bool _autoRefresh;
    private string? _culture;
    private bool _actualSize;
    private int _slideShowDelay;
    private bool _scrollNavigation;
    private bool _autoAdvance;

    private double? _top;
    private double? _left;
    private bool _hideUnavailable;
    private List<string> _includeNodeProperties;
    private bool _searchNodes;
    private bool _searchAllProperties;
    private bool _searchRawData;
    private bool _storeMetadata;
    private bool _storeWorkflow;
    private bool _scanUnavailable;
    private bool _showNotifications;
    private List<ExternalApplication> _externalApplications;
    private string _sortQueriesBy;
    private bool _showTags;
    private bool _permanentlyDelete;
    private bool _confirmDeletion;
    private string _version;
    private bool _showFilenames;
    private int _thumbnailSpacing;
    private ThumbnailViewMode _thumbnailViewMode;
    private RenderMode _renderMode;
    private PreviewWindowState _previewWindowState;
    
    // Tagging configuration
    private float _joyTagThreshold;
    private float _wdTagThreshold;
    private bool _enableJoyTag;
    private bool _enableWDTag;
    private string _joyTagModelPath;
    private string _joyTagTagsPath;
    private string _wdTagModelPath;
    private string _wdTagTagsPath;
    private string _joyCaptionModelPath;
    private string _joyCaptionMMProjPath;
    // Caption provider settings
    private CaptionProviderType _captionProvider;
    private string _externalCaptionBaseUrl;
    private string _externalCaptionModel;
    private string _externalCaptionApiKey;
    private bool _storeTagConfidence;
    private bool _enableWDV3Large;
    private string _wdV3LargeModelPath;
    private string _wdV3LargeTagsPath;
    private float _wdV3LargeThreshold;
    // Tagging/Captioning dialog checkbox states
    private bool _tagDialogAutoTagEnabled = true;  // Merged JoyTag + WDTag
    private bool _tagDialogCaptionEnabled;
    private bool _tagDialogEmbeddingEnabled = false;
    private bool _tagDialogFaceDetectionEnabled = false;
    private string _joyCaptionDefaultPrompt;
    private string _databaseSchema;
    private bool _createMetadataBackup;
    private bool _writeTagsToMetadata;
    private bool _writeCaptionsToMetadata;
    
    // Tagging settings
    private int _taggingConcurrentWorkers = 4;
    private bool _skipAlreadyTaggedImages = false;
    private string _taggingGpuDevices = "0";
    private string _taggingGpuVramRatios = "32";
    
    // Captioning settings
    private bool _skipAlreadyCaptionedImages = false;
    private string _captioningGpuDevices = "0";
    private string _captioningModelsPerDevice = "3";
    private int _captioningModelTTLMinutes = 2;
    
    // Embedding settings
    private int _embeddingConcurrentWorkers = 9;  // Default: 7 on GPU0 + 2 on GPU1
    private string _embeddingGpuDevices = "0,1";
    private string _embeddingGpuVramRatios = "32,12";  // RTX 5090 + RTX 3080 Ti
    private int _embeddingBatchSize = 32;
    private bool _skipAlreadyEmbeddedImages = true;
    
    // Face detection settings
    private int _faceDetectionConcurrentWorkers = 4;
    private string _faceDetectionGpuDevices = "0";
    private float _faceDetectionConfidenceThreshold = 0.5f;
    private float _faceClusterThreshold = 0.6f;  // Similarity threshold for auto-clustering
    private bool _skipAlreadyProcessedFaces = true;
    private bool _storeFaceCrops = true;  // Store face crop images
    private bool _autoClusterFaces = true;  // Automatically cluster similar faces
    
    // Auto-processing on scan
    private bool _autoTagOnScan = false;
    private bool _autoCaptionOnScan = false;
    private bool _autoFaceDetectionOnScan = false;
    
    private bool _writeGenerationParamsToMetadata;
    private CaptionHandlingMode _captionHandlingMode;

    public Settings()
    {
        NSFWTags = new List<string>() { "nsfw", "nude", "naked" };
        DatabaseSchema = "public"; // PostgreSQL default schema
        FileExtensions = ".png, .jpg, .jpeg, .webp, .avif, .gif, .bmp, .tiff, .mp4, .avi, .webm";
        Theme = "System";
        PageSize = 100;
        ThumbnailSize = 128;
        UseBuiltInViewer = true;
        OpenInFullScreen = true;
        CustomCommandLineArgs = "%1";
        Culture = "default";

        SortAlbumsBy = "Name";
        SortBy = "Date Created";
        SortQueriesBy = "Name";
        
        SortDirection = "Z-A";
        MetadataSection = new MetadataSectionSettings();
        MetadataSection.Attach(this);

        MainGridWidth = "5*";
        MainGridWidth2 = "*";
        NavigationThumbnailGridWidth = "*";
        NavigationThumbnailGridWidth2 = "3*";
        PreviewGridHeight = "*";
        PreviewGridHeight2 = "3*";
        SlideShowDelay = 5;

        //WatchFolders = true;
        ScanUnavailable = false;
        ShowNotifications = true;
        FitToPreview = true;
        ExternalApplications = new List<ExternalApplication>();

        SearchNodes = true;
        IncludeNodeProperties = new List<string>
        {
            "text",
            "text_g",
            "text_l",
            "text_positive",
            "text_negative",
        };

        ConfirmDeletion = true;
        
        // Tagging defaults
        JoyTagThreshold = 0.5f;
        WDTagThreshold = 0.5f;
        WDV3LargeThreshold = 0.5f;
        EnableJoyTag = true;
        
        // Metadata writing defaults
        CreateMetadataBackup = true;
        WriteTagsToMetadata = true;
        WriteCaptionsToMetadata = true;
        WriteGenerationParamsToMetadata = true;
        CaptionHandlingMode = CaptionHandlingMode.Overwrite; // Default behavior
        EnableWDTag = true;
        EnableWDV3Large = true;
        StoreTagConfidence = false;
        JoyTagModelPath = @"models\onnx\joytag\jtModel.onnx";
        JoyTagTagsPath = @"models\onnx\joytag\jtTags.csv";
        WDTagModelPath = @"models\onnx\wdv3_large_tag\wdV3LargeModel.onnx";
        WDTagTagsPath = @"models\onnx\wdv3_large_tag\wdV3LargeTags.csv";
        WDV3LargeModelPath = @"models\onnx\wdv3_large_tag\wdV3LargeModel.onnx";
        WDV3LargeTagsPath = @"models\onnx\wdv3_large_tag\wdV3LargeTags.csv";
        JoyCaptionModelPath = @"models\Joycaption\llama-joycaption-beta-one-hf-llava.i1-Q4_K_S.gguf";
        JoyCaptionMMProjPath = @"models\Joycaption\llava.projector";
        JoyCaptionDefaultPrompt = "detailed";
        // Caption provider defaults
        CaptionProvider = CaptionProviderType.LocalJoyCaption; // default to local
        ExternalCaptionBaseUrl = "http://localhost:1234/v1"; // LM Studio default
        ExternalCaptionModel = ""; // user must set
        ExternalCaptionApiKey = ""; // optional depending on provider

        NavigationSection = new NavigationSectionSettings();
        NavigationSection.Attach(this);
        PreviewWindowState = new PreviewWindowState();

        //if (initialize)
        //{
        //    RecurseFolders = true;
        //}

        Instance = this;
    }

    public List<string> IncludeNodeProperties
    {
        get => _includeNodeProperties;
        set => UpdateList(ref _includeNodeProperties, value);
    }


    public string FileExtensions
    {
        get => _fileExtensions;
        set => UpdateValue(ref _fileExtensions, value);
    }

    //public bool? RecurseFolders
    //{
    //    get => _recurseFolders;
    //    set => UpdateValue(ref _recurseFolders, value);
    //}

    public List<string> NSFWTags
    {
        get => _nsfwTags;
        set => UpdateList(ref _nsfwTags, value);
    }

    public string ModelRootPath
    {
        get => _modelRootPath;
        set => UpdateValue(ref _modelRootPath, value);
    }

    public int PageSize
    {
        get => _pageSize;
        set => UpdateValue(ref _pageSize, value);
    }


    #region Window State
    public string? MainGridWidth
    {
        get => _mainGridWidth;
        set => UpdateValue(ref _mainGridWidth, value);
    }

    public string? MainGridWidth2
    {
        get => _mainGridWidth2;
        set => UpdateValue(ref _mainGridWidth2, value);
    }

    public string? NavigationThumbnailGridWidth
    {
        get => _navigationThumbnailGridWidth;
        set => UpdateValue(ref _navigationThumbnailGridWidth, value);
    }

    public string? NavigationThumbnailGridWidth2
    {
        get => _navigationThumbnailGridWidth2;
        set => UpdateValue(ref _navigationThumbnailGridWidth2, value);
    }


    public string? PreviewGridHeight
    {
        get => _previewGridHeight;
        set => UpdateValue(ref _previewGridHeight, value);
    }

    public string? PreviewGridHeight2
    {
        get => _previewGridHeight2;
        set => UpdateValue(ref _previewGridHeight2, value);
    }
    
    public double? Top
    {
        get => _top;
        set => UpdateValue(ref _top, value);
    }

    public double? Left
    {
        get => _left;
        set => UpdateValue(ref _left, value);
    }

    public WindowState? WindowState
    {
        get => _windowState;
        set => UpdateValue(ref _windowState, value);
    }

    public Size? WindowSize
    {
        get => _windowSize;
        set => UpdateValue(ref _windowSize, value);
    }
    #endregion


    public string Theme
    {
        get => _theme;
        set => UpdateValue(ref _theme, value);
    }

    //public bool WatchFolders
    //{
    //    get => _watchFolders;
    //    set => UpdateValue(ref _watchFolders, value);
    //}

    public bool HideNSFW
    {
        get => _hideNsfw;
        set => UpdateValue(ref _hideNsfw, value);
    }

    public bool HideDeleted
    {
        get => _hideDeleted;
        set => UpdateValue(ref _hideDeleted, value);
    }

    public bool NSFWBlur
    {
        get => _nsfwBlur;
        set => UpdateValue(ref _nsfwBlur, value);
    }
  
    public bool CheckForUpdatesOnStartup
    {
        get => _checkForUpdatesOnStartup;
        set => UpdateValue(ref _checkForUpdatesOnStartup, value);
    }

    public bool ScanForNewImagesOnStartup
    {
        get => _scanForNewImagesOnStartup;
        set => UpdateValue(ref _scanForNewImagesOnStartup, value);
    }

    public bool FitToPreview
    {
        get => _fitToPreview;
        set => UpdateValue(ref _fitToPreview, value);
    }

    public int ThumbnailSize
    {
        get => _thumbnailSize;
        set => UpdateValue(ref _thumbnailSize, value);
    }

    public bool AutoTagNSFW
    {
        get => _autoTagNSFW;
        set => UpdateValue(ref _autoTagNSFW, value);
    }

    public string HashCache
    {
        get => _hashCache;
        set => UpdateValue(ref _hashCache, value);
    }

    public bool PortableMode
    {
        get => _portableMode;
        set => UpdateValue(ref _portableMode, value);
    }

    public bool? UseBuiltInViewer
    {
        get => _useBuiltInViewer;
        set => UpdateValue(ref _useBuiltInViewer, value);
    }

    public bool? OpenInFullScreen
    {
        get => _openInFullScreen;
        set => UpdateValue(ref _openInFullScreen, value);
    }

    public bool? UseSystemDefault
    {
        get => _useSystemDefault;
        set => UpdateValue(ref _useSystemDefault, value);
    }

    public bool? UseCustomViewer
    {
        get => _useCustomViewer;
        set => UpdateValue(ref _useCustomViewer, value);
    }

    public string CustomCommandLine
    {
        get => _customCommandLine;
        set => UpdateValue(ref _customCommandLine, value);
    }

    public string CustomCommandLineArgs
    {
        get => _customCommandLineArgs;
        set => UpdateValue(ref _customCommandLineArgs, value);
    }

    public string SortAlbumsBy
    {
        get => _sortAlbumsBy;
        set => UpdateValue(ref _sortAlbumsBy, value);
    }

    public string SortBy
    {
        get => _sortBy;
        set => UpdateValue(ref _sortBy, value);
    }

    public string SortDirection
    {
        get => _sortDirection;
        set => UpdateValue(ref _sortDirection, value);
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set => UpdateValue(ref _autoRefresh, value);
    }

    public string? Culture
    {
        get => _culture;
        set => UpdateValue(ref _culture, value);
    }

    public MetadataSectionSettings MetadataSection { get; set; }

    public NavigationSectionSettings NavigationSection { get; set; }

    public bool ActualSize
    {
        get => _actualSize;
        set => UpdateValue(ref _actualSize, value);
    }

    public int SlideShowDelay
    {
        get => _slideShowDelay;
        set => UpdateValue(ref _slideShowDelay, value);
    }

    public bool ScrollNavigation
    {
        get => _scrollNavigation;
        set => UpdateValue(ref _scrollNavigation, value);
    }

    public bool AutoAdvance
    {
        get => _autoAdvance;
        set => UpdateValue(ref _autoAdvance, value);
    }

    public bool HideUnavailable
    {
        get => _hideUnavailable;
        set => UpdateValue(ref _hideUnavailable, value);
    }

    public bool SearchNodes
    {
        get => _searchNodes;
        set => UpdateValue(ref _searchNodes, value);
    }

    public bool SearchAllProperties
    {
        get => _searchAllProperties;
        set => UpdateValue(ref _searchAllProperties, value);
    }

    public bool SearchRawData
    {
        get => _searchRawData;
        set => UpdateValue(ref _searchRawData, value);
    }

    public bool StoreMetadata
    {
        get => _storeMetadata;
        set => UpdateValue(ref _storeMetadata, value);
    }

    public bool StoreWorkflow
    {
        get => _storeWorkflow;
        set => UpdateValue(ref _storeWorkflow, value);
    }

    public bool ScanUnavailable
    {
        get => _scanUnavailable;
        set => UpdateValue(ref _scanUnavailable, value);
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => UpdateValue(ref _showNotifications, value);
    }

    public List<ExternalApplication> ExternalApplications
    {
        get => _externalApplications;
        set => UpdateValue(ref _externalApplications, value);
    }

    public string SortQueriesBy
    {
        get => _sortQueriesBy;
        set => UpdateValue(ref _sortQueriesBy, value);
    }

    public bool ShowTags
    {
        get => _showTags;
        set => UpdateValue(ref _showTags, value);
    }

    public bool PermanentlyDelete
    {
        get => _permanentlyDelete;
        set => UpdateValue(ref _permanentlyDelete, value);
    }

    public string Version
    {
        get => _version;
        set => UpdateValue(ref _version, value);
    }

    public bool ConfirmDeletion
    {
        get => _confirmDeletion;
        set => UpdateValue(ref _confirmDeletion, value);
    }


    public bool ShowFilenames
    {
        get => _showFilenames;
        set => UpdateValue(ref _showFilenames, value);
    }

    public int ThumbnailSpacing
    {
        get => _thumbnailSpacing;
        set => UpdateValue(ref _thumbnailSpacing, value);
    }

    public ThumbnailViewMode ThumbnailViewMode
    {
        get => _thumbnailViewMode;
        set => UpdateValue(ref _thumbnailViewMode, value);
    }

    public RenderMode RenderMode
    {
        get => _renderMode;
        set => UpdateValue(ref _renderMode, value);
    }

    public PreviewWindowState PreviewWindowState
    {
        get => _previewWindowState;
        set => UpdateValue(ref _previewWindowState, value);
    }

    public float JoyTagThreshold
    {
        get => _joyTagThreshold;
        set => UpdateValue(ref _joyTagThreshold, value);
    }

    public float WDTagThreshold
    {
        get => _wdTagThreshold;
        set => UpdateValue(ref _wdTagThreshold, value);
    }

    public bool EnableJoyTag
    {
        get => _enableJoyTag;
        set => UpdateValue(ref _enableJoyTag, value);
    }

    public bool EnableWDTag
    {
        get => _enableWDTag;
        set => UpdateValue(ref _enableWDTag, value);
    }

    public string JoyTagModelPath
    {
        get => _joyTagModelPath;
        set => UpdateValue(ref _joyTagModelPath, value);
    }

    public string JoyTagTagsPath
    {
        get => _joyTagTagsPath;
        set => UpdateValue(ref _joyTagTagsPath, value);
    }

    public string WDTagModelPath
    {
        get => _wdTagModelPath;
        set => UpdateValue(ref _wdTagModelPath, value);
    }

    public string WDTagTagsPath
    {
        get => _wdTagTagsPath;
        set => UpdateValue(ref _wdTagTagsPath, value);
    }

    public string JoyCaptionModelPath
    {
        get => _joyCaptionModelPath;
        set => UpdateValue(ref _joyCaptionModelPath, value);
    }

    public string JoyCaptionMMProjPath
    {
        get => _joyCaptionMMProjPath;
        set => UpdateValue(ref _joyCaptionMMProjPath, value);
    }

    public CaptionProviderType CaptionProvider
    {
        get => _captionProvider;
        set => UpdateValue(ref _captionProvider, value);
    }

    public string ExternalCaptionBaseUrl
    {
        get => _externalCaptionBaseUrl;
        set => UpdateValue(ref _externalCaptionBaseUrl, value);
    }

    public string ExternalCaptionModel
    {
        get => _externalCaptionModel;
        set => UpdateValue(ref _externalCaptionModel, value);
    }

    public string ExternalCaptionApiKey
    {
        get => _externalCaptionApiKey;
        set => UpdateValue(ref _externalCaptionApiKey, value);
    }

    public bool StoreTagConfidence
    {
        get => _storeTagConfidence;
        set => UpdateValue(ref _storeTagConfidence, value);
    }

    public bool EnableWDV3Large
    {
        get => _enableWDV3Large;
        set => UpdateValue(ref _enableWDV3Large, value);
    }

    public string WDV3LargeModelPath
    {
        get => _wdV3LargeModelPath;
        set => UpdateValue(ref _wdV3LargeModelPath, value);
    }

    public string WDV3LargeTagsPath
    {
        get => _wdV3LargeTagsPath;
        set => UpdateValue(ref _wdV3LargeTagsPath, value);
    }

    public float WDV3LargeThreshold
    {
        get => _wdV3LargeThreshold;
        set => UpdateValue(ref _wdV3LargeThreshold, value);
    }

    // Tagging dialog checkbox states
    public bool TagDialogAutoTagEnabled
    {
        get => _tagDialogAutoTagEnabled;
        set => UpdateValue(ref _tagDialogAutoTagEnabled, value);
    }

    public bool TagDialogCaptionEnabled
    {
        get => _tagDialogCaptionEnabled;
        set => UpdateValue(ref _tagDialogCaptionEnabled, value);
    }

    public int TaggingConcurrentWorkers
    {
        get => _taggingConcurrentWorkers;
        set => UpdateValue(ref _taggingConcurrentWorkers, Math.Max(1, Math.Min(30, value))); // 1-30 workers
    }

    public bool SkipAlreadyTaggedImages
    {
        get => _skipAlreadyTaggedImages;
        set => UpdateValue(ref _skipAlreadyTaggedImages, value);
    }

    public string TaggingGpuDevices
    {
        get => _taggingGpuDevices;
        set => UpdateValue(ref _taggingGpuDevices, value ?? "0");
    }

    public string TaggingGpuVramRatios
    {
        get => _taggingGpuVramRatios;
        set => UpdateValue(ref _taggingGpuVramRatios, value ?? "32");
    }

    public bool SkipAlreadyCaptionedImages
    {
        get => _skipAlreadyCaptionedImages;
        set => UpdateValue(ref _skipAlreadyCaptionedImages, value);
    }

    public string CaptioningGpuDevices
    {
        get => _captioningGpuDevices;
        set => UpdateValue(ref _captioningGpuDevices, value ?? "0");
    }

    public string CaptioningModelsPerDevice
    {
        get => _captioningModelsPerDevice;
        set => UpdateValue(ref _captioningModelsPerDevice, value ?? "3");
    }

    public int CaptioningModelTTLMinutes
    {
        get => _captioningModelTTLMinutes;
        set => UpdateValue(ref _captioningModelTTLMinutes, Math.Max(-1, Math.Min(60, value))); // -1=keep loaded, 0=instant, 1-60 min
    }

    // Embedding settings
    public bool TagDialogEmbeddingEnabled
    {
        get => _tagDialogEmbeddingEnabled;
        set => UpdateValue(ref _tagDialogEmbeddingEnabled, value);
    }

    public int EmbeddingConcurrentWorkers
    {
        get => _embeddingConcurrentWorkers;
        set => UpdateValue(ref _embeddingConcurrentWorkers, Math.Max(1, Math.Min(16, value)));
    }

    public string EmbeddingGpuDevices
    {
        get => _embeddingGpuDevices;
        set => UpdateValue(ref _embeddingGpuDevices, value);
    }

    public string EmbeddingGpuVramRatios
    {
        get => _embeddingGpuVramRatios;
        set => UpdateValue(ref _embeddingGpuVramRatios, value);
    }

    public int EmbeddingBatchSize
    {
        get => _embeddingBatchSize;
        set => UpdateValue(ref _embeddingBatchSize, Math.Max(8, Math.Min(128, value)));
    }

    public bool SkipAlreadyEmbeddedImages
    {
        get => _skipAlreadyEmbeddedImages;
        set => UpdateValue(ref _skipAlreadyEmbeddedImages, value);
    }

    // Face detection settings
    public bool TagDialogFaceDetectionEnabled
    {
        get => _tagDialogFaceDetectionEnabled;
        set => UpdateValue(ref _tagDialogFaceDetectionEnabled, value);
    }

    public int FaceDetectionConcurrentWorkers
    {
        get => _faceDetectionConcurrentWorkers;
        set => UpdateValue(ref _faceDetectionConcurrentWorkers, Math.Max(1, Math.Min(16, value)));
    }

    public string FaceDetectionGpuDevices
    {
        get => _faceDetectionGpuDevices;
        set => UpdateValue(ref _faceDetectionGpuDevices, value);
    }

    public float FaceDetectionConfidenceThreshold
    {
        get => _faceDetectionConfidenceThreshold;
        set => UpdateValue(ref _faceDetectionConfidenceThreshold, Math.Max(0.1f, Math.Min(0.99f, value)));
    }

    public float FaceClusterThreshold
    {
        get => _faceClusterThreshold;
        set => UpdateValue(ref _faceClusterThreshold, Math.Max(0.3f, Math.Min(0.95f, value)));
    }

    public bool SkipAlreadyProcessedFaces
    {
        get => _skipAlreadyProcessedFaces;
        set => UpdateValue(ref _skipAlreadyProcessedFaces, value);
    }

    public bool StoreFaceCrops
    {
        get => _storeFaceCrops;
        set => UpdateValue(ref _storeFaceCrops, value);
    }

    public bool AutoClusterFaces
    {
        get => _autoClusterFaces;
        set => UpdateValue(ref _autoClusterFaces, value);
    }

    public bool AutoTagOnScan
    {
        get => _autoTagOnScan;
        set => UpdateValue(ref _autoTagOnScan, value);
    }

    public bool AutoCaptionOnScan
    {
        get => _autoCaptionOnScan;
        set => UpdateValue(ref _autoCaptionOnScan, value);
    }

    public bool AutoFaceDetectionOnScan
    {
        get => _autoFaceDetectionOnScan;
        set => UpdateValue(ref _autoFaceDetectionOnScan, value);
    }

    public string JoyCaptionDefaultPrompt
    {
        get => _joyCaptionDefaultPrompt;
        set => UpdateValue(ref _joyCaptionDefaultPrompt, value);
    }

    public string DatabaseSchema
    {
        get => _databaseSchema;
        set => UpdateValue(ref _databaseSchema, value);
    }

    private string _databaseConnectionString;
    
    /// <summary>
    /// PostgreSQL connection string. If empty, uses default from AppInfo.
    /// Format: Host=localhost;Port=5436;Database=diffusion_images;Username=user;Password=pass
    /// </summary>
    public string DatabaseConnectionString
    {
        get => _databaseConnectionString;
        set => UpdateValue(ref _databaseConnectionString, value);
    }

    /// <summary>
    /// Create .bak backup before modifying image metadata
    /// </summary>
    public bool CreateMetadataBackup
    {
        get => _createMetadataBackup;
        set => UpdateValue(ref _createMetadataBackup, value);
    }

    /// <summary>
    /// Include tags when writing metadata
    /// </summary>
    public bool WriteTagsToMetadata
    {
        get => _writeTagsToMetadata;
        set => UpdateValue(ref _writeTagsToMetadata, value);
    }

    /// <summary>
    /// Include captions when writing metadata
    /// </summary>
    public bool WriteCaptionsToMetadata
    {
        get => _writeCaptionsToMetadata;
        set => UpdateValue(ref _writeCaptionsToMetadata, value);
    }

    /// <summary>
    /// Include generation parameters (prompt, sampler, etc.) when writing metadata
    /// </summary>
    public bool WriteGenerationParamsToMetadata
    {
        get => _writeGenerationParamsToMetadata;
        set => UpdateValue(ref _writeGenerationParamsToMetadata, value);
    }

    /// <summary>
    /// How to handle existing captions when generating new ones
    /// </summary>
    public CaptionHandlingMode CaptionHandlingMode
    {
        get => _captionHandlingMode;
        set => UpdateValue(ref _captionHandlingMode, value);
    }
}

public class PreviewWindowState
{
    public bool IsSet { get; set; }
    public WindowState State { get; set; }
    public double Top { get; set; }
    public double Left { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsFullScreen { get; set; }
}


