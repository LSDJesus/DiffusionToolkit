using System;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Common;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Thumbnails;
using Diffusion.Toolkit.Classes;
using Diffusion.Tagging.Services;
using Diffusion.Captioning.Services;

namespace Diffusion.Toolkit.Services;

public class WindowService
{
    private Window _window;

    public Window CurrentWindow => _window;

    public void SetWindow(Window window)
    {
        _window = window;
    }
}

public class ServiceLocator
{
    private static WindowService? _windowService;
    private static AlbumService? _albumService;
    private static FileService? _fileService;
    private static NavigatorService? _navigatorService;
    private static ExternalApplicationsService? _externalApplicationsService;
    private static ThumbnailService? _thumbnailService;
    private static DatabaseWriterService? _databaseWriterService;
    private static MetadataScannerService? _metadataScannerService;
    private static FolderService? _folderService;
    private static ProgressService? _progressService;
    private static PostgreSQLDataStore? _dataStore; // Primary PostgreSQL database

    //private static ScanService? _scanManager;
    private static Settings? _settings;
    private static PreviewService? _previewService;
    private static ThumbnailNavigationService? _thumbnailNavigationService;
    private static TaggingService? _taggingService;
    private static NotificationService? _notificationService;
    private static ScanningService? _scanningService;
    private static ContextMenuService? _contextMenuService;
    private static JoyTagService? _joyTagService;
    private static WDTagService? _wdTagService;
    private static JoyCaptionService? _joyCaptionService;
    private static ICaptionService? _captionService;
    private static ModelResourceService? _modelResourceService;
    private static BackgroundTaggingService? _backgroundTaggingService;

    public static PostgreSQLDataStore? DataStore => _dataStore; // Primary PostgreSQL database
    public static Settings? Settings => _settings;
    public static ToastService? ToastService { get; set; }
    public static Dispatcher? Dispatcher { get; set; }
    public static BackgroundTaggingService? BackgroundTaggingService => _backgroundTaggingService;

    public static void SetDataStore(PostgreSQLDataStore dataStore)
    {
        _dataStore = dataStore;
        // Initialize BackgroundTaggingService when DataStore is set
        _backgroundTaggingService = new BackgroundTaggingService(dataStore);
    }

    public static void SetSettings(Settings? settings)
    {
        _settings = settings;
    }

    public static void SetNavigatorService(NavigatorService navigatorService)
    {
        _navigatorService = navigatorService;
    }

    public static PreviewService PreviewService
    {
        get { return _previewService ??= new PreviewService(); }
    }

    public static ThumbnailNavigationService ThumbnailNavigationService
    {
        get { return _thumbnailNavigationService ??= new ThumbnailNavigationService(); }
    }

    public static SearchService SearchService
    {
        get;
        set;
    }

    public static TaggingService TaggingService
    {
        get { return _taggingService ??= new TaggingService(); }
    }

    public static NotificationService NotificationService
    {
        get { return _notificationService ??= new NotificationService(); }
    }

    public static ScanningService ScanningService
    {
        get { return _scanningService ??= new ScanningService(); }
    }

    public static ModelResourceService ModelResourceService
    {
        get { return _modelResourceService ??= new ModelResourceService(); }
    }

    public static MainModel? MainModel { get; set; }

    public static FolderService FolderService
    {
        get { return _folderService ??= new FolderService(); }
    }


    public static ProgressService ProgressService
    {
        get { return _progressService ??= new ProgressService(); }
    }

    public static MessageService MessageService
    {
        get;
        set;
    }

    public static MetadataScannerService MetadataScannerService
    {
        get { return _metadataScannerService ??= new MetadataScannerService(); }
    }


    public static DatabaseWriterService DatabaseWriterService
    {
        get { return _databaseWriterService ??= new DatabaseWriterService(); }
    }

    public static ThumbnailService ThumbnailService
    {
        get { return _thumbnailService ??= new ThumbnailService(); }
    }

    public static ContextMenuService ContextMenuService
    {
        get { return _contextMenuService ??= new ContextMenuService(); }
    }

    public static ExternalApplicationsService ExternalApplicationsService
    {
        get { return _externalApplicationsService ??= new ExternalApplicationsService(); }
    }

    public static NavigatorService NavigatorService
    {
        get { return _navigatorService; }
    }

    public static FileService FileService
    {
        get { return _fileService ??= new FileService(); }
    }

    public static AlbumService AlbumService
    {
        get { return _albumService ??= new AlbumService(); }
    }

    public static WindowService WindowService
    {
        get { return _windowService ??= new WindowService(); }
    }

    public static JoyTagService? JoyTagService
    {
        get
        {
            if (_joyTagService == null && _settings != null)
            {
                var modelPath = System.IO.Path.IsPathRooted(_settings.JoyTagModelPath)
                    ? _settings.JoyTagModelPath
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _settings.JoyTagModelPath);
                var tagsPath = System.IO.Path.IsPathRooted(_settings.JoyTagTagsPath)
                    ? _settings.JoyTagTagsPath
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _settings.JoyTagTagsPath);

                if (System.IO.File.Exists(modelPath) && System.IO.File.Exists(tagsPath))
                {
                    _joyTagService = new JoyTagService(modelPath, tagsPath, _settings.JoyTagThreshold);
                }
            }
            return _joyTagService;
        }
    }

    public static WDTagService? WDTagService
    {
        get
        {
            if (_wdTagService == null && _settings != null)
            {
                var modelPath = System.IO.Path.IsPathRooted(_settings.WDTagModelPath)
                    ? _settings.WDTagModelPath
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _settings.WDTagModelPath);
                var tagsPath = System.IO.Path.IsPathRooted(_settings.WDTagTagsPath)
                    ? _settings.WDTagTagsPath
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _settings.WDTagTagsPath);

                if (System.IO.File.Exists(modelPath) && System.IO.File.Exists(tagsPath))
                {
                    _wdTagService = new WDTagService(modelPath, tagsPath, _settings.WDTagThreshold);
                }
            }
            return _wdTagService;
        }
    }

    public static JoyCaptionService? JoyCaptionService
    {
        get
        {
            if (_joyCaptionService == null && _settings != null)
            {
                var modelPath = System.IO.Path.IsPathRooted(_settings.JoyCaptionModelPath)
                    ? _settings.JoyCaptionModelPath
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _settings.JoyCaptionModelPath);
                var mmprojPath = System.IO.Path.IsPathRooted(_settings.JoyCaptionMMProjPath)
                    ? _settings.JoyCaptionMMProjPath
                    : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _settings.JoyCaptionMMProjPath);

                if (System.IO.File.Exists(modelPath) && System.IO.File.Exists(mmprojPath))
                {
                    _joyCaptionService = new JoyCaptionService(modelPath, mmprojPath);
                }
            }
            return _joyCaptionService;
        }
    }

    public static ICaptionService? CaptionService
    {
        get
        {
            if (_captionService != null) return _captionService;
            if (_settings == null) return null;

            if (_settings.CaptionProvider == Configuration.CaptionProviderType.LocalJoyCaption)
            {
                // Return JoyCaption if available, else null
                _captionService = JoyCaptionService;
                return _captionService;
            }
            else // OpenAI-compatible HTTP
            {
                if (!string.IsNullOrWhiteSpace(_settings.ExternalCaptionBaseUrl) && !string.IsNullOrWhiteSpace(_settings.ExternalCaptionModel))
                {
                    _captionService = new Diffusion.Captioning.Services.HttpCaptionService(
                        _settings.ExternalCaptionBaseUrl,
                        _settings.ExternalCaptionModel,
                        _settings.ExternalCaptionApiKey);
                    return _captionService;
                }
            }

            return null;
        }
    }
}

