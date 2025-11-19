# Diffusion Watcher - Background File Indexing Service

## What It Does

**Diffusion Watcher** is a lightweight background service that runs in your system tray and continuously monitors your AI image folders. It automatically indexes new files as they're created, keeping your database always up-to-date.

## Key Features

✅ **Real-time monitoring** - Detects new files instantly via FileSystemWatcher  
✅ **Blazing fast quick-scan** - Indexes file paths, sizes, and dates in seconds  
✅ **Background metadata extraction** - Optionally extracts prompts/parameters when idle  
✅ **System tray interface** - Minimal UI, runs silently in background  
✅ **Smart resource usage** - Single-threaded metadata scan to avoid CPU spikes  
✅ **Auto-start with Windows** - (coming soon)  
✅ **Instant Toolkit startup** - Database already populated when you open DiffusionToolkit

## Architecture

### Two-Phase Operation

**Phase 1: Quick Scan (Always Active)**
- Monitors watched folders in real-time
- Instantly indexes: file path, name, size, created date, modified date
- Uses PostgreSQL COPY for bulk imports (extremely fast)
- Marks files with `scan_phase = 0` (quick-scanned only)

**Phase 2: Metadata Extraction (Optional Background)**
- Runs single-threaded when system is idle
- Extracts: prompts, parameters, model info, workflow, etc.
- Updates records to `scan_phase = 1` (full metadata)
- Can be enabled/disabled from tray menu

## How It Works

```
[File Created/Modified]
         ↓
[FileSystemWatcher detects change]
         ↓
[Add to quick-scan queue]
         ↓
[Batch process (1000 files at a time)]
         ↓
[PostgreSQL COPY bulk insert]
         ↓
[Queue for background metadata extraction] (if enabled)
         ↓
[Single-threaded metadata scanner processes when idle]
```

## Usage

### Starting the Watcher

1. **Launch**: Run `Diffusion.Watcher.exe`
2. **Tray Icon**: A system tray icon appears showing status
3. **Automatic**: The watcher reads your configured folders from the database
4. **Initial Scan**: Performs a full quick-scan on startup (optional)

### Tray Menu Options

- **Status** - Shows current activity and queue size
- **Pause/Resume Watching** - Temporarily stop file monitoring
- **Scan Now** - Trigger immediate full quick-scan
- **Background Metadata: Enabled/Disabled** - Toggle metadata extraction
- **Settings** - Configure behavior (coming soon)
- **View Logs** - Open log file in Notepad
- **Exit** - Stop the watcher service

### Double-Click Behavior

Double-clicking the tray icon shows detailed statistics:
- Number of watched folders
- Total indexed images
- Quick-scanned vs metadata-extracted counts
- Files currently in queue

## Performance

**Quick Scan:**
- ~10,000 files/second (SSD)
- Minimal CPU usage
- Tiny memory footprint (~50MB)

**Metadata Scan:**
- ~10-50 files/second (depends on image format)
- Single-threaded to avoid CPU spikes
- Only runs when enabled

## Integration with Diffusion Toolkit

### Before (Without Watcher)
1. Open DiffusionToolkit
2. Click "Rescan" button
3. Wait 10-30 minutes for scan to complete
4. Start browsing

### After (With Watcher)
1. Open DiffusionToolkit
2. Images are already indexed
3. Start browsing immediately!

### On-Demand Metadata

From DiffusionToolkit, you can:
- **Rebuild** - Extract metadata for all images (or just quick-scanned ones)
- **Per-folder scan** - Extract metadata only for specific folders
- **Smart detection** - Only re-scan if file size/hash changed

## Configuration

The watcher automatically reads configuration from:
- Database connection string (shared with DiffusionToolkit)
- Watched folders (configured in DiffusionToolkit settings)

## File Monitoring

**Detects:**
- ✅ New files created
- ✅ Files modified (size/timestamp changed)
- ✅ Files renamed
- ✅ Files deleted (marks as unavailable)

**Ignores:**
- ❌ Non-image files (only monitors .png, .jpg, .jpeg, .webp)
- ❌ Temporary files
- ❌ Hidden files

## Advanced Features

### Change Detection

When a file is modified:
1. Quick-scan updates the file size and date
2. If metadata extraction is enabled, it re-extracts metadata
3. Smart comparison: only updates if content actually changed

### Batch Processing

Files are processed in batches of 1000 for optimal database performance:
- Reduces database round-trips
- Minimizes lock contention
- Maximizes bulk import efficiency

### Error Handling

- Failed file reads are logged and skipped
- Database errors trigger automatic retry
- Corrupted files don't crash the watcher

## System Requirements

- Windows 10/11 (64-bit)
- .NET 10.0 Desktop Runtime
- PostgreSQL 18 with pgvector (same as DiffusionToolkit)
- 50MB RAM minimum
- Minimal CPU usage

## Future Enhancements

- [ ] Auto-start with Windows registry
- [ ] Settings UI for custom configuration
- [ ] Configurable scan schedules (e.g., "only scan 2-6 AM")
- [ ] Battery-aware scanning (pause on laptop battery)
- [ ] Network share monitoring
- [ ] Multi-database support
- [ ] Remote monitoring dashboard
- [ ] Hash-based duplicate detection
- [ ] Automatic image tagging/categorization

## Technical Details

### Database Schema

Uses the existing `scan_phase` column:
- `0` = Quick-scanned only (path, size, dates)
- `1` = Full metadata extracted

### Threading Model

- **Main thread**: UI event loop (system tray)
- **Quick-scan worker**: Batches files and bulk-inserts to database
- **Metadata worker**: Single-threaded background processor

### Memory Efficiency

- Uses queues (ConcurrentQueue) for file processing
- Batch processing prevents memory buildup
- FileSystemWatcher is very lightweight (~1KB per folder)

## Troubleshooting

**Watcher not detecting files:**
- Check if folders exist and are accessible
- Verify FileSystemWatcher events are firing (check logs)
- Ensure database connection is working

**Slow quick-scan:**
- Check database connection latency
- Verify SSD performance
- Check if PostgreSQL has sufficient resources

**High CPU usage:**
- Disable background metadata extraction
- Reduce batch size (edit source code)
- Check for disk I/O bottlenecks

## Building from Source

```bash
dotnet build Diffusion.Watcher.csproj
dotnet publish -c Release -r win-x64 --self-contained false
```

## License

Same as Diffusion Toolkit (see main LICENSE file)

---

**Pro Tip:** Start the watcher, let it run overnight, and wake up to a fully indexed library!
