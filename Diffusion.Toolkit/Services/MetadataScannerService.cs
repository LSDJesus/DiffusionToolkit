using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.IO;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services;

public class ScanCompletionEvent
{
    public Action? OnMetadataCompleted { get; set; }
    public Action? OnDatabaseWriteCompleted { get; set; }
}

public class MetadataScannerService
{
    private Channel<FileScanJob>? _channel;
    private Channel<FileScanJob> _queueChannel = Channel.CreateUnbounded<FileScanJob>();

    private CancellationTokenSource? _cancellationTokenSource;

    private readonly int _degreeOfParallelism = 2;

    private Settings _settings => ServiceLocator.Settings!;


    public async Task QueueBatchAsync(IEnumerable<string> paths, ScanCompletionEvent scanCompletionEvent, CancellationToken cancellationToken)
    {
        var dt = ServiceLocator.DatabaseWriterService.StartAsync(cancellationToken);

        if (!dt.IsStarted)
        {
            // Fire-and-forget: continuation runs asynchronously after database write completes
            if (dt.Task != null)
            {
                _ = dt.Task.ContinueWith(d =>
                {
                    var message = new List<string>();
                    if (d.Result.Added > 0)
                    {
                        message.Add($"{d.Result.Added} images added");
                    }
                    if (d.Result.Updated > 0)
                    {
                        message.Add($"{d.Result.Updated} images updated");
                    }
                    if (d.Result is { Added: 0, Updated: 0 })
                    {
                        message.Add("No images were found");
                    }

                    var toast = string.Join("\r\n", message);

                    if (ServiceLocator.ToastService != null)
                    {
                        ServiceLocator.ToastService.Toast(toast, "");
                    }
                    scanCompletionEvent?.OnDatabaseWriteCompleted?.Invoke();
                    ServiceLocator.ProgressService.CompleteTask();
                    ServiceLocator.ProgressService.ClearProgress();
                    ServiceLocator.ProgressService.SetStatus("");

                    _ = ServiceLocator.FolderService.LoadFolders().ContinueWith(d =>
                    {
                        if (ServiceLocator.Settings != null && ServiceLocator.Settings.AutoRefresh)
                        {
                            ServiceLocator.SearchService.RefreshResults();
                        }
                    });
                });
            }
        }

        var mt = StartAsync(cancellationToken);


        if (!mt.IsStarted)
        {
            // Fire-and-forget: continuation runs asynchronously after metadata scan completes
            if (mt.Task != null)
            {
                _ = mt.Task.ContinueWith(d =>
                {
                    scanCompletionEvent?.OnMetadataCompleted?.Invoke();
                    ServiceLocator.DatabaseWriterService.Complete();
                });
            }
        }

        var i = 0;

        foreach (var path in paths)
        {
            if (_channel == null)
            {
                throw new InvalidOperationException("_channel is not initialized. Call StartAsync() before queuing jobs.");
            }
            await _channel.Writer.WriteAsync(new FileScanJob() { Path = path });

            i++;
            if (i % 33 == 0)
            {
                ServiceLocator.ProgressService.AddTotal(i);
                i = 0;
            }
        }

        ServiceLocator.ProgressService.AddTotal(i);


        if (_channel != null)
        {
            _channel.Writer.Complete();
        }
    }

    private bool _queueRunning;

    public void StartQueue(CancellationToken cancellationToken)
    {
        if (!_queueRunning)
        {
            // Fire-and-forget: background task for processing queue
            _ = Task.Run(async () => await ProcessQueueTaskAsync(cancellationToken));
            _queueRunning = true;
        }
    }

    public async Task QueueAsync(string path, CancellationToken cancellationToken)
    {
        // Fire-and-forget: database writer starts asynchronously
        _ = ServiceLocator.DatabaseWriterService.StartQueueAsync(cancellationToken);
        StartQueue(cancellationToken);

        await _queueChannel.Writer.WriteAsync(new FileScanJob() { Path = path });
    }

    private bool _isStarted;
    private bool _isCompleted;
    private Task<int>? _currentTask;

    public StartResult<int> StartAsync(CancellationToken token)
    {
        Debug.WriteLine("Entering Start");

        if (_isStarted && !_isCompleted)
        {
            return new StartResult<int>() { IsStarted = true, Task = _currentTask };
        }

        _isStarted = true;
        _isCompleted = false;

        ServiceLocator.ProgressService.ResetTotal();

        _cancellationTokenSource = new CancellationTokenSource();
        _channel = Channel.CreateUnbounded<FileScanJob>();

        CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, token);

        var consumers = new List<Task<int>>();

        for (var i = 0; i < _degreeOfParallelism; i++)
        {
            consumers.Add(Task.Run(async () => await ProcessTaskAsync(linkedCts.Token)));
        }

        Debug.WriteLine($"Created {consumers.Count} consumers");

        _currentTask = Task.WhenAll(consumers).ContinueWith(t =>
        {
            _isCompleted = true;

            return consumers.Where(q => q.IsCompletedSuccessfully).Select(p => p.Result).Sum();
        });

        return new StartResult<int>() { Task = _currentTask };
    }

    public void Stop()
    {
        _cancellationTokenSource?.Cancel();
        _channel?.Writer.Complete();
    }

    public void Close()
    {
        if (_channel != null)
        {
            _channel.Writer.Complete();
        }
    }


    private async Task<int> ProcessTaskAsync(CancellationToken token)
    {
        Debug.WriteLine($"Entering ProcessTaskAsync");

        var count = 0;

        if (_channel == null)
        {
            Debug.WriteLine("_channel is null in ProcessTaskAsync");
            return count;
        }

        while (await _channel.Reader.WaitToReadAsync(token))
        {
            var job = await _channel.Reader.ReadAsync(token);

            try
            {
                if (File.Exists(job.Path))
                {
                    var fileParameters = Metadata.ReadFromFile(job.Path);

                    count++;

                    // Only check for hash matches if fileParameters and hash are not null
                    if (fileParameters != null && fileParameters.Hash != null)
                    {
                        if (ServiceLocator.DataStore != null)
                        {
                            var hashMatches = ServiceLocator.DataStore.GetImageIdByHash(fileParameters.Hash);

                            if (hashMatches.Any())
                            {
                                var moved = hashMatches.FirstOrDefault(d => !File.Exists(d.Path));

                                if (moved != null)
                                {
                                    ServiceLocator.DataStore.UpdateImagePath(moved.Id, job.Path);

                                    await ServiceLocator.DatabaseWriterService.QueueUpdateAsync(fileParameters, _settings.StoreMetadata, _settings.StoreWorkflow);
                                    continue;
                                }
                            }
                        }
                    }

                    if (ServiceLocator.DataStore != null && ServiceLocator.DataStore.ImageExists(job.Path))
                    {
                        if (fileParameters != null)
                        {
                            await ServiceLocator.DatabaseWriterService.QueueUpdateAsync(fileParameters, _settings.StoreMetadata, _settings.StoreWorkflow);
                        }
                    }
                    else
                    {
                        if (fileParameters != null)
                        {
                            await ServiceLocator.DatabaseWriterService.QueueAddAsync(fileParameters, _settings.StoreMetadata, _settings.StoreWorkflow);
                        }
                    }
                }
                else
                {
                    await ServiceLocator.DatabaseWriterService.QueueSkipAsync(new FileParameters()
                    {
                        Path = job.Path
                    });

                }

            }
            catch (Exception ex)
            {
                Logger.Log($"Error scanning {job.Path}:" + ex.Message);
            }
        }

        Debug.WriteLine($"Exiting Task... Count: {count}");

        return count;
    }



    private async Task<int> ProcessQueueTaskAsync(CancellationToken token)
    {
        var count = 0;

        while (await _queueChannel.Reader.WaitToReadAsync(token))
        {
            var job = await _queueChannel.Reader.ReadAsync(token);

            try
            {
                if (File.Exists(job.Path))
                {
                    var fileParameters = Metadata.ReadFromFile(job.Path);

                    count++;

                    if (fileParameters != null && fileParameters.Hash != null && ServiceLocator.DataStore != null)
                    {
                        var hashMatches = ServiceLocator.DataStore.GetImageIdByHash(fileParameters.Hash);

                        if (hashMatches.Any())
                        {
                            var moved = hashMatches.FirstOrDefault(d => !File.Exists(d.Path));

                            if (moved != null)
                            {
                                ServiceLocator.DataStore.UpdateImagePath(moved.Id, job.Path);

                                await ServiceLocator.DatabaseWriterService.QueueAsync(fileParameters, QueueType.Move, _settings.StoreMetadata, _settings.StoreWorkflow);

                                continue;
                            }
                        }
                    }

                    if (ServiceLocator.DataStore != null && ServiceLocator.DataStore.ImageExists(job.Path))
                    {
                        if (fileParameters != null)
                        {
                            await ServiceLocator.DatabaseWriterService.QueueAsync(fileParameters, QueueType.Update, _settings.StoreMetadata, _settings.StoreWorkflow);
                        }
                    }
                    else
                    {
                        if (fileParameters != null)
                        {
                            await ServiceLocator.DatabaseWriterService.QueueAsync(fileParameters, QueueType.Add, _settings.StoreMetadata, _settings.StoreWorkflow);
                        }
                    }


                }
                else
                {
                    await ServiceLocator.DatabaseWriterService.QueueAsync(new FileParameters() { Path = job.Path }, QueueType.Skip, _settings.StoreMetadata, _settings.StoreWorkflow);
                }

            }
            catch (Exception ex)
            {
                Logger.Log($"Error scanning {job.Path}:" + ex.Message);
            }
        }

        return count;
    }
}