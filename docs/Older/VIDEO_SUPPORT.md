# Video File Support

Diffusion Toolkit now supports cataloging video files (MP4, AVI, WEBM) alongside images.

## Features

- **Video Metadata Extraction**: Automatically extracts duration, codecs, frame rate, and bitrate
- **Video Thumbnails**: Generates thumbnails from the first frame of videos
- **Visual Indicators**: Video files display a 🎥 camera icon in the gallery view
- **Search & Filter**: Video files are indexed and searchable like images

## FFmpeg Requirement

Video thumbnail generation requires **FFmpeg** to be available on your system.

### Option 1: System PATH (Recommended)

1. Download FFmpeg from https://ffmpeg.org/download.html
2. Extract to a folder (e.g., `C:\ffmpeg`)
3. Add `C:\ffmpeg\bin` to your system PATH environment variable
4. Restart Diffusion Toolkit

### Option 2: Local Installation

1. Create a folder named `ffmpeg` in the Diffusion Toolkit installation directory
2. Download FFmpeg binaries and extract `ffmpeg.exe` to this folder
3. Restart Diffusion Toolkit

### Verification

On first video scan, Diffusion Toolkit will attempt to use FFmpeg. If not found:
- Video files will still be indexed with metadata
- Thumbnails will fail to generate (default icon shown)
- Check the log for FFmpeg initialization errors

## Supported Video Formats

- **MP4** (H.264, H.265/HEVC)
- **AVI** (various codecs)
- **WEBM** (VP8, VP9)

## Technical Details

### Database Schema

Six new optional fields added to the `image` table:
- `duration_ms` - Video duration in milliseconds
- `video_codec` - Video codec name (e.g., "h264", "vp9")
- `audio_codec` - Audio codec name (e.g., "aac", "opus")
- `frame_rate` - Frames per second (decimal)
- `bitrate` - Total bitrate in kbps
- `is_video` - Boolean flag (true for video files)

### Thumbnail Generation

- Captures frame at 0.5 seconds into the video
- Scaled to match thumbnail size setting (default 512px width)
- Cached like image thumbnails for performance
- Temporary JPEG files auto-deleted after loading

### Metadata Extraction

Uses MetadataExtractor library to parse video container metadata:
- **MP4**: Reads QuickTime/MP4 metadata directories
- **AVI**: Parses RIFF headers
- **WEBM**: Extracts Matroska metadata

## Troubleshooting

### Videos not showing thumbnails

1. Check FFmpeg is installed and in PATH
2. Verify FFmpeg works: `ffmpeg -version` in command prompt
3. Check Diffusion Toolkit logs for FFmpeg errors
4. Try re-scanning the folder with videos

### Videos not being detected

1. Ensure file extensions are enabled in Settings
2. Check `Settings.FileExtensions` includes `.mp4`, `.avi`, `.webm`
3. Verify files are not corrupted (try opening in media player)

### Performance concerns

- Video thumbnail generation is slower than image thumbnails
- First scan of video-heavy folders may take longer
- Thumbnails are cached after first generation
- Consider scanning video folders separately if performance is an issue

## Future Enhancements

Potential improvements for video support:
- Video playback in preview pane
- Multiple frame thumbnails (filmstrip view)
- Video-specific AI tagging (scene detection, object recognition per frame)
- Audio waveform visualization
- Batch video conversion tools
- Video trimming/export features
