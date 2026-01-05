# Batch Image Conversion to WebP

## Overview
Diffusion Toolkit includes a batch image conversion feature that allows you to convert large collections of JPEG and PNG images to the modern WebP format. WebP provides significantly better compression than JPEG while maintaining comparable quality, resulting in substantial disk space savings.

## Features

### Format Options
- **WebP Lossy**: Smaller file sizes with very good quality (similar to JPEG)
- **WebP Lossless**: Perfect quality preservation (similar to PNG) with better compression

### Quality Control
- Adjustable quality slider (50-100%)
- Default: 85% (good balance between quality and file size)
- Recommended:
  - **75-85%**: Best balance for general use
  - **90-100%**: Near-lossless quality for archival

### Safety Features
- **Metadata Preservation**: Automatically copies EXIF data and generation parameters from original to converted file
- **Delete Original Option**: Optionally remove original files after successful conversion (with confirmation prompt)
- **JPEG-Only Filter**: Skip PNG files and only convert JPEG/JPG images

### Progress Tracking
- Real-time progress bar
- File count (processed / total)
- Current file being converted
- Space saved calculation in MB

## How to Use

### From Folder Context Menu
1. In the search/browse view, find the folder you want to convert
2. Right-click the folder
3. Select **"Convert Images to WebP..."**
4. Choose your conversion settings:
   - Format: Lossy (smaller) or Lossless (perfect quality)
   - Quality: Adjust slider as needed
   - Options: Check desired options
5. Click **"Start Conversion"**

### Conversion Process
1. The tool scans the selected folder for convertible images
2. Each image is loaded and re-encoded in WebP format
3. Metadata is copied from original to converted file
4. Database is updated with new file paths (.webp extension)
5. Original files are optionally deleted
6. Search results automatically refresh to show converted images

## Technical Details

### Supported Input Formats
- JPEG (.jpg, .jpeg)
- PNG (.png)

### Output Format
- WebP (.webp) - Both lossy and lossless modes

### Metadata Preservation
The following metadata is preserved during conversion:
- Prompt
- Negative Prompt
- Steps
- Sampler
- CFG Scale
- Seed
- Width & Height
- Model Name
- Model Hash

### Database Updates
- File paths are automatically updated in the database
- All metadata and tags remain intact
- Album associations are preserved
- Thumbnail cache is rebuilt as needed

## Performance Considerations

### Encoding Speed
- **Lossy WebP**: Fast encoding (~50-100 images/minute on modern CPU)
- **Lossless WebP**: Slower encoding (~20-50 images/minute)
- Speed depends on:
  - Image resolution
  - CPU performance
  - Quality settings (higher = slower)

### Space Savings
Typical results:
- **JPEG → WebP Lossy (85%)**: 20-40% smaller files
- **PNG → WebP Lossy (85%)**: 40-70% smaller files
- **PNG → WebP Lossless**: 15-30% smaller files

Example: Converting 10,000 JPEG images (average 2MB each):
- Original size: ~20 GB
- Converted size: ~12-16 GB
- **Space saved: 4-8 GB (20-40% reduction)**

## Safety & Recovery

### Backup Recommendations
Before converting large collections:
1. **Make a backup** of important images
2. Test conversion on a small folder first
3. Verify quality with sample images
4. Only use "Delete Original" if you're confident

### Cancellation
- Click **"Cancel"** button to stop conversion
- Already converted images are kept
- Database is updated for completed conversions
- Partial progress is shown

### Error Handling
- If an image fails to convert, it's logged and skipped
- Conversion continues with remaining images
- Summary shows count of failed conversions
- Original files are preserved on conversion failure

## Troubleshooting

### Conversion Fails
- **Check file permissions**: Ensure files are not read-only
- **Close other programs**: Some programs lock image files
- **Check disk space**: Ensure enough space for converted files

### Metadata Not Preserved
- Verify "Preserve Metadata" is checked
- Some metadata may not transfer between all formats
- Check original file has embedded metadata

### Database Not Updated
- Ensure PostgreSQL is running
- Check connection in Settings
- Refresh search results manually if needed

## Future Enhancements
Potential future additions:
- AVIF format support (requires plugin)
- Batch resize during conversion
- Multiple folder selection
- Scheduled/background conversion
- Quality comparison preview
- Undo conversion feature
