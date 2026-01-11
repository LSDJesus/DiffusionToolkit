using Diffusion.Common;
using Diffusion.Common.Query;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Path = System.IO.Path;
using Diffusion.Toolkit.Classes;
using Search = Diffusion.Toolkit.Pages.Search;
using Diffusion.Toolkit.Thumbnails;
using Diffusion.IO;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Toolkit.Themes;
using Microsoft.Win32;
using Diffusion.Toolkit.Pages;
using MessageBox = System.Windows.MessageBox;
using Model = Diffusion.Common.Model;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Diffusion.Civitai.Models;
using WPFLocalizeExtension.Engine;
using Diffusion.Toolkit.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml;
using Diffusion.Toolkit.Services;
using Diffusion.Toolkit.Common;
using Diffusion.Toolkit.Configuration;
using Settings = Diffusion.Toolkit.Configuration.Settings;
using Diffusion.Database.PostgreSQL.Models;
using Image = System.Windows.Controls.Image;
using System.Windows.Media;
using SixLabors.ImageSharp;
using Size = System.Windows.Size;

namespace Diffusion.Toolkit
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : BorderlessWindow
    {
        private readonly MainModel _model;
        private NavigatorService _navigatorService;

        private PostgreSQLDataStore _dataStore => ServiceLocator.DataStore;
        private Settings _settings;



        private Configuration<Settings> _configuration;
        //private Toolkit.Settings? _settings;


        private Search _search;
        private Pages.Settings _settingsPage;
        private Pages.Models _models;
        private bool _tipsOpen;
        private MessagePopupManager _messagePopupManager;

        public MainWindow()
        {

            try
            {
                Logger.Log("===========================================");
                Logger.Log($"Started Diffusion Toolkit {AppInfo.Version}");


                _configuration = new Configuration<Settings>(AppInfo.SettingsPath, AppInfo.IsPortable);


                //Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                //Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

                InitializeComponent();

                AppDomain currentDomain = AppDomain.CurrentDomain;
                currentDomain.UnhandledException += new UnhandledExceptionEventHandler(GlobalExceptionHandler);

                QueryBuilder.Samplers = File.ReadAllLines("samplers.txt").ToList();

                Logger.Log($"Creating Thumbnail loader");



                _navigatorService = new NavigatorService(this);
                _navigatorService.OnNavigate += OnNavigate;

                ServiceLocator.SetNavigatorService(_navigatorService);

                SystemEvents.UserPreferenceChanged += SystemEventsOnUserPreferenceChanged;

                ServiceLocator.WindowService.SetWindow(this);

                _model = new MainModel();
                _model.CurrentProgress = 0;
                _model.TotalProgress = 100;

                ServiceLocator.MainModel = _model;
                ServiceLocator.Dispatcher = Dispatcher;

                _model.RenameFileCommand = new AsyncCommand<string>((o) => RenameImageEntry());
                _model.OpenWithCommand = new AsyncCommand<string>((o) => OpenWith(this, o));
                _model.Rescan = new AsyncCommand<object>(RescanTask);
                _model.Rebuild = new AsyncCommand<object>(RebuildTask);

                _model.ReloadHashes = new AsyncCommand<object>(async (o) =>
                {
                    await LoadModelsAsync();
                    await _messagePopupManager.Show("Models have been reloaded", "Diffusion Toolkit", PopupButtons.OK);
                });

                _model.SettingsCommand = new RelayCommand<object>(ShowSettings);

                _model.CancelCommand = new AsyncCommand<object>((o) => CancelProgress());
                _model.AboutCommand = new RelayCommand<object>((o) => ShowAbout());

                // Background tagging/captioning commands
                _model.ToggleBackgroundPauseCommand = new RelayCommand<object>((o) => ToggleBackgroundPause());
                _model.StopBackgroundProcessingCommand = new RelayCommand<object>((o) => StopBackgroundProcessing());
                _model.ReleaseModelsCommand = new RelayCommand<object>((o) => ReleaseModels());
                
                // Legacy individual commands (kept for compatibility)
                _model.StartTaggingCommand = new RelayCommand<object>((o) => StartTagging());
                _model.PauseTaggingCommand = new RelayCommand<object>((o) => PauseTagging());
                _model.StopTaggingCommand = new RelayCommand<object>((o) => StopTagging());
                _model.ClearTaggingQueueCommand = new RelayCommand<object>((o) => ClearTaggingQueue());
                
                _model.StartCaptioningCommand = new RelayCommand<object>((o) => StartCaptioning());
                _model.PauseCaptioningCommand = new RelayCommand<object>((o) => PauseCaptioning());
                _model.StopCaptioningCommand = new RelayCommand<object>((o) => StopCaptioning());
                _model.ClearCaptioningQueueCommand = new RelayCommand<object>((o) => ClearCaptioningQueue());
                
                _model.StartEmbeddingCommand = new RelayCommand<object>((o) => StartEmbedding());
                _model.PauseEmbeddingCommand = new RelayCommand<object>((o) => PauseEmbedding());
                _model.StopEmbeddingCommand = new RelayCommand<object>((o) => StopEmbedding());
                _model.ClearEmbeddingQueueCommand = new RelayCommand<object>((o) => ClearEmbeddingQueue());
                
                _model.StartFaceDetectionCommand = new RelayCommand<object>((o) => StartFaceDetection());
                _model.PauseFaceDetectionCommand = new RelayCommand<object>((o) => PauseFaceDetection());
                _model.StopFaceDetectionCommand = new RelayCommand<object>((o) => StopFaceDetection());
                _model.ClearFaceDetectionQueueCommand = new RelayCommand<object>((o) => ClearFaceDetectionQueue());
                
                // Unified processing commands
                _model.StartUnifiedProcessingCommand = new RelayCommand<object>((o) => StartUnifiedProcessing());
                _model.PauseUnifiedProcessingCommand = new RelayCommand<object>((o) => PauseUnifiedProcessing());
                _model.StopUnifiedProcessingCommand = new RelayCommand<object>((o) => StopUnifiedProcessing());
                _model.ClearAllQueuesCommand = new RelayCommand<object>((o) => ClearAllQueues());
                
                // Initialize VRAM estimate
                _model.UpdateEstimatedVramUsage();
                
                // NOTE: Background service event subscriptions are done in SubscribeToBackgroundServices()
                // which is called after SetDataStore() creates the services

                InitEvents();
                InitAlbums();
                InitQueries();

                _model.Refresh = new RelayCommand<object>((o) => Refresh());

                // TODO: Remove
                _model.QuickCopy = new RelayCommand<object>((o) =>
                {
                    var win = new QuickCopy();
                    win.Owner = this;
                    win.ShowDialog();
                });

                _model.Escape = new RelayCommand<object>((o) => Escape());

                _model.PropertyChanged += ModelOnPropertyChanged;



                this.Loaded += OnLoaded;
                this.Closing += OnClosing;
                _model.CloseCommand = new RelayCommand<object>(o =>
                {
                    this.Close();
                });

                DataContext = _model;

                // Initialize database profile switcher
                if (DatabaseProfileSwitcher != null)
                {
                    var profiles = _settings.DatabaseProfiles ?? new List<DatabaseProfile>();
                    if (profiles.Count == 0)
                    {
                        // Create a default profile from current connection
                        profiles = new List<DatabaseProfile>
                        {
                            new DatabaseProfile
                            {
                                Name = "Default",
                                ConnectionString = _settings.DatabaseConnectionString,
                                Schema = _settings.DatabaseSchema ?? "public",
                                Description = "Default connection",
                                Color = "#4CAF50"
                            }
                        };
                        _settings.DatabaseProfiles = profiles;
                        _settings.ActiveDatabaseProfile = "Default";
                    }
                    
                    DatabaseProfileSwitcher.ItemsSource = profiles;
                    DatabaseProfileSwitcher.SelectedValue = _settings.ActiveDatabaseProfile;
                    DatabaseProfileSwitcher.SelectionChanged += DatabaseProfile_SelectionChanged;
                }

                _messagePopupManager = new MessagePopupManager(this, PopupHost, Frame, Dispatcher);

                ServiceLocator.MessageService = new MessageService(_messagePopupManager);
                ServiceLocator.ToastService = new ToastService(ToastPopup);

                PreviewMouseDown += (sender, args) =>
                {
                    if (args.Source == QueryInput) return;
                    QueryPopup.IsOpen = false;
                };

                Deactivated += (sender, args) =>
                {
                    //QueryPopup.IsOpen = false;
                    //ToastPopup.IsOpen = false;
                    //_messagePopupManager.Cancel();
                };


                StateChanged += (sender, args) =>
                {
                    if (WindowState == WindowState.Minimized)
                    {
                        QueryPopup.IsOpen = false;
                        ToastPopup.IsOpen = false;
                        _messagePopupManager.Cancel();
                    }
                };

                string menuLanguage= null;

                // TODO: Find a better eay that doesn't get called even when mouseover?
                // Maybe only when the language has changed?
                Menu.LayoutUpdated += (sender, args) =>
                {
                    if (Thread.CurrentThread.CurrentCulture.Name != menuLanguage)
                    {
                        Menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        MenuWidth = new GridLength(Menu.DesiredSize.Width + 10);
                        menuLanguage = Thread.CurrentThread.CurrentCulture.Name;
                    }
                };

                //Thread.CurrentThread.CurrentCulture = new CultureInfo("pt-PT");
                //Thread.CurrentThread.CurrentUICulture = new CultureInfo("pt-PT");
                //FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(
                //    XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

                //var control = Application.Current.FindResource(typeof(Slider));
                //using (XmlTextWriter writer = new XmlTextWriter(@"slider.xaml", System.Text.Encoding.UTF8))
                //{
                //    writer.Formatting = Formatting.Indented;
                //    XamlWriter.Save(control, writer);
                //}

            }
            catch (Exception ex)
            {
                Logger.Log(ex.Message);
            }

        }

        private void ShowInExplorer(FolderViewModel folder)
        {
            var processInfo = new ProcessStartInfo()
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder.Path}\"",
                UseShellExecute = true
            };

            Process.Start(processInfo);

            //Process.Start("explorer.exe", $"/select,\"{p}\"");
        }

        private async Task UnavailableFiles(object o)
        {
            var window = new UnavailableFilesWindow();
            window.Owner = this;
            window.ShowDialog();

            if (window.DialogResult is true)
            {
                if (window.Model.RemoveImmediately)
                {
                    var result = await ServiceLocator.MessageService.Show("Are you sure you want to remove all unavailable files?", "Scan for Unavailable Images", PopupButtons.YesNo);
                    if (result == PopupResult.No)
                    {
                        return;
                    }
                }

                // Fire-and-forget: background task for scanning unavailable images
                _ = Task.Run(async () =>
                {
                    if (await ServiceLocator.ProgressService.TryStartTask())
                    {
                        try
                        {
                            await ServiceLocator.ScanningService.ScanUnavailable(window.Model, ServiceLocator.ProgressService.CancellationToken);
                        }
                        finally
                        {
                            ServiceLocator.ProgressService.CompleteTask();
                            ServiceLocator.ProgressService.SetStatus(GetLocalizedText("Actions.Scanning.Completed"));
                        }
                    }
                });

            }


        }

        private void ToggleNavigationPane()
        {
            _model.Settings.NavigationSection.ToggleSection();
        }

        private void ResetLayout()
        {
            _search.ResetLayout();
        }

        private void ToggleVisibility(string s)
        {
            switch (s)
            {
                case "Navigation.Folders":
                    _model.Settings.NavigationSection.ShowFolders = !_model.Settings.NavigationSection.ShowFolders;
                    break;
                case "Navigation.Models":
                    _model.Settings.NavigationSection.ShowModels = !_model.Settings.NavigationSection.ShowModels;
                    break;
                case "Navigation.Albums":
                    _model.Settings.NavigationSection.ShowAlbums = !_model.Settings.NavigationSection.ShowAlbums;
                    break;
                case "Navigation.Queries":
                    _model.Settings.NavigationSection.ShowQueries = !_model.Settings.NavigationSection.ShowQueries;
                    break;
                case "Navigation.ModelLibrary":
                    _model.Settings.NavigationSection.ShowModelLibrary = !_model.Settings.NavigationSection.ShowModelLibrary;
                    break;
            }
        }

        private void ToggleAutoRefresh()
        {
            _settings.AutoRefresh = !_settings.AutoRefresh;
        }

        private void Escape()
        {
            _messagePopupManager.Cancel();
        }

        private void Refresh()
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                _search.SearchImages(null);
            }
            else
            {
                _search.ReloadMatches(null);
            }

            _ = ServiceLocator.FolderService.LoadFolders();
        }

        private PreviewWindow? _previewWindow;

        private void PopoutPreview(bool hidePreview, bool maximized, bool fullscreen)
        {
            if (_previewWindow == null)
            {
                if (hidePreview)
                {
                    _model.IsPreviewVisible = false;
                    _search.SetPreviewVisible(_model.IsPreviewVisible);
                }

                _previewWindow = new PreviewWindow();

                _previewWindow.WindowState = maximized ? WindowState.Maximized : WindowState.Normal;

                _previewWindow.Owner = this;

                // TODO: better implementation for StartNavigation events
                _previewWindow.PreviewKeyUp += _search.ExtOnKeyUp;
                _previewWindow.PreviewKeyDown += _search.ExtOnKeyDown;

                _previewWindow.AdvanceSlideShow = _search.Advance;

                _previewWindow.OnDrop = (s) => _search.LoadPreviewImage(s);
                //_previewWindow.Changed = (id) => _search.Update(id);

                _previewWindow.Closed += (sender, args) =>
                {
                    _search.OnCurrentImageChange = null;
                    _search.ThumbnailListView.FocusCurrentItem();
                    _previewWindow = null;
                    _search.NavigationCompleted -= SearchOnNavigationCompleted;
                };
                _previewWindow.SetCurrentImage(_search.CurrentImage);

                _search.NavigationCompleted += SearchOnNavigationCompleted;

                _search.OnCurrentImageChange = (image) =>
                {
                    _previewWindow?.SetCurrentImage(image);
                };

                void UpdatePreviewWindowState()
                {
                    _settings.PreviewWindowState.Top = _previewWindow.Top;
                    _settings.PreviewWindowState.Left = _previewWindow.Left;
                    _settings.PreviewWindowState.Height = _previewWindow.Height;
                    _settings.PreviewWindowState.Width = _previewWindow.Width;
                    _settings.PreviewWindowState.State = _previewWindow.WindowState;
                    _settings.PreviewWindowState.IsFullScreen = _previewWindow.IsFullScreen;
                    _settings.PreviewWindowState.IsSet = true;
                    _settings.SetDirty();
                }

                _previewWindow.LocationChanged += (sender, args) =>
                {
                    UpdatePreviewWindowState();
                };

                _previewWindow.SizeChanged += (sender, args) =>
                {
                    UpdatePreviewWindowState();
                };

                _previewWindow.StateChanged += (sender, args) =>
                {
                    UpdatePreviewWindowState();
                };

                if (_settings.PreviewWindowState.IsSet)
                {
                    _previewWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    _previewWindow.WindowState = _settings.PreviewWindowState.State;
                    _previewWindow.Width = _settings.PreviewWindowState.Width;
                    _previewWindow.Height = _settings.PreviewWindowState.Height;
                    _previewWindow.Top = _settings.PreviewWindowState.Top;
                    _previewWindow.Left = _settings.PreviewWindowState.Left;
                }

                if (fullscreen || _settings.PreviewWindowState.IsFullScreen)
                {
                    _previewWindow.ShowFullScreen();
                }
                else
                {
                    _previewWindow.Show();
                }

            }
            else
            {
                _previewWindow.SetCurrentImage(_search.CurrentImage);
            }
        }

        private void SearchOnNavigationCompleted(object? sender, EventArgs e)
        {
            _previewWindow?.SetFocus();
        }


        private void GlobalExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = ((Exception)e.ExceptionObject);

            Logger.Log($"An unhandled exception occured: {exception.Message}\r\n\r\n{exception.StackTrace}");

            MessageBox.Show(this, exception.Message, "An unhandled exception occured", MessageBoxButton.OK, MessageBoxImage.Exclamation);

        }

        private void TogglePreview()
        {
            _model.IsPreviewVisible = !_model.IsPreviewVisible;
            _search.SetPreviewVisible(_model.IsPreviewVisible);
        }

        private void SetThumbnailSize(int size)
        {
            _settings.ThumbnailSize = size;
            ServiceLocator.ThumbnailService.Size = _settings.ThumbnailSize;
            _model.ThumbnailSize = _settings.ThumbnailSize;
            _search.SetThumbnailSize(_settings.ThumbnailSize);
            _prompts.SetThumbnailSize(_settings.ThumbnailSize);
        }

        private void SystemEventsOnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Color)
            {
                UpdateTheme(_settings.Theme);
            }
        }

        private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainModel.ShowIcons))
            {
                _search.SetOpacityView(_model.ShowIcons);
            }
            else if (e.PropertyName == nameof(MainModel.HideIcons))
            {
                _search.SetIconVisibility(_model.HideIcons);
            }
            else if (e.PropertyName == nameof(MainModel.SelectedImages))
            {
                _search.UpdateResults();
            }
            else if (e.PropertyName == nameof(MainModel.CurrentAlbum))
            {
                //Debug.WriteLine(_model.CurrentAlbum.Name);
                //_search.SetView("albums");
                //_search.SearchImages(null);
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // === PHASE 1: Load Settings First ===
            // Settings must be loaded before database connection to get connection string and schema
            var isFirstTime = false;
            IReadOnlyList<string> newFolders = null;
            var _showReleaseNotes = false;

            if (!_configuration.Exists())
            {
                Logger.Log($"Opening Settings for first time");

                _settings = new Settings();
                isFirstTime = true;

                UpdateTheme(_settings.Theme);

                var welcome = new WelcomeWindow(_settings);
                welcome.Owner = this;
                welcome.ShowDialog();

                newFolders = welcome.SelectedPaths;

                if (_settings.IsDirty())
                {
                    _configuration.Save(_settings);
                    _settings.SetPristine();
                }

                ThumbnailCache.CreateInstance(_settings.PageSize * 5, _settings.PageSize * 2);
            }
            else
            {
                try
                {
                    _configuration.Load(out _settings);

                    // Initialize sections if null (may be missing from older config files)
                    _settings.MetadataSection ??= new MetadataSectionSettings();
                    _settings.NavigationSection ??= new NavigationSectionSettings();
                    
                    _settings.MetadataSection.Attach(_settings);
                    _settings.NavigationSection.Attach(_settings);
                    _settings.UseBuiltInViewer ??= true;
                    _settings.SortAlbumsBy ??= "Name";
                    _settings.Theme ??= "System";

                    UpdateTheme(_settings.Theme);

                    _settings.PortableMode = _configuration.Portable;
                    _settings.SetPristine();

                    ThumbnailCache.CreateInstance(_settings.PageSize * 5, _settings.PageSize * 2);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, "An error occured while loading configuration settings. The application will exit", "Startup failed!", MessageBoxButton.OK, MessageBoxImage.Error);
                    throw;
                }
            }
            
            // Make settings available globally BEFORE database initialization
            ServiceLocator.SetSettings(_settings);

            // === PHASE 2: Initialize PostgreSQL with Retry Logic ===
            // Use active database profile if configured, otherwise fall back to direct connection string
            var postgresConnectionString = _settings.GetActiveConnectionString();
            if (string.IsNullOrWhiteSpace(postgresConnectionString))
            {
                postgresConnectionString = AppInfo.PostgreSQLConnectionString;
            }
            
            // Get schema from active profile or settings
            var activeProfile = _settings.DatabaseProfiles.FirstOrDefault(p => p.Name == _settings.ActiveDatabaseProfile);
            var schema = activeProfile?.Schema ?? _settings.DatabaseSchema ?? "public";
            
            PostgreSQLDataStore? pgDataStore = null;
            const int maxRetries = 3;
            const int retryDelayMs = 2000;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Logger.Log($"PostgreSQL connection attempt {attempt}/{maxRetries}...");
                    pgDataStore = new PostgreSQLDataStore(postgresConnectionString);
                    
                    // Set active schema from profile/settings BEFORE Create() so migrations use correct schema
                    pgDataStore.CurrentSchema = schema;
                    
                    await pgDataStore.Create(() => null, _ => { });
                    
                    // Sync settings with actual schema (Create() may have detected fallback)
                    if (_settings.DatabaseSchema != pgDataStore.CurrentSchema)
                    {
                        Logger.Log($"Syncing settings schema: '{_settings.DatabaseSchema}' -> '{pgDataStore.CurrentSchema}'");
                        _settings.DatabaseSchema = pgDataStore.CurrentSchema;
                    }
                    
                    ServiceLocator.SetDataStore(pgDataStore);
                    Logger.Log($"✓ PostgreSQL connection established (Profile: {_settings.ActiveDatabaseProfile})");
                    
                    // Subscribe to background services now that they exist
                    SubscribeToBackgroundServices();
                    
                    // Detect schema type and check permissions
                    var schemaType = await pgDataStore.DetectSchemaTypeAsync();
                    var hasWrite = await pgDataStore.HasWritePermissionAsync();
                    
                    // Update profile with detected info
                    if (activeProfile != null)
                    {
                        activeProfile.SchemaType = schemaType;
                        if (!hasWrite && !activeProfile.IsReadOnly)
                        {
                            Logger.Log($"⚠️ No write permission detected - consider enabling read-only mode for this profile");
                        }
                    }
                    
                    // Update UI status
                    if (ServiceLocator.MainModel != null)
                    {
                        ServiceLocator.MainModel.IsDatabaseConnected = true;
                        var badge = activeProfile?.Badge ?? "";
                        var readOnlyIndicator = (activeProfile?.IsReadOnly == true || !hasWrite) ? " [Read-Only]" : "";
                        ServiceLocator.MainModel.DatabaseStatus = $"Connected: {_settings.ActiveDatabaseProfile}{badge}{readOnlyIndicator} ({pgDataStore.GetStatus()})";
                    }
                    break;
                }
                catch (Exception pgEx)
                {
                    Logger.Log($"✗ PostgreSQL connection attempt {attempt} failed: {pgEx.Message}");
                    
                    // Update UI status on failure
                    if (ServiceLocator.MainModel != null)
                    {
                        ServiceLocator.MainModel.IsDatabaseConnected = false;
                        ServiceLocator.MainModel.DatabaseStatus = $"Failed (attempt {attempt}/{maxRetries})";
                    }
                    
                    if (attempt == maxRetries)
                    {
                        var result = MessageBox.Show(this, 
                            $"Failed to connect to PostgreSQL database after {maxRetries} attempts.\n\n" +
                            $"Make sure Docker is running with: docker-compose up -d\n\n" +
                            $"Connection: {postgresConnectionString.Split(';').FirstOrDefault()}\n\n" +
                            $"Error: {pgEx.Message}\n\n" +
                            $"Would you like to retry?", 
                            "Database Connection Error", 
                            MessageBoxButton.YesNo, 
                            MessageBoxImage.Error);
                        
                        if (result == MessageBoxResult.Yes)
                        {
                            attempt = 0; // Reset retry counter
                            continue;
                        }
                        
                        // User cancelled - update status and exit
                        if (ServiceLocator.MainModel != null)
                        {
                            ServiceLocator.MainModel.DatabaseStatus = "Not Connected";
                        }
                        return;
                    }
                    
                    await Task.Delay(retryDelayMs);
                }
            }

            // === PHASE 3: Version Checks and UI Setup ===
            if (!SemanticVersion.TryParse(_settings.Version, out var semVer))
            {
                semVer = SemanticVersion.Parse("v0.0.0");
            }





            // TODO: Find a better place to put this:
            // Set defaults for new version features
            if (semVer < SemanticVersion.Parse("v1.9.0"))
            {
                _settings.ShowTags = true;
                _settings.NavigationSection.ShowFolders = true;
                _settings.NavigationSection.ShowAlbums = true;
                _settings.NavigationSection.ShowQueries = true;
                _settings.ConfirmDeletion = true;
                _settings.PermanentlyDelete = false;
            }


            if (semVer < AppInfo.Version)
            {
                _showReleaseNotes = true;
                _settings.Version = AppInfo.Version.ToString();
            }


            _settings.PropertyChanged += (s, args) =>
            {
                if (!_isClosing)
                {
                    OnSettingsChanged(args);
                }
            };

            if (_settings.WindowState.HasValue)
            {
                this.WindowState = _settings.WindowState.Value;
            }

            if (_settings.WindowSize.HasValue)
            {
                this.Width = _settings.WindowSize.Value.Width;
                this.Height = _settings.WindowSize.Value.Height;
            }

            if (_settings.Top.HasValue)
            {
                this.Top = _settings.Top.Value;
            }

            if (_settings.Left.HasValue)
            {
                this.Left = _settings.Left.Value;
            }

            _settings.Culture ??= "default";

            if (_settings.Culture == "default")
            {
                LocalizeDictionary.Instance.Culture = CultureInfo.CurrentCulture;
            }
            else
            {
                LocalizeDictionary.Instance.Culture = new CultureInfo(_settings.Culture);
            }

            _model.AutoRefresh = _settings.AutoRefresh;
            _model.HideNSFW = _settings.HideNSFW;
            _model.HideDeleted = _settings.HideDeleted;
            _model.HideUnavailable = _settings.HideUnavailable;
            _model.PermanentlyDelete = _settings.PermanentlyDelete;

            // TODO: Get rid of globals
            QueryBuilder.HideNSFW = _model.HideNSFW;
            QueryBuilder.HideDeleted = _model.HideDeleted;
            QueryBuilder.HideUnavailable = _model.HideUnavailable;

            _model.NSFWBlur = _settings.NSFWBlur;
            _model.FitToPreview = _settings.FitToPreview;
            _model.ActualSize = _settings.ActualSize;
            _model.AutoAdvance = _settings.AutoAdvance;
            _model.ShowTags = _settings.ShowTags;
            _model.ShowNotifications = _settings.ShowNotifications;
            _model.ShowFilenames = _settings.ShowFilenames;

            _model.Settings = _settings;

            //KeyDown += (o, args) =>
            //{
            //    IInputElement focusedControl = Keyboard.FocusedElement;
            //    IInputElement focusedControl2 = FocusManager.GetFocusedElement(this);
            //};

            Activated += OnActivated;
            StateChanged += OnStateChanged;
            SizeChanged += OnSizeChanged;
            LocationChanged += OnLocationChanged;

            ServiceLocator.SetSettings(_settings);

            Logger.Log($"Initializing pages");

            //var total = _dataStore.GetTotal();

            //var text = GetLocalizedText("Main.Status.ImagesInDatabase").Replace("{count}", $"{total:n0}");

            //_model.Status = text;
            //_model.TotalProgress = 100;

            _models = new Pages.Models();

            _models.OnModelUpdated = (model) =>
            {
                var updatedModel = _modelsCollection.FirstOrDefault(m => m.Path == model.Path);
                if (updatedModel != null)
                {
                    updatedModel.SHA256 = model.SHA256;
                    _search.SetModels(_modelsCollection);
                }
            };

            _search = new Search();

            _search.MoveFiles = (files) =>
            {
                using var dialog = new CommonOpenFileDialog();
                dialog.IsFolderPicker = true;

                if (dialog.ShowDialog(this) == CommonFileDialogResult.Ok)
                {
                    var path = dialog.FileName;

                    var isInPath = false;

                    // TODO: Fix
                    throw new NotImplementedException("Too Lazy to fix");

                    // Check if the target path is in the list of selected diffusion folders

                    //if (_settings.RecurseFolders.GetValueOrDefault(true))
                    //{
                    //    foreach (var folder in ServiceLocator.FolderService.RootFolders)
                    //    {
                    //        if (path.StartsWith(folder.Path, true, CultureInfo.InvariantCulture))
                    //        {
                    //            isInPath = true;
                    //            break;
                    //        }
                    //    }

                    //    // Now check if the path falls under one of the excluded paths.

                    //    foreach (var folder in ServiceLocator.FolderService.ExcludedFolders)
                    //    {
                    //        if (path.StartsWith(folder.Path, true, CultureInfo.InvariantCulture))
                    //        {
                    //            isInPath = false;
                    //            break;
                    //        }
                    //    }
                    //}
                    //else
                    //{
                    //    // If recursion is turned off, the path must specifically equal one of the diffusion folders

                    //    foreach (var imagePath in ServiceLocator.FolderService.RootFolders.Select(d => d.Path))
                    //    {
                    //        if (path.Equals(imagePath, StringComparison.InvariantCultureIgnoreCase))
                    //        {
                    //            isInPath = true;
                    //            break;
                    //        }
                    //    }
                    //}

                    Task.Run(async () =>
                    {
                        await ServiceLocator.FileService.MoveFiles(files.Select(d => new ImagePath() { Id = d.Id, Path = d.Path }).ToList(), path, !isInPath);

                        if (!isInPath)
                        {
                            _search.SearchImages();
                        }
                    });

                }

            };

            _prompts = new Prompts();
            _settingsPage = new Pages.Settings(this);
            _faceGallery = new Pages.FaceGallery();


            _model.ThumbnailSize = _settings.ThumbnailSize;
            _model.ThumbnailViewMode = _settings.ThumbnailViewMode;

            _search.SetThumbnailSize(_settings.ThumbnailSize);
            _search.SetPageSize(_settings.PageSize);

            _prompts.SetThumbnailSize(_settings.ThumbnailSize);
            _prompts.SetPageSize(_settings.PageSize);

            _search.OnPopout = () => PopoutPreview(true, true, false);
            _search.OnCurrentImageOpen = OnCurrentImageOpen;

            _model.GotoUrl = new RelayCommand<string>((url) =>
            {
                _navigatorService.Goto(url);
            });

            _navigatorService.OnNavigate += (o, args) =>
            {
                _model.ActiveView = args.TargetUri.Path.ToLower() switch
                {
                    "search" when args.TargetUri.Fragment != null => args.TargetUri.Fragment.ToLower() switch
                    {
                        "favorites" => "Favorites",
                        "deleted" => "For Deletion",
                        "images" => "Diffusions",
                        "folders" => "Folders",
                        "albums" => "Albums",
                        _ => _model.ActiveView
                    },
                    "search" => "Diffusions",
                    "models" => "Models",
                    "prompts" => "Prompts",
                    "settings" => "Settings",
                    "facegallery" => "Face Gallery",
                    _ => _model.ActiveView
                };
                
                // Handle FaceGallery page navigation with group ID fragment
                if (args.TargetUri.Path.ToLower() == "facegallery" && args.TargetUri.Fragment != null)
                {
                    if (int.TryParse(args.TargetUri.Fragment, out var groupId))
                    {
                        _ = _faceGallery.LoadGroupAsync(groupId);
                    }
                }
            };


            ServiceLocator.FolderService.CreateWatchers();

            //_navigatorService.Goto("search");

            Logger.Log($"Loading models");

            LoadAlbums();
            LoadQueries();
            LoadModels();
            LoadImageModels();
            await InitFolders();

            ServiceLocator.ContextMenuService.Go();

            Logger.Log($"{_modelsCollection.Count} models loaded");

            Logger.Log($"Starting Services...");

            // Initialize GPU Resource Orchestrator with settings
            ServiceLocator.InitializeGpuOrchestrator();

            ServiceLocator.ThumbnailService.Size = _settings.ThumbnailSize;

            _ = ServiceLocator.ThumbnailService.StartAsync();

            if (_settings.CheckForUpdatesOnStartup)
            {
                _ = Task.Run(async () =>
                {
                    var checker = new UpdateChecker();

                    Logger.Log($"Checking for latest version");
                    try
                    {
                        var hasUpdate = await checker.CheckForUpdate();

                        if (hasUpdate)
                        {
                            var result = await _messagePopupManager.Show(GetLocalizedText("Main.Update.UpdateAvailable"), "Diffusion Toolkit", PopupButtons.YesNo);
                            if (result == PopupResult.Yes)
                            {
                                CallUpdater();
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        await _messagePopupManager.Show(exception.Message, "Update error", PopupButtons.OK);
                    }
                });
            }


            if (isFirstTime)
            {
                // Automatically scans images...
                await ServiceLocator.FolderService.ApplyFolderChanges(newFolders.Select(d => new FolderChange()
                {
                    ChangeType = ChangeType.Add,
                    FolderType = FolderType.Root,
                    Watched = true,
                    Recursive = true,
                    Path = d,
                }), confirmScan: false).ContinueWith(d =>
                {
                    ServiceLocator.NavigatorService.Goto("search");

                    if (ServiceLocator.FolderService.HasRootFolders)
                    {
                        // Wait for a bit, then show thumbnails
                        _ = Task.Delay(5000).ContinueWith(t =>
                        {
                            ServiceLocator.SearchService.ExecuteSearch();
                            ServiceLocator.MessageService.ShowMedium(GetLocalizedText("FirstScan.Message"),
                                GetLocalizedText("FirstScan.Title"), PopupButtons.OK);
                        });
                    }

                });
            }
            else
            {
                ServiceLocator.NavigatorService.Goto("search");

                if (ServiceLocator.FolderService.HasRootFolders)
                {
                    if (_settings.ScanForNewImagesOnStartup)
                    {
                        Logger.Log($"Scanning for new images");

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                if (await ServiceLocator.ProgressService.TryStartTask())
                                {
                                    try
                                    {
                                        await ServiceLocator.ScanningService.ScanWatchedFolders(false, false, ServiceLocator.ProgressService.CancellationToken);
                                    }
                                    finally
                                    {
                                        ServiceLocator.ProgressService.CompleteTask();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Error in startup scan: {ex.Message}");
                                Logger.Log($"  Stack: {ex.StackTrace}");
                            }
                        });
                    }
                }
            }

            RenderOptions.ProcessRenderMode = _settings.RenderMode;

            Logger.Log($"Init completed");

            // Check for pending tagged/captioned images
            await CheckPendingQueueAsync();

            if (_showReleaseNotes)
            {
                ShowReleaseNotes();
            }
            // Cleanup();
        }

        private async Task CheckPendingQueueAsync()
        {
            try
            {
                var dataStore = ServiceLocator.DataStore;
                if (dataStore == null) return;

                var pendingTagging = await dataStore.CountImagesNeedingTagging();
                var pendingCaptioning = await dataStore.CountImagesNeedingCaptioning();

                if (pendingTagging > 0 || pendingCaptioning > 0)
                {
                    // Show status but don't start processing
                    if (pendingTagging > 0)
                    {
                        _model.IsTaggingActive = true;
                        _model.TaggingStatus = $"Queue: {pendingTagging} images";
                        Logger.Log($"Found {pendingTagging} images pending tagging");
                    }

                    if (pendingCaptioning > 0)
                    {
                        _model.IsCaptioningActive = true;
                        _model.CaptioningStatus = $"Queue: {pendingCaptioning} images";
                        Logger.Log($"Found {pendingCaptioning} images pending captioning");
                    }

                    // Optionally notify user
                    ServiceLocator.ToastService?.Toast(
                        $"Found {pendingTagging} images queued for tagging, {pendingCaptioning} for captioning.\nClick ▶ buttons in status bar to start processing.",
                        "Pending Queue");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error checking pending queue: {ex.Message}");
            }
        }

        private async Task Cleanup()
        {
            await CleanRemovedFoldersInternal();
        }

        //private void NavigatorServiceOnOnNavigate(object? sender, NavigateEventArgs e)
        //{
        //    if (e.CurrentUrl == "settings")
        //    {
        //        var changes = _settingsPage.ApplySettings();

        //        _ = ExecuteFolderChanges(changes);
        //    }
        //}


        private void OnCurrentImageOpen(ImageViewModel obj)
        {
            //if (obj == null) return;
            var p = obj.Path;

            ServiceLocator.FileService.UpdateViewed(obj.Id);

            if (_settings.UseBuiltInViewer.GetValueOrDefault(true))
            {
                PopoutPreview(false, true, _settings.OpenInFullScreen.GetValueOrDefault(true));
            }
            else if (_settings.UseSystemDefault.GetValueOrDefault(false))
            {
                var processInfo = new ProcessStartInfo()
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{p}\"",
                    UseShellExecute = true
                };

                try
                {
                    Process.Start(processInfo);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

            }
            else if (_settings.UseCustomViewer.GetValueOrDefault(false))
            {
                var args = _settings.CustomCommandLineArgs;

                if (string.IsNullOrWhiteSpace(args))
                {
                    args = "%1";
                }

                if (string.IsNullOrEmpty(_settings.CustomCommandLine))
                {
                    MessageBox.Show(this, "No custom viewer set. Please check Settings", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }


                if (!File.Exists(_settings.CustomCommandLine))
                {
                    MessageBox.Show(this, "The specified application does not exist", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var processInfo = new ProcessStartInfo()
                {
                    FileName = _settings.CustomCommandLine,
                    Arguments = args.Replace("%1", $"\"{p}\""),
                    UseShellExecute = true
                };

                try
                {
                    Process.Start(processInfo);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message + "\r\n\r\nPlease check that the path to your custom viewer is valid and that the arguments are correct", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

            }


            //PopoutPreview(false, true);

            //Process.Start("explorer.exe", $"/select,\"{p}\"");

        }

        private async void ShowSettings(object obj)
        {
            _navigatorService.Goto("settings");
        }


        private void UpdateTheme(string theme)
        {
            ThemeManager.ChangeTheme(theme);
        }


        private void OnNavigate(object sender, NavigateEventArgs args)
        {
            _model.Page = args.TargetPage;
        }


        private ICollection<Model> _modelsCollection = new List<Model>();
        private Prompts _prompts;
        private Pages.FaceGallery _faceGallery;
        private bool _isLoadingModels = false;

        private void LoadModels()
        {
            // Fire and forget - non-blocking model loading
            _ = LoadModelsAsync();
        }

        private async Task LoadModelsAsync()
        {
            // Prevent concurrent model loading
            if (_isLoadingModels)
                return;

            _isLoadingModels = true;

            try
            {
                // Run the heavy file I/O operations on a background thread
                var (modelsCollection, otherModels) = await Task.Run(() =>
                {
                    ICollection<Model> models;
                    if (!string.IsNullOrEmpty(_settings.ModelRootPath) && Directory.Exists(_settings.ModelRootPath))
                    {
                        models = ModelScanner.Scan(_settings.ModelRootPath).ToList();
                    }
                    else
                    {
                        models = new List<Model>();
                    }

                    // Load hash cache
                    if (!string.IsNullOrEmpty(_settings.HashCache) && File.Exists(_settings.HashCache))
                    {
                        try
                        {
                            string text = File.ReadAllText(_settings.HashCache);

                            // Fix unquoted "NaN" strings, which sometimes show up in SafeTensor metadata.
                            text = text.Replace("NaN", "null");

                            var hashes = JsonSerializer.Deserialize<Hashes>(text);
                            var modelLookup = models.ToDictionary(m => m.Path);

                            var index = "checkpoint/".Length;

                            foreach (var hash in hashes.hashes)
                            {
                                if (index < hash.Key.Length)
                                {
                                    var path = hash.Key.Substring(index);

                                    if (modelLookup.TryGetValue(path, out var model))
                                    {
                                        model.SHA256 = hash.Value.sha256;
                                    }
                                    else
                                    {
                                        models.Add(new Model()
                                        {
                                            Filename = Path.GetFileNameWithoutExtension(path),
                                            Path = path,
                                            SHA256 = hash.Value.sha256,
                                            IsLocal = true
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            // Log error but don't block - will show message on UI thread
                            Logger.Log($"Error loading hash cache: {e.Message}");
                        }
                    }

                    // Load other models from models.json
                    var others = new List<Model>();
                    var modelsJsonPath = Path.Combine(AppDir, "models.json");
                    if (File.Exists(modelsJsonPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(modelsJsonPath);

                            var options = new JsonSerializerOptions()
                            {
                                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                Converters = { new JsonStringEnumConverter() }
                            };

                            var civitAiModels = JsonSerializer.Deserialize<LiteModelCollection>(json, options);

                            foreach (var model in civitAiModels.Models)
                            {
                                foreach (var modelVersion in model.ModelVersions)
                                {
                                    foreach (var versionFile in modelVersion.Files)
                                    {
                                        others.Add(new Model()
                                        {
                                            Filename = Path.GetFileNameWithoutExtension(versionFile.Name),
                                            Hash = versionFile.Hashes.AutoV1,
                                            SHA256 = versionFile.Hashes.SHA256,
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log(ex.Message);
                        }
                    }

                    return (models, others);
                });

                _modelsCollection = modelsCollection;
                _allModels = _modelsCollection.Concat(otherModels).ToList();

                // Update UI on dispatcher thread
                await Dispatcher.InvokeAsync(() =>
                {
                    _search.SetModels(_allModels);
                    _models.SetModels(_modelsCollection);
                    QueryBuilder.SetModels(_allModels);
                });
            }
            finally
            {
                _isLoadingModels = false;
            }
        }

        private ICollection<Model> _allModels = new List<Model>();

        private async Task TryScanFolders()
        {
            if (ServiceLocator.FolderService.HasRootFolders)
            {
                if (await ServiceLocator.MessageService.Show("Do you want to scan your folders now?", "Setup", PopupButtons.YesNo) == PopupResult.Yes)
                {
                    await Scan();
                }
            }
            else
            {
                await ServiceLocator.MessageService.ShowMedium("You have not setup any image folders.\r\n\r\nAdd one or more folders first, then click the Scan Folders for new images icon in the toolbar.", "Setup", PopupButtons.OK);
            }
        }


    }



    public class Hashes
    {
        public Dictionary<string, HashInfo> hashes { get; set; }
    }

    public class HashInfo
    {
        public double mtime { get; set; }
        public string sha256 { get; set; }
    }

    static class WindowExtensions
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);


        public static double ActualTop(this Window window)
        {
            switch (window.WindowState)
            {
                case WindowState.Normal:
                    return window.Top;
                case WindowState.Minimized:
                    return window.RestoreBounds.Top;
                case WindowState.Maximized:
                    {
                        RECT rect;
                        GetWindowRect((new WindowInteropHelper(window)).Handle, out rect);
                        return rect.Top;
                    }
            }
            return 0;
        }
        public static double ActualLeft(this Window window)
        {
            switch (window.WindowState)
            {
                case WindowState.Normal:
                    return window.Left;
                case WindowState.Minimized:
                    return window.RestoreBounds.Left;
                case WindowState.Maximized:
                    {
                        RECT rect;
                        GetWindowRect((new WindowInteropHelper(window)).Handle, out rect);
                        return rect.Left;
                    }
            }
            return 0;
        }
    }

    // MainWindow partial - Background tagging event handlers
    public partial class MainWindow
    {
        private void OnTaggingProgressChanged(object? sender, Services.ProgressEventArgs e)
        {
            // Status is updated via StatusChanged event with ETA
            Dispatcher.Invoke(() =>
            {
                _model.IsTaggingActive = true;
                _model.TaggingQueueCount = ServiceLocator.BackgroundTaggingService?.TaggingQueueRemaining ?? 0;
            });
        }

        private void OnCaptioningProgressChanged(object? sender, Services.ProgressEventArgs e)
        {
            // Status is updated via StatusChanged event with ETA
            Dispatcher.Invoke(() =>
            {
                _model.IsCaptioningActive = true;
                _model.CaptioningQueueCount = ServiceLocator.BackgroundTaggingService?.CaptioningQueueRemaining ?? 0;
            });
        }

        private void OnBackgroundStatusChanged(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                // Update appropriate status based on content
                if (status.StartsWith("Tagging:"))
                {
                    _model.TaggingStatus = status;
                    // Extract queue count and ETA from status string
                    // Format: "Tagging: X/Y (Queue: Z, ETA: HH:MM:SS)"
                    var match = System.Text.RegularExpressions.Regex.Match(status, @"Queue:\s*(\d+),\s*ETA:\s*([\d:]+)");
                    if (match.Success)
                    {
                        _model.TaggingQueueCount = int.Parse(match.Groups[1].Value);
                    }
                }
                else if (status.StartsWith("Captioning:"))
                {
                    _model.CaptioningStatus = status;
                    var match = System.Text.RegularExpressions.Regex.Match(status, @"Queue:\s*(\d+),\s*ETA:\s*([\d:]+)");
                    if (match.Success)
                    {
                        _model.CaptioningQueueCount = int.Parse(match.Groups[1].Value);
                    }
                }
                else if (status.StartsWith("Embedding:"))
                {
                    _model.EmbeddingStatus = status;
                    var match = System.Text.RegularExpressions.Regex.Match(status, @"Queue:\s*(\d+),\s*ETA:\s*([\d:]+)");
                    if (match.Success)
                    {
                        _model.EmbeddingQueueCount = int.Parse(match.Groups[1].Value);
                    }
                }
            });
        }

        private void OnTaggingCompleted(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _model.IsTaggingActive = false;
                _model.TaggingStatus = "";
                ServiceLocator.ToastService?.Toast("Tagging completed", "Background Tagging");
            });
        }

        private void OnCaptioningCompleted(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _model.IsCaptioningActive = false;
                _model.CaptioningStatus = "";
                ServiceLocator.ToastService?.Toast("Captioning completed", "Background Captioning");
            });
        }

        private void ToggleBackgroundPause()
        {
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            if (service.IsTaggingPaused || service.IsCaptioningPaused)
            {
                service.ResumeTagging();
                service.ResumeCaptioning();
            }
            else
            {
                service.PauseTagging();
                service.PauseCaptioning();
            }
        }

        private void StopBackgroundProcessing()
        {
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StopTagging();
            service.StopCaptioning();
            
            _model.IsTaggingActive = false;
            _model.IsCaptioningActive = false;
            _model.TaggingStatus = "";
            _model.CaptioningStatus = "";
        }

        private void ReleaseModels()
        {
            // Release small tagging models immediately
            ServiceLocator.JoyTagService?.ReleaseModel();
            ServiceLocator.WDTagService?.ReleaseModel();
            
            // Release caption model (will be reloaded when needed)
            ServiceLocator.CaptionService?.ReleaseModel();
            
            ServiceLocator.ToastService?.Toast("Models released from VRAM", "Memory Management");
            Logger.Log("All models released from VRAM");
        }

        private async void StartTagging()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null)
                {
                    ServiceLocator.InitializeProcessingOrchestrator();
                    orchestrator = ServiceLocator.ProcessingOrchestrator;
                }
                if (orchestrator == null) return;
                
                var gpuAllocation = ParseGpuAllocation(ServiceLocator.Settings.SoloTaggingAllocation);
                await orchestrator.StartServiceAsync(Services.Processing.ProcessingType.Tagging, gpuAllocation, default);
                _model.IsTaggingActive = true;
                Logger.Log("Started tagging from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StartTagging();
            _model.IsTaggingActive = true;
            Logger.Log("Started tagging from UI");
        }

        private async void PauseTagging()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                var tagOrch = orchestrator.GetOrchestrator(Services.Processing.ProcessingType.Tagging);
                if (tagOrch?.IsPaused == true)
                    await orchestrator.ResumeServiceAsync(Services.Processing.ProcessingType.Tagging);
                else
                    await orchestrator.PauseServiceAsync(Services.Processing.ProcessingType.Tagging);
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            if (service.IsTaggingPaused)
            {
                service.ResumeTagging();
                Logger.Log("Resumed tagging");
            }
            else
            {
                service.PauseTagging();
                Logger.Log("Paused tagging");
            }
        }

        private async void StopTagging()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                await orchestrator.StopServiceAsync(Services.Processing.ProcessingType.Tagging);
                _model.IsTaggingActive = false;
                _model.TaggingStatus = "";
                Logger.Log("Stopped tagging from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StopTagging();
            _model.IsTaggingActive = false;
            _model.TaggingStatus = "";
            Logger.Log("Stopped tagging from UI");
        }

        private async void ClearTaggingQueue()
        {
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            var result = MessageBox.Show(
                "Clear tagging queue? This will reset needs_tagging flags without removing existing tags.",
                "Clear Tagging Queue",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await service.ClearTaggingQueue();
                Logger.Log("Cleared tagging queue from UI");
            }
        }

        private async void StartCaptioning()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null)
                {
                    ServiceLocator.InitializeProcessingOrchestrator();
                    orchestrator = ServiceLocator.ProcessingOrchestrator;
                }
                if (orchestrator == null) return;
                
                var gpuAllocation = ParseGpuAllocation(ServiceLocator.Settings.SoloCaptioningAllocation);
                await orchestrator.StartServiceAsync(Services.Processing.ProcessingType.Captioning, gpuAllocation, default);
                _model.IsCaptioningActive = true;
                Logger.Log("Started captioning from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StartCaptioning();
            _model.IsCaptioningActive = true;
            Logger.Log("Started captioning from UI");
        }

        private async void PauseCaptioning()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                var captOrch = orchestrator.GetOrchestrator(Services.Processing.ProcessingType.Captioning);
                if (captOrch?.IsPaused == true)
                    await orchestrator.ResumeServiceAsync(Services.Processing.ProcessingType.Captioning);
                else
                    await orchestrator.PauseServiceAsync(Services.Processing.ProcessingType.Captioning);
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            if (service.IsCaptioningPaused)
            {
                service.ResumeCaptioning();
                Logger.Log("Resumed captioning");
            }
            else
            {
                service.PauseCaptioning();
                Logger.Log("Paused captioning");
            }
        }

        private async void StopCaptioning()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                await orchestrator.StopServiceAsync(Services.Processing.ProcessingType.Captioning);
                _model.IsCaptioningActive = false;
                _model.CaptioningStatus = "";
                Logger.Log("Stopped captioning from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StopCaptioning();
            _model.IsCaptioningActive = false;
            _model.CaptioningStatus = "";
            Logger.Log("Stopped captioning from UI");
        }

        private async void ClearCaptioningQueue()
        {
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            var result = MessageBox.Show(
                "Clear captioning queue? This will reset needs_captioning flags without removing existing captions.",
                "Clear Captioning Queue",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await service.ClearCaptioningQueue();
                Logger.Log("Cleared captioning queue from UI");
            }
        }

        private async void StartEmbedding()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null)
                {
                    ServiceLocator.InitializeProcessingOrchestrator();
                    orchestrator = ServiceLocator.ProcessingOrchestrator;
                }
                if (orchestrator == null) return;
                
                var gpuAllocation = ParseGpuAllocation(ServiceLocator.Settings.SoloEmbeddingAllocation);
                await orchestrator.StartServiceAsync(Services.Processing.ProcessingType.Embedding, gpuAllocation, default);
                _model.IsEmbeddingActive = true;
                Logger.Log("Started embedding from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StartEmbedding();
            _model.IsEmbeddingActive = true;
            Logger.Log("Started embedding from UI");
        }

        private async void PauseEmbedding()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                var embOrch = orchestrator.GetOrchestrator(Services.Processing.ProcessingType.Embedding);
                if (embOrch?.IsPaused == true)
                    await orchestrator.ResumeServiceAsync(Services.Processing.ProcessingType.Embedding);
                else
                    await orchestrator.PauseServiceAsync(Services.Processing.ProcessingType.Embedding);
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            if (service.IsEmbeddingPaused)
            {
                service.ResumeEmbedding();
                Logger.Log("Resumed embedding");
            }
            else
            {
                service.PauseEmbedding();
                Logger.Log("Paused embedding");
            }
        }

        private async void StopEmbedding()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                await orchestrator.StopServiceAsync(Services.Processing.ProcessingType.Embedding);
                _model.IsEmbeddingActive = false;
                _model.EmbeddingStatus = "";
                Logger.Log("Stopped embedding from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StopEmbedding();
            _model.IsEmbeddingActive = false;
            _model.EmbeddingStatus = "";
            Logger.Log("Stopped embedding from UI");
        }

        private async void ClearEmbeddingQueue()
        {
            var service = ServiceLocator.DataStore;
            if (service == null) return;

            var result = MessageBox.Show(
                "Clear embedding queue? This will reset needs_embedding flags without removing existing embeddings.",
                "Clear Embedding Queue",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await service.ClearEmbeddingQueue();
                Logger.Log("Cleared embedding queue from UI");
            }
        }

        private void OnEmbeddingProgressChanged(object? sender, ProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var elapsed = DateTime.Now - DateTime.Now.AddSeconds(-e.Current); // Approximate
                var imgPerSec = e.Current > 0 ? e.Current / Math.Max(1, elapsed.TotalSeconds) : 0;
                _model.EmbeddingStatus = $"Embedding: {e.Current}/{e.Total} ({e.Percentage:F1}%)";
                _model.IsEmbeddingActive = true;
                _model.EmbeddingQueueCount = ServiceLocator.BackgroundTaggingService?.EmbeddingQueueRemaining ?? 0;
            });
        }

        private void OnEmbeddingCompleted(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _model.IsEmbeddingActive = false;
                _model.EmbeddingStatus = "";
                Logger.Log("Embedding completed");
            });
        }

        #region Face Detection

        private async void StartFaceDetection()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null)
                {
                    ServiceLocator.InitializeProcessingOrchestrator();
                    orchestrator = ServiceLocator.ProcessingOrchestrator;
                }
                if (orchestrator == null) return;
                
                var gpuAllocation = ParseGpuAllocation(ServiceLocator.Settings.SoloFaceDetectionAllocation);
                await orchestrator.StartServiceAsync(Services.Processing.ProcessingType.FaceDetection, gpuAllocation, default);
                _model.IsFaceDetectionActive = true;
                Logger.Log("Started face detection from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundFaceDetectionService;
            if (service == null) return;

            service.StartFaceDetection();
            _model.IsFaceDetectionActive = true;
            Logger.Log("Started face detection from UI");
        }

        private async void PauseFaceDetection()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                var faceOrch = orchestrator.GetOrchestrator(Services.Processing.ProcessingType.FaceDetection);
                if (faceOrch?.IsPaused == true)
                    await orchestrator.ResumeServiceAsync(Services.Processing.ProcessingType.FaceDetection);
                else
                    await orchestrator.PauseServiceAsync(Services.Processing.ProcessingType.FaceDetection);
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundFaceDetectionService;
            if (service == null) return;

            if (service.IsFaceDetectionPaused)
            {
                service.ResumeFaceDetection();
                Logger.Log("Resumed face detection");
            }
            else
            {
                service.PauseFaceDetection();
                Logger.Log("Paused face detection");
            }
        }

        private async void StopFaceDetection()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                await orchestrator.StopServiceAsync(Services.Processing.ProcessingType.FaceDetection);
                _model.IsFaceDetectionActive = false;
                _model.FaceDetectionStatus = "";
                Logger.Log("Stopped face detection from UI (new architecture)");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundFaceDetectionService;
            if (service == null) return;

            service.StopFaceDetection();
            _model.IsFaceDetectionActive = false;
            _model.FaceDetectionStatus = "";
            Logger.Log("Stopped face detection from UI");
        }

        private async void ClearFaceDetectionQueue()
        {
            var dataStore = ServiceLocator.DataStore;
            if (dataStore == null) return;

            var result = MessageBox.Show(
                "Clear face detection queue? This will reset needs_face_detection flags without removing existing detections.",
                "Clear Face Detection Queue",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await dataStore.ClearFaceDetectionQueue();
                Logger.Log("Cleared face detection queue from UI");
            }
        }

        #region Unified Processing Commands
        
        private async void StartUnifiedProcessing()
        {
            // Check if at least one process is enabled
            if (!_model.EnableTagging && !_model.EnableCaptioning && !_model.EnableEmbedding && !_model.EnableFaceDetection)
            {
                ServiceLocator.ToastService?.Toast("Select at least one processor to start", "Background Processing");
                return;
            }
            
            _model.IsUnifiedProcessingActive = true;
            
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null)
                {
                    ServiceLocator.InitializeProcessingOrchestrator();
                    orchestrator = ServiceLocator.ProcessingOrchestrator;
                }
                if (orchestrator == null) return;
                
                try
                {
                    // Build GPU allocation from concurrent settings (all services share)
                    var gpuAllocation = new Dictionary<int, int>();
                    var settings = ServiceLocator.Settings;
                    
                    // For concurrent mode, use the concurrent allocations
                    if (_model.EnableTagging)
                        MergeGpuAllocation(gpuAllocation, ParseGpuAllocation(settings.ConcurrentTaggingAllocation));
                    if (_model.EnableCaptioning)
                        MergeGpuAllocation(gpuAllocation, ParseGpuAllocation(settings.ConcurrentCaptioningAllocation));
                    if (_model.EnableEmbedding)
                        MergeGpuAllocation(gpuAllocation, ParseGpuAllocation(settings.ConcurrentEmbeddingAllocation));
                    if (_model.EnableFaceDetection)
                        MergeGpuAllocation(gpuAllocation, ParseGpuAllocation(settings.ConcurrentFaceDetectionAllocation));
                    
                    // Start all enabled services
                    await orchestrator.StartAllAsync(gpuAllocation, Services.Processing.ProcessingMode.Concurrent);
                    
                    // Update individual active states
                    _model.IsTaggingActive = _model.EnableTagging;
                    _model.IsCaptioningActive = _model.EnableCaptioning;
                    _model.IsEmbeddingActive = _model.EnableEmbedding;
                    _model.IsFaceDetectionActive = _model.EnableFaceDetection;
                    
                    var enabledCount = new[] { _model.EnableTagging, _model.EnableCaptioning, _model.EnableEmbedding, _model.EnableFaceDetection }.Count(x => x);
                    ServiceLocator.ToastService?.Toast($"Started {enabledCount} processor(s) (new architecture)", "Background Processing");
                    Logger.Log($"Started unified processing with {enabledCount} processors (new architecture)");
                }
                catch (Exception ex)
                {
                    _model.IsUnifiedProcessingActive = false;
                    Logger.Log($"Failed to start unified processing: {ex.Message}");
                    ServiceLocator.ToastService?.Toast($"Failed to start: {ex.Message}", "Error");
                }
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;
            
            try
            {
                await service.StartSelectedProcessorsAsync(
                    _model.EnableTagging,
                    _model.EnableCaptioning,
                    _model.EnableEmbedding,
                    _model.EnableFaceDetection);
                
                // Update individual active states
                _model.IsTaggingActive = _model.EnableTagging;
                _model.IsCaptioningActive = _model.EnableCaptioning;
                _model.IsEmbeddingActive = _model.EnableEmbedding;
                _model.IsFaceDetectionActive = _model.EnableFaceDetection;
                
                var enabledCount = new[] { _model.EnableTagging, _model.EnableCaptioning, _model.EnableEmbedding, _model.EnableFaceDetection }.Count(x => x);
                ServiceLocator.ToastService?.Toast($"Started {enabledCount} processor(s)", "Background Processing");
                Logger.Log($"Started unified processing with {enabledCount} processors");
            }
            catch (Exception ex)
            {
                _model.IsUnifiedProcessingActive = false;
                Logger.Log($"Failed to start unified processing: {ex.Message}");
                ServiceLocator.ToastService?.Toast($"Failed to start: {ex.Message}", "Error");
            }
        }
        
        private async void PauseUnifiedProcessing()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                if (orchestrator.IsAnyPaused)
                {
                    await orchestrator.ResumeAllAsync();
                    Logger.Log("Resumed all processors (new architecture)");
                    ServiceLocator.ToastService?.Toast("Resumed all processors", "Background Processing");
                }
                else
                {
                    await orchestrator.PauseAllAsync();
                    Logger.Log("Paused all processors (new architecture)");
                    ServiceLocator.ToastService?.Toast("Paused all processors", "Background Processing");
                }
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            if (service.IsAnyProcessorPaused)
            {
                service.ResumeAllProcessors();
                Logger.Log("Resumed all processors");
                ServiceLocator.ToastService?.Toast("Resumed all processors", "Background Processing");
            }
            else
            {
                service.PauseAllProcessors();
                Logger.Log("Paused all processors");
                ServiceLocator.ToastService?.Toast("Paused all processors", "Background Processing");
            }
        }
        
        private async void StopUnifiedProcessing()
        {
            // Check if new processing architecture is enabled
            if (ServiceLocator.Settings?.UseNewProcessingArchitecture == true)
            {
                var orchestrator = ServiceLocator.ProcessingOrchestrator;
                if (orchestrator == null) return;
                
                await orchestrator.StopAllAsync();
                
                _model.IsUnifiedProcessingActive = false;
                _model.IsTaggingActive = false;
                _model.IsCaptioningActive = false;
                _model.IsEmbeddingActive = false;
                _model.IsFaceDetectionActive = false;
                _model.TaggingStatus = "";
                _model.CaptioningStatus = "";
                _model.EmbeddingStatus = "";
                _model.FaceDetectionStatus = "";
                
                Logger.Log("Stopped all processors from UI (new architecture)");
                ServiceLocator.ToastService?.Toast("Stopped all processors", "Background Processing");
                return;
            }
            
            // Legacy path
            var service = ServiceLocator.BackgroundTaggingService;
            if (service == null) return;

            service.StopAllProcessors();
            
            _model.IsUnifiedProcessingActive = false;
            _model.IsTaggingActive = false;
            _model.IsCaptioningActive = false;
            _model.IsEmbeddingActive = false;
            _model.IsFaceDetectionActive = false;
            _model.TaggingStatus = "";
            _model.CaptioningStatus = "";
            _model.EmbeddingStatus = "";
            _model.FaceDetectionStatus = "";
            
            Logger.Log("Stopped all processors from UI");
            ServiceLocator.ToastService?.Toast("Stopped all processors", "Background Processing");
        }
        
        private async void ClearAllQueues()
        {
            var result = MessageBox.Show(
                "Clear ALL processing queues? This will reset all needs_* flags without removing existing data.",
                "Clear All Queues",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;
            
            var service = ServiceLocator.BackgroundTaggingService;
            var dataStore = ServiceLocator.DataStore;
            
            if (service != null)
            {
                await service.ClearTaggingQueue();
                await service.ClearCaptioningQueue();
            }
            
            if (dataStore != null)
            {
                await dataStore.ClearEmbeddingQueue();
                await dataStore.ClearFaceDetectionQueue();
            }
            
            // Refresh queue counts
            await service?.RefreshQueueCountsAsync()!;
            
            Logger.Log("Cleared all processing queues from UI");
            ServiceLocator.ToastService?.Toast("Cleared all queues", "Background Processing");
        }
        
        #endregion

        private void OnFaceDetectionProgressChanged(object? sender, ProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _model.FaceDetectionStatus = $"Faces: {e.Current}/{e.Total} ({e.Percentage:F1}%)";
                _model.IsFaceDetectionActive = true;
                _model.FaceDetectionQueueCount = ServiceLocator.BackgroundFaceDetectionService?.FaceDetectionQueueRemaining ?? 0;
            });
        }

        private void OnFaceDetectionCompleted(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _model.IsFaceDetectionActive = false;
                _model.FaceDetectionStatus = "";
                Logger.Log("Face detection completed");
            });
        }

        private void OnFaceDetectionStatusChanged(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                _model.FaceDetectionStatus = status;
                // Extract queue count from status
                // Format: "Faces: X/Y | Found: Z | A.B img/s | ETA: HH:MM:SS"
                var match = System.Text.RegularExpressions.Regex.Match(status, @"Faces:\s*\d+/(\d+)");
                if (match.Success)
                {
                    var total = int.Parse(match.Groups[1].Value);
                    _model.FaceDetectionQueueCount = ServiceLocator.BackgroundFaceDetectionService?.FaceDetectionQueueRemaining ?? total;
                }
            });
        }

        private void OnQueueCountsChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (ServiceLocator.BackgroundTaggingService != null)
                {
                    var t = ServiceLocator.BackgroundTaggingService.TaggingQueueRemaining;
                    var c = ServiceLocator.BackgroundTaggingService.CaptioningQueueRemaining;
                    var em = ServiceLocator.BackgroundTaggingService.EmbeddingQueueRemaining;
                    Logger.Log($"OnQueueCountsChanged: Setting model T={t}, C={c}, E={em}");
                    _model.TaggingQueueCount = t;
                    _model.CaptioningQueueCount = c;
                    _model.EmbeddingQueueCount = em;
                }
            });
        }

        private void OnFaceDetectionQueueCountChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (ServiceLocator.BackgroundFaceDetectionService != null)
                {
                    _model.FaceDetectionQueueCount = ServiceLocator.BackgroundFaceDetectionService.FaceDetectionQueueRemaining;
                }
            });
        }

        /// <summary>
        /// Subscribe to background service events after SetDataStore creates the services
        /// </summary>
        private void SubscribeToBackgroundServices()
        {
            Logger.Log("Subscribing to background service events...");
            
            // Subscribe to new processing orchestrator events (when enabled)
            if (ServiceLocator.ProcessingOrchestrator != null)
            {
                ServiceLocator.ProcessingOrchestrator.ProgressChanged += OnNewOrchestratorProgressChanged;
                ServiceLocator.ProcessingOrchestrator.StatusChanged += OnNewOrchestratorStatusChanged;
                ServiceLocator.ProcessingOrchestrator.ServiceCompleted += OnNewOrchestratorServiceCompleted;
                ServiceLocator.ProcessingOrchestrator.AllServicesCompleted += OnNewOrchestratorAllCompleted;
            }
            
            // Subscribe to background tagging service events (legacy)
            if (ServiceLocator.BackgroundTaggingService != null)
            {
                ServiceLocator.BackgroundTaggingService.TaggingProgressChanged += OnTaggingProgressChanged;
                ServiceLocator.BackgroundTaggingService.CaptioningProgressChanged += OnCaptioningProgressChanged;
                ServiceLocator.BackgroundTaggingService.EmbeddingProgressChanged += OnEmbeddingProgressChanged;
                ServiceLocator.BackgroundTaggingService.TaggingCompleted += OnTaggingCompleted;
                ServiceLocator.BackgroundTaggingService.CaptioningCompleted += OnCaptioningCompleted;
                ServiceLocator.BackgroundTaggingService.EmbeddingCompleted += OnEmbeddingCompleted;
                ServiceLocator.BackgroundTaggingService.StatusChanged += OnBackgroundStatusChanged;
                ServiceLocator.BackgroundTaggingService.QueueCountsChanged += OnQueueCountsChanged;
            }
            
            // Subscribe to face detection service events
            if (ServiceLocator.BackgroundFaceDetectionService != null)
            {
                ServiceLocator.BackgroundFaceDetectionService.FaceDetectionProgressChanged += OnFaceDetectionProgressChanged;
                ServiceLocator.BackgroundFaceDetectionService.FaceDetectionCompleted += OnFaceDetectionCompleted;
                ServiceLocator.BackgroundFaceDetectionService.StatusChanged += OnFaceDetectionStatusChanged;
                ServiceLocator.BackgroundFaceDetectionService.QueueCountChanged += OnFaceDetectionQueueCountChanged;
            }
            
            // Trigger async refresh of queue counts from database
            // This will fire the events we just subscribed to
            _ = Task.Run(async () =>
            {
                try
                {
                    if (ServiceLocator.BackgroundTaggingService != null)
                        await ServiceLocator.BackgroundTaggingService.RefreshQueueCountsAsync();
                    if (ServiceLocator.BackgroundFaceDetectionService != null)
                        await ServiceLocator.BackgroundFaceDetectionService.RefreshQueueCountAsync();
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error refreshing queue counts: {ex.Message}");
                }
            });
        }
        
        #region New Processing Orchestrator Event Handlers
        
        private void OnNewOrchestratorProgressChanged(object? sender, Services.Processing.ProcessingProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var etaStr = e.EstimatedTimeRemaining.HasValue 
                    ? $" | ETA: {e.EstimatedTimeRemaining.Value:hh\\:mm\\:ss}" 
                    : "";
                    
                switch (e.ProcessingType)
                {
                    case Services.Processing.ProcessingType.Tagging:
                        _model.TaggingStatus = $"Tagging: {e.Current}/{e.Total} ({e.Percentage:F1}%){etaStr}";
                        _model.TaggingQueueCount = e.Remaining;
                        break;
                    case Services.Processing.ProcessingType.Captioning:
                        _model.CaptioningStatus = $"Captioning: {e.Current}/{e.Total} ({e.Percentage:F1}%){etaStr}";
                        _model.CaptioningQueueCount = e.Remaining;
                        break;
                    case Services.Processing.ProcessingType.Embedding:
                        _model.EmbeddingStatus = $"Embedding: {e.Current}/{e.Total} ({e.Percentage:F1}%){etaStr}";
                        _model.EmbeddingQueueCount = e.Remaining;
                        break;
                    case Services.Processing.ProcessingType.FaceDetection:
                        _model.FaceDetectionStatus = $"Faces: {e.Current}/{e.Total} ({e.Percentage:F1}%){etaStr}";
                        _model.FaceDetectionQueueCount = e.Remaining;
                        break;
                }
            });
        }
        
        private void OnNewOrchestratorStatusChanged(object? sender, Services.Processing.ProcessingStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                switch (e.ProcessingType)
                {
                    case Services.Processing.ProcessingType.Tagging:
                        _model.TaggingStatus = e.Status;
                        _model.IsTaggingActive = e.IsRunning;
                        break;
                    case Services.Processing.ProcessingType.Captioning:
                        _model.CaptioningStatus = e.Status;
                        _model.IsCaptioningActive = e.IsRunning;
                        break;
                    case Services.Processing.ProcessingType.Embedding:
                        _model.EmbeddingStatus = e.Status;
                        _model.IsEmbeddingActive = e.IsRunning;
                        break;
                    case Services.Processing.ProcessingType.FaceDetection:
                        _model.FaceDetectionStatus = e.Status;
                        _model.IsFaceDetectionActive = e.IsRunning;
                        break;
                }
            });
        }
        
        private void OnNewOrchestratorServiceCompleted(object? sender, Services.Processing.ProcessingType type)
        {
            Dispatcher.Invoke(() =>
            {
                switch (type)
                {
                    case Services.Processing.ProcessingType.Tagging:
                        _model.IsTaggingActive = false;
                        _model.TaggingStatus = "";
                        break;
                    case Services.Processing.ProcessingType.Captioning:
                        _model.IsCaptioningActive = false;
                        _model.CaptioningStatus = "";
                        break;
                    case Services.Processing.ProcessingType.Embedding:
                        _model.IsEmbeddingActive = false;
                        _model.EmbeddingStatus = "";
                        break;
                    case Services.Processing.ProcessingType.FaceDetection:
                        _model.IsFaceDetectionActive = false;
                        _model.FaceDetectionStatus = "";
                        break;
                }
            });
        }
        
        private void OnNewOrchestratorAllCompleted(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _model.IsUnifiedProcessingActive = false;
                ServiceLocator.ToastService?.Toast("All processing complete", "Background Processing");
            });
        }
        
        #endregion

        private async void DatabaseProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DatabaseProfileSwitcher?.SelectedItem is DatabaseProfile profile)
            {
                if (profile.Name == _settings.ActiveDatabaseProfile)
                    return; // No change

                var result = MessageBox.Show(this,
                    $"Switch to database profile '{profile.Name}'?\n\n" +
                    $"This will close the current connection and reconnect to:\n{profile.ConnectionString.Split(';').FirstOrDefault()}\n\n" +
                    $"The application will need to restart.",
                    "Switch Database Profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _settings.ActiveDatabaseProfile = profile.Name;
                    _configuration.Save(_settings);

                    // Restart application
                    System.Diagnostics.Process.Start(Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);
                    Application.Current.Shutdown();
                }
                else
                {
                    // Revert selection
                    DatabaseProfileSwitcher.SelectionChanged -= DatabaseProfile_SelectionChanged;
                    DatabaseProfileSwitcher.SelectedValue = _settings.ActiveDatabaseProfile;
                    DatabaseProfileSwitcher.SelectionChanged += DatabaseProfile_SelectionChanged;
                }
            }
        }

        /// <summary>
        /// Refresh the database profile switcher dropdown (call after profiles are added/edited/deleted)
        /// </summary>
        public void RefreshDatabaseProfileSwitcher()
        {
            if (DatabaseProfileSwitcher != null && _settings != null)
            {
                var profiles = _settings.DatabaseProfiles ?? new List<DatabaseProfile>();
                DatabaseProfileSwitcher.SelectionChanged -= DatabaseProfile_SelectionChanged;
                DatabaseProfileSwitcher.ItemsSource = null; // Clear first to force refresh
                DatabaseProfileSwitcher.ItemsSource = profiles;
                DatabaseProfileSwitcher.SelectedValue = _settings.ActiveDatabaseProfile;
                DatabaseProfileSwitcher.SelectionChanged += DatabaseProfile_SelectionChanged;
            }
        }

        /// <summary>
        /// Parse GPU allocation string (e.g., "1,0" means 1 worker on GPU 0, 0 workers on GPU 1)
        /// Returns dictionary of GPU ID -> worker count
        /// </summary>
        private Dictionary<int, int> ParseGpuAllocation(string allocation)
        {
            var result = new Dictionary<int, int>();
            if (string.IsNullOrEmpty(allocation)) return result;
            
            var parts = allocation.Split(',');
            for (int gpuId = 0; gpuId < parts.Length; gpuId++)
            {
                if (int.TryParse(parts[gpuId].Trim(), out var workerCount) && workerCount > 0)
                {
                    result[gpuId] = workerCount;
                }
            }
            return result;
        }

        /// <summary>
        /// Merge GPU allocations - takes the maximum worker count per GPU
        /// </summary>
        private void MergeGpuAllocation(Dictionary<int, int> target, Dictionary<int, int> source)
        {
            foreach (var kvp in source)
            {
                if (target.TryGetValue(kvp.Key, out var existing))
                    target[kvp.Key] = Math.Max(existing, kvp.Value);
                else
                    target[kvp.Key] = kvp.Value;
            }
        }

        #endregion
    }
}