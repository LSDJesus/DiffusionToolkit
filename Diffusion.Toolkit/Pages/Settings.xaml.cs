using Diffusion.Common;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Themes;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Localization;
using Diffusion.Toolkit.Services;
using System.Net.Http;
using System.Net.Http.Json;
using Diffusion.Captioning.Services;

namespace Diffusion.Toolkit.Pages
{

    /// <summary>
    /// Interaction logic for Settings.xaml
    /// </summary>
    public partial class Settings : NavigationPage
    {
        private SettingsModel _model = new SettingsModel();
        public Configuration.Settings _settings => ServiceLocator.Settings!;

        private List<FolderChange> _folderChanges = new List<FolderChange>();

        private PostgreSQLDataStore _dataStore => ServiceLocator.DataStore!;

        private string GetLocalizedText(string key)
        {
            return (string?)JsonLocalizationProvider.Instance.GetLocalizedObject(key, null!, CultureInfo.InvariantCulture) ?? key;
        }

        public Settings(Window window) : base("settings")
        {
            _window = window;
            InitializeComponent();

            _model.PropertyChanged += ModelOnPropertyChanged;

            InitializeSettings();
            LoadCultures();

            _model.SetPristine();

            ServiceLocator.NavigatorService.OnNavigate += (sender, args) =>
            {
                InitializeSettings();
                if (args.TargetUri.Path.ToLower() == "settings" && args.TargetUri.Fragment != null && args.TargetUri.Fragment.ToLowerInvariant() == "externalapplications")
                {
                    ExternalApplicationsTab.IsSelected = true;
                }
            };

            DataContext = _model;
        }

        private void InitializeSettings()
        {
            _model.CheckForUpdatesOnStartup = _settings.CheckForUpdatesOnStartup;
            _model.ScanForNewImagesOnStartup = _settings.ScanForNewImagesOnStartup;

            _model.AutoRefresh = _settings.AutoRefresh;

            _model.ModelRootPath = _settings.ModelRootPath;
            _model.FileExtensions = _settings.FileExtensions;
            _model.SoftwareOnly = _settings.RenderMode == RenderMode.SoftwareOnly;

            _model.PageSize = _settings.PageSize;
            _model.UseBuiltInViewer = _settings.UseBuiltInViewer.GetValueOrDefault(true);
            _model.OpenInFullScreen = _settings.OpenInFullScreen.GetValueOrDefault(true);
            _model.UseSystemDefault = _settings.UseSystemDefault.GetValueOrDefault(false);
            _model.UseCustomViewer = _settings.UseCustomViewer.GetValueOrDefault(false);
            _model.CustomCommandLine = _settings.CustomCommandLine;
            _model.CustomCommandLineArgs = _settings.CustomCommandLineArgs;
            _model.SlideShowDelay = _settings.SlideShowDelay;
            _model.ScrollNavigation = _settings.ScrollNavigation;
            _model.AdvanceOnTag = _settings.AutoAdvance;
            _model.ShowFilenames = _settings.ShowFilenames;
            _model.PermanentlyDelete = _settings.PermanentlyDelete;
            _model.ConfirmDeletion = _settings.ConfirmDeletion;
            
            // Initialize database profiles
            _model.DatabaseProfiles = new ObservableCollection<DatabaseProfile>(_settings.DatabaseProfiles);

            _model.AutoTagNSFW = _settings.AutoTagNSFW;
            _model.NSFWTags = string.Join("\r\n", _settings.NSFWTags);

            _model.HashCache = _settings.HashCache;
            _model.PortableMode = _settings.PortableMode;

            _model.StoreMetadata = _settings.StoreMetadata;
            _model.StoreWorkflow = _settings.StoreWorkflow;
            _model.ScanUnavailable = _settings.ScanUnavailable;

            _model.ExternalApplications = new ObservableCollection<ExternalApplicationModel>(_settings.ExternalApplications?.Select(d => new ExternalApplicationModel()
            {
                CommandLineArgs = d.CommandLineArgs,
                Name = d.Name,
                Path = d.Path
            }) ?? new List<ExternalApplicationModel>());

            _model.Theme = _settings.Theme;
            _model.Culture = _settings.Culture ?? string.Empty;
            
            // JoyTag settings
            _model.EnableJoyTag = _settings.EnableJoyTag;
            _model.JoyTagThreshold = _settings.JoyTagThreshold;
            _model.JoyTagModelPath = _settings.JoyTagModelPath;
            _model.JoyTagTagsPath = _settings.JoyTagTagsPath;
            
            // WD tagger settings
            _model.EnableWDTag = _settings.EnableWDTag;
            _model.WDTagThreshold = _settings.WDTagThreshold;
            _model.WDTagModelPath = _settings.WDTagModelPath;
            _model.WDTagTagsPath = _settings.WDTagTagsPath;
            
            // JoyCaption settings
            _model.JoyCaptionModelPath = _settings.JoyCaptionModelPath;
            _model.JoyCaptionMMProjPath = _settings.JoyCaptionMMProjPath;
            _model.JoyCaptionDefaultPrompt = _settings.JoyCaptionDefaultPrompt;
            _model.CaptionHandlingMode = _settings.CaptionHandlingMode;
            _model.CaptionProvider = _settings.CaptionProvider;
            _model.ExternalCaptionBaseUrl = _settings.ExternalCaptionBaseUrl;
            _model.ExternalCaptionModel = _settings.ExternalCaptionModel;
            _model.ExternalCaptionApiKey = _settings.ExternalCaptionApiKey;
            
            // Tagging/Captioning output settings
            _model.StoreTagConfidence = _settings.StoreTagConfidence;
            _model.CreateMetadataBackup = _settings.CreateMetadataBackup;
            _model.WriteTagsToMetadata = _settings.WriteTagsToMetadata;
            _model.WriteCaptionsToMetadata = _settings.WriteCaptionsToMetadata;
            _model.WriteGenerationParamsToMetadata = _settings.WriteGenerationParamsToMetadata;
            
            // Global GPU settings
            _model.GpuDevices = _settings.GpuDevices;
            _model.GpuVramCapacity = _settings.GpuVramCapacity;
            _model.MaxVramUsagePercent = _settings.MaxVramUsagePercent;
            _model.UseNewProcessingArchitecture = _settings.UseNewProcessingArchitecture;
            _model.EnableDynamicVramAllocation = _settings.EnableDynamicVramAllocation;
            _model.DefaultOnnxWorkersPerGpu = _settings.DefaultOnnxWorkersPerGpu;
            
            // GPU Model Allocation - Concurrent mode
            _model.ConcurrentCaptioningAllocation = _settings.ConcurrentCaptioningAllocation;
            _model.ConcurrentTaggingAllocation = _settings.ConcurrentTaggingAllocation;
            _model.ConcurrentEmbeddingAllocation = _settings.ConcurrentEmbeddingAllocation;
            _model.ConcurrentFaceDetectionAllocation = _settings.ConcurrentFaceDetectionAllocation;
            
            // GPU Model Allocation - Solo mode
            _model.SoloCaptioningAllocation = _settings.SoloCaptioningAllocation;
            _model.SoloTaggingAllocation = _settings.SoloTaggingAllocation;
            _model.SoloEmbeddingAllocation = _settings.SoloEmbeddingAllocation;
            _model.SoloFaceDetectionAllocation = _settings.SoloFaceDetectionAllocation;
            
            // Processing skip settings
            _model.SkipAlreadyTaggedImages = _settings.SkipAlreadyTaggedImages;
            _model.SkipAlreadyCaptionedImages = _settings.SkipAlreadyCaptionedImages;
            _model.SkipAlreadyEmbeddedImages = _settings.SkipAlreadyEmbeddedImages;
            _model.SkipAlreadyProcessedFaces = _settings.SkipAlreadyProcessedFaces;
            
            // Initialize schema selector
            InitializeSchemaSelector();
            
            // Load connection info
            try
            {
                var connection = _dataStore.GetConnection();
                _model.Host = connection.Host ?? "";
                _model.Port = connection.Port;
                _model.Database = connection.Database ?? "";
                _model.Username = connection.Username ?? "";
                _model.Status = _dataStore.GetStatus();
            }
            catch
            {
                _model.Status = "Not connected";
            }
            
            _model.SetPristine();
        }
        
        private async void InitializeSchemaSelector()
        {
            var comboBox = FindName("SchemaComboBox") as ComboBox;
            if (comboBox == null) return;
            
            // Clear existing items
            comboBox.Items.Clear();
            
            try
            {
                // Load all application schemas from database
                var schemas = await ((Database.PostgreSQL.PostgreSQLDataStore)_dataStore).GetApplicationSchemasAsync();
                
                foreach (var schema in schemas)
                {
                    var displayName = char.ToUpper(schema[0]) + schema.Substring(1);
                    var item = new ComboBoxItem { Content = displayName, Tag = schema };
                    comboBox.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading schemas: {ex.Message}");
                // Fallback to just public if query fails
                comboBox.Items.Add(new ComboBoxItem { Content = "Public", Tag = "public" });
            }
            
            // Set current schema selection
            var currentSchema = _settings.DatabaseSchema ?? "public";
            _model.DatabaseSchema = currentSchema;
            
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag as string == currentSchema)
                {
                    comboBox.SelectedItem = item;
                    break;
                }
            }
        }
        
        private void RefreshConnectionInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var connection = _dataStore.GetConnection();
                _model.Host = connection.Host ?? "";
                _model.Port = connection.Port;
                _model.Database = connection.Database ?? "";
                _model.Username = connection.Username ?? "";
                _model.Status = _dataStore.GetStatus();
            }
            catch (Exception ex)
            {
                _model.Status = $"Error: {ex.Message}";
            }
        }

        private void LoadCultures()
        {
            var cultures = new List<Langauge>
            {
                new ("Default", "default"),
            };

            _model.ThemeOptions = new List<OptionValue>()
            {
                new (GetLocalizedText("Settings.Themes.Theme.System") ?? "System", "System"),
                new (GetLocalizedText("Settings.Themes.Theme.Light") ?? "Light", "Light"),
                new (GetLocalizedText("Settings.Themes.Theme.Dark") ?? "Dark", "Dark")
            };

            try
            {
                var configPath = Path.Combine(AppInfo.AppDir, "Localization", "languages.json");

                var langs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath));

                if (langs != null)
                {
                    foreach (var (name, culture) in langs)
                    {
                        cultures.Add(new Langauge(name, culture));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading languages.json: {ex.Message}");
            }

            _model.Cultures = new ObservableCollection<Langauge>(cultures);

        }


        private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsModel.Theme):
                    ThemeManager.ChangeTheme(_model.Theme);
                    break;
                case nameof(SettingsModel.Culture):
                    _settings.Culture = _model.Culture;
                    break;
                case nameof(SettingsModel.SlideShowDelay):
                    {
                        if (_model.SlideShowDelay < 1)
                        {
                            _model.SlideShowDelay = 1;
                        }
                        if (_model.SlideShowDelay > 100)
                        {
                            _model.SlideShowDelay = 100;
                        }

                        break;
                    }
            }
        }

        private Window _window;
        
        private void BrowseModelPath_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.IsFolderPicker = true;
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.ModelRootPath = dialog.FileName;
            }
        }
        
        private async void ScanModels_OnClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_model.ModelRootPath))
            {
                MessageBox.Show(this._window, "Please set a Models Root path first.", "Scan Models", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            if (!Directory.Exists(_model.ModelRootPath))
            {
                MessageBox.Show(this._window, $"The Models Root path does not exist:\n{_model.ModelRootPath}", 
                    "Scan Models", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Save the path first
            _settings.ModelRootPath = _model.ModelRootPath;
            
            // Register default model folders based on the root path
            await RegisterModelFoldersAsync(_model.ModelRootPath);
            
            // Scan all registered folders
            await ServiceLocator.ModelResourceService.ScanAllFoldersAsync(CancellationToken.None);
            
            MessageBox.Show(this._window, "Model scan complete. Check the log for details.", "Scan Models", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private async Task RegisterModelFoldersAsync(string modelsRoot)
        {
            var subfolderTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "loras", ResourceTypes.Lora },
                { "embeddings", ResourceTypes.Embedding },
                { "checkpoints", ResourceTypes.Checkpoint },
                { "Stable-diffusion", ResourceTypes.Checkpoint }, // A1111 style
                { "diffusion_models", ResourceTypes.DiffusionModel },
                { "unet", ResourceTypes.Unet },
                { "upscale_models", ResourceTypes.Upscaler },
                { "vae", ResourceTypes.Vae },
                { "controlnet", ResourceTypes.ControlNet },
                { "clip", ResourceTypes.Clip },
            };
            
            foreach (var (subdir, resourceType) in subfolderTypes)
            {
                var path = Path.Combine(modelsRoot, subdir);
                if (Directory.Exists(path))
                {
                    var folder = new ModelFolder
                    {
                        Path = path,
                        ResourceType = resourceType,
                        Recursive = true,
                        Enabled = true
                    };
                    await ServiceLocator.DataStore.UpsertModelFolderAsync(folder);
                    Logger.Log($"Registered model folder: {path} ({resourceType})");
                }
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void Backup_DB(object sender, RoutedEventArgs e)
        {
            // PostgreSQL backups require pg_dump command-line tool
            var result = MessageBox.Show(this._window,
                "PostgreSQL backups must be performed using pg_dump command-line tool.\n\n" +
                "Example: pg_dump -h localhost -p 5436 -U diffusion -d diffusion_images -F c -f backup.dump\n\n" +
                "See documentation for details.",
                "Backup Database", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void Restore_DB(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(this._window,
                $"PostgreSQL backups must be performed using pg_restore command-line tool.\n\n" +
                $"Example: pg_restore -h localhost -p 5436 -U diffusion -d diffusion_images -c -F c backup.dump\n\n" +
                $"This will clear existing data before restoring.\n\nSee documentation for details.",
                "Restore Database", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BrowseHashCache_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.Filters.Add(new CommonFileDialogFilter("A1111 cache", "*.json"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.HashCache = dialog.FileName;
            }
        }

        private void BrowseCustomViewer_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.DefaultFileName = _model.CustomCommandLine;
            dialog.Filters.Add(new CommonFileDialogFilter("Executable files", "*.exe;*.bat;*.cmd"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.CustomCommandLine = dialog.FileName;
            }
        }


        private void BrowseExternalApplicationPath_OnClick(object sender, RoutedEventArgs e)
        {
            //var button = (Button)sender;
            //var dc = (ExternalApplication)button.DataContext;
            var app = _model.SelectedApplication;

            if (app == null) return;

            using var dialog = new CommonOpenFileDialog();
            dialog.DefaultFileName = app.Path;
            dialog.Filters.Add(new CommonFileDialogFilter("Executable files", "*.exe;*.bat;*.cmd"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                app.Path = dialog.FileName;
            }
        }

        private void RemoveExternalApplication_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedApplication != null)
            {
                var result = MessageBox.Show(this._window,
                $"Are you sure you want to remove \"{_model.SelectedApplication.Name}\"?",
                "Remove Application", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No);

                if (result == MessageBoxResult.Yes)
                {
                    _model.ExternalApplications.Remove(_model.SelectedApplication);
                }
            }
        }

        private void AddExternalApplication_OnClick(object sender, RoutedEventArgs e)
        {
            var newApplication = new ExternalApplicationModel()
            {
                Name = "Application",
                Path = ""
            };
            _model.ExternalApplications.Add(newApplication);
            _model.SelectedApplication = newApplication;
        }

        private void MoveExternalApplicationUp_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedApplication != null)
            {
                var index = _model.ExternalApplications.IndexOf(_model.SelectedApplication);

                if (index > 0)
                {
                    _model.ExternalApplications.Move(index, index - 1);
                }
            }
        }

        private void MoveExternalApplicationDown_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedApplication != null)
            {
                var index = _model.ExternalApplications.IndexOf(_model.SelectedApplication);

                if (index < _model.ExternalApplications.Count)
                {
                    _model.ExternalApplications.Move(index, index + 1);
                }
            }
        }

        private void ApplyChanges_OnClick(object sender, RoutedEventArgs e)
        {
            ApplySettings();
            
            _model.SetPristine();
        }

        private void RevertChanges_OnClick(object sender, RoutedEventArgs e)
        {
            InitializeSettings();
        }


        public void ApplySettings()
        {
            if (_model.IsDirty)
            {
                _settings.SetPristine();

                _settings.ModelRootPath = _model.ModelRootPath;
                _settings.FileExtensions = _model.FileExtensions;
                _settings.Theme = _model.Theme;
                _settings.PageSize = _model.PageSize;
                _settings.AutoRefresh = _model.AutoRefresh;

                _settings.CheckForUpdatesOnStartup = _model.CheckForUpdatesOnStartup;
                _settings.ScanForNewImagesOnStartup = _model.ScanForNewImagesOnStartup;
                _settings.AutoTagNSFW = _model.AutoTagNSFW;
                _settings.NSFWTags = _model.NSFWTags.Split("\r\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _settings.HashCache = _model.HashCache;
                _settings.PortableMode = _model.PortableMode;
                _settings.RenderMode = _model.SoftwareOnly ? RenderMode.SoftwareOnly : RenderMode.Default;
                
                _settings.UseBuiltInViewer = _model.UseBuiltInViewer;
                _settings.OpenInFullScreen = _model.OpenInFullScreen;
                _settings.UseSystemDefault = _model.UseSystemDefault;
                _settings.UseCustomViewer = _model.UseCustomViewer;
                _settings.CustomCommandLine = _model.CustomCommandLine;
                _settings.CustomCommandLineArgs = _model.CustomCommandLineArgs;
                _settings.SlideShowDelay = _model.SlideShowDelay;
                _settings.ScrollNavigation = _model.ScrollNavigation;
                _settings.AutoAdvance = _model.AdvanceOnTag;
                _settings.ShowFilenames = _model.ShowFilenames;
                _settings.PermanentlyDelete = _model.PermanentlyDelete;
                _settings.ConfirmDeletion = _model.ConfirmDeletion;

                _settings.StoreMetadata = _model.StoreMetadata;
                _settings.StoreWorkflow = _model.StoreWorkflow;
                _settings.ScanUnavailable = _model.ScanUnavailable;
                _settings.ExternalApplications = _model.ExternalApplications.Select(d => new ExternalApplication()
                {
                    CommandLineArgs = d.CommandLineArgs,
                    Name = d.Name,
                    Path = d.Path
                }).ToList();

                _settings.Culture = _model.Culture;
                
                // JoyTag settings
                _settings.EnableJoyTag = _model.EnableJoyTag;
                _settings.JoyTagThreshold = _model.JoyTagThreshold;
                _settings.JoyTagModelPath = _model.JoyTagModelPath;
                _settings.JoyTagTagsPath = _model.JoyTagTagsPath;
                
                // WD tagger settings
                _settings.EnableWDTag = _model.EnableWDTag;
                _settings.WDTagThreshold = _model.WDTagThreshold;
                _settings.WDTagModelPath = _model.WDTagModelPath;
                _settings.WDTagTagsPath = _model.WDTagTagsPath;
                
                // JoyCaption settings
                _settings.JoyCaptionModelPath = _model.JoyCaptionModelPath;
                _settings.JoyCaptionMMProjPath = _model.JoyCaptionMMProjPath;
                _settings.JoyCaptionDefaultPrompt = _model.JoyCaptionDefaultPrompt;
                _settings.CaptionHandlingMode = _model.CaptionHandlingMode;
                _settings.CaptionProvider = _model.CaptionProvider;
                _settings.ExternalCaptionBaseUrl = _model.ExternalCaptionBaseUrl;
                _settings.ExternalCaptionModel = _model.ExternalCaptionModel;
                _settings.ExternalCaptionApiKey = _model.ExternalCaptionApiKey;
                
                // Tagging/Captioning output settings
                _settings.StoreTagConfidence = _model.StoreTagConfidence;
                _settings.CreateMetadataBackup = _model.CreateMetadataBackup;
                _settings.WriteTagsToMetadata = _model.WriteTagsToMetadata;
                _settings.WriteCaptionsToMetadata = _model.WriteCaptionsToMetadata;
                _settings.WriteGenerationParamsToMetadata = _model.WriteGenerationParamsToMetadata;
                
                // Global GPU settings
                _settings.GpuDevices = _model.GpuDevices;
                _settings.GpuVramCapacity = _model.GpuVramCapacity;
                _settings.MaxVramUsagePercent = _model.MaxVramUsagePercent;
                _settings.UseNewProcessingArchitecture = _model.UseNewProcessingArchitecture;
                _settings.EnableDynamicVramAllocation = _model.EnableDynamicVramAllocation;
                _settings.DefaultOnnxWorkersPerGpu = _model.DefaultOnnxWorkersPerGpu;
                
                // GPU Model Allocation - Concurrent mode
                _settings.ConcurrentCaptioningAllocation = _model.ConcurrentCaptioningAllocation;
                _settings.ConcurrentTaggingAllocation = _model.ConcurrentTaggingAllocation;
                _settings.ConcurrentEmbeddingAllocation = _model.ConcurrentEmbeddingAllocation;
                _settings.ConcurrentFaceDetectionAllocation = _model.ConcurrentFaceDetectionAllocation;
                
                // GPU Model Allocation - Solo mode
                _settings.SoloCaptioningAllocation = _model.SoloCaptioningAllocation;
                _settings.SoloTaggingAllocation = _model.SoloTaggingAllocation;
                _settings.SoloEmbeddingAllocation = _model.SoloEmbeddingAllocation;
                _settings.SoloFaceDetectionAllocation = _model.SoloFaceDetectionAllocation;
                
                // Processing skip settings
                _settings.SkipAlreadyTaggedImages = _model.SkipAlreadyTaggedImages;
                _settings.SkipAlreadyCaptionedImages = _model.SkipAlreadyCaptionedImages;
                _settings.SkipAlreadyEmbeddedImages = _model.SkipAlreadyEmbeddedImages;
                _settings.SkipAlreadyProcessedFaces = _model.SkipAlreadyProcessedFaces;
                
                // Re-initialize GPU orchestrator with new settings
                ServiceLocator.GpuOrchestrator?.Initialize(
                    _model.GpuDevices,
                    _model.GpuVramCapacity,
                    _model.MaxVramUsagePercent / 100.0);

                var connection = _dataStore.GetConnection();
                _model.Host = connection.Host ?? "";
                _model.Port = connection.Port;
                _model.Database = connection.Database ?? "";
                _model.Username = connection.Username ?? "";
                _model.Status = _dataStore.GetStatus();

                _model.SetPristine();
            }
        }

        private void SchemaComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = (ComboBox)sender;
            if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is string schema)
            {
                // Only show toast if schema is actually changing (not same as current)
                var oldSchema = _settings.DatabaseSchema;
                
                _settings.DatabaseSchema = schema;
                _dataStore.CurrentSchema = schema;
                
                // Only show notification and refresh if schema actually changed
                if (oldSchema != schema)
                {
                    // Refresh the search results to show images from new schema
                    ServiceLocator.SearchService?.RefreshResults();
                    
                    ServiceLocator.ToastService?.Toast($"Switched to '{item.Content}' collection", "Schema Changed");
                }
            }
        }

        private async void CreateSchema_Click(object sender, RoutedEventArgs e)
        {
            var schemaName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter schema name (lowercase, no spaces):",
                "Create New Schema",
                "");
            
            if (string.IsNullOrWhiteSpace(schemaName))
                return;
                
            schemaName = schemaName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
            
            if (schemaName == "public" || schemaName == "pg_catalog" || schemaName == "information_schema")
            {
                MessageBox.Show("Cannot use reserved schema name.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            try
            {
                Logger.Log($"Creating schema: {schemaName}");
                
                // Create the new schema with all tables
                await _dataStore.CreateSchemaWithTables(schemaName);
                Logger.Log($"Schema {schemaName} created and initialized");
                
                // Add new schema to ComboBox
                var comboBox = FindName("SchemaComboBox") as ComboBox;
                if (comboBox != null)
                {
                    var newItem = new ComboBoxItem { Content = char.ToUpper(schemaName[0]) + schemaName.Substring(1), Tag = schemaName };
                    comboBox.Items.Add(newItem);
                    comboBox.SelectedItem = newItem;
                }
                
                MessageBox.Show($"Schema '{schemaName}' created successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create schema: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Logger.Log($"Schema creation error: {ex}");
            }
        }

        private async void TestCaptionApi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_model.CaptionProvider != CaptionProviderType.OpenAICompatible)
                {
                    MessageBox.Show(this._window, "Select 'OpenAI-Compatible HTTP API' as Provider.", "Test Connection", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (string.IsNullOrWhiteSpace(_model.ExternalCaptionBaseUrl))
                {
                    MessageBox.Show(this._window, "Please set Base URL.", "Test Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var client = new HttpClient();
                client.BaseAddress = new Uri(_model.ExternalCaptionBaseUrl.TrimEnd('/'));
                if (!string.IsNullOrWhiteSpace(_model.ExternalCaptionApiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _model.ExternalCaptionApiKey);
                }

                // Try fetch models list first
                var ok = false;
                string? modelList = null;
                try
                {
                    var resp = await client.GetAsync("/v1/models");
                    if (!resp.IsSuccessStatusCode)
                    {
                        resp = await client.GetAsync("/models");
                    }
                    if (resp.IsSuccessStatusCode)
                    {
                        ok = true;
                        var content = await resp.Content.ReadAsStringAsync();
                        // Parse model names if available
                        try
                        {
                            var modelsResponse = JsonSerializer.Deserialize<ModelsResponse>(content);
                            if (modelsResponse?.data != null && modelsResponse.data.Length > 0)
                            {
                                // Populate the dropdown with available models
                                _model.AvailableModels.Clear();
                                foreach (var model in modelsResponse.data)
                                {
                                    _model.AvailableModels.Add(model.id);
                                }
                                
                                modelList = string.Join(", ", modelsResponse.data.Take(5).Select(m => m.id));
                                if (modelsResponse.data.Length > 5)
                                {
                                    modelList += $" (and {modelsResponse.data.Length - 5} more)";
                                }
                            }
                        }
                        catch { /* ignore parse errors */ }
                    }
                }
                catch
                {
                    // ignore
                }

                if (!ok)
                {
                    // Fallback: ping /chat/completions with minimal payload
                    var testModel = string.IsNullOrWhiteSpace(_model.ExternalCaptionModel) ? "gpt-4" : _model.ExternalCaptionModel;
                    var payload = new
                    {
                        model = testModel,
                        messages = new[] { new { role = "user", content = "ping" } },
                        max_tokens = 1
                    };
                    try
                    {
                        var resp2 = await client.PostAsJsonAsync("/v1/chat/completions", payload);
                        if (!resp2.IsSuccessStatusCode)
                        {
                            resp2 = await client.PostAsJsonAsync("/chat/completions", payload);
                        }
                        ok = resp2.IsSuccessStatusCode;
                    }
                    catch { /* ignore */ }
                }

                var message = ok ? "Connection OK" : "Connection failed";
                if (ok && modelList != null)
                {
                    message += $"\n\nAvailable models:\n{modelList}";
                }
                
                MessageBox.Show(this._window, message, "Test Connection",
                    MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this._window, $"Connection error: {ex.Message}", "Test Connection", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Helper class for parsing /models response
        private class ModelsResponse
        {
            public ModelInfo[]? data { get; set; }
        }

        private class ModelInfo
        {
            public string id { get; set; } = "";
        }

        private async void TestCaptionApiWithImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_model.CaptionProvider != CaptionProviderType.OpenAICompatible)
                {
                    MessageBox.Show(this._window, "Select 'OpenAI-Compatible HTTP API' as Provider.", "Sample Caption", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (string.IsNullOrWhiteSpace(_model.ExternalCaptionBaseUrl))
                {
                    MessageBox.Show(this._window, "Please set Base URL.", "Sample Caption", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Pick a sample image
                using var dialog = new CommonOpenFileDialog();
                dialog.Title = "Select an image to caption";
                dialog.Filters.Add(new CommonFileDialogFilter("Images", "*.png;*.jpg;*.jpeg;*.webp"));
                if (dialog.ShowDialog(this._window) != CommonFileDialogResult.Ok)
                    return;

                var imagePath = dialog.FileName;
                var promptTag = _model.JoyCaptionDefaultPrompt ?? "detailed";
                var promptText = promptTag switch
                {
                    "detailed" => JoyCaptionService.PROMPT_DETAILED,
                    "short" => JoyCaptionService.PROMPT_SHORT,
                    "technical" => JoyCaptionService.PROMPT_TECHNICAL,
                    "precise_colors" => JoyCaptionService.PROMPT_PRECISE_COLORS,
                    "character" => JoyCaptionService.PROMPT_CHARACTER_FOCUS,
                    "scene" => JoyCaptionService.PROMPT_SCENE_FOCUS,
                    "artistic" => JoyCaptionService.PROMPT_ARTISTIC_STYLE,
                    "booru" => JoyCaptionService.PROMPT_BOORU_TAGS,
                    _ => JoyCaptionService.PROMPT_DETAILED
                };

                // Temporarily apply current UI values to settings for service to use
                var oldProvider = _settings.CaptionProvider;
                var oldBaseUrl = _settings.ExternalCaptionBaseUrl;
                var oldModel = _settings.ExternalCaptionModel;
                var oldApiKey = _settings.ExternalCaptionApiKey;
                
                _settings.CaptionProvider = _model.CaptionProvider;
                _settings.ExternalCaptionBaseUrl = _model.ExternalCaptionBaseUrl;
                _settings.ExternalCaptionModel = _model.ExternalCaptionModel;
                _settings.ExternalCaptionApiKey = _model.ExternalCaptionApiKey;

                try
                {
                    var service = ServiceLocator.CaptionService;
                    if (service == null)
                    {
                        MessageBox.Show(this._window, "Caption service is not available.", "Sample Caption", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var result = await service.CaptionImageAsync(imagePath, promptText, CancellationToken.None);
                    MessageBox.Show(this._window, result.Caption ?? "(empty)", "Caption Result", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    // Restore original settings
                    _settings.CaptionProvider = oldProvider;
                    _settings.ExternalCaptionBaseUrl = oldBaseUrl;
                    _settings.ExternalCaptionModel = oldModel;
                    _settings.ExternalCaptionApiKey = oldApiKey;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this._window, $"Caption error: {ex.Message}", "Sample Caption", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Database Profile Management
        
        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Profile switching happens on Apply Changes or via quick switcher
        }

        private void AddProfile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new DatabaseProfileDialog(null, _window);
            if (dialog.ShowDialog() == true && dialog.Profile != null)
            {
                var profiles = _settings.DatabaseProfiles.ToList();
                profiles.Add(dialog.Profile);
                _settings.DatabaseProfiles = profiles;
                _model.DatabaseProfiles = new ObservableCollection<DatabaseProfile>(profiles);
                _settings.ActiveDatabaseProfile = dialog.Profile.Name;
                _model.SetDirty();
                
                // Refresh the main window's profile switcher dropdown
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.RefreshDatabaseProfileSwitcher();
                }
            }
        }

        private void EditProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileComboBox.SelectedItem is DatabaseProfile profile)
            {
                var dialog = new DatabaseProfileDialog(profile, _window);
                if (dialog.ShowDialog() == true && dialog.Profile != null)
                {
                    var profiles = _settings.DatabaseProfiles.ToList();
                    var index = profiles.FindIndex(p => p.Name == profile.Name);
                    if (index >= 0)
                    {
                        profiles[index] = dialog.Profile;
                        _settings.DatabaseProfiles = profiles;
                        _model.DatabaseProfiles = new ObservableCollection<DatabaseProfile>(profiles);
                        _model.SetDirty();
                        
                        // Refresh the main window's profile switcher dropdown
                        if (_window is MainWindow mainWindow)
                        {
                            mainWindow.RefreshDatabaseProfileSwitcher();
                        }
                    }
                }
            }
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileComboBox.SelectedItem is DatabaseProfile profile)
            {
                var result = MessageBox.Show(
                    _window,
                    $"Are you sure you want to delete the profile '{profile.Name}'?\n\nThis will not delete any database data, only the profile configuration.",
                    "Delete Profile",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );

                if (result == MessageBoxResult.Yes)
                {
                    var profiles = _settings.DatabaseProfiles.ToList();
                    profiles.RemoveAll(p => p.Name == profile.Name);
                    _settings.DatabaseProfiles = profiles;
                    _model.DatabaseProfiles = new ObservableCollection<DatabaseProfile>(profiles);
                    
                    // Switch to first profile if we deleted the active one
                    if (_settings.ActiveDatabaseProfile == profile.Name && profiles.Count > 0)
                    {
                        _settings.ActiveDatabaseProfile = profiles[0].Name;
                    }
                    
                    _model.SetDirty();
                    
                    // Refresh the main window's profile switcher dropdown
                    if (_window is MainWindow mainWindow)
                    {
                        mainWindow.RefreshDatabaseProfileSwitcher();
                    }
                }
            }
        }
        
        /// <summary>
        /// Calculate VRAM usage for the current allocation settings.
        /// 
        /// Architecture:
        /// - ONNX services (Tagging, Embedding, FaceDetection): 1 model per GPU, N workers share it.
        ///   Allocation value = worker count (VRAM is fixed per GPU regardless of worker count).
        /// - LLM services (Captioning): N models per GPU (VRAM permitting), 1 worker per model.
        ///   Allocation value = model count (VRAM scales linearly with count).
        /// </summary>
        private void CalculateVram_Click(object sender, RoutedEventArgs e)
        {
            // Measured VRAM values (in GB) - per model instance
            const double CaptioningVramPerModel = 5.6;  // JoyCaption Q4 GGUF - scales with count
            const double TaggingVramFixed = 2.6;         // JoyTag + WD - fixed per GPU
            const double EmbeddingVramFixed = 7.6;       // BGE + CLIP (all 4) - fixed per GPU
            const double FaceDetectionVramFixed = 0.8;   // YOLO + ArcFace - fixed per GPU
            
            // Parse GPU capacities and max usage
            var vramCapacities = _model.GpuVramCapacity
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => double.TryParse(s, out var v) ? v : 32.0)
                .ToArray();
            
            var maxUsage = _model.MaxVramUsagePercent / 100.0;
            var maxUsableVram = vramCapacities.Select(v => v * maxUsage).ToArray();
            
            // Calculate concurrent mode
            var concurrentResult = CalculateConcurrentVram(
                _model.ConcurrentCaptioningAllocation,
                _model.ConcurrentTaggingAllocation,
                _model.ConcurrentEmbeddingAllocation,
                _model.ConcurrentFaceDetectionAllocation,
                CaptioningVramPerModel, TaggingVramFixed, EmbeddingVramFixed, FaceDetectionVramFixed,
                maxUsableVram);
            
            _model.ConcurrentVramResult = concurrentResult;
            
            // Calculate solo modes
            var soloResults = new System.Text.StringBuilder();
            
            // Captioning: scales with model count
            var soloCaptioning = CalculateSoloVram(
                _model.SoloCaptioningAllocation, CaptioningVramPerModel, isOnnxFixed: false, maxUsableVram);
            soloResults.AppendLine($"Captioning: {soloCaptioning}");
            
            // ONNX services: fixed VRAM regardless of worker count
            var soloTagging = CalculateSoloVram(
                _model.SoloTaggingAllocation, TaggingVramFixed, isOnnxFixed: true, maxUsableVram);
            soloResults.AppendLine($"Tagging: {soloTagging}");
            
            var soloEmbedding = CalculateSoloVram(
                _model.SoloEmbeddingAllocation, EmbeddingVramFixed, isOnnxFixed: true, maxUsableVram);
            soloResults.AppendLine($"Embedding: {soloEmbedding}");
            
            var soloFaceDetection = CalculateSoloVram(
                _model.SoloFaceDetectionAllocation, FaceDetectionVramFixed, isOnnxFixed: true, maxUsableVram);
            soloResults.AppendLine($"Face Detection: {soloFaceDetection}");
            
            _model.SoloVramResults = soloResults.ToString().TrimEnd();
        }
        
        /// <summary>
        /// Calculate VRAM for concurrent mode (all services running together).
        /// </summary>
        private string CalculateConcurrentVram(
            string captioningAlloc, string taggingAlloc, string embeddingAlloc, string faceDetectionAlloc,
            double captioningVramPerModel, double taggingVramFixed, double embeddingVramFixed, double faceDetectionVramFixed,
            double[] maxUsableVram)
        {
            var gpuCount = maxUsableVram.Length;
            var gpuUsage = new double[gpuCount];
            
            // Captioning: VRAM scales with model count
            AddScalingVram(captioningAlloc, captioningVramPerModel, gpuUsage);
            
            // ONNX services: fixed VRAM per GPU (regardless of worker count)
            AddFixedVram(taggingAlloc, taggingVramFixed, gpuUsage);
            AddFixedVram(embeddingAlloc, embeddingVramFixed, gpuUsage);
            AddFixedVram(faceDetectionAlloc, faceDetectionVramFixed, gpuUsage);
            
            // Check each GPU
            var results = new System.Text.StringBuilder();
            bool allOk = true;
            
            for (int i = 0; i < gpuCount; i++)
            {
                var used = gpuUsage[i];
                var max = maxUsableVram[i];
                var status = used <= max ? "✓ OK" : "✗ OVER!";
                
                if (used > max) allOk = false;
                
                results.AppendLine($"GPU{i}: {used:F1}GB / {max:F1}GB {status}");
            }
            
            return (allOk ? "✓ All GPUs OK\n" : "✗ VRAM EXCEEDED!\n") + results.ToString().TrimEnd();
        }
        
        /// <summary>
        /// Calculate VRAM for a single service in solo mode.
        /// </summary>
        private string CalculateSoloVram(string allocation, double vramAmount, bool isOnnxFixed, double[] maxUsableVram)
        {
            var gpuCount = maxUsableVram.Length;
            var gpuUsage = new double[gpuCount];
            
            if (isOnnxFixed)
            {
                AddFixedVram(allocation, vramAmount, gpuUsage);
            }
            else
            {
                AddScalingVram(allocation, vramAmount, gpuUsage);
            }
            
            bool allOk = true;
            var parts = new List<string>();
            
            for (int i = 0; i < gpuCount; i++)
            {
                if (gpuUsage[i] > 0)
                {
                    var max = maxUsableVram[i];
                    var ok = gpuUsage[i] <= max;
                    if (!ok) allOk = false;
                    parts.Add($"GPU{i}:{gpuUsage[i]:F1}/{max:F1}GB");
                }
            }
            
            return (allOk ? "✓ " : "✗ ") + string.Join(" ", parts);
        }
        
        /// <summary>
        /// Add VRAM for LLM services where VRAM scales with model count.
        /// allocation = "2,1" means 2 models on GPU0 (2×VRAM), 1 model on GPU1 (1×VRAM)
        /// </summary>
        private void AddScalingVram(string allocation, double vramPerModel, double[] gpuUsage)
        {
            var counts = allocation
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0)
                .ToArray();
            
            for (int i = 0; i < Math.Min(counts.Length, gpuUsage.Length); i++)
            {
                gpuUsage[i] += counts[i] * vramPerModel;
            }
        }
        
        /// <summary>
        /// Add VRAM for ONNX services where VRAM is fixed per GPU (1 model shared by N workers).
        /// allocation = "10,5" means 10 workers on GPU0, 5 workers on GPU1 - but both use same VRAM.
        /// </summary>
        private void AddFixedVram(string allocation, double fixedVram, double[] gpuUsage)
        {
            var counts = allocation
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0)
                .ToArray();
            
            for (int i = 0; i < Math.Min(counts.Length, gpuUsage.Length); i++)
            {
                // Any workers > 0 means 1 model loaded
                if (counts[i] > 0)
                {
                    gpuUsage[i] += fixedVram;
                }
            }
        }
    }
}