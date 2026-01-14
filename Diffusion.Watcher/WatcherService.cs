using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.Text.Json;
using Diffusion.Common;
using Diffusion.Common.Models;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.Tagging.Services;
using Diffusion.Captioning.Services;
using Diffusion.FaceDetection.Services;
using Npgsql;
using Dapper;
using DBModels = Diffusion.Database.PostgreSQL.Models;

namespace Diffusion.Watcher;

/// <summary>
/// Core background service that watches folders and manages scanning
/// </summary>
public class WatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentQueue<string> _quickScanQueue = new();
    private readonly ConcurrentQueue<string> _metadataScanQueue = new();
    private readonly PostgreSQLDataStore _dataStore;
    private readonly CancellationTokenSource _cts = new();
    private readonly Configuration<WatcherSettings> _configuration;
    private WatcherSettings? _settings;
    
    private Task? _quickScanWorker;
    private Task? _metadataScanWorker;
    private Task? _aiProcessingWorker;
    private Task? _httpListenerTask;
    private HttpListener? _httpListener;
    
    // AI Services
    private JoyTagService? _joyTagService;
    private WDV3LargeTagService? _wdTagService;
    private ICaptionService? _captionService;
    private FaceDetectionService? _faceDetectionService;
    
    private bool _isPaused;
    private bool _isDisposed;
    private bool _isConnected;

    public int HttpPort { get; set; } = 19284; // Default port for Watcher API

    public bool IsPaused => _isPaused;
    public bool IsConnected => _isConnected;
    public bool BackgroundMetadataEnabled { get; set; } = true;

    public event EventHandler<WatcherStatusEventArgs>? StatusChanged;

    public WatcherService() : this(null)
    {
    }

    public WatcherService(string? connectionString)
    {
        _configuration = new Configuration<WatcherSettings>(AppInfo.SettingsPath, AppInfo.IsPortable);
        LoadSettings();

        try
        {
            connectionString ??= GetConnectionString();
            _dataStore = new PostgreSQLDataStore(connectionString);
            _isConnected = true;
        }
        catch (Exception ex)
        {
            Logger.Log($"WatcherService failed to connect to database: {ex.Message}");
            _dataStore = null!;
            _isConnected = false;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (_configuration.TryLoad(out var settings))
            {
                _settings = settings;
                Logger.Log("WatcherService loaded settings from config.json");
            }
            else
            {
                Logger.Log("WatcherService could not load settings, using defaults");
                _settings = new WatcherSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error loading settings: {ex.Message}");
            _settings = new WatcherSettings();
        }
    }

    public void Start()
    {
        if (!_isConnected || _dataStore == null)
        {
            Logger.Log("WatcherService cannot start: database not connected");
            UpdateStatus("Database not connected", 0);
            return;
        }

        try
        {
            // Load watched folders from database
            var folders = _dataStore.GetFolders().Where(f => f.IsRoot).ToList();
            
            foreach (var folder in folders)
            {
                StartWatchingFolder(folder.Path);
            }

            // Start worker threads
            _quickScanWorker = Task.Run(QuickScanWorkerLoop, _cts.Token);
            _metadataScanWorker = Task.Run(MetadataScanWorkerLoop, _cts.Token);
            _aiProcessingWorker = Task.Run(AIProcessingWorkerLoop, _cts.Token);
            
            // Start HTTP listener
            StartHttpListener();

            UpdateStatus($"Watching {folders.Count} folders", _quickScanQueue.Count);
            
            // Trigger initial scan
            TriggerFullScan();
        }
        catch (Exception ex)
        {
            Logger.Log($"WatcherService.Start failed: {ex.Message}");
            UpdateStatus($"Error: {ex.Message}", 0);
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        
        if (_httpListener != null && _httpListener.IsListening)
        {
            try
            {
                _httpListener.Stop();
                _httpListener.Close();
            }
            catch { /* Ignore */ }
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        var tasks = new[] { _quickScanWorker, _metadataScanWorker, _aiProcessingWorker }
            .Where(t => t != null && !t.IsCompleted)
            .ToArray();
        
        if (tasks.Length > 0)
        {
            try
            {
                Task.WaitAll(tasks!, TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Tasks cancelled, expected
            }
        }
    }

    public void Pause()
    {
        _isPaused = true;
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
        }
        UpdateStatus("Paused", 0);
    }

    public void Resume()
    {
        _isPaused = false;
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = true;
        }
        UpdateStatus($"Watching {_watchers.Count} folders", _quickScanQueue.Count);
    }

    public void TriggerFullScan()
    {
        Task.Run(() =>
        {
            var folders = _dataStore.GetFolders().Where(f => f.IsRoot).ToList();
            var extensions = new[] { ".png", ".jpg", ".jpeg", ".webp" };

            foreach (var folder in folders)
            {
                if (_cts.Token.IsCancellationRequested) break;

                try
                {
                    var files = Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                        .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

                    foreach (var file in files)
                    {
                        _quickScanQueue.Enqueue(file);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error scanning folder {folder.Path}: {ex.Message}");
                }
            }

            UpdateStatus($"Scanning {_quickScanQueue.Count} files", _quickScanQueue.Count);
        });
    }

    public WatcherStatistics GetStatistics()
    {
        using var conn = _dataStore.OpenConnection();
        
        var totalImages = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM image");
        var quickScanned = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM image WHERE scan_phase = 0");
        var metadataScanned = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM image WHERE scan_phase = 1");

        return new WatcherStatistics
        {
            WatchedFolders = _watchers.Count,
            TotalImages = totalImages,
            QuickScannedImages = quickScanned,
            MetadataScannedImages = metadataScanned,
            FilesInQueue = _quickScanQueue.Count + _metadataScanQueue.Count,
            MetadataScanEnabled = BackgroundMetadataEnabled
        };
    }

    private void StartWatchingFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Logger.Log($"Folder not found, skipping: {path}");
            return;
        }

        var watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        watcher.Created += OnFileCreated;
        watcher.Changed += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Deleted += OnFileDeleted;

        _watchers.Add(watcher);
        
        Logger.Log($"Now watching: {path}");
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (IsImageFile(e.FullPath) && !_isPaused)
        {
            _quickScanQueue.Enqueue(e.FullPath);
            UpdateStatus("File added", _quickScanQueue.Count);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsImageFile(e.FullPath) && !_isPaused)
        {
            // Queue for re-scan (will update if hash changed)
            _quickScanQueue.Enqueue(e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsImageFile(e.FullPath) && !_isPaused)
        {
            // TODO: Handle rename in database (update path)
            _quickScanQueue.Enqueue(e.FullPath);
        }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsImageFile(e.FullPath) && !_isPaused)
        {
            // Mark as unavailable in database
            Task.Run(() =>
            {
                try
                {
                    using var conn = _dataStore.OpenConnection();
                    conn.Execute("UPDATE image SET unavailable = true WHERE path = @path", new { path = e.FullPath });
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error marking file as unavailable: {ex.Message}");
                }
            });
        }
    }

    private void StartHttpListener()
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{HttpPort}/");
            _httpListener.Start();
            _httpListenerTask = Task.Run(HttpListenerLoop, _cts.Token);
            Logger.Log($"Watcher API listening on http://localhost:{HttpPort}/");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start HTTP listener: {ex.Message}");
        }
    }

    private async Task HttpListenerLoop()
    {
        while (!_cts.Token.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                _ = Task.Run(() => HandleHttpRequest(context));
            }
            catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Log($"HTTP listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleHttpRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/process_image")
            {
                using var reader = new StreamReader(request.InputStream);
                var body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<ProcessImageRequest>(body);

                if (data != null && (!string.IsNullOrEmpty(data.Path) || data.Id > 0))
                {
                    // Trigger immediate processing
                    _ = Task.Run(async () => {
                        if (data.Id > 0)
                        {
                            await ProcessImageById(data.Id);
                        }
                        else if (!string.IsNullOrEmpty(data.Path))
                        {
                            await ProcessImageByPath(data.Path);
                        }
                    });

                    response.StatusCode = (int)HttpStatusCode.Accepted;
                    var responseData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { status = "accepted" }));
                    await response.OutputStream.WriteAsync(responseData);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                }
            }
            else if (request.Url?.AbsolutePath == "/status")
            {
                var stats = GetStatistics();
                var responseData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stats));
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(responseData);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error handling HTTP request: {ex.Message}");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            response.Close();
        }
    }

    private async Task AIProcessingWorkerLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (_isPaused)
                {
                    await Task.Delay(5000, _cts.Token);
                    continue;
                }

                bool worked = false;

                // 1. Check for images needing face detection
                var imagesNeedingFace = await _dataStore.GetImagesNeedingFaceDetection(batchSize: 5);
                if (imagesNeedingFace.Any())
                {
                    foreach (var id in imagesNeedingFace)
                    {
                        await ProcessFaceDetection(id);
                    }
                    worked = true;
                }

                // 2. Check for images needing tagging
                var imagesNeedingTagging = await _dataStore.GetImagesNeedingTagging(batchSize: 5);
                if (imagesNeedingTagging.Any())
                {
                    foreach (var id in imagesNeedingTagging)
                    {
                        await ProcessTagging(id);
                    }
                    worked = true;
                }

                // 3. Check for images needing captioning
                var imagesNeedingCaptioning = await _dataStore.GetImagesNeedingCaptioning(batchSize: 5);
                if (imagesNeedingCaptioning.Any())
                {
                    foreach (var id in imagesNeedingCaptioning)
                    {
                        await ProcessCaptioning(id);
                    }
                    worked = true;
                }

                if (!worked)
                {
                    await Task.Delay(10000, _cts.Token);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AI processing worker error: {ex.Message}");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private async Task ProcessImageById(int id)
    {
        await ProcessFaceDetection(id);
        await ProcessTagging(id);
        await ProcessCaptioning(id);
    }

    private async Task ProcessImageByPath(string path)
    {
        var id = await _dataStore.GetImageIdByPathAsync(path);
        if (id.HasValue)
        {
            await ProcessImageById(id.Value);
        }
    }

    private async Task InitializeFaceDetectionService()
    {
        if (_faceDetectionService != null) return;

        try
        {
            var modelRoot = _settings?.ModelRootPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
            var config = FaceDetectionConfig.CreateDefault(modelRoot);
            
            _faceDetectionService = FaceDetectionService.FromConfig(config);
            Logger.Log("WatcherService initialized FaceDetectionService");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize FaceDetectionService: {ex.Message}");
        }
    }

    private async Task InitializeTaggingServices()
    {
        if (_joyTagService != null && _wdTagService != null) return;

        try
        {
            var modelRoot = _settings?.ModelRootPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
            
            if (_joyTagService == null)
            {
                var joyTagModelPath = Path.Combine(modelRoot, "joytag", "model.onnx");
                var joyTagTagsPath = Path.Combine(modelRoot, "joytag", "id2label.json");
                
                if (File.Exists(joyTagModelPath) && File.Exists(joyTagTagsPath))
                {
                    _joyTagService = new JoyTagService(joyTagModelPath, joyTagTagsPath);
                    await _joyTagService.InitializeAsync();
                }
            }

            if (_wdTagService == null)
            {
                var wdModelPath = Path.Combine(modelRoot, "wdv3", "model.onnx");
                var wdTagsPath = Path.Combine(modelRoot, "wdv3", "selected_tags.csv");

                if (File.Exists(wdModelPath) && File.Exists(wdTagsPath))
                {
                    _wdTagService = new WDV3LargeTagService(wdModelPath, wdTagsPath);
                    await _wdTagService.InitializeAsync();
                }
            }
            
            Logger.Log("WatcherService initialized TaggingServices");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize TaggingServices: {ex.Message}");
        }
    }

    private async Task InitializeCaptioningService()
    {
        if (_captionService != null) return;

        try
        {
            if (_settings?.CaptionProvider == CaptionProviderType.LocalJoyCaption)
            {
                var modelRoot = _settings?.ModelRootPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
                var modelPath = _settings?.JoyCaptionModelPath;
                var clipPath = _settings?.JoyCaptionMMProjPath;

                if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
                {
                    _captionService = new JoyCaptionService(modelPath, clipPath);
                }
            }
            else if (_settings?.CaptionProvider == CaptionProviderType.OpenAICompatible)
            {
                var baseUrl = _settings?.ExternalCaptionBaseUrl;
                var model = _settings?.ExternalCaptionModel;
                var apiKey = _settings?.ExternalCaptionApiKey;

                if (!string.IsNullOrEmpty(baseUrl))
                {
                    _captionService = new HttpCaptionService(baseUrl, model, apiKey);
                }
            }
            
            Logger.Log("WatcherService initialized CaptioningService");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to initialize CaptioningService: {ex.Message}");
        }
    }

    private async Task ProcessFaceDetection(int imageId)
    {
        try
        {
            await InitializeFaceDetectionService();
            if (_faceDetectionService == null) return;

            var image = await _dataStore.GetImageByIdAsync(imageId);
            if (image == null) return;

            Logger.Log($"Background processing face detection for image {imageId}: {image.Path}");
            
            var result = await _faceDetectionService.ProcessImageAsync(image.Path);
            if (result.Faces.Any())
            {
                await _dataStore.StoreFaceDetections(imageId, result.Faces);
                Logger.Log($"Detected {result.Faces.Count} faces in image {imageId}");
            }
            
            await _dataStore.SetNeedsFaceDetection(new List<int> { imageId }, false);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error in background face detection for image {imageId}: {ex.Message}");
        }
    }

    private async Task ProcessTagging(int imageId)
    {
        try
        {
            await InitializeTaggingServices();
            if (_joyTagService == null && _wdTagService == null) return;

            var image = await _dataStore.GetImageByIdAsync(imageId);
            if (image == null) return;

            Logger.Log($"Background processing tagging for image {imageId}: {image.Path}");

            var allTags = new HashSet<string>();

            if (_joyTagService != null)
            {
                var tags = await _joyTagService.TagImageAsync(image.Path);
                foreach (var tag in tags) allTags.Add(tag.Tag);
            }

            if (_wdTagService != null)
            {
                var tags = await _wdTagService.TagImageAsync(image.Path);
                foreach (var tag in tags) allTags.Add(tag.Tag);
            }

            if (allTags.Any())
            {
                // We need to convert HashSet<string> to IEnumerable<(string tag, float confidence)>
                // Since we don't have individual confidence scores here, we'll use 1.0
                var tagData = allTags.Select(t => (t, 1.0f));
                await _dataStore.StoreImageTagsAsync(imageId, tagData);
            }

            await _dataStore.SetNeedsTagging(new List<int> { imageId }, false);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error in background tagging for image {imageId}: {ex.Message}");
        }
    }

    private async Task ProcessCaptioning(int imageId)
    {
        try
        {
            await InitializeCaptioningService();
            if (_captionService == null) return;

            var image = await _dataStore.GetImageByIdAsync(imageId);
            if (image == null) return;

            Logger.Log($"Background processing captioning for image {imageId}: {image.Path}");

            var result = await _captionService.CaptionImageAsync(image.Path);
            if (!string.IsNullOrEmpty(result.Caption))
            {
                await _dataStore.StoreCaptionAsync(imageId, result.Caption);
            }

            await _dataStore.SetNeedsCaptioning(new List<int> { imageId }, false);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error in background captioning for image {imageId}: {ex.Message}");
        }
    }

    private async Task QuickScanWorkerLoop()
    {
        var batchSize = 1000;
        var batch = new List<string>();

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Collect batch
                while (batch.Count < batchSize && _quickScanQueue.TryDequeue(out var file))
                {
                    batch.Add(file);
                }

                if (batch.Count > 0)
                {
                    await ProcessQuickScanBatch(batch);
                    
                    // Queue for metadata extraction
                    if (BackgroundMetadataEnabled)
                    {
                        foreach (var file in batch)
                        {
                            _metadataScanQueue.Enqueue(file);
                        }
                    }
                    
                    batch.Clear();
                    UpdateStatus($"Indexed {batch.Count} files", _quickScanQueue.Count);
                }
                else
                {
                    await Task.Delay(1000, _cts.Token);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Quick scan error: {ex.Message}");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private async Task MetadataScanWorkerLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (!BackgroundMetadataEnabled)
                {
                    await Task.Delay(5000, _cts.Token);
                    continue;
                }

                if (_metadataScanQueue.TryDequeue(out var file))
                {
                    await ProcessMetadataScan(file);
                    UpdateStatus("Extracting metadata", _quickScanQueue.Count);
                }
                else
                {
                    // No files in queue, check database for quick-scanned images
                    var pendingPaths = _dataStore.GetQuickScannedPaths(limit: 10).ToList();
                    
                    if (pendingPaths.Any())
                    {
                        foreach (var path in pendingPaths)
                        {
                            await ProcessMetadataScan(path);
                        }
                    }
                    else
                    {
                        UpdateStatus($"Idle - watching {_watchers.Count} folders", 0);
                        await Task.Delay(10000, _cts.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Metadata scan error: {ex.Message}");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private async Task ProcessQuickScanBatch(List<string> files)
    {
        await Task.Run(() =>
        {
            var fileInfoList = new List<(string path, long fileSize, DateTime createdDate, DateTime modifiedDate)>();
            
            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists)
                    {
                        fileInfoList.Add((file, fi.Length, fi.CreationTimeUtc, fi.LastWriteTimeUtc));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error reading file {file}: {ex.Message}");
                }
            }

            if (fileInfoList.Any())
            {
                using var conn = _dataStore.OpenConnection();
                
                // Pre-populate folder cache to avoid queries during COPY operation
                var rootFolderCache = conn.Query<DBModels.Folder>("SELECT id, parent_id, root_folder_id, path, image_count, scanned_date, unavailable, archived, excluded, is_root FROM folder")
                    .ToDictionary(f => f.Path, f => f);
                
                _dataStore.QuickAddImages(conn, fileInfoList, rootFolderCache, _cts.Token);
            }
        });
    }

    private async Task ProcessMetadataScan(string file)
    {
        await Task.Run(async () =>
        {
            try
            {
                // TODO: Extract metadata using MetadataScanner
                // For now, just mark as scanned
                using var conn = _dataStore.OpenConnection();
                conn.Execute("UPDATE image SET scan_phase = 1 WHERE path = @path AND scan_phase = 0", 
                    new { path = file });
                
                var id = await _dataStore.GetImageIdByPathAsync(file);
                if (id.HasValue)
                {
                    // Use smart queue methods that respect the needs_* flag state
                    // For new images, needs_* will be NULL so they will always be queued
                    // Default to skipping already-processed images (sensible default for auto-scan)
                    if (_settings?.AutoTagOnScan == true)
                    {
                        await _dataStore.SmartQueueForTagging(new List<int> { id.Value }, skipAlreadyProcessed: true);
                    }
                    if (_settings?.AutoCaptionOnScan == true)
                    {
                        await _dataStore.SmartQueueForCaptioning(new List<int> { id.Value }, skipAlreadyProcessed: true);
                    }
                    if (_settings?.AutoFaceDetectionOnScan == true)
                    {
                        // Always skip already-processed for face detection
                        await _dataStore.SmartQueueForFaceDetection(new List<int> { id.Value });
                    }
                    // Note: AutoEmbeddingOnScan not yet supported in WatcherSettings
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error extracting metadata for {file}: {ex.Message}");
            }
        });
    }

    private bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp";
    }

    private void UpdateStatus(string status, int queuedFiles)
    {
        StatusChanged?.Invoke(this, new WatcherStatusEventArgs
        {
            Status = status,
            FilesQueued = queuedFiles
        });
    }

    private string GetConnectionString()
    {
        // Priority: Environment variable > File > Default
        var envConnection = Environment.GetEnvironmentVariable("DIFFUSION_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(envConnection))
            return envConnection;
        
        // Try connection file in app directory
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var connFilePath = Path.Combine(appDir, "database.connection");
        if (File.Exists(connFilePath))
        {
            try
            {
                var fileConn = File.ReadAllText(connFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(fileConn))
                    return fileConn;
            }
            catch { /* Fall through */ }
        }
        
        // Try AppData connection file
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DiffusionToolkit",
            "database.connection");
        if (File.Exists(appDataPath))
        {
            try
            {
                var fileConn = File.ReadAllText(appDataPath).Trim();
                if (!string.IsNullOrWhiteSpace(fileConn))
                    return fileConn;
            }
            catch { /* Fall through */ }
        }
        
        // Default for local Docker
        return AppInfo.DefaultConnectionString;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        _cts.Cancel();
        
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        
        _cts.Dispose();
        _isDisposed = true;
    }
}

public class WatcherStatusEventArgs : EventArgs
{
    public string Status { get; set; } = "";
    public int FilesQueued { get; set; }
}

public class WatcherStatistics
{
    public int WatchedFolders { get; set; }
    public int TotalImages { get; set; }
    public int QuickScannedImages { get; set; }
    public int MetadataScannedImages { get; set; }
    public int FilesInQueue { get; set; }
    public bool MetadataScanEnabled { get; set; }
}

public class ProcessImageRequest
{
    public int Id { get; set; }
    public string? Path { get; set; }
}
