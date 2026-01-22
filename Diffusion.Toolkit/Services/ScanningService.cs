using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Diffusion.Common;
using Diffusion.Database.PostgreSQL;
using Diffusion.Database.PostgreSQL.Models;
using Diffusion.IO;
using Diffusion.Scanner;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Localization;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Services;

public class ScanningService
{
    private EmbeddingExtractor? _embeddingExtractor;

    private EmbeddingExtractor EmbeddingExtractor
    {
        get
        {
            if (_embeddingExtractor == null)
            {
                _embeddingExtractor = new EmbeddingExtractor(ServiceLocator.DataStore!);
            }
            return _embeddingExtractor;
        }
    }

    private string GetLocalizedText(string key)
    {
        try
        {
            return (string?)JsonLocalizationProvider.Instance?.GetLocalizedObject(key, null, CultureInfo.InvariantCulture) ?? key;
        }
        catch
        {
            return key;
        }
    }

    private Settings _settings => ServiceLocator.Settings!;

    private PostgreSQLDataStore _dataStore => ServiceLocator.DataStore!;

    public async Task CheckUnavailableFolders()
    {
        if (ServiceLocator.MainModel == null || ServiceLocator.MainModel.FoldersBusy) return;

        await Task.Run(() =>
        {
            ServiceLocator.MainModel.FoldersBusy = true;

            try
            {
                foreach (var folder in ServiceLocator.FolderService.RootFolders)
                {
                    // Check if we can access the path
                    if (Directory.Exists(folder.Path))
                    {
                        if (folder is { Unavailable: true })
                        {
                            _dataStore.SetFolderUnavailable(folder.Id, false, true);
                        }
                    }
                    else
                    {
                        _dataStore.SetFolderUnavailable(folder.Id, true, true);
                    }
                }
            }
            finally
            {
                ServiceLocator.MainModel.FoldersBusy = false;
            }


        });

    }

    private int CheckFilesUnavailable(List<Diffusion.Database.PostgreSQL.Models.ImagePath> folderImages, HashSet<string> folderImagesHashSet, CancellationToken cancellationToken)
    {
        int unavailable = 0;

        //folderImagesHashSet
        var unavailableFiles = new HashSet<string>();

        ServiceLocator.ProgressService.InitializeProgress(folderImagesHashSet.Count);

        var i = 0;

        foreach (var folderImage in folderImagesHashSet)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!File.Exists(folderImage))
            {
                unavailableFiles.Add(folderImage);
            }

            if (i % 33 == 0)
            {
                ServiceLocator.ProgressService.SetProgress(i, "Scanning {current} of {total}");
            }

            i++;
        }


        if (!cancellationToken.IsCancellationRequested)
        {
            //var allDirectoryFiles = MetadataScanner.GetFiles(path, settings.FileExtensions, true, cancellationToken).ToHashSet();

            //var unavailableFiles = folderImagesHashSet.Except(allDirectoryFiles).ToHashSet();


            var unavailableIds = new List<int>();

            foreach (var image in folderImages.Where(f => unavailableFiles.Contains(f.Path)))
            {
                unavailableIds.Add(image.Id);
            }

            var currentUnavailable = _dataStore.GetUnavailable(true);

            var newlyUnavailable = unavailableIds.Except(currentUnavailable.Select(i => i.Id)).ToList();

            foreach (var chunk in newlyUnavailable.Chunk(100))
            {
                _dataStore.SetUnavailable(chunk, true);
            }

            unavailable += newlyUnavailable.Count;
        }

        return unavailable;

    }


    public async Task ScanFolder(FolderViewModel folder, bool fullScan)
    {
        if (await ServiceLocator.ProgressService.TryStartTask())
        {
            var filesToScan = new List<string>();

            var cancellationToken = ServiceLocator.ProgressService.CancellationToken;

            var selectedFolders = ServiceLocator.FolderService.SelectedFolders.ToList();

            foreach (var selectedFolder in selectedFolders)
            {
                var existingFiles = fullScan
                    ? new HashSet<string>()
                    : ServiceLocator.FolderService.GetFiles(selectedFolder.Id, true).Select(d => d.Path).ToHashSet();

                filesToScan.AddRange(await ServiceLocator.ScanningService.GetFilesToScan(selectedFolder.Path, selectedFolder.IsRecursive, existingFiles, cancellationToken));
            }

            await ServiceLocator.MetadataScannerService.QueueBatchAsync(filesToScan,
                new ScanCompletionEvent()
                {
                    OnDatabaseWriteCompleted = () =>
                    {
                        if (folder.Id == 0)
                        {
                            var dataStore = ServiceLocator.DataStore;
                            if (dataStore != null)
                            {
                                var dbEntity = dataStore.GetFolder(folder.Path);
                                if (dbEntity != null)
                                {
                                    folder.IsScanned = true;
                                    folder.Id = dbEntity.Id;
                                }
                            }
                        }
                        ServiceLocator.FolderService.RefreshData();
                        ServiceLocator.SearchService.RefreshResults();
                        // Fire-and-forget: folder refresh runs asynchronously in background
                        _ = ServiceLocator.FolderService.LoadFolders();
                    }
                },
                cancellationToken);
        }
    }

    public async Task<IEnumerable<string>> GetFilesToScan(string path, bool recursive, HashSet<string> ignoreFiles, CancellationToken cancellationToken)
    {
        var excludePaths = ServiceLocator.FolderService.ExcludedOrArchivedFolderPaths;

        return await Task.Run(() => MetadataScanner.GetFiles(path, _settings.FileExtensions, ignoreFiles, recursive, excludePaths, cancellationToken).ToList());
    }

    public async Task ScanWatchedFolders(bool updateImages, bool reportIfNone, CancellationToken cancellationToken)
    {
        Logger.Log($"ScanWatchedFolders called - updateImages={updateImages}, reportIfNone={reportIfNone}");
        
        // Ensure required services are available
        if (ServiceLocator.DataStore == null)
        {
            Logger.Log("ScanWatchedFolders: DataStore is null, aborting");
            return;
        }
        
        if (ServiceLocator.FolderService == null)
        {
            Logger.Log("ScanWatchedFolders: FolderService is null, aborting");
            return;
        }
   

        ServiceLocator.ProgressService?.SetStatus(GetLocalizedText("Actions.Scanning.BeginScanning"));

        try
        {
            var filesToScan = new List<string>();

            var gatheringFilesMessage = GetLocalizedText("Actions.Scanning.GatheringFiles");

            var excludedPaths = ServiceLocator.FolderService.ExcludedOrArchivedFolderPaths.ToHashSet();

            // Gather files asynchronously to avoid blocking
            await Task.Run(() =>
            {
                foreach (var folder in ServiceLocator.FolderService.RootFolders)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (Directory.Exists(folder.Path))
                    {
                        var folderImages = _dataStore.GetAllPathImages(folder.Path).ToList();

                        var folderImagesHashSet = folderImages.Select(p => p.Path).ToHashSet();

                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (ServiceLocator.ProgressService != null)
                        {
                            ServiceLocator.ProgressService.SetStatus(gatheringFilesMessage.Replace("{path}", folder.Path));
                        }

                        var ignoreFiles = updateImages ? null : folderImagesHashSet;

                        filesToScan.AddRange(MetadataScanner.GetFiles(folder.Path, _settings.FileExtensions, ignoreFiles, folder.Recursive, excludedPaths, cancellationToken).ToList());
                    }
                    else
                    {
                        _dataStore.SetFolderUnavailable(folder.Id, true, true);
                    }
                }
            }, cancellationToken);

            Logger.Log($"ScanWatchedFolders: Gathered {filesToScan.Count} files to scan, updateImages={updateImages}");

            // Phase 1: Quick scan - add basic file info rapidly (only for new files)
            if (!updateImages)
            {
                Logger.Log($"ScanWatchedFolders: Entering QUICK SCAN path with {filesToScan.Count} files");
                var addedCount = await QuickScanFiles(filesToScan, cancellationToken);

                if (addedCount > 0)
                {
                    Logger.Log($"Quick scan complete: {addedCount} new files indexed. Use 'Rebuild' to extract full metadata.");
                    ServiceLocator.ToastService?.Toast($"{addedCount} images quick-scanned. Use Rebuild for full metadata extraction.", "Quick Scan Complete");
                    
                    // Refresh UI immediately after quick scan so images appear right away
                    ServiceLocator.SearchService?.RefreshResults();
                }
                else
                {
                    Logger.Log("Quick scan complete: No new files found.");
                    ServiceLocator.ToastService?.Toast("No new images found", "Quick Scan Complete");
                }
            }
            else
            {
                // Phase 2: Full metadata extraction (Rebuild mode)
                Logger.Log($"Starting full metadata extraction for {filesToScan.Count} files...");
                await ServiceLocator.MetadataScannerService.QueueBatchAsync(filesToScan, null, cancellationToken);
            }

        }
        catch (Exception ex)
        {
            await ServiceLocator.MessageService.ShowMedium(ex.Message,
                "Scan Error", PopupButtons.OK);
        }
        finally
        {
            // Always clear status to prevent frozen progress bar
            ServiceLocator.ProgressService?.ClearStatus();
        }
    }

    /// <summary>
    /// Scan a newly added folder with two-phase approach:
    /// Phase 1: Quick scan for immediate visibility
    /// Phase 2: Automatic background deep scan for metadata
    /// </summary>
    public async Task ScanNewFolder(List<string> filePaths, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0) return;

        Logger.Log($"ScanNewFolder: Starting two-phase scan for {filePaths.Count} files");

        // Phase 1: Quick scan - adds files with basic info immediately
        var addedCount = await QuickScanFiles(filePaths, cancellationToken);

        if (addedCount > 0)
        {
            Logger.Log($"ScanNewFolder: Quick scan added {addedCount} files. Starting background metadata extraction...");
            ServiceLocator.ToastService?.Toast($"{addedCount} images indexed. Extracting metadata in background...", "Scanning");
            
            // Refresh UI so quick-scanned images appear immediately
            ServiceLocator.SearchService.RefreshResults();

            // Phase 2: Automatically start deep scan in background (non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    // Small delay to let UI settle
                    await Task.Delay(2000, cancellationToken);
                    
                    Logger.Log("ScanNewFolder: Starting background deep scan...");
                    await DeepScanPendingImages(batchSize: 200, cancellationToken);
                    
                    Logger.Log("ScanNewFolder: Background metadata extraction complete");
                    if (ServiceLocator.ToastService != null)
                    {
                        ServiceLocator.ToastService.Toast("Metadata extraction complete", "Scanning");
                    }
                    ServiceLocator.SearchService.RefreshResults();
                }
                catch (Exception ex)
                {
                    Logger.Log($"ScanNewFolder background deep scan error: {ex.Message}");
                }
            }, cancellationToken);
        }
        else
        {
            Logger.Log("ScanNewFolder: No new files found");
            if (ServiceLocator.ToastService != null)
            {
                ServiceLocator.ToastService.Toast("No new images found", "Scanning");
            }
        }
    }

    /// <summary>
    /// Phase 1: Quick scan that rapidly indexes files with basic information
    /// Returns the number of files actually added to the database
    /// </summary>
    private async Task<int> QuickScanFiles(List<string> filePaths, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0) return 0;

        ServiceLocator.ProgressService.SetStatus($"Quick scanning {filePaths.Count} files...");

        return await Task.Run(() =>
        {
            var fileInfoList = new List<(string path, long fileSize, DateTime createdDate, DateTime modifiedDate)>();

            // Gather basic file info (very fast - no metadata parsing)
            foreach (var filePath in filePaths)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Exists)
                    {
                        fileInfoList.Add((
                            filePath,
                            fileInfo.Length,
                            fileInfo.CreationTimeUtc,
                            fileInfo.LastWriteTimeUtc
                        ));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Quick scan error for {filePath}: {ex.Message}");
                }
            }

            if (fileInfoList.Count == 0) return 0;

            // Bulk insert into database
            using var conn = _dataStore.OpenConnection();
            
            // Pre-populate folder cache to avoid queries during COPY operation
            var folderCache = conn.Query<Folder>("SELECT id, parent_id, root_folder_id, path, image_count, scanned_date, unavailable, archived, excluded, is_root FROM folder")
                .ToDictionary(f => f.Path, f => f);
            
            var added = _dataStore.QuickAddImages(conn, fileInfoList, folderCache, cancellationToken);
            
            Logger.Log($"Quick scan complete: {added} files indexed out of {fileInfoList.Count} candidates");
            
            // Clear the progress status
            ServiceLocator.ProgressService.ClearStatus();
            
            return added;
        }, cancellationToken);
    }

    /// <summary>
    /// Background task: Deep scan images that were quick-scanned
    /// </summary>
    public async Task DeepScanPendingImages(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pendingPaths = _dataStore.GetQuickScannedPaths(limit: batchSize).ToList();
                
                if (pendingPaths.Count == 0)
                {
                    break; // No more pending images
                }

                ServiceLocator.ProgressService.SetStatus($"Deep scanning {pendingPaths.Count} images...");
                
                await ServiceLocator.MetadataScannerService.QueueBatchAsync(pendingPaths, null, cancellationToken);
                
                // Wait for batch to complete
                await Task.Delay(1000, cancellationToken);
            }

            Logger.Log("Deep scan complete");
        }
        finally
        {
            // Always clear status to prevent frozen progress bar
            ServiceLocator.ProgressService.ClearStatus();
        }
    }


    public (Image, IReadOnlyCollection<IO.Node>) ProcessFile(FileParameters file, bool storeMetadata, bool storeWorkflow)
    {
        var newNodes = new List<IO.Node>();

        var fileInfo = new FileInfo(file.Path);

        var image = new Image()
        {
            Prompt = file.Prompt,
            NegativePrompt = file.NegativePrompt,
            Path = file.Path,
            FileName = fileInfo.Name,
            Width = file.Width,
            Height = file.Height,
            ModelHash = file.ModelHash,
            Model = file.Model,
            Steps = file.Steps,
            Sampler = file.Sampler,
            CfgScale = file.CFGScale,
            Seed = file.Seed,
            BatchPos = file.BatchPos,
            BatchSize = file.BatchSize,
            CreatedDate = fileInfo.CreationTime,
            ModifiedDate = fileInfo.LastWriteTime,
            AestheticScore = file.AestheticScore,
            HyperNetwork = file.HyperNetwork,
            HyperNetworkStrength = file.HyperNetworkStrength,
            ClipSkip = file.ClipSkip,
            FileSize = file.FileSize,
            NoMetadata = file.NoMetadata,
            WorkflowId = file.WorkflowId,
            HasError = file.HasError,
            Hash = file.Hash,
            ScanPhase = 1  // Mark as fully scanned with metadata (DeepScan)
        };

        if (storeMetadata)
        {
            image.Workflow = file.Workflow;
        }

        if (!string.IsNullOrEmpty(file.HyperNetwork) && !file.HyperNetworkStrength.HasValue)
        {
            image.HyperNetworkStrength = 1;
        }

        if (_settings.AutoTagNSFW)
        {
            if (_settings.NSFWTags.Any(t => image.Prompt != null && image.Prompt.ToLower().Contains(t.Trim().ToLower())))
            {
                image.NSFW = true;
            }
        }

        if (storeWorkflow && file.Nodes is { Count: > 0 })
        {
            foreach (var fileNode in file.Nodes)
            {
                fileNode.ImageRef = image;
            }

            newNodes.AddRange(file.Nodes);
        }

        // Extract embeddings from prompts
        _ = ProcessEmbeddingsAsync(file, CancellationToken.None);

        return (image, newNodes);
    }

    /// <summary>
    /// Extract and store embeddings from file parameters asynchronously (fire-and-forget)
    /// </summary>
    private async Task ProcessEmbeddingsAsync(FileParameters file, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file.Prompt) && string.IsNullOrWhiteSpace(file.NegativePrompt))
                return;

            // Extract embeddings (explicit + implicit matching)
            var embeddings = await EmbeddingExtractor.ExtractEmbeddingsAsync(
                file.Prompt ?? string.Empty,
                file.NegativePrompt ?? string.Empty);

            if (embeddings?.Count > 0)
            {
                // Store on FileParameters for later processing in AddImages
                file.ProcessedEmbeddings = embeddings;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ProcessEmbeddingsAsync failed for {file.Path}: {ex.Message}");
            // Don't throw - embedding extraction is optional
        }
    }

    private readonly object _lock = new object();

    public int UpdateImages(IReadOnlyCollection<Image> images, IReadOnlyCollection<IO.Node> nodes, IReadOnlyCollection<string> includeProperties, Dictionary<string, Folder> folderCache, bool storeWorkflow, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            using var db = _dataStore.OpenConnection();

            int updated = 0;

            try
            {
                //db.BeginTransaction();

                updated =
                    _dataStore.UpdateImagesByPath(db, images, includeProperties, folderCache, cancellationToken);

                if (storeWorkflow && nodes.Any())
                {
                    _dataStore.UpdateNodes(db, nodes, cancellationToken);
                }

                //db.Commit();

            }
            catch (Exception ex)
            {
                //db.Rollback();
                Logger.Log("UpdateImages: " + ex.Message + " " + ex.StackTrace);
            }

            return updated;
        }
    }

    public int AddImages(IReadOnlyCollection<Image> images, IReadOnlyCollection<IO.Node> nodes, IReadOnlyCollection<string> includeProperties, Dictionary<string, Folder> folderCache, bool storeWorkflow, CancellationToken cancellationToken)
    {
        lock (_lock)
        {

            using var db = _dataStore.OpenConnection();

            int added = 0;

            try
            {
                //db.BeginTransaction();

                added = _dataStore.AddImages(db, images, includeProperties, folderCache, cancellationToken);

                if (storeWorkflow && nodes.Any())
                {
                    _dataStore.AddNodes(db, nodes, cancellationToken);
                }

                //db.Commit();

            }
            catch (Exception ex)
            {
                // db.Rollback();
                Logger.Log("AddImages: " + ex.Message + " " + ex.StackTrace);
            }

            return added;
        }
    }


    public (int, long) ScanFiles(IList<string> filesToScan, bool updateImages, bool storeMetadata, bool storeWorkflow, CancellationToken cancellationToken)
    {
        //foreach (var file in filesToScan)
        //{
        //    _ = ServiceLocator.MetadataScannerService.QueueAsync(file);
        //}

        try
        {
            var added = 0;
            var scanned = 0;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var max = filesToScan.Count;

            ServiceLocator.ProgressService.InitializeProgress(max);

            var folderIdCache = new Dictionary<string, int>();

            var newImages = new List<Image>();
            var newNodes = new List<IO.Node>();

            var includeProperties = new List<string>();

            if (_settings.AutoTagNSFW)
            {
                includeProperties.Add(nameof(Image.NSFW));
            }

            if (storeMetadata)
            {
                includeProperties.Add(nameof(Image.Workflow));
            }

            var scanning = GetLocalizedText("Actions.Scanning.Status");

            foreach (var file in MetadataScanner.Scan(filesToScan))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                scanned++;



                // var (image, nodes) = ProcessFile(file, storeMetadata, storeWorkflow);

                //var fileInfo = new FileInfo(file.Path);

                //var image = new Image()
                //{
                //    Prompt = file.Prompt,
                //    NegativePrompt = file.NegativePrompt,
                //    Path = file.Path,
                //    FileName = fileInfo.Name,
                //    Width = file.Width,
                //    Height = file.Height,
                //    ModelHash = file.ModelHash,
                //    Model = file.Model,
                //    Steps = file.Steps,
                //    Sampler = file.Sampler,
                //    CFGScale = file.CFGScale,
                //    Seed = file.Seed,
                //    BatchPos = file.BatchPos,
                //    BatchSize = file.BatchSize,
                //    CreatedDate = fileInfo.CreationTime,
                //    ModifiedDate = fileInfo.LastWriteTime,
                //    AestheticScore = file.AestheticScore,
                //    HyperNetwork = file.HyperNetwork,
                //    HyperNetworkStrength = file.HyperNetworkStrength,
                //    ClipSkip = file.ClipSkip,
                //    FileSize = file.FileSize,
                //    NoMetadata = file.NoMetadata,
                //    WorkflowId = file.WorkflowId,
                //    HasError = file.HasError
                //};

                //if (storeMetadata)
                //{
                //    image.Workflow = file.Workflow;
                //}

                //if (!string.IsNullOrEmpty(file.HyperNetwork) && !file.HyperNetworkStrength.HasValue)
                //{
                //    image.HyperNetworkStrength = 1;
                //}

                //if (_settings.AutoTagNSFW)
                //{
                //    if (_settings.NSFWTags.Any(t => image.Prompt != null && image.Prompt.ToLower().Contains(t.Trim().ToLower())))
                //    {
                //        image.NSFW = true;
                //    }
                //}

                //if (storeWorkflow && file.Nodes is { Count: > 0 })
                //{
                //    foreach (var fileNode in file.Nodes)
                //    {
                //        fileNode.ImageRef = image;
                //    }

                //    newNodes.AddRange(file.Nodes);
                //}

                //newImages.Add(image);
                //newNodes.AddRange(nodes);

                //if (newImages.Count == 100)
                //{
                //    if (updateImages)
                //    {
                //        added += UpdateImages(newImages, newNodes, includeProperties, folderIdCache, storeWorkflow, cancellationToken);
                //    }
                //    else
                //    {
                //        added += AddImages(newImages, newNodes, includeProperties, folderIdCache, storeWorkflow, cancellationToken);
                //    }

                //    newNodes.Clear();
                //    newImages.Clear();
                //}

                //if (scanned % 33 == 0)
                //{
                //    ServiceLocator.ProgressService.SetProgress(scanned, scanning);
                //}
            }

            //if (newImages.Count > 0)
            //{
            //    if (updateImages)
            //    {
            //        added += UpdateImages(newImages, newNodes, includeProperties, folderIdCache, storeWorkflow, cancellationToken);
            //    }
            //    else
            //    {
            //        added += AddImages(newImages, newNodes, includeProperties, folderIdCache, storeWorkflow, cancellationToken);
            //    }
            //}

            stopwatch.Stop();

            var elapsedTime = stopwatch.ElapsedMilliseconds / 1000;

            return (added, elapsedTime);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            ServiceLocator.ProgressService.ClearProgress();
        }

    }

    public async Task ScanUnavailable(UnavailableFilesModel options, CancellationToken token)
    {
        var candidateImages = new List<int>();
        var restoredImages = new List<int>();

        try
        {
            if (options.UseRootFolders)
            {
                var rootFolders = options.ImagePaths.Where(f => f.IsSelected);

                var total = 0;

                foreach (var folder in rootFolders)
                {
                    total += _dataStore.CountAllPathImages(folder.Path);
                }

                ServiceLocator.ProgressService.InitializeProgress(total);

                var current = 0;

                var scanning = GetLocalizedText("Actions.Scanning.Status");

                var excludePaths = ServiceLocator.FolderService.ExcludedOrArchivedFolderPaths;

                foreach (var folder in rootFolders)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    HashSet<string> ignoreFiles = new HashSet<string>();

                    var folderImages = _dataStore.GetAllPathImages(folder.Path);

                    // Remove images from excluded paths, otherwise they appear as Unavailable
                    folderImages = folderImages.Where(d => !excludePaths.Any(p =>
                        p.Equals(Path.GetDirectoryName(d.Path), StringComparison.OrdinalIgnoreCase)));

                    var imageLookup = folderImages.ToDictionary(d => d.Path);


                    if (Directory.Exists(folder.Path))
                    {
                        //var filesOnDisk = MetadataScanner.GetFiles(folder.Path, _settings.FileExtensions, null, _settings.RecurseFolders.GetValueOrDefault(true), null);
                        var filesOnDisk = MetadataScanner.GetFiles(folder.Path, _settings.FileExtensions, ignoreFiles, folder.Recursive, excludePaths, token);

                        foreach (var file in filesOnDisk)
                        {
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            if (imageLookup.TryGetValue(file, out var imagePath))
                            {
                                if (imagePath.Unavailable)
                                {
                                    restoredImages.Add(imagePath.Id);
                                }

                                imageLookup.Remove(file);
                            }

                            current++;

                            if (current % 113 == 0)
                            {
                                ServiceLocator.ProgressService.SetProgress(current, scanning);
                            }
                        }

                        foreach (var image in imageLookup)
                        {
                            candidateImages.Add(image.Value.Id);
                        }
                    }
                    else
                    {
                        if (options.ShowUnavailableRootFolders)
                        {
                            foreach (var image in imageLookup)
                            {
                                candidateImages.Add(image.Value.Id);

                                current++;

                                if (current % 113 == 0)
                                {
                                    ServiceLocator.ProgressService.SetProgress(current, scanning);
                                }
                            }
                        }

                    }
                }

                ServiceLocator.ProgressService.SetProgress(total);
            }


            if (restoredImages.Any())
            {
                _dataStore.SetUnavailable(restoredImages, false);
            }

            var unavailableFiles = GetLocalizedText("UnavailableFiles");

            if (candidateImages.Any())
            {
                if (options.JustUpdate)
                {
                    //var currentUnavailableImages = _dataStore.GetUnavailable(true);
                    //candidateImages = candidateImages.Except(currentUnavailableImages.Select(i => i.Id)).ToList();
                    _dataStore.SetUnavailable(candidateImages, true);

                    var updated = GetLocalizedText("UnavailableFiles.Results.Updated");
                    updated = updated.Replace("{count}", $"{candidateImages.Count:#,###,##0}");

                    if (restoredImages.Any())
                    {
                        var restored = GetLocalizedText("UnavailableFiles.Results.Restored");
                        updated += " " + restored.Replace("{count}", $"{candidateImages.Count:#,###,##0}");
                    }

                    await ServiceLocator.MessageService.Show(updated, unavailableFiles, PopupButtons.OK);
                }
                else if (options.MarkForDeletion)
                {
                    //var currentUnavailableImages = _dataStore.GetUnavailable(true);
                    //candidateImages = candidateImages.Except(currentUnavailableImages.Select(i => i.Id)).ToList();

                    _dataStore.SetUnavailable(candidateImages, true);
                    _dataStore.SetDeleted(candidateImages, true);

                    var marked = GetLocalizedText("UnavailableFiles.Results.MarkedForDeletion");
                    marked = marked.Replace("{count}", $"{candidateImages.Count:#,###,##0}");

                    if (restoredImages.Any())
                    {
                        var restored = GetLocalizedText("UnavailableFiles.Results.Restored");
                        marked += " " + restored.Replace("{count}", $"{candidateImages.Count:#,###,##0}");
                    }

                    await ServiceLocator.MessageService.Show(marked, unavailableFiles, PopupButtons.OK);
                }
                else if (options.RemoveImmediately)
                {
                    _dataStore.RemoveImages(candidateImages);

                    var removed = GetLocalizedText("UnavailableFiles.Results.Removed");
                    removed = removed.Replace("{count}", $"{candidateImages.Count:#,###,##0}");

                    if (restoredImages.Any())
                    {
                        var restored = GetLocalizedText("UnavailableFiles.Results.Restored");
                        removed += " " + restored.Replace("{count}", $"{candidateImages.Count:#,###,##0}");
                    }


                    await ServiceLocator.MessageService.Show(removed, unavailableFiles, PopupButtons.OK);
                }
            }
        }
        finally
        {
            ServiceLocator.ProgressService.ClearProgress();
        }
    }


    public void Report(int added, int unavailable, long elapsedTime, bool updateImages, bool foldersUnavailable, bool foldersRestored)
    {
        var scanComplete = updateImages
            ? GetLocalizedText("Actions.Scanning.RebuildComplete.Caption")
            : GetLocalizedText("Actions.Scanning.ScanComplete.Caption");

        if (added == 0 && unavailable == 0)
        {
            var message = GetLocalizedText("Actions.Scanning.NoNewImages.Toast");
            if (ServiceLocator.ToastService != null)
            {
                ServiceLocator.ToastService.Toast(message, scanComplete);
            }
        }
        else
        {
            var updatedMessage = GetLocalizedText("Actions.Scanning.ImagesUpdated.Toast");
            var addedMessage = GetLocalizedText("Actions.Scanning.ImagesAdded.Toast");
            var unavailableMessage = GetLocalizedText("Actions.Scanning.FilesUnavailable.Toast");
            var foldersUnavailableMessage = GetLocalizedText("Actions.Scanning.FoldersUnavailable.Toast");
            var foldersRestoredMessage = GetLocalizedText("Actions.Scanning.FoldersRestored.Toast");

            updatedMessage = updatedMessage.Replace("{count}", $"{added:#,###,##0}");
            addedMessage = addedMessage.Replace("{count}", $"{added:#,###,##0}");
            unavailableMessage = unavailableMessage.Replace("{count}", $"{unavailable:#,###,##0}");

            var newOrOpdated = updateImages ? updatedMessage : addedMessage;

            var messages = new[]
             {
                        added > 0 ? newOrOpdated : string.Empty,
                        unavailable > 0 ? unavailableMessage : string.Empty,
                        foldersUnavailable ? foldersUnavailableMessage : string.Empty,
                        foldersRestored ? foldersRestoredMessage : string.Empty,
                    };

            foreach (var message in messages.Where(m => !string.IsNullOrEmpty(m)))
            {
                if (ServiceLocator.ToastService != null)
                {
                    ServiceLocator.ToastService.Toast(message, scanComplete, 5);
                }
            }

        }

        SetTotalFilesStatus();
    }

    public void SetTotalFilesStatus()
    {
        var total = _dataStore.GetTotal();

        ServiceLocator.ProgressService.SetStatus($"{total:###,###,##0} images in database");
    }

}