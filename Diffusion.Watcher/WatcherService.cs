using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
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
    
    private Task? _quickScanWorker;
    private Task? _metadataScanWorker;
    private bool _isPaused;
    private bool _isDisposed;
    private bool _isConnected;

    public bool IsPaused => _isPaused;
    public bool IsConnected => _isConnected;
    public bool BackgroundMetadataEnabled { get; set; } = true;

    public event EventHandler<WatcherStatusEventArgs>? StatusChanged;

    public WatcherService() : this(null)
    {
    }

    public WatcherService(string? connectionString)
    {
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
        
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        var tasks = new[] { _quickScanWorker, _metadataScanWorker }
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
        await Task.Run(() =>
        {
            try
            {
                // TODO: Extract metadata using MetadataScanner
                // For now, just mark as scanned
                using var conn = _dataStore.OpenConnection();
                conn.Execute("UPDATE image SET scan_phase = 1 WHERE path = @path AND scan_phase = 0", 
                    new { path = file });
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
