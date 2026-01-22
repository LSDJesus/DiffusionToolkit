using Diffusion.Toolkit.Services;
using Diffusion.Scanner;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Thumbnails;

public class ThumbailResult
{
    public bool Success { get; }
    public BitmapSource? Image { get; }

    public ThumbailResult(BitmapSource image)
    {
        Image = image;
        Success = true;
    }

    private ThumbailResult(bool success)
    {
        Success = success;
    }

    public static ThumbailResult Failed => new ThumbailResult(false);

}

public class ThumbnailService
{
    private static ThumbnailService? _instance;
    private readonly Channel<Job<ThumbnailJob, ThumbailResult>> _channel = Channel.CreateUnbounded<Job<ThumbnailJob, ThumbailResult>>();
    private readonly int _degreeOfParallelism = 2;

    private CancellationTokenSource? cancellationTokenSource;

    private Stream _defaultStream;

    public ThumbnailService()
    {

        _defaultStream = new MemoryStream();

        _enableCache = true;

        //StreamResourceInfo sri = Application.GetResourceStream(new Uri("Images/thumbnail.png", UriKind.Relative));
        //if (sri != null)
        //{
        //    using (Stream s = sri.Stream)
        //    {
        //        sri.Stream.CopyTo(_defaultStream);
        //        _defaultStream.Position = 0;
        //    }
        //}
    }

    private Dispatcher _dispatcher
    {
        get
        {
            return ServiceLocator.Dispatcher ?? throw new InvalidOperationException("ServiceLocator.Dispatcher is not initialized.");
        }
    }


    public void QueueImage(ImageEntry image)
    {
        image.LoadState = LoadState.Loading;

        var job = new ThumbnailJob()
        {
            BatchId = image.BatchId,
            EntryType = image.EntryType,
            Path = image.Path,
            Height = image.Height,
            Width = image.Width,
            IsVideo = image.IsVideo
        };

        _ = QueueAsync(job, (d) =>
        {
            image.LoadState = LoadState.Loaded;

            if (d.Success)
            {
                _dispatcher.Invoke(() =>
                {
                    image.Thumbnail = d.Image;
                    if (d.Image != null)
                    {
                        image.ThumbnailHeight = d.Image.Height;
                        image.ThumbnailWidth = d.Image.Width;
                    }
                });
            }
            else
            {
                _dispatcher.Invoke(() => { image.Unavailable = true; });
            }

            //Debug.WriteLine($"Finished job {job.RequestId}");
            //OnPropertyChanged(nameof(Thumbnail));
        });
    }


    public int Size
    {
        get => _size;
        set
        {
            _size = value;
            ThumbnailCache.Instance.Clear();
        }
    }

    public bool EnableCache
    {
        get => _enableCache;
        set
        {
            _enableCache = value;
            if (!value)
            {
                ThumbnailCache.Instance.Clear();
            }
        }
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        _channel.Writer.Complete();
    }

    public Task Start()
    {
        cancellationTokenSource = new CancellationTokenSource();

        var consumers = new List<Task>();

        for (var i = 0; i < _degreeOfParallelism; i++)
        {
            consumers.Add(ProcessTaskAsync(cancellationTokenSource.Token));
        }

        return Task.WhenAll(consumers);
    }

    public Task StartAsync()
    {
        cancellationTokenSource = new CancellationTokenSource();

        var consumers = new List<Task>();

        for (var i = 0; i < _degreeOfParallelism; i++)
        {
            consumers.Add(Task.Run(async () => await ProcessTaskAsync(cancellationTokenSource.Token)));
        }

        return Task.WhenAll(consumers);
    }

    private int _size = 128;
    private bool _enableCache;


    private Random r = new Random();

    private long _currentBatchId;

    public long StartBatch()
    {
        return _currentBatchId = r.NextInt64(long.MaxValue);
    }


    public void StopCurrentBatch()
    {

    }


    private async Task ProcessTaskAsync(CancellationToken token)
    {

        while (await _channel.Reader.WaitToReadAsync(token))
        {
            try
            {
                var job = await _channel.Reader.ReadAsync(token);

                // Exit early if the batch has changed
                if (job.Data == null || job.Data.BatchId != _currentBatchId)
                {
                    continue;
                }

                if (_enableCache)
                {
                    if (string.IsNullOrEmpty(job.Data.Path) ||
                        !ThumbnailCache.Instance.TryGetThumbnail(job.Data.Path, Size,
                            out BitmapSource? thumbnail))
                    {
                        // Debug.WriteLine($"Loading from disk");
                        if (job.Data.EntryType == EntryType.File)
                        {
                            if (string.IsNullOrEmpty(job.Data.Path) || !File.Exists(job.Data.Path))
                            {
                                job.Completion?.Invoke(ThumbailResult.Failed);
                                continue;
                            }

                            // Generate video thumbnail if it's a video file
                            if (job.Data.IsVideo)
                            {
                                thumbnail = await GetVideoThumbnailAsync(job.Data.Path, Size);
                            }
                            else
                            {
                                thumbnail = GetThumbnailImmediate(job.Data.Path, job.Data.Width, job.Data.Height, Size);
                            }
                        }
                        else if (job.Data.EntryType == EntryType.Model)
                        {
                            // Model thumbnails should already be cached during scanning
                            // If not found, use default model icon
                            thumbnail = GetDefaultModelThumbnailImmediate();
                        }
                        else
                        {
                            thumbnail = GetDefaultThumbnailImmediate();
                        }

                        if (thumbnail != null && !string.IsNullOrEmpty(job.Data.Path))
                        {
                            ThumbnailCache.Instance.AddThumbnail(job.Data.Path, Size, (BitmapImage)thumbnail);
                        }

                        job.Completion?.Invoke(thumbnail != null ? new ThumbailResult(thumbnail) : ThumbailResult.Failed);

                    }
                    else
                    {
                        job.Completion?.Invoke(thumbnail != null ? new ThumbailResult(thumbnail) : ThumbailResult.Failed);
                    }
                }
                else
                {
                    BitmapImage thumbnail;

                    // Debug.WriteLine($"Loading from disk");
                    if (job.Data.EntryType == EntryType.File)
                    {
                        if (!File.Exists(job.Data.Path))
                        {
                            job.Completion?.Invoke(ThumbailResult.Failed);
                            continue;
                        }
                        thumbnail = GetThumbnailImmediate(job.Data.Path, job.Data.Width, job.Data.Height, Size);
                    }
                    else if (job.Data.EntryType == EntryType.Model)
                    {
                        // Model thumbnails should already be cached during scanning
                        // Try to get from cache, otherwise use default
                        if (!string.IsNullOrEmpty(job.Data.Path) &&
                            ThumbnailCache.Instance.TryGetThumbnail(job.Data.Path, Size, out var cachedThumb) && cachedThumb is BitmapImage bmp)
                        {
                            thumbnail = bmp;
                        }
                        else
                        {
                            thumbnail = GetDefaultModelThumbnailImmediate();
                        }
                    }
                    else
                    {
                        thumbnail = GetDefaultThumbnailImmediate();
                    }

                    if (!string.IsNullOrEmpty(job.Data.Path))
                    {
                        ThumbnailCache.Instance.AddThumbnail(job.Data.Path, Size, thumbnail);
                    }

                    job.Completion?.Invoke(new ThumbailResult(thumbnail));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }


        }
        Debug.WriteLine("Shutting down Thumbnail Task");
    }

    public async Task QueueAsync(ThumbnailJob job, Action<ThumbailResult> completion)
    {
        if (cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested)
        {
            await _channel.Writer.WriteAsync(new Job<ThumbnailJob, ThumbailResult>() { Data = job, Completion = completion });
        }
    }

    private static Stream GenerateThumbnail(string path, int width, int height, int size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var bitmap = new BitmapImage();
        //bitmap.UriSource = new Uri(path);
        bitmap.BeginInit();

        if (width > height)
        {
            bitmap.DecodePixelWidth = size;
        }
        else
        {
            bitmap.DecodePixelHeight = size;
        }
        //bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, null));

        var ministream = new MemoryStream();

        encoder.Save(ministream);

        ministream.Seek(0, SeekOrigin.Begin);

        return ministream;
    }

    public BitmapImage GetThumbnailDirect(string path, int width, int height)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        if (File.Exists(path))
        {
            bitmap.StreamSource = GenerateThumbnail(path, width, height, Size);
        }
        else
        {
            bitmap.StreamSource = null;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    //public (BitmapImage, Stream) GetThumbnailImmediateStream(string path, int width, int height, int size)
    //{
    //    var bitmap = new BitmapImage();
    //    bitmap.BeginInit();

    //    Stream stream = null;

    //    if (File.Exists(path))
    //    {
    //        stream = GenerateThumbnail(path, width, height, size);
    //    }

    //    bitmap.StreamSource = stream;

    //    bitmap.EndInit();
    //    bitmap.Freeze();
    //    return (bitmap;
    //}

    public (int, int) GetBitmapSize(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);

            int width = decoder.Frames[0].PixelWidth;
            int height = decoder.Frames[0].PixelHeight;

            return (width, height);
        }
    }

    public BitmapImage GetThumbnailImmediate(string path, int width, int height, int size)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        if (File.Exists(path))
        {
            bitmap.StreamSource = GenerateThumbnail(path, width, height, size);
        }
        else
        {
            bitmap.StreamSource = null;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private BitmapImage GetDefaultThumbnailImmediate()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = _defaultStream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Get a default thumbnail for model resources without a preview
    /// </summary>
    private BitmapImage GetDefaultModelThumbnailImmediate()
    {
        // For now, use the same default as folders
        // TODO: Add a specific model icon resource
        return GetDefaultThumbnailImmediate();
    }

    /// <summary>
    /// Generates a thumbnail from a video file using FFmpeg.
    /// Creates a temporary JPEG file and loads it as BitmapImage.
    /// </summary>
    private async Task<BitmapImage?> GetVideoThumbnailAsync(string videoPath, int size)
    {
        try
        {
            // Initialize FFmpeg if not already done
            if (!VideoThumbnailGenerator.IsInitialized)
            {
                VideoThumbnailGenerator.Initialize();
            }

            // Create temp file for thumbnail
            var tempPath = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid()}.jpg");

            // Generate thumbnail from video (capture at 0.5 seconds)
            bool success = await VideoThumbnailGenerator.GenerateThumbnailAsync(
                videoPath, 
                tempPath, 
                width: size, 
                captureTimeSeconds: 0.5);

            if (!success || !File.Exists(tempPath))
            {
                return null;
            }

            // Load thumbnail into BitmapImage
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(tempPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            // Clean up temp file
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Ignore cleanup errors
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error generating video thumbnail for {videoPath}: {ex.Message}");
            return null;
        }
    }



    private static BitmapImage GetThumbnail(string path, int width, int height)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var bitmap = new BitmapImage();
        //bitmap.UriSource = new Uri(path);
        bitmap.BeginInit();

        var size = 128;
        bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
        if (width > height)
        {
            bitmap.DecodePixelWidth = size;
        }
        else
        {
            bitmap.DecodePixelHeight = size;
        }
        //bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

}