# Diffusion Toolkit - AI Copilot Instructions

## Project Overview
Diffusion Toolkit is a Windows desktop application for organizing, indexing, and searching AI-generated images. It scans image metadata (EXIF, PNG/WebP embeds) from multiple AI frameworks (AUTOMATIC1111, InvokeAI, NovelAI, Fooocus, etc.), stores it in PostgreSQL with pgvector, and provides filtering/tagging/album management plus semantic similarity search.

**Tech Stack**: C# / .NET 6 / WPF / PostgreSQL 18 + pgvector / GPU-accelerated embeddings  
**Architecture**: Multi-project modular design with service-oriented patterns  
**Target**: Windows 64-bit only, scales to 1M+ images  
**Embeddings**: BGE-large-en-v1.5 (text), CLIP (images), ONNX Runtime GPU

## Critical Architecture Patterns

### 1. **Modular Project Structure**
- **`Diffusion.Toolkit`** - WPF UI application (main entry point)
- **`Diffusion.Database`** - SQLite data layer with custom path expression extensions
- **`Diffusion.Scanner` (Diffusion.IO.csproj)** - Image file scanning & metadata extraction
- **`Diffusion.Common`** - Shared models and utilities
- **`Diffusion.Civitai`**, **`Diffusion.Github`** - External API integrations
- **`Diffusion.Updater`** - Auto-update companion executable

The solution uses **partial classes** extensively in UI (`MainWindow.xaml.*.cs` split by feature: Scanning, Models, Queries, Albums, Events, etc.) to keep files manageable.

### 2. **Data Flow Pipeline**
```
Folder Scanner → MetadataScanner → DatabaseWriterService → SQLite (DataStore)
                        ↓
                  Image metadata extraction 
                  (PNG/EXIF/WebP parsing)
                        ↓
                  Queue async writes via Channels
                        ↓
                  Database updates (thumbnail generation)
```

Key insight: **Writing is asynchronous and debounced** via `DatabaseWriterService` using `System.Threading.Channels` for queue management. See `Services/DatabaseWriterService.cs` for the write pattern.

### 3. **Service Locator Pattern**
Central singleton access point: `ServiceLocator` in `Services/ServiceLocator.cs`
- Provides static access to DataStore, ProgressService, FolderService, etc.
- Used throughout WPF code to avoid dependency injection boilerplate
- Initialize in `App.xaml.cs` before UI construction

### 4. **Database Layer (PostgreSQL)**
- **File**: `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.cs` (partial class split across multiple files)
- Uses **Dapper** ORM with Npgsql (PostgreSQL ADO.NET provider)
- **Critical**: Requires pgvector extension for similarity search; ltree for hierarchical paths
- Connection pooling via `NpgsqlDataSource` for high concurrency
- Thread-safe with `_lock` object for write operations
- Split partial classes:
  - `PostgreSQLDataStore.Search.cs` - Query/filter operations
  - `PostgreSQLDataStore.MetaData.cs` - Image tagging (favorite, rating, NSFW, deletion)
  - `PostgreSQLDataStore.Album.cs` - Album management
  - `PostgreSQLDataStore.Folder.cs` - Folder operations
  - `PostgreSQLDataStore.VectorSearch.cs` - **NEW**: Semantic similarity search via pgvector

### 5. **Async Patterns**
- Heavy use of `Task`-based async patterns (no sync-over-async)
- `CancellationToken` passed through all long-running operations
- Typically wrapped in `ServiceLocator.ProgressService.TryStartTask()` for mutual exclusion
- Example: `ScanWatchedFolders()` in ScanningService handles cancellation properly

### 6. **Metadata Extraction**
Different image formats require specialized parsing:
- **PNG**: StealthPng steganography + standard PNGInfo
- **JPEG/JPG**: EXIF data extraction
- **WebP**: Custom WebP parser
- **TXT**: Sidecar metadata files

See `Diffusion.Scanner/MetadataScanner.cs` and `Diffusion.Scanner/ComfyUI.cs` for multi-framework support.

## Build & Deployment

### Local Build
```powershell
dotnet build Diffusion.Toolkit.sln
# or use the "build" task in VS Code
```

### Release Build
```powershell
.\publish.cmd
# Output: ./build/ folder with single-file executable
```

**Release settings** (in `publish.cmd`):
- Single-file publish (`/p:PublishSingleFile=true`)
- Release configuration
- Windows 64-bit runtime (`-r win-x64`)
- Symbols stripped for size

### Docker Dependency
The database requires Docker with PostgreSQL 18 + pgvector extension. Start with `docker-compose up -d`. Default connection: `Host=localhost;Port=5436;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025`

## Common Workflows

### Adding Image Scanning Logic
1. Add metadata extraction in `Diffusion.Scanner/MetadataScanner.cs`
2. Create `FileParameters` object with image path and metadata
3. Queue via `ServiceLocator.MetadataScannerService.QueueAsync()`
4. DatabaseWriterService processes queue asynchronously

### Adding Database Fields
1. Add property to relevant model in `Diffusion.Database/Models/`
2. Define migration in `Diffusion.Database/Migrations.cs` with `MigrateAttribute`
3. Schema creation happens automatically via `db.CreateTable<T>()` pattern
4. Access via DataStore partial classes (search, metadata, etc.)

### Adding UI Features
1. Add XAML to `MainWindow.xaml` or create new Window
2. Add codebehind to `MainWindow.xaml.cs` or split into feature file (e.g., `MainWindow.xaml.Queries.cs`)
3. Wire into navigation via `NavigatorService`
4. Use `ServiceLocator` for service access
5. Bind to `MainModel` properties for reactive updates

### Localization
- Strings in `Diffusion.Toolkit/Localization/*.json`
- Use `GetLocalizedText(key)` helper in services
- UI bindings use `WPFLocalizeExtension` (see Toolkit.csproj dependencies)

## Project-Specific Conventions

### Thread Safety
- SQLite operations use `_lock` in DataStore
- WPF UI updates use `Dispatcher.Invoke()` when crossing thread boundaries
- Long-running tasks wrapped with `Task.Run()` to avoid blocking UI thread

### Error Handling
- Global exception handler in `App.xaml.cs` (`GlobalExceptionHandler`)
- Logging via `Logger` class (Diffusion.Common/Logger.cs)
- User-facing errors shown via `MessagePopupManager` (toasts/dialogs)

### File Paths
- Portable mode supported: settings/database location based on execution context (see `Diffusion.Toolkit.Configuration`)
- Use `Path` utilities for cross-platform safety even though Windows-only target

### Naming Conventions
- Models use PascalCase properties (auto-mapping with Dapper)
- Services end with "Service" suffix
- Partial class files use `ClassName.FeatureName.cs` pattern
- Database queries use parameterized SQL (SQL injection prevention)

## Key Integration Points

### Scanning Workflow
**File**: `MainWindow.xaml.Scanning.cs`
- `RescanTask()` → `ScanningService.ScanWatchedFolders(rebuild=false)`
- `RebuildTask()` → `ScanningService.ScanWatchedFolders(rebuild=true)` (forces re-parse)
- Progress tracked via `ProgressService` (allows cancellation)

### Search/Filter
**File**: `Diffusion.Database/DataStore.Search.cs`
- Custom `QueryBuilder` class converts user filters to SQL
- Supports complex boolean logic on image properties
- Uses SQLite path extension for folder hierarchy queries

### Update Checking
**File**: `Diffusion.Common/UpdateChecker.cs`
- Queries GitHub API for latest release
- Called periodically; result shown in toolbar
- Auto-launch of Diffusion.Updater exe for installation

## Debugging Tips

1. **Database Issues**: Check `extensions/path0.dll` exists in output directory
2. **Scanning Hangs**: Verify cancellation token propagation and `TryStartTask()` mutex
3. **UI Freezes**: Ensure long operations use `Task.Run()`, not blocking threads
4. **Metadata Not Extracted**: Check `MetadataScanner.GetFiles()` extension filter and format support
5. **Localization Missing**: Verify JSON keys exist in all language files; fallback to en-US

## Testing
- `TestHarness` project for integration testing
- `TestBed` project for experimental features
- No formal unit test framework; mostly manual verification

---

**Last Updated**: November 2025  
**Version Target**: .NET 6.0 (Windows Desktop Runtime 6+)
