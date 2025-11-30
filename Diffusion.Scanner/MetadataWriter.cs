using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Diffusion.Common;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Diffusion.Scanner;

/// <summary>
/// Writes metadata back to image files (PNG tEXt chunks, JPEG EXIF, WebP EXIF/XMP)
/// </summary>
public static class MetadataWriter
{
    /// <summary>
    /// Write tags and captions to image metadata
    /// </summary>
    public static bool WriteMetadata(string filePath, MetadataWriteRequest request)
    {
        try
        {
            Logger.Log($"MetadataWriter.WriteMetadata called for: {filePath}");
            Logger.Log($"  Tags: {request.Tags?.Count ?? 0}, Caption: {!string.IsNullOrEmpty(request.Caption)}, CreateBackup: {request.CreateBackup}");
            
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            
            var result = ext switch
            {
                ".png" => WritePngMetadata(filePath, request),
                ".jpg" or ".jpeg" => WriteJpegMetadata(filePath, request),
                ".webp" => WriteWebpMetadata(filePath, request),
                _ => false // Unsupported format
            };
            
            Logger.Log($"  Write result: {result}");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to write metadata to {filePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write metadata to PNG file using tEXt chunks (compatible with SD WebUI, ComfyUI)
    /// </summary>
    private static bool WritePngMetadata(string filePath, MetadataWriteRequest request)
    {
        try
        {
            using var image = Image.Load(filePath);
            var pngMetadata = image.Metadata.GetPngMetadata();

            // Write generation parameters if provided
            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                var parameters = BuildParametersString(request);
                pngMetadata.TextData.Add(new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("parameters", parameters, string.Empty, string.Empty));
            }

            // Write tags as comma-separated list with confidence
            if (request.Tags?.Any() == true)
            {
                var tagString = BuildTagString(request.Tags);
                pngMetadata.TextData.Add(new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("tags", tagString, string.Empty, string.Empty));
            }

            // Write caption if provided
            if (!string.IsNullOrWhiteSpace(request.Caption))
            {
                pngMetadata.TextData.Add(new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("caption", request.Caption, string.Empty, string.Empty));
            }

            // Write aesthetic score if provided
            if (request.AestheticScore.HasValue)
            {
                pngMetadata.TextData.Add(new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("aesthetic_score", 
                    request.AestheticScore.Value.ToString(CultureInfo.InvariantCulture), 
                    string.Empty, string.Empty));
            }

            // Save back to file
            var encoder = new PngEncoder();
            
            // Create backup first
            if (request.CreateBackup)
            {
                CreateBackup(filePath);
            }

            image.SaveAsPng(filePath, encoder);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"PNG metadata write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write metadata to JPEG file using EXIF tags
    /// </summary>
    private static bool WriteJpegMetadata(string filePath, MetadataWriteRequest request)
    {
        try
        {
            using var image = Image.Load(filePath);
            var exifProfile = image.Metadata.ExifProfile ?? new ExifProfile();

            // Write generation parameters in UserComment (0x9286)
            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                var parameters = BuildParametersString(request);
                exifProfile.SetValue(ExifTag.UserComment, parameters);
            }

            // Write tags in ImageDescription (0x010E)
            if (request.Tags?.Any() == true)
            {
                var tagString = BuildTagString(request.Tags);
                exifProfile.SetValue(ExifTag.ImageDescription, tagString);
            }

            // Write caption in XPComment (0x9C9C)
            if (!string.IsNullOrWhiteSpace(request.Caption))
            {
                exifProfile.SetValue(ExifTag.XPComment, request.Caption);
            }

            image.Metadata.ExifProfile = exifProfile;

            // Create backup first
            if (request.CreateBackup)
            {
                CreateBackup(filePath);
            }

            image.SaveAsJpeg(filePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"JPEG metadata write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write metadata to WebP file using EXIF
    /// </summary>
    private static bool WriteWebpMetadata(string filePath, MetadataWriteRequest request)
    {
        try
        {
            using var image = Image.Load(filePath);
            var exifProfile = image.Metadata.ExifProfile ?? new ExifProfile();

            // Write generation parameters in UserComment
            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                var parameters = BuildParametersString(request);
                exifProfile.SetValue(ExifTag.UserComment, parameters);
            }

            // Write tags
            if (request.Tags?.Any() == true)
            {
                var tagString = BuildTagString(request.Tags);
                exifProfile.SetValue(ExifTag.ImageDescription, tagString);
            }

            // Write caption
            if (!string.IsNullOrWhiteSpace(request.Caption))
            {
                exifProfile.SetValue(ExifTag.XPComment, request.Caption);
            }

            image.Metadata.ExifProfile = exifProfile;

            // Create backup first
            if (request.CreateBackup)
            {
                CreateBackup(filePath);
            }

            image.SaveAsWebp(filePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"WebP metadata write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Build SD WebUI-compatible parameters string
    /// </summary>
    private static string BuildParametersString(MetadataWriteRequest request)
    {
        var sb = new StringBuilder();
        
        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            sb.Append(request.Prompt);
        }

        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
        {
            sb.Append("\nNegative prompt: ").Append(request.NegativePrompt);
        }

        // Add parameters line if we have any
        var paramsList = new List<string>();
        
        if (request.Steps > 0)
            paramsList.Add($"Steps: {request.Steps}");
        
        if (!string.IsNullOrWhiteSpace(request.Sampler))
            paramsList.Add($"Sampler: {request.Sampler}");
        
        if (request.CFGScale > 0)
            paramsList.Add($"CFG scale: {request.CFGScale.ToString(CultureInfo.InvariantCulture)}");
        
        if (request.Seed.HasValue)
            paramsList.Add($"Seed: {request.Seed.Value}");
        
        if (request.Width > 0 && request.Height > 0)
            paramsList.Add($"Size: {request.Width}x{request.Height}");
        
        if (!string.IsNullOrWhiteSpace(request.Model))
            paramsList.Add($"Model: {request.Model}");
        
        if (!string.IsNullOrWhiteSpace(request.ModelHash))
            paramsList.Add($"Model hash: {request.ModelHash}");

        if (paramsList.Any())
        {
            sb.Append("\n").Append(string.Join(", ", paramsList));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build comma-separated tag string with optional confidence scores
    /// </summary>
    private static string BuildTagString(List<TagWithConfidence> tags)
    {
        return string.Join(", ", tags.Select(t => 
            t.Confidence < 1.0f 
                ? $"{t.Tag}:{t.Confidence:F2}" 
                : t.Tag));
    }

    /// <summary>
    /// Create backup of original file with .bak extension
    /// </summary>
    private static void CreateBackup(string filePath)
    {
        var backupPath = filePath + ".bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(filePath, backupPath, false);
        }
    }
}

/// <summary>
/// Request object for metadata writing
/// </summary>
public class MetadataWriteRequest
{
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public int Steps { get; set; }
    public string? Sampler { get; set; }
    public decimal CFGScale { get; set; }
    public long? Seed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Model { get; set; }
    public string? ModelHash { get; set; }
    public List<TagWithConfidence>? Tags { get; set; }
    public string? Caption { get; set; }
    public decimal? AestheticScore { get; set; }
    public bool CreateBackup { get; set; } = true;
}

/// <summary>
/// Tag with optional confidence score
/// </summary>
public class TagWithConfidence
{
    public string Tag { get; set; } = string.Empty;
    public float Confidence { get; set; } = 1.0f;
}
