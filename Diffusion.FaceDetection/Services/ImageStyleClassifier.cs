using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Diffusion.Common;

namespace Diffusion.FaceDetection.Services;

/// <summary>
/// Image style types for face detection routing
/// </summary>
public enum ImageStyle
{
    /// <summary>Photorealistic human faces - use ArcFace</summary>
    Realistic,
    
    /// <summary>Anime/cartoon/illustration - use CLIP + anime detector</summary>
    Anime,
    
    /// <summary>3D CGI/rendered - use CLIP + ArcFace blend</summary>
    ThreeD,
    
    /// <summary>Mixed or uncertain style - use ensemble</summary>
    Mixed
}

/// <summary>
/// Lightweight style classifier for routing images to appropriate face detection pipeline.
/// Uses heuristics and edge/color analysis rather than a trained model for simplicity.
/// </summary>
public class ImageStyleClassifier
{
    /// <summary>
    /// Classify image style using visual heuristics
    /// </summary>
    public ImageStyle Classify(string imagePath)
    {
        try
        {
            using var image = Image.Load<Rgb24>(imagePath);
            return ClassifyImage(image);
        }
        catch (Exception ex)
        {
            Logger.Log($"Style classification failed: {ex.Message}");
            return ImageStyle.Mixed; // Default to mixed/ensemble
        }
    }

    /// <summary>
    /// Classify from loaded image
    /// </summary>
    public ImageStyle ClassifyImage(Image<Rgb24> image)
    {
        // Resize for faster analysis
        using var thumbnail = image.Clone(ctx => ctx.Resize(256, 256));
        
        // Calculate various metrics
        var colorMetrics = AnalyzeColors(thumbnail);
        var edgeMetrics = AnalyzeEdges(thumbnail);
        var textureMetrics = AnalyzeTexture(thumbnail);
        
        // Anime indicators:
        // - High color saturation with limited palette
        // - Strong, clean edges (cell shading)
        // - Smooth textures (no skin pores)
        var animeScore = 0f;
        animeScore += colorMetrics.Saturation > 0.5f ? 0.3f : 0f;
        animeScore += colorMetrics.UniqueColorRatio < 0.3f ? 0.3f : 0f;
        animeScore += edgeMetrics.EdgeSharpness > 0.6f ? 0.2f : 0f;
        animeScore += textureMetrics.Smoothness > 0.7f ? 0.2f : 0f;
        
        // Realistic indicators:
        // - Natural color distribution
        // - Soft edges with gradients
        // - Complex textures (skin, hair detail)
        var realisticScore = 0f;
        realisticScore += colorMetrics.Saturation < 0.4f ? 0.2f : 0f;
        realisticScore += colorMetrics.UniqueColorRatio > 0.5f ? 0.3f : 0f;
        realisticScore += textureMetrics.Complexity > 0.5f ? 0.3f : 0f;
        realisticScore += edgeMetrics.EdgeSharpness < 0.4f ? 0.2f : 0f;
        
        // 3D CGI indicators:
        // - High saturation but complex colors
        // - Clean but soft edges
        // - Medium texture complexity
        var threeDScore = 0f;
        threeDScore += colorMetrics.Saturation > 0.4f && colorMetrics.UniqueColorRatio > 0.4f ? 0.3f : 0f;
        threeDScore += edgeMetrics.EdgeSharpness > 0.3f && edgeMetrics.EdgeSharpness < 0.6f ? 0.3f : 0f;
        threeDScore += textureMetrics.Complexity > 0.3f && textureMetrics.Smoothness > 0.4f ? 0.4f : 0f;
        
        // Determine winner
        var maxScore = Math.Max(Math.Max(animeScore, realisticScore), threeDScore);
        
        if (maxScore < 0.4f)
        {
            return ImageStyle.Mixed;
        }
        
        if (animeScore >= realisticScore && animeScore >= threeDScore)
        {
            return ImageStyle.Anime;
        }
        if (realisticScore >= animeScore && realisticScore >= threeDScore)
        {
            return ImageStyle.Realistic;
        }
        return ImageStyle.ThreeD;
    }

    private (float Saturation, float UniqueColorRatio) AnalyzeColors(Image<Rgb24> image)
    {
        var saturationSum = 0f;
        var colorSet = new HashSet<int>();
        var pixelCount = 0;
        
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    var p = row[x];
                    
                    // Calculate saturation
                    var max = Math.Max(Math.Max(p.R, p.G), p.B);
                    var min = Math.Min(Math.Min(p.R, p.G), p.B);
                    var saturation = max > 0 ? (max - min) / (float)max : 0f;
                    saturationSum += saturation;
                    
                    // Quantize color for unique count (reduce to 32 levels per channel)
                    var quantized = ((p.R / 8) << 10) | ((p.G / 8) << 5) | (p.B / 8);
                    colorSet.Add(quantized);
                    
                    pixelCount++;
                }
            }
        });
        
        var avgSaturation = saturationSum / pixelCount;
        var maxPossibleColors = 32 * 32 * 32; // With 32 levels
        var uniqueRatio = colorSet.Count / (float)Math.Min(pixelCount, maxPossibleColors);
        
        return (avgSaturation, uniqueRatio);
    }

    private (float EdgeSharpness, float EdgeDensity) AnalyzeEdges(Image<Rgb24> image)
    {
        var edgeCount = 0;
        var strongEdgeCount = 0;
        var totalGradient = 0f;
        
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 1; y < accessor.Height - 1; y++)
            {
                var prevRow = accessor.GetRowSpan(y - 1);
                var row = accessor.GetRowSpan(y);
                var nextRow = accessor.GetRowSpan(y + 1);
                
                for (int x = 1; x < accessor.Width - 1; x++)
                {
                    var c = row[x];
                    var l = row[x - 1];
                    var r = row[x + 1];
                    var t = prevRow[x];
                    var b = nextRow[x];
                    
                    // Sobel-like gradient
                    var gx = Math.Abs((r.R + r.G + r.B) - (l.R + l.G + l.B)) / 3f;
                    var gy = Math.Abs((b.R + b.G + b.B) - (t.R + t.G + t.B)) / 3f;
                    var gradient = MathF.Sqrt(gx * gx + gy * gy);
                    
                    totalGradient += gradient;
                    
                    if (gradient > 30) edgeCount++;
                    if (gradient > 80) strongEdgeCount++; // Sharp edge
                }
            }
        });
        
        var pixelCount = (image.Width - 2) * (image.Height - 2);
        var edgeDensity = edgeCount / (float)pixelCount;
        var sharpness = edgeCount > 0 ? strongEdgeCount / (float)edgeCount : 0f;
        
        return (sharpness, edgeDensity);
    }

    private (float Smoothness, float Complexity) AnalyzeTexture(Image<Rgb24> image)
    {
        var varianceSum = 0f;
        var localVarianceSum = 0f;
        var sampleCount = 0;
        
        // Sample 8x8 blocks
        for (int by = 0; by < image.Height - 8; by += 8)
        {
            for (int bx = 0; bx < image.Width - 8; bx += 8)
            {
                var blockSum = 0f;
                var blockSumSq = 0f;
                var blockPixels = 0;
                
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = by; y < by + 8 && y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = bx; x < bx + 8 && x < accessor.Width; x++)
                        {
                            var p = row[x];
                            var luma = (p.R * 0.299f + p.G * 0.587f + p.B * 0.114f) / 255f;
                            blockSum += luma;
                            blockSumSq += luma * luma;
                            blockPixels++;
                        }
                    }
                });
                
                if (blockPixels > 0)
                {
                    var mean = blockSum / blockPixels;
                    var variance = (blockSumSq / blockPixels) - (mean * mean);
                    localVarianceSum += variance;
                    sampleCount++;
                }
            }
        }
        
        var avgVariance = sampleCount > 0 ? localVarianceSum / sampleCount : 0f;
        
        // Low variance = smooth (anime)
        // High variance = complex texture (realistic)
        var smoothness = 1f - Math.Min(avgVariance * 10f, 1f);
        var complexity = Math.Min(avgVariance * 10f, 1f);
        
        return (smoothness, complexity);
    }
}
