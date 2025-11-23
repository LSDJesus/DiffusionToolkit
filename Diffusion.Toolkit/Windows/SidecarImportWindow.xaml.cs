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
using System.Windows.Controls;
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

            // Get all sidecar files (.txt or .json based on format)
            string[] sidecarFiles;
            if (FindName("FormatDanbooruJson") is RadioButton rbJson && rbJson.IsChecked == true)
            {
                sidecarFiles = Directory.GetFiles(_folderPath, "*.json", SearchOption.AllDirectories);
            }
            else
            {
                sidecarFiles = Directory.GetFiles(_folderPath, "*.txt", SearchOption.AllDirectories);
            }
            
            TotalFiles = sidecarFiles.Length;
            ProcessedFiles = 0;

            var storeConfidence = ServiceLocator.Settings?.StoreTagConfidence ?? false;
            var archiveSidecars = FindName("ArchiveSidecarsCheckBox") is CheckBox cb && cb.IsChecked == true;
            var successfulImports = new List<string>();

            foreach (var sidecarFile in sidecarFiles)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                CurrentFileName = Path.GetFileName(sidecarFile);
                ProgressMessage = "Processing...";

                var baseFileName = Path.GetFileNameWithoutExtension(sidecarFile).ToLowerInvariant();
                
                // Find corresponding image
                if (!imageDict.TryGetValue(baseFileName, out var image))
                {
                    ProcessedFiles++;
                    continue; // No matching image
                }

                try
                {
                    var content = await File.ReadAllTextAsync(sidecarFile, _cancellationTokenSource.Token);
                    
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        ProcessedFiles++;
                        continue;
                    }

                    // Process based on selected format
                    bool success = false;
                    if (FormatCommaDelimited.IsChecked == true)
                    {
                        success = await ProcessCommaDelimitedTags(dataStore, image.Id, content);
                    }
                    else if (FormatCommaWithConfidence.IsChecked == true)
                    {
                        success = await ProcessTagsWithConfidence(dataStore, image.Id, content);
                    }
                    else if (FormatPrompt.IsChecked == true)
                    {
                        success = await ProcessPrompt(dataStore, image.Id, content);
                    }
                    else if (FindName("FormatDanbooruJson") is RadioButton rbDanbooruJson && rbDanbooruJson.IsChecked == true)
                    {
                        success = await ProcessDanbooruJson(dataStore, image.Id, content);
                    }
                    else if (FormatOther.IsChecked == true)
                    {
                        success = await ProcessOther(dataStore, image.Id, content);
                    }
                    
                    if (success)
                    {
                        successfulImports.Add(sidecarFile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error processing {sidecarFile}: {ex.Message}");
                }

                ProcessedFiles++;
            }

            // Archive successfully imported sidecars
            if (archiveSidecars && successfulImports.Any())
            {
                ProgressMessage = "Archiving sidecar files...";
                await ArchiveSidecarFiles(successfulImports);
            }

            ProgressMessage = "Complete!";
            MessageBox.Show($"Successfully imported {successfulImports.Count} sidecar file(s)!\n" +
                (archiveSidecars ? $"Archived to 'archive' subfolders." : ""),
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async Task<bool> ProcessCommaDelimitedTags(dynamic dataStore, int imageId, string content)
    {
        var tags = content.Split(',')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => (Tag: t, Confidence: 1.0f))
            .ToList();

        if (tags.Any())
        {
            var source = GenerateSourceName();
            await dataStore.StoreImageTagsAsync(imageId, tags, source);
            return true;
        }
        return false;
    }

    private async Task<bool> ProcessTagsWithConfidence(dynamic dataStore, int imageId, string content)
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
            var source = GenerateSourceName();
            await dataStore.StoreImageTagsAsync(imageId, tags, source);
            return true;
        }
        return false;
    }

    private async Task<bool> ProcessPrompt(dynamic dataStore, int imageId, string content)
    {
        // Update prompt field in image table (use current schema)
        var sql = $"UPDATE {((Database.PostgreSQL.PostgreSQLDataStore)dataStore).Table("image")} SET prompt = @prompt WHERE id = @imageId";
        
        using var connection = await ((Database.PostgreSQL.PostgreSQLDataStore)dataStore).OpenConnectionAsync();
        await connection.ExecuteAsync(sql, new { imageId, prompt = content });
        return true;
    }

    private async Task ProcessCaption(dynamic dataStore, int imageId, string content)
    {
        // Store as caption
        await dataStore.StoreCaptionAsync(imageId, content, "sidecar", null, 0, 0f);
    }

    private async Task<bool> ProcessOther(dynamic dataStore, int imageId, string content)
    {
        // Store in generated_tags field (legacy)
        const string sql = "UPDATE image SET generated_tags = @generatedTags WHERE id = @imageId";
        
        using var connection = await ((Database.PostgreSQL.PostgreSQLDataStore)dataStore).OpenConnectionAsync();
        await connection.ExecuteAsync(sql, new { imageId, generatedTags = content });
        return true;
    }

    /// <summary>
    /// Generate source name based on folder path (e.g., "danbooru_scrape" from folder name)
    /// </summary>
    private string GenerateSourceName()
    {
        // Extract folder name as source (e.g., E:\_Dataset\danbooru_scrape\images → "danbooru_scrape")
        var folderName = Path.GetFileName(_folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        
        // Sanitize: lowercase, replace spaces/special chars with underscores
        var source = Regex.Replace(folderName, @"[^\w]+", "_").ToLowerInvariant().Trim('_');
        
        // Fallback to "sidecar" if empty
        return string.IsNullOrWhiteSpace(source) ? "sidecar" : source;
    }

    /// <summary>
    /// Process Danbooru JSON format (tags + rating + metadata)
    /// </summary>
    private async Task<bool> ProcessDanbooruJson(dynamic dataStore, int imageId, string content)
    {
        try
        {
            using var jsonDoc = System.Text.Json.JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            var tags = new List<(string Tag, float Confidence)>();
            
            // Extract all tags with category prefixes for organization
            if (root.TryGetProperty("tag_string_general", out var generalTags))
            {
                var generalTagList = generalTags.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                tags.AddRange(generalTagList.Select(t => (t, 1.0f)));
            }
            
            if (root.TryGetProperty("tag_string_character", out var charTags))
            {
                var charTagList = charTags.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                tags.AddRange(charTagList.Select(t => ($"character:{t}", 1.0f)));
            }
            
            if (root.TryGetProperty("tag_string_copyright", out var copyrightTags))
            {
                var copyrightTagList = copyrightTags.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                tags.AddRange(copyrightTagList.Select(t => ($"copyright:{t}", 1.0f)));
            }
            
            if (root.TryGetProperty("tag_string_artist", out var artistTags))
            {
                var artistTagList = artistTags.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                tags.AddRange(artistTagList.Select(t => ($"artist:{t}", 1.0f)));
            }
            
            if (root.TryGetProperty("tag_string_meta", out var metaTags))
            {
                var metaTagList = metaTags.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                tags.AddRange(metaTagList.Select(t => ($"meta:{t}", 1.0f)));
            }

            // Store tags with "danbooru" source
            if (tags.Any())
            {
                var source = GenerateSourceName();
                await dataStore.StoreImageTagsAsync(imageId, tags, source);
            }

            // Map rating to NSFW enum and update image
            if (root.TryGetProperty("rating", out var rating))
            {
                var ratingStr = rating.GetString();
                int nsfwValue = ratingStr switch
                {
                    "g" => 0, // General/Safe
                    "s" => 0, // Safe
                    "q" => 1, // Questionable
                    "e" => 2, // Explicit
                    _ => 0
                };

                // Update image NSFW rating
                var sql = $"UPDATE {((Database.PostgreSQL.PostgreSQLDataStore)dataStore).Table("image")} SET nsfw = @nsfw WHERE id = @imageId";
                using var connection = await ((Database.PostgreSQL.PostgreSQLDataStore)dataStore).OpenConnectionAsync();
                await connection.ExecuteAsync(sql, new { imageId, nsfw = nsfwValue });
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to parse Danbooru JSON: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Archive successfully imported sidecar files to "archive" subfolder
    /// </summary>
    private async Task ArchiveSidecarFiles(List<string> files)
    {
        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                try
                {
                    var dir = Path.GetDirectoryName(file);
                    if (string.IsNullOrEmpty(dir)) continue;

                    var archiveDir = Path.Combine(dir, "archive");
                    Directory.CreateDirectory(archiveDir);

                    var fileName = Path.GetFileName(file);
                    var destFile = Path.Combine(archiveDir, fileName);

                    // Handle duplicates by appending (1), (2), etc.
                    if (File.Exists(destFile))
                    {
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                        var ext = Path.GetExtension(fileName);
                        int counter = 1;
                        
                        while (File.Exists(destFile))
                        {
                            destFile = Path.Combine(archiveDir, $"{nameWithoutExt} ({counter}){ext}");
                            counter++;
                        }
                    }

                    File.Move(file, destFile);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to archive {file}: {ex.Message}");
                }
            }
        });
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
