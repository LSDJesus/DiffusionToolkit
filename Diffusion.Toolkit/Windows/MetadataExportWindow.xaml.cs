using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Dapper;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Windows;

public partial class MetadataExportWindow : Window, INotifyPropertyChanged
{
    private readonly string _folderPath;
    private readonly int _folderId;
    private CancellationTokenSource? _cancellationTokenSource;

    private bool _isProcessing;
    private int _processedFiles;
    private int _totalFiles;
    private string _progressMessage = "";
    private string _currentFileName = "";
    private int _imageCount;
    private bool _exportTags = true;
    private bool _exportCaptions = true;
    private bool _exportGenerationParams = true;
    private bool _createBackup = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MetadataExportWindow(string folderPath, int folderId)
    {
        InitializeComponent();
        _folderPath = folderPath;
        _folderId = folderId;
        DataContext = this;

        // Count images with metadata
        CountImagesWithMetadata();
    }

    private void CountImagesWithMetadata()
    {
        try
        {
            var dataStore = ServiceLocator.DataStore;
            if (dataStore == null) return;

            var images = dataStore.GetFolderImages(_folderId, true);
            ImageCount = images.Count();
        }
        catch
        {
            ImageCount = 0;
        }
    }

    public string FolderPath => _folderPath;

    public int ImageCount
    {
        get => _imageCount;
        set
        {
            _imageCount = value;
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
            OnPropertyChanged(nameof(CanExport));
        }
    }

    public bool CanExport => !IsProcessing && (ExportTags || ExportCaptions || ExportGenerationParams);

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

    public bool ExportTags
    {
        get => _exportTags;
        set
        {
            _exportTags = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExport));
        }
    }

    public bool ExportCaptions
    {
        get => _exportCaptions;
        set
        {
            _exportCaptions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExport));
        }
    }

    public bool ExportGenerationParams
    {
        get => _exportGenerationParams;
        set
        {
            _exportGenerationParams = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExport));
        }
    }

    public bool CreateBackup
    {
        get => _createBackup;
        set
        {
            _createBackup = value;
            OnPropertyChanged();
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
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
            var imagePaths = dataStore.GetFolderImages(_folderId, true).ToList();
            
            // Filter by format if needed
            if (FilterPngJpegWebp.IsChecked == true)
            {
                imagePaths = imagePaths.Where(img =>
                {
                    var ext = Path.GetExtension(img.Path).ToLowerInvariant();
                    return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp";
                }).ToList();
            }

            TotalFiles = imagePaths.Count;
            ProcessedFiles = 0;

            int successCount = 0;
            int skipCount = 0;

            foreach (var imagePath in imagePaths)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                CurrentFileName = Path.GetFileName(imagePath.Path);
                ProgressMessage = "Exporting...";

                try
                {
                    // Load full image with all metadata
                    var image = dataStore.GetImage(imagePath.Id);
                    if (image == null)
                    {
                        skipCount++;
                        ProcessedFiles++;
                        continue;
                    }

                    var request = new Scanner.MetadataWriteRequest
                    {
                        CreateBackup = CreateBackup
                    };

                    // Get tags from database
                    if (ExportTags)
                    {
                        var tags = await GetImageTags(image.Id);
                        if (tags.Any())
                        {
                            request.Tags = tags.Select(t => new Scanner.TagWithConfidence
                            {
                                Tag = t.Tag,
                                Confidence = t.Confidence
                            }).ToList();
                        }
                    }

                    // Get caption from database
                    if (ExportCaptions)
                    {
                        var caption = await GetImageCaption(image.Id);
                        if (!string.IsNullOrWhiteSpace(caption))
                        {
                            request.Caption = caption;
                        }
                    }

                    // Get generation parameters
                    if (ExportGenerationParams)
                    {
                        request.Prompt = image.Prompt;
                        request.NegativePrompt = image.NegativePrompt;
                        request.Steps = image.Steps;
                        request.Sampler = image.Sampler;
                        request.CFGScale = image.CfgScale;
                        request.Seed = image.Seed != 0 ? image.Seed : null;
                        request.Width = image.Width;
                        request.Height = image.Height;
                        request.Model = image.Model;
                        request.ModelHash = image.ModelHash;
                        request.AestheticScore = image.AestheticScore;
                    }

                    // Write to file
                    var success = Scanner.MetadataWriter.WriteMetadata(imagePath.Path, request);
                    if (success)
                        successCount++;
                    else
                        skipCount++;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error exporting metadata for {imagePath.Path}: {ex.Message}");
                    skipCount++;
                }

                ProcessedFiles++;
            }

            ProgressMessage = "Complete!";
            var message = $"Successfully exported metadata to {successCount} file(s)!";
            if (skipCount > 0)
                message += $"\n{skipCount} file(s) skipped or failed.";
            
            MessageBox.Show(message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancelled";
            MessageBox.Show("Export cancelled by user.", "Cancelled", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ProgressMessage = "Error occurred";
            MessageBox.Show($"Error during export: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
        }
    }

    private async Task<List<(string Tag, float Confidence)>> GetImageTags(int imageId)
    {
        var dataStore = ServiceLocator.DataStore;
        if (dataStore is not Database.PostgreSQL.PostgreSQLDataStore pgStore)
            return new List<(string Tag, float Confidence)>();

        try
        {
            using var conn = await pgStore.OpenConnectionAsync();
            var sql = $@"
                SELECT tag, confidence 
                FROM {pgStore.Table("image_tags")} 
                WHERE image_id = @imageId
                ORDER BY confidence DESC";
            
            var results = await conn.QueryAsync<(string Tag, float Confidence)>(sql, new { imageId });
            return results.ToList();
        }
        catch
        {
            return new List<(string Tag, float Confidence)>();
        }
    }

    private async Task<string?> GetImageCaption(int imageId)
    {
        var dataStore = ServiceLocator.DataStore;
        if (dataStore is not Database.PostgreSQL.PostgreSQLDataStore pgStore)
            return null;

        try
        {
            using var conn = await pgStore.OpenConnectionAsync();
            var sql = $@"
                SELECT caption 
                FROM {pgStore.Table("image_captions")} 
                WHERE image_id = @imageId
                ORDER BY created_at DESC
                LIMIT 1";
            
            return await conn.QueryFirstOrDefaultAsync<string>(sql, new { imageId });
        }
        catch
        {
            return null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing)
        {
            _cancellationTokenSource?.Cancel();
        }
        else
        {
            Close();
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
