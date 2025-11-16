using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Diffusion.Tagging.Models;

namespace Diffusion.Tagging.Services;

/// <summary>
/// Service for preprocessing images for WD tagger models
/// Handles WD 1.4, WDv3, and WDv3 Large models
/// </summary>
public class WDImagePreprocessor
{
    /// <summary>
    /// Preprocesses an image for WD tagger prediction
    /// - Resizes to 448x448 with white padding (Lanczos resampling)
    /// - Converts to BGR format
    /// - Returns tensor in NHWC format (1, 448, 448, 3) with raw byte values (0-255)
    /// </summary>
    public async Task<WDInputData> PreprocessImageAsync(string imagePath)
    {
        var inputData = new WDInputData
        {
            Input = new DenseTensor<float>(new[] { 1, 448, 448, 3 })
        };

        using (var image = await Image.LoadAsync<Bgr24>(imagePath))
        {
            // Resize to 448x448 with white padding (matching original WD implementation)
            var resizeOptions = new ResizeOptions
            {
                Mode = ResizeMode.BoxPad,
                Position = AnchorPositionMode.Center,
                Sampler = KnownResamplers.Lanczos3,
                PadColor = Color.White,
                Size = new Size(448, 448)
            };

            image.Mutate(ctx => ctx.Resize(resizeOptions));

            // Convert to tensor - NHWC format with BGR byte values
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var pixelRow = accessor.GetRowSpan(y);
                    for (int x = 0; x < pixelRow.Length; x++)
                    {
                        var pixel = pixelRow[x];

                        // WD models expect raw BGR byte values (0-255)
                        // Note: Bgr24 already has correct color order, but we need to swap R and B
                        inputData.Input![0, y, x, 0] = pixel.B;  // R channel (swapped to match original)
                        inputData.Input[0, y, x, 1] = pixel.G;   // G channel
                        inputData.Input[0, y, x, 2] = pixel.R;   // B channel (swapped to match original)
                    }
                }
            });
        }

        return inputData;
    }
}
