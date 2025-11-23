using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using Dapper;

namespace Diffusion.Toolkit.Windows
{
    public partial class ImageConversionWindow : Window
    {
        private readonly string _folderPath;
        private readonly int _folderId;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isConverting;

        public ImageConversionWindow(string folderPath, int folderId)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _folderId = folderId;

            // Set window title with folder name
            Title = $"Batch Image Conversion - {Path.GetFileName(folderPath)}";
        }

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (_isConverting) return;

            // Confirm if delete original is checked
            if (DeleteOriginalCheck.IsChecked == true)
            {
                var result = MessageBox.Show(
                    "Are you sure you want to delete the original files after conversion?\n\n" +
                    "This action cannot be undone!",
                    "Confirm Deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _isConverting = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // Update UI
            ConvertButton.IsEnabled = false;
            CancelButton.Content = "Cancel";
            ProgressSection.Visibility = Visibility.Visible;
            SummarySection.Visibility = Visibility.Collapsed;

            try
            {
                await ConvertImagesAsync(_cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                CurrentFileText.Text = "Conversion cancelled by user";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error during conversion: {ex.Message}",
                    "Conversion Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isConverting = false;
                ConvertButton.IsEnabled = true;
                CancelButton.Content = "Close";
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isConverting)
            {
                _cancellationTokenSource?.Cancel();
            }
            else
            {
                Close();
            }
        }

        private async Task ConvertImagesAsync(CancellationToken cancellationToken)
        {
            // Get target format
            var isLossy = WebPRadio.IsChecked == true;
            var targetExtension = ".webp";
            var quality = (int)QualitySlider.Value;
            var deleteOriginal = DeleteOriginalCheck.IsChecked == true;
            var preserveMetadata = PreserveMetadataCheck.IsChecked == true;
            var onlyJpeg = OnlyJpegCheck.IsChecked == true;

            // Get list of images to convert
            var imagePaths = await GetImagePathsAsync(onlyJpeg);
            
            if (imagePaths.Count == 0)
            {
                MessageBox.Show(
                    "No images found to convert in this folder.",
                    "No Images",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            TotalCountText.Text = imagePaths.Count.ToString();
            ConversionProgress.Maximum = imagePaths.Count;

            int processed = 0;
            int succeeded = 0;
            int failed = 0;
            long originalSize = 0;
            long convertedSize = 0;
            var errors = new List<string>();

            foreach (var imagePath in imagePaths)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                try
                {
                    var fileName = Path.GetFileName(imagePath);
                    CurrentFileText.Text = $"Converting: {fileName}";

                    // Get original file size
                    var originalFileInfo = new FileInfo(imagePath);
                    originalSize += originalFileInfo.Length;

                    // Generate output path
                    var outputPath = Path.ChangeExtension(imagePath, targetExtension);

                    // Convert the image
                    await Task.Run(() =>
                    {
                        using var image = Image.Load(imagePath);

                        var encoder = new WebpEncoder
                        {
                            Quality = quality,
                            FileFormat = isLossy ? WebpFileFormatType.Lossy : WebpFileFormatType.Lossless,
                            Method = WebpEncodingMethod.BestQuality
                        };
                        image.Save(outputPath, encoder);
                    }, cancellationToken);

                    // Get converted file size
                    var convertedFileInfo = new FileInfo(outputPath);
                    convertedSize += convertedFileInfo.Length;

                    // Preserve metadata if requested
                    if (preserveMetadata)
                    {
                        await PreserveMetadataAsync(imagePath, outputPath);
                    }

                    // Update database - change file path
                    await UpdateDatabasePathAsync(imagePath, outputPath);

                    // Delete original if requested
                    if (deleteOriginal)
                    {
                        File.Delete(imagePath);
                    }

                    succeeded++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{Path.GetFileName(imagePath)}: {ex.Message}");
                }

                processed++;
                ProcessedCountText.Text = processed.ToString();
                ConversionProgress.Value = processed;

                // Update space saved
                var savedBytes = originalSize - convertedSize;
                var savedMB = savedBytes / (1024.0 * 1024.0);
                SpaceSavedText.Text = $"{savedMB:F2} MB";
            }

            // Show summary
            ProgressSection.Visibility = Visibility.Collapsed;
            SummarySection.Visibility = Visibility.Visible;

            var format = isLossy ? "WebP (Lossy)" : "WebP (Lossless)";
            var summaryLines = new List<string>
            {
                $"Conversion to {format} completed!",
                "",
                $"✓ Successfully converted: {succeeded} images",
                $"✗ Failed: {failed} images",
                $"💾 Space saved: {(originalSize - convertedSize) / (1024.0 * 1024.0):F2} MB ({((1 - (double)convertedSize / originalSize) * 100):F1}% reduction)",
                $"⚙ Quality setting: {quality}%"
            };

            if (deleteOriginal)
            {
                summaryLines.Add($"🗑 Original files deleted: {succeeded}");
            }

            if (errors.Any())
            {
                summaryLines.Add("");
                summaryLines.Add("Errors:");
                summaryLines.AddRange(errors.Take(10).Select(e => $"• {e}"));
                if (errors.Count > 10)
                {
                    summaryLines.Add($"... and {errors.Count - 10} more errors");
                }
            }

            SummaryText.Text = string.Join("\n", summaryLines);
        }

        private async Task<List<string>> GetImagePathsAsync(bool onlyJpeg)
        {
            return await Task.Run(() =>
            {
                using var conn = ServiceLocator.DataStore!.OpenConnection();

                var sql = @"
                    SELECT path 
                    FROM image 
                    WHERE folder_id = @folderId 
                    AND is_video = false";

                if (onlyJpeg)
                {
                    sql += " AND (LOWER(path) LIKE '%.jpg' OR LOWER(path) LIKE '%.jpeg')";
                }
                else
                {
                    sql += " AND (LOWER(path) LIKE '%.jpg' OR LOWER(path) LIKE '%.jpeg' OR LOWER(path) LIKE '%.png')";
                }

                var paths = conn.Query<string>(sql, new { folderId = _folderId }).ToList();

                // Filter to only existing files
                return paths.Where(File.Exists).ToList();
            });
        }

        private async Task PreserveMetadataAsync(string sourcePath, string targetPath)
        {
            // Read metadata from source
            var metadata = await Task.Run(() =>
            {
                using var conn = ServiceLocator.DataStore!.OpenConnection();
                
                var sql = @"
                    SELECT prompt, negative_prompt, steps, sampler, cfg_scale, seed, 
                           width, height, model_name, model_hash
                    FROM image 
                    WHERE path = @path";

                return conn.QueryFirstOrDefault(sql, new { path = sourcePath });
            });

            if (metadata != null)
            {
                // Use MetadataWriter to write to new file
                var metadataRequest = new Scanner.MetadataWriteRequest
                {
                    Prompt = metadata.prompt,
                    NegativePrompt = metadata.negative_prompt,
                    Steps = metadata.steps,
                    Sampler = metadata.sampler,
                    CFGScale = metadata.cfg_scale,
                    Seed = metadata.seed,
                    Width = metadata.width,
                    Height = metadata.height,
                    Model = metadata.model_name,
                    ModelHash = metadata.model_hash
                };

                await Task.Run(() =>
                {
                    Scanner.MetadataWriter.WriteMetadata(targetPath, metadataRequest);
                });
            }
        }

        private async Task UpdateDatabasePathAsync(string oldPath, string newPath)
        {
            await Task.Run(() =>
            {
                using var conn = ServiceLocator.DataStore!.OpenConnection();
                
                var sql = "UPDATE image SET path = @newPath WHERE path = @oldPath";
                conn.Execute(sql, new { oldPath, newPath });
            });
        }
    }
}
