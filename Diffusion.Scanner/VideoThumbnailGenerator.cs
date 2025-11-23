using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xabe.FFmpeg;

namespace Diffusion.Scanner;

/// <summary>
/// Generates thumbnail images from video files using FFmpeg.
/// Extracts a frame from the first second of the video and saves as JPEG.
/// </summary>
public static class VideoThumbnailGenerator
{
    private static bool _ffmpegInitialized = false;
    private static readonly object _initLock = new object();

    /// <summary>
    /// Ensures FFmpeg binaries are available.
    /// Call this once during application startup.
    /// Set FFmpeg path manually if binaries are not in system PATH.
    /// </summary>
    public static void Initialize(string? ffmpegPath = null)
    {
        if (_ffmpegInitialized) return;

        lock (_initLock)
        {
            if (_ffmpegInitialized) return;

            try
            {
                // If custom path provided, use it
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    FFmpeg.SetExecutablesPath(ffmpegPath);
                }
                // Otherwise try application directory
                else
                {
                    var localFfmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
                    if (Directory.Exists(localFfmpegPath) && File.Exists(Path.Combine(localFfmpegPath, "ffmpeg.exe")))
                    {
                        FFmpeg.SetExecutablesPath(localFfmpegPath);
                    }
                    // If not found, assume ffmpeg is in system PATH
                }

                _ffmpegInitialized = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to initialize FFmpeg: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Generates a thumbnail from a video file.
    /// </summary>
    /// <param name="videoPath">Full path to the video file (MP4, AVI, WEBM, etc.)</param>
    /// <param name="outputPath">Full path where thumbnail JPEG should be saved</param>
    /// <param name="width">Maximum width of thumbnail (height auto-calculated to maintain aspect ratio)</param>
    /// <param name="captureTimeSeconds">Time in video to capture frame (default 0.5 seconds)</param>
    /// <returns>True if successful, false otherwise</returns>
    public static async Task<bool> GenerateThumbnailAsync(
        string videoPath, 
        string outputPath, 
        int width = 512, 
        double captureTimeSeconds = 0.5)
    {
        if (!_ffmpegInitialized)
        {
            Initialize();
        }

        if (!File.Exists(videoPath))
        {
            return false;
        }

        try
        {
            // Get video info
            var mediaInfo = await FFmpeg.GetMediaInfo(videoPath);
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();
            
            if (videoStream == null)
            {
                return false; // No video stream found
            }

            // Calculate height to maintain aspect ratio
            var aspectRatio = (double)videoStream.Height / videoStream.Width;
            var height = (int)(width * aspectRatio);

            // Ensure capture time doesn't exceed video duration
            var captureSec = Math.Min(captureTimeSeconds, mediaInfo.Duration.TotalSeconds - 0.1);
            if (captureSec < 0) captureSec = 0;

            var captureTime = TimeSpan.FromSeconds(captureSec);

            // Create snapshot conversion
            var conversion = FFmpeg.Conversions.New()
                .AddParameter($"-ss {captureSec:F3}") // Seek to position
                .AddParameter($"-i \"{videoPath}\"") // Input file
                .AddParameter("-vframes 1") // Extract 1 frame
                .AddParameter($"-vf scale={width}:{height}") // Scale to size
                .AddParameter($"\"{outputPath}\""); // Output file

            // Execute conversion
            await conversion.Start();

            return File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            // Log error but don't throw - thumbnail generation is non-critical
            Console.WriteLine($"Error generating thumbnail for {videoPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Generates a thumbnail synchronously (blocks until complete).
    /// Use this for compatibility with existing synchronous scanning code.
    /// </summary>
    public static bool GenerateThumbnail(
        string videoPath, 
        string outputPath, 
        int width = 512, 
        double captureTimeSeconds = 0.5)
    {
        return GenerateThumbnailAsync(videoPath, outputPath, width, captureTimeSeconds)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Checks if FFmpeg is initialized and ready to generate thumbnails.
    /// </summary>
    public static bool IsInitialized => _ffmpegInitialized;
}
