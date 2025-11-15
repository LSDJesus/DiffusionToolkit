using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Diffusion.Tagging.Models;

namespace Diffusion.Tagging.Services;

/// <summary>
/// Service for preprocessing images for JoyTag model input
/// </summary>
public class JoyTagImagePreprocessor
{
    // CLIP normalization values
    private const float MEAN_R = 0.48145466f;
    private const float MEAN_G = 0.4578275f;
    private const float MEAN_B = 0.40821073f;
    private const float STD_R = 0.26862954f;
    private const float STD_G = 0.26130258f;
    private const float STD_B = 0.27577711f;

    /// <summary>
    /// Preprocesses an image for JoyTag prediction
    /// - Resizes to 448x448 with white padding
    /// - Normalizes using CLIP mean/std values
    /// - Returns tensor in NCHW format (1, 3, 448, 448)
    /// </summary>
    public async Task<JoyTagInputData> PreprocessImageAsync(string imagePath)
    {
        var inputData = new JoyTagInputData
        {
            Input = new DenseTensor<float>(new[] { 1, 3, 448, 448 })
        };

        using (var image = await Image.LoadAsync<Rgb24>(imagePath))
        {
            // Resize to 448x448 with white padding
            var resizeOptions = new ResizeOptions
            {
                Mode = ResizeMode.BoxPad,
                Position = AnchorPositionMode.Center,
                Sampler = KnownResamplers.Bicubic,
                PadColor = Color.White,
                Size = new Size(448, 448)
            };

            image.Mutate(ctx => ctx.Resize(resizeOptions));

            // Normalize pixels using CLIP normalization
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var pixelRow = accessor.GetRowSpan(y);
                    for (int x = 0; x < pixelRow.Length; x++)
                    {
                        var pixel = pixelRow[x];

                        // Normalize: (value / 255.0 - mean) / std
                        inputData.Input![0, 0, y, x] = (pixel.R / 255.0f - MEAN_R) / STD_R;
                        inputData.Input[0, 1, y, x] = (pixel.G / 255.0f - MEAN_G) / STD_G;
                        inputData.Input[0, 2, y, x] = (pixel.B / 255.0f - MEAN_B) / STD_B;
                    }
                }
            });
        }

        return inputData;
    }
}
