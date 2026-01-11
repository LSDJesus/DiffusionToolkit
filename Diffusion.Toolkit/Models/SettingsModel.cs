using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Diffusion.Common;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Models;

public class SettingsModel : BaseNotify
{
    private string _modelRootPath;
    private ObservableCollection<string> _imagePaths;
    private int _selectedIndex;
    private int _excludedSelectedIndex;
    private string _fileExtensions;
    private int _pageSize;
    private string _theme;
    private string _culture;
    private bool _checkForUpdatesOnStartup;
    private bool _scanForNewImagesOnStartup;
    private bool _autoTagNsfw;
    private string _nsfwTags;
    private string _hashCache;
    private bool _portableMode;
    private ObservableCollection<string> _excludePaths;
    //private bool? _recurseFolders;
    private ICommand _escape;
    private bool _useBuiltInViewer;
    private bool _openInFullScreen;
    private bool _useSystemDefault;
    private bool _useCustomViewer;
    private string _customCommandLine;
    private string _customCommandLineArgs;
    private bool _autoRefresh;
    private IEnumerable<Langauge> _cultures;
    private int _slideShowDelay;
    private bool _scrollNavigation;
    private bool _advanceOnTag;
    private bool _storeMetadata;
    private bool _storeWorkflow;
    private ObservableCollection<ExternalApplicationModel> _externalApplications;
    private bool _scanUnavailable;
    private ExternalApplicationModel? _selectedApplication;
    private IEnumerable<OptionValue> _themeOptions;
    private bool _isFoldersDirty;
    private bool _confirmDeletion;
    private bool _permanentlyDelete;
    private bool _showFilenames;
    private bool _softwareOnly;
    private string _host;
    private int _port;
    private string _database;
    private string _username;
    private string _status;
    private string _databaseSchema;
    
    // JoyTag settings
    private bool _enableJoyTag;
    private float _joyTagThreshold;
    private string _joyTagModelPath;
    private string _joyTagTagsPath;
    
    // WD tagger settings
    private bool _enableWDTag;
    private float _wdTagThreshold;
    private string _wdTagModelPath;
    private string _wdTagTagsPath;
    
    // JoyCaption settings
    private string _joyCaptionModelPath;
    private string _joyCaptionMMProjPath;
    private string _joyCaptionDefaultPrompt;
    
    // Tagging/Captioning output settings
    private bool _storeTagConfidence;
    private bool _autoWriteMetadata;
    private bool _createMetadataBackup;
    private bool _writeTagsToMetadata;
    private bool _writeCaptionsToMetadata;
    private bool _writeGenerationParamsToMetadata;
    
    // Global GPU Settings
    private string _gpuDevices = "0";
    private string _gpuVramCapacity = "32";
    private int _maxVramUsagePercent = 85;
    
    // GPU Model Allocation - Concurrent mode (all processes running together)
    private string _concurrentCaptioningAllocation = "1,0";
    private string _concurrentTaggingAllocation = "1,0";
    private string _concurrentEmbeddingAllocation = "1,0";
    private string _concurrentFaceDetectionAllocation = "1,0";
    
    // GPU Model Allocation - Solo mode (single process running alone)
    private string _soloCaptioningAllocation = "1,0";
    private string _soloTaggingAllocation = "1,0";
    private string _soloEmbeddingAllocation = "1,0";
    private string _soloFaceDetectionAllocation = "1,0";
    
    // VRAM Calculator results
    private string _concurrentVramResult = "";
    private string _soloVramResults = "";
    
    // Processing skip settings
    private bool _skipAlreadyTaggedImages;
    private bool _skipAlreadyCaptionedImages;
    private bool _skipAlreadyEmbeddedImages = true;
    private bool _skipAlreadyProcessedFaces = true;
    
    // Legacy settings (kept for UI binding compatibility during transition)
    private int _taggingConcurrentWorkers = 4;
    private string _taggingGpuDevices = "0";
    private string _taggingGpuVramRatios = "32";
    private string _captioningGpuDevices = "0";
    private string _captioningModelsPerDevice = "3";
    private int _captioningModelTTLMinutes = 2;
    
    // Caption provider & handling
    private CaptionHandlingMode _captionHandlingMode;
    private CaptionProviderType _captionProvider;
    private string _externalCaptionBaseUrl;
    private string _externalCaptionModel;
    private string _externalCaptionApiKey;
    private ObservableCollection<string> _availableModels;
    
    // Database profiles
    private ObservableCollection<Configuration.DatabaseProfile> _databaseProfiles;

    public SettingsModel()
    {
        SelectedIndex = -1;
        ExcludedSelectedIndex = -1;
        _modelRootPath = string.Empty;
        _imagePaths = new ObservableCollection<string>();
        _fileExtensions = string.Empty;
        _theme = string.Empty;
        _culture = string.Empty;
        _nsfwTags = string.Empty;
        _hashCache = string.Empty;
        _excludePaths = new ObservableCollection<string>();
        _escape = null!;
        _customCommandLine = string.Empty;
        _customCommandLineArgs = string.Empty;
        _cultures = new List<Langauge>();
        _externalApplications = new ObservableCollection<ExternalApplicationModel>();
        ExternalApplications = new ObservableCollection<ExternalApplicationModel>();
        _themeOptions = new List<OptionValue>();
        _host = string.Empty;
        _database = string.Empty;
        _username = string.Empty;
        _status = string.Empty;
        _databaseSchema = "public";
        
        // JoyTag defaults
        _joyTagModelPath = string.Empty;
        _joyTagTagsPath = string.Empty;
        
        // WD tagger defaults
        _wdTagModelPath = string.Empty;
        _wdTagTagsPath = string.Empty;
        
        // JoyCaption defaults
        _joyCaptionModelPath = string.Empty;
        _joyCaptionMMProjPath = string.Empty;
        _joyCaptionDefaultPrompt = "detailed";
        
        // Tagging/Captioning output defaults
        _storeTagConfidence = false;
        _autoWriteMetadata = false;
        _createMetadataBackup = true;
        _writeTagsToMetadata = true;
        _writeCaptionsToMetadata = true;
        _writeGenerationParamsToMetadata = true;
        
        // Caption provider defaults
        _captionHandlingMode = CaptionHandlingMode.Overwrite;
        _captionProvider = CaptionProviderType.LocalJoyCaption;
        _externalCaptionBaseUrl = string.Empty;
        _externalCaptionModel = string.Empty;
        _externalCaptionApiKey = string.Empty;
        _availableModels = new ObservableCollection<string>();
        //ExternalApplications.CollectionChanged += ExternalApplicationsOnCollectionChanged;

        PropertyChanged += OnPropertyChanged;
    }

    private void ExternalApplicationsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        int i = 1;

        foreach (var application in ExternalApplications)
        {
            application.Shortcut = i switch
            {
                >= 1 and <= 9 => $"Shift+{i}",
                10 => $"Shift+0",
                _ => ""
            };

            i++;
        }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ServiceLocator.MainModel!.IsSettingsDirty = IsDirty;
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetField(ref _selectedIndex, value, false);
    }

    public int ExcludedSelectedIndex
    {
        get => _excludedSelectedIndex;
        set => SetField(ref _excludedSelectedIndex, value, false);
    }

    public string FileExtensions
    {
        get => _fileExtensions;
        set => SetField(ref _fileExtensions, value);
    }

    public string ModelRootPath
    {
        get => _modelRootPath;
        set => SetField(ref _modelRootPath, value);
    }

    public int PageSize
    {
        get => _pageSize;
        set => SetField(ref _pageSize, value);
    }

    public string Theme
    {
        get => _theme;
        set => SetField(ref _theme, value);
    }

    public string Culture
    {
        get => _culture;
        set => SetField(ref _culture, value);
    }

    public IEnumerable<Langauge> Cultures
    {
        get => _cultures;
        set => SetField(ref _cultures, value);
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set => SetField(ref _autoRefresh, value);
    }


    public bool CheckForUpdatesOnStartup
    {
        get => _checkForUpdatesOnStartup;
        set => SetField(ref _checkForUpdatesOnStartup, value);
    }

    public bool PortableMode
    {
        get => _portableMode;
        set => SetField(ref _portableMode, value);
    }

    public bool ScanForNewImagesOnStartup
    {
        get => _scanForNewImagesOnStartup;
        set => SetField(ref _scanForNewImagesOnStartup, value);
    }

    public bool AutoTagNSFW
    {
        get => _autoTagNsfw;
        set => SetField(ref _autoTagNsfw, value);
    }

    public string NSFWTags
    {
        get => _nsfwTags;
        set => SetField(ref _nsfwTags, value);
    }

    public string HashCache
    {
        get => _hashCache;
        set => SetField(ref _hashCache, value);
    }

    public ICommand Escape
    {
        get => _escape;
        set => SetField(ref _escape, value);
    }

    public bool UseBuiltInViewer
    {
        get => _useBuiltInViewer;
        set => SetField(ref _useBuiltInViewer, value);
    }

    public bool OpenInFullScreen
    {
        get => _openInFullScreen;
        set => SetField(ref _openInFullScreen, value);
    }

    public bool UseSystemDefault
    {
        get => _useSystemDefault;
        set => SetField(ref _useSystemDefault, value);
    }

    public bool UseCustomViewer
    {
        get => _useCustomViewer;
        set => SetField(ref _useCustomViewer, value);
    }

    public string CustomCommandLine
    {
        get => _customCommandLine;
        set => SetField(ref _customCommandLine, value);
    }
    public string CustomCommandLineArgs
    {
        get => _customCommandLineArgs;
        set => SetField(ref _customCommandLineArgs, value);
    }

    public IEnumerable<OptionValue> ThemeOptions
    {
        get => _themeOptions;
        set => _themeOptions = value;
    }

    public int SlideShowDelay
    {
        get => _slideShowDelay;
        set => SetField(ref _slideShowDelay, value);
    }

    public bool ScrollNavigation
    {
        get => _scrollNavigation;
        set => SetField(ref _scrollNavigation, value);
    }

    public bool AdvanceOnTag
    {
        get => _advanceOnTag;
        set => SetField(ref _advanceOnTag, value);
    }

    public bool StoreMetadata
    {
        get => _storeMetadata;
        set => SetField(ref _storeMetadata, value);
    }

    public bool StoreWorkflow
    {
        get => _storeWorkflow;
        set => SetField(ref _storeWorkflow, value);
    }

    public bool ScanUnavailable
    {
        get => _scanUnavailable;
        set => SetField(ref _scanUnavailable, value);
    }

    public bool ShowFilenames
    {
        get => _showFilenames;
        set => SetField(ref _showFilenames, value);
    }

    public bool PermanentlyDelete
    {
        get => _permanentlyDelete;
        set => SetField(ref _permanentlyDelete, value);
    }

    public bool ConfirmDeletion
    {
        get => _confirmDeletion;
        set => SetField(ref _confirmDeletion, value);
    }

    public ObservableCollection<ExternalApplicationModel> ExternalApplications
    {
        get => _externalApplications;
        set
        {
            SetField(ref _externalApplications, value);
            RegisterObservableChanges(_externalApplications);
            _externalApplications.CollectionChanged += ExternalApplicationsOnCollectionChanged;
        }
    }

    public bool SoftwareOnly
    {
        get => _softwareOnly;
        set => SetField(ref _softwareOnly, value);
    }

    public ExternalApplicationModel? SelectedApplication
    {
        get => _selectedApplication;
        set => SetField(ref _selectedApplication, value, false);
    }

    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    public string Database
    {
        get => _database;
        set => SetField(ref _database, value);
    }

    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string DatabaseSchema
    {
        get => _databaseSchema;
        set => SetField(ref _databaseSchema, value);
    }

    // JoyTag properties
    public bool EnableJoyTag
    {
        get => _enableJoyTag;
        set => SetField(ref _enableJoyTag, value);
    }

    public float JoyTagThreshold
    {
        get => _joyTagThreshold;
        set => SetField(ref _joyTagThreshold, value);
    }

    public string JoyTagModelPath
    {
        get => _joyTagModelPath;
        set => SetField(ref _joyTagModelPath, value);
    }

    public string JoyTagTagsPath
    {
        get => _joyTagTagsPath;
        set => SetField(ref _joyTagTagsPath, value);
    }

    // WD tagger properties (aliases for XAML bindings that use WDV3Large naming)
    public bool EnableWDTag
    {
        get => _enableWDTag;
        set => SetField(ref _enableWDTag, value);
    }
    
    // Alias for XAML binding
    public bool EnableWDV3Large
    {
        get => _enableWDTag;
        set => SetField(ref _enableWDTag, value);
    }

    public float WDTagThreshold
    {
        get => _wdTagThreshold;
        set => SetField(ref _wdTagThreshold, value);
    }
    
    // Alias for XAML binding
    public float WDV3LargeThreshold
    {
        get => _wdTagThreshold;
        set => SetField(ref _wdTagThreshold, value);
    }

    public string WDTagModelPath
    {
        get => _wdTagModelPath;
        set => SetField(ref _wdTagModelPath, value);
    }
    
    // Alias for XAML binding
    public string WDV3LargeModelPath
    {
        get => _wdTagModelPath;
        set => SetField(ref _wdTagModelPath, value);
    }

    public string WDTagTagsPath
    {
        get => _wdTagTagsPath;
        set => SetField(ref _wdTagTagsPath, value);
    }
    
    // Alias for XAML binding
    public string WDV3LargeTagsPath
    {
        get => _wdTagTagsPath;
        set => SetField(ref _wdTagTagsPath, value);
    }

    // JoyCaption properties
    public string JoyCaptionModelPath
    {
        get => _joyCaptionModelPath;
        set => SetField(ref _joyCaptionModelPath, value);
    }

    public string JoyCaptionMMProjPath
    {
        get => _joyCaptionMMProjPath;
        set => SetField(ref _joyCaptionMMProjPath, value);
    }

    public string JoyCaptionDefaultPrompt
    {
        get => _joyCaptionDefaultPrompt;
        set => SetField(ref _joyCaptionDefaultPrompt, value);
    }

    // Tagging/Captioning output properties
    public bool StoreTagConfidence
    {
        get => _storeTagConfidence;
        set => SetField(ref _storeTagConfidence, value);
    }

    public bool AutoWriteMetadata
    {
        get => _autoWriteMetadata;
        set => SetField(ref _autoWriteMetadata, value);
    }

    public bool CreateMetadataBackup
    {
        get => _createMetadataBackup;
        set => SetField(ref _createMetadataBackup, value);
    }

    public bool WriteTagsToMetadata
    {
        get => _writeTagsToMetadata;
        set => SetField(ref _writeTagsToMetadata, value);
    }

    public bool WriteCaptionsToMetadata
    {
        get => _writeCaptionsToMetadata;
        set => SetField(ref _writeCaptionsToMetadata, value);
    }

    public bool WriteGenerationParamsToMetadata
    {
        get => _writeGenerationParamsToMetadata;
        set => SetField(ref _writeGenerationParamsToMetadata, value);
    }

    public CaptionHandlingMode CaptionHandlingMode
    {
        get => _captionHandlingMode;
        set => SetField(ref _captionHandlingMode, value);
    }

    public CaptionProviderType CaptionProvider
    {
        get => _captionProvider;
        set => SetField(ref _captionProvider, value);
    }

    public string ExternalCaptionBaseUrl
    {
        get => _externalCaptionBaseUrl;
        set => SetField(ref _externalCaptionBaseUrl, value);
    }

    public string ExternalCaptionModel
    {
        get => _externalCaptionModel;
        set => SetField(ref _externalCaptionModel, value);
    }

    public string ExternalCaptionApiKey
    {
        get => _externalCaptionApiKey;
        set => SetField(ref _externalCaptionApiKey, value);
    }

    // Global GPU Settings
    public string GpuDevices
    {
        get => _gpuDevices;
        set => SetField(ref _gpuDevices, value);
    }

    public string GpuVramCapacity
    {
        get => _gpuVramCapacity;
        set => SetField(ref _gpuVramCapacity, value);
    }

    public int MaxVramUsagePercent
    {
        get => _maxVramUsagePercent;
        set => SetField(ref _maxVramUsagePercent, Math.Max(50, Math.Min(95, value)));
    }

    // GPU Model Allocation - Concurrent mode (all processes running together)
    public string ConcurrentCaptioningAllocation
    {
        get => _concurrentCaptioningAllocation;
        set => SetField(ref _concurrentCaptioningAllocation, value);
    }

    public string ConcurrentTaggingAllocation
    {
        get => _concurrentTaggingAllocation;
        set => SetField(ref _concurrentTaggingAllocation, value);
    }

    public string ConcurrentEmbeddingAllocation
    {
        get => _concurrentEmbeddingAllocation;
        set => SetField(ref _concurrentEmbeddingAllocation, value);
    }

    public string ConcurrentFaceDetectionAllocation
    {
        get => _concurrentFaceDetectionAllocation;
        set => SetField(ref _concurrentFaceDetectionAllocation, value);
    }

    // GPU Model Allocation - Solo mode (single process running alone)
    public string SoloCaptioningAllocation
    {
        get => _soloCaptioningAllocation;
        set => SetField(ref _soloCaptioningAllocation, value);
    }

    public string SoloTaggingAllocation
    {
        get => _soloTaggingAllocation;
        set => SetField(ref _soloTaggingAllocation, value);
    }

    public string SoloEmbeddingAllocation
    {
        get => _soloEmbeddingAllocation;
        set => SetField(ref _soloEmbeddingAllocation, value);
    }

    public string SoloFaceDetectionAllocation
    {
        get => _soloFaceDetectionAllocation;
        set => SetField(ref _soloFaceDetectionAllocation, value);
    }

    // VRAM Calculator results (display only)
    public string ConcurrentVramResult
    {
        get => _concurrentVramResult;
        set => SetField(ref _concurrentVramResult, value);
    }

    public string SoloVramResults
    {
        get => _soloVramResults;
        set => SetField(ref _soloVramResults, value);
    }

    // Processing skip settings
    public bool SkipAlreadyTaggedImages
    {
        get => _skipAlreadyTaggedImages;
        set => SetField(ref _skipAlreadyTaggedImages, value);
    }

    public bool SkipAlreadyCaptionedImages
    {
        get => _skipAlreadyCaptionedImages;
        set => SetField(ref _skipAlreadyCaptionedImages, value);
    }

    public bool SkipAlreadyEmbeddedImages
    {
        get => _skipAlreadyEmbeddedImages;
        set => SetField(ref _skipAlreadyEmbeddedImages, value);
    }

    public bool SkipAlreadyProcessedFaces
    {
        get => _skipAlreadyProcessedFaces;
        set => SetField(ref _skipAlreadyProcessedFaces, value);
    }

    // Legacy settings (kept for backward compatibility)
    [Obsolete("Use global GPU settings instead")]
    public int TaggingConcurrentWorkers
    {
        get => _taggingConcurrentWorkers;
        set => SetField(ref _taggingConcurrentWorkers, Math.Max(1, Math.Min(30, value)));
    }

    [Obsolete("Use GpuDevices instead")]
    public string TaggingGpuDevices
    {
        get => _taggingGpuDevices;
        set => SetField(ref _taggingGpuDevices, value);
    }

    [Obsolete("Use GpuVramCapacity instead")]
    public string TaggingGpuVramRatios
    {
        get => _taggingGpuVramRatios;
        set => SetField(ref _taggingGpuVramRatios, value);
    }

    [Obsolete("Use GpuDevices instead")]
    public string CaptioningGpuDevices
    {
        get => _captioningGpuDevices;
        set => SetField(ref _captioningGpuDevices, value);
    }

    [Obsolete("Handled by GPU orchestrator")]
    public string CaptioningModelsPerDevice
    {
        get => _captioningModelsPerDevice;
        set => SetField(ref _captioningModelsPerDevice, value);
    }

    public int CaptioningModelTTLMinutes
    {
        get => _captioningModelTTLMinutes;
        set => SetField(ref _captioningModelTTLMinutes, value);
    }

    public ObservableCollection<string> AvailableModels
    {
        get => _availableModels;
        set => SetField(ref _availableModels, value);
    }

    public ObservableCollection<Configuration.DatabaseProfile> DatabaseProfiles
    {
        get => _databaseProfiles ??= new ObservableCollection<Configuration.DatabaseProfile>();
        set => SetField(ref _databaseProfiles, value);
    }

    public override bool IsDirty => _isDirty;

}