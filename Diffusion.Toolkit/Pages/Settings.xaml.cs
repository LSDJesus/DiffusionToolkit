using Diffusion.Common;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Themes;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
using Dapper;
using Diffusion.Database;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Localization;
using Diffusion.Toolkit.Services;

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
            
            // Tagging/Captioning output settings
            _model.StoreTagConfidence = _settings.StoreTagConfidence;
            _model.AutoWriteMetadata = _settings.AutoWriteMetadata;
            _model.CreateMetadataBackup = _settings.CreateMetadataBackup;
            _model.WriteTagsToMetadata = _settings.WriteTagsToMetadata;
            _model.WriteCaptionsToMetadata = _settings.WriteCaptionsToMetadata;
            _model.WriteGenerationParamsToMetadata = _settings.WriteGenerationParamsToMetadata;
            
            // Initialize schema selector
            InitializeSchemaSelector();
            
            _model.SetPristine();
        }
        
        private void InitializeSchemaSelector()
        {
            var comboBox = FindName("SchemaComboBox") as ComboBox;
            if (comboBox == null) return;
            
            // Set current schema selection (PostgreSQL default is "public")
            var currentSchema = _settings.DatabaseSchema ?? "public";
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag as string == currentSchema)
                {
                    comboBox.SelectedItem = item;
                    break;
                }
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
                
                // Tagging/Captioning output settings
                _settings.StoreTagConfidence = _model.StoreTagConfidence;
                _settings.AutoWriteMetadata = _model.AutoWriteMetadata;
                _settings.CreateMetadataBackup = _model.CreateMetadataBackup;
                _settings.WriteTagsToMetadata = _model.WriteTagsToMetadata;
                _settings.WriteCaptionsToMetadata = _model.WriteCaptionsToMetadata;
                _settings.WriteGenerationParamsToMetadata = _model.WriteGenerationParamsToMetadata;

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
                _settings.DatabaseSchema = schema;
                _dataStore.CurrentSchema = schema;
                
                // Refresh the search results to show images from new schema
                ServiceLocator.SearchService?.RefreshResults();
                
                ServiceLocator.ToastService?.Toast($"Switched to '{item.Content}' collection", "Schema Changed");
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
                await using var conn = await _dataStore.OpenConnectionAsync();
                
                // Create schema
                await conn.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\"");
                
                // Copy table structure from main schema
                var tables = new[] { "image", "image_tags", "folder", "album", "album_image", "saved_query", "image_captions" };
                foreach (var table in tables)
                {
                    try
                    {
                        await conn.ExecuteAsync($"CREATE TABLE IF NOT EXISTS \"{schemaName}\".\"{table}\" (LIKE main.\"{table}\" INCLUDING ALL)");
                    }
                    catch
                    {
                        // Table might not exist in main schema yet, continue
                    }
                }
                
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


    }



}
