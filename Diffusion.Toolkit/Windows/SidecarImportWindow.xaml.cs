using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Dapper;
using Diffusion.Common;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Windows;

public partial class SidecarImportWindow : Window, INotifyPropertyChanged
{
    private readonly string _folderPath;
    private readonly int _folderId;
    private CancellationTokenSource? _cancellationTokenSource;

    private bool _isProcessing;
    private int _processedFiles;
    private int _totalFiles;
    private string _progressMessage = "";
    private string _currentFileName = "";
    private int _sidecarFileCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SidecarImportWindow(string folderPath, int folderId)
    {
        InitializeComponent();
        _folderPath = folderPath;
        _folderId = folderId;
        DataContext = this;

        // Count sidecar files
        CountSidecarFiles();
    }

    private void CountSidecarFiles()
    {
        try
        {
            var txtFiles = Directory.GetFiles(_folderPath, "*.txt", SearchOption.AllDirectories);
            SidecarFileCount = txtFiles.Length;
        }
        catch
        {
            SidecarFileCount = 0;
        }
    }

    public string FolderPath => _folderPath;

    public int SidecarFileCount
    {
        get => _sidecarFileCount;
        set
        {
            _sidecarFileCount = value;
            OnPropertyChanged();
        }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            _isProcessing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanImport));
        }
    }

    public bool CanImport => !IsProcessing;

    public int ProcessedFiles
    {
        get => _processedFiles;
        set
        {
            _processedFiles = value;
            OnPropertyChanged();
        }
    }

    public int TotalFiles
    {
        get => _totalFiles;
        set
        {
            _totalFiles = value;
            OnPropertyChanged();
        }
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        set
        {
            _progressMessage = value;
            OnPropertyChanged();
        }
    }

    public string CurrentFileName
    {
        get => _currentFileName;
        set
        {
            _currentFileName = value;
            OnPropertyChanged();
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        IsProcessing = true;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var dataStore = ServiceLocator.DataStore;
            if (dataStore == null)
            {
                MessageBox.Show("Database not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get all images in folder
            var images = dataStore.GetFolderImages(_folderId, true);
            var imageDict = images.ToDictionary(img => Path.GetFileNameWithoutExtension(img.Path).ToLowerInvariant(), img => img);

            // Get all .txt files
            var txtFiles = Directory.GetFiles(_folderPath, "*.txt", SearchOption.AllDirectories);
            TotalFiles = txtFiles.Length;
            ProcessedFiles = 0;

            var storeConfidence = ServiceLocator.Settings?.StoreTagConfidence ?? false;

            foreach (var txtFile in txtFiles)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                CurrentFileName = Path.GetFileName(txtFile);
                ProgressMessage = "Processing...";

                var baseFileName = Path.GetFileNameWithoutExtension(txtFile).ToLowerInvariant();
                
                // Find corresponding image
                if (!imageDict.TryGetValue(baseFileName, out var image))
                {
                    ProcessedFiles++;
                    continue; // No matching image
                }

                try
                {
                    var content = await File.ReadAllTextAsync(txtFile, _cancellationTokenSource.Token);
                    
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        ProcessedFiles++;
                        continue;
                    }

                    // Process based on selected format
                    if (FormatCommaDelimited.IsChecked == true)
                    {
                        await ProcessCommaDelimitedTags(dataStore, image.Id, content);
                    }
                    else if (FormatCommaWithConfidence.IsChecked == true)
                    {
                        await ProcessTagsWithConfidence(dataStore, image.Id, content);
                    }
                    else if (FormatPrompt.IsChecked == true)
                    {
                        await ProcessPrompt(dataStore, image.Id, content);
                    }
                    else if (FormatCaption.IsChecked == true)
                    {
                        await ProcessCaption(dataStore, image.Id, content);
                    }
                    else if (FormatOther.IsChecked == true)
                    {
                        await ProcessOther(dataStore, image.Id, content);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error processing {txtFile}: {ex.Message}");
                }

                ProcessedFiles++;
            }

            ProgressMessage = "Complete!";
            MessageBox.Show($"Successfully imported {ProcessedFiles} sidecar files!", "Success", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancelled";
            MessageBox.Show("Import cancelled by user.", "Cancelled", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error occurred";
            MessageBox.Show($"Error during import: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
        }
    }

    private async Task ProcessCommaDelimitedTags(dynamic dataStore, int imageId, string content)
    {
        var tags = content.Split(',')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => (Tag: t, Confidence: 1.0f))
            .ToList();

        if (tags.Any())
        {
            await dataStore.StoreImageTagsAsync(imageId, tags, "sidecar");
        }
    }

    private async Task ProcessTagsWithConfidence(dynamic dataStore, int imageId, string content)
    {
        var tags = new List<(string Tag, float Confidence)>();
        
        // Match patterns like "tag:0.95" or "tag (0.95)" or just "tag"
        var matches = Regex.Matches(content, @"([^,:\(\)]+?)(?:\s*[\(:]?\s*(\d+\.?\d*)\s*\)?)?(?:,|$)");
        
        foreach (Match match in matches)
        {
            var tag = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(tag)) continue;

            float confidence = 1.0f;
            if (match.Groups[2].Success && float.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                confidence = parsed;
            }

            tags.Add((tag, confidence));
        }

        if (tags.Any())
        {
            await dataStore.StoreImageTagsAsync(imageId, tags, "sidecar");
        }
    }

    private async Task ProcessPrompt(dynamic dataStore, int imageId, string content)
    {
        // Update prompt field in image table
        const string sql = "UPDATE image SET prompt = @prompt WHERE id = @imageId";
        
        using var connection = await ((Database.PostgreSQL.PostgreSQLDataStore)dataStore).OpenConnectionAsync();
        await connection.ExecuteAsync(sql, new { imageId, prompt = content });
    }

    private async Task ProcessCaption(dynamic dataStore, int imageId, string content)
    {
        // Store as caption
        await dataStore.StoreCaptionAsync(imageId, content, "sidecar", null, 0, 0f);
    }

    private async Task ProcessOther(dynamic dataStore, int imageId, string content)
    {
        // Store in generated_tags field (legacy)
        const string sql = "UPDATE image SET generated_tags = @generatedTags WHERE id = @imageId";
        
        using var connection = await ((Database.PostgreSQL.PostgreSQLDataStore)dataStore).OpenConnectionAsync();
        await connection.ExecuteAsync(sql, new { imageId, generatedTags = content });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing)
        {
            _cancellationTokenSource?.Cancel();
        }
        else
        {
            DialogResult = false;
            Close();
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
