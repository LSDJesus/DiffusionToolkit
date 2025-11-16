# PostgreSQL Migration Status - November 15, 2025

## ✅ COMPLETED (90% done)

### Core Infrastructure
- ✅ PostgreSQLDataStore with 11 partial classes created
- ✅ All critical Image CRUD operations ported:
  - RemoveImage(s), UpdateViewed, GetImageIdByHash
  - UpdateImagePath, ImageExists, UpdateImageFilename
  - GetImagesTaggedForDeletion, MoveImage
  - GetAllPathImages, CountAllPathImages
  - GetFolderImages (with recursive CTE), GetImagePaths
  - UpdateImageFolderId
- ✅ Bulk operations (AddImages, UpdateImagesByPath) with snake_case mapping
- ✅ Node operations (AddNodes, UpdateNodes, DeleteNodes)
- ✅ Query management (QueryExists, GetQueries, GetQuery, CreateOrUpdateQuery, RenameQuery, RemoveQuery)
- ✅ ServiceLocator refactored (PostgreSQL primary, SQLite thumbnails only)
- ✅ All UI type signatures updated to PostgreSQLDataStore
- ✅ Property aliases added (NSFW ↔ Nsfw, CFGScale → CfgScale)
- ✅ CountSize.Deconstruct() method added
- ✅ Sorting/Paging type aliases (PgSorting, PgPaging)

### Models Created
- ✅ ImagePath, HashMatch, UpdateViewed
- ✅ Query, QueryItem
- ✅ NSFW property alias on ImageEntity and ImageView

## 🔄 REMAINING (~10% - Est. 30-45 min)

### Type Ambiguity Fixes (20 errors)
**Issue**: Multiple namespaces define same types (Album, UsedPrompt, ImagePath, ImageView)

**Solution needed**: Add fully qualified type names or using aliases in affected files:
- `Diffusion.Toolkit/Pages/Prompts.xaml.cs` - UsedPrompt ambiguity (lines 94, 105)
- `Diffusion.Toolkit/Pages/Search.xaml.cs` - Album ambiguity (lines 1062, 1067, 1077)
- `Diffusion.Toolkit/Pages/Search.xaml.cs` - ImageView ambiguity (line 1359)
- `Diffusion.Toolkit/Controls/ThumbnailView.xaml.cs` - ImagePath ambiguity (line 682)
- `Diffusion.Toolkit/MainWindow.xaml.Albums.cs` - Album conversion (line 235)
- `Diffusion.Toolkit/MainWindow.xaml.Tools.cs` - Album conversion (line 228)

### Type Conversion Fixes (15 errors)
**Issue**: PostgreSQL models don't inherit from SQLite models

**Files needing fixes**:
1. `FolderService.cs` lines 284, 287, 317, 320, 680, 1315
   - Issue: FolderView → Folder conversions
   - Issue: int → bool conversions (likely GetImageCount returning int vs expected bool)

2. `Search.xaml.cs` lines 909, 912
   - Issue: long → int conversions for counts
   - Fix: Add explicit casts `(int)count`

3. `MainWindow.xaml.Portable.cs` line 132
   - Issue: DataStore → PostgreSQLDataStore conversion
   - Fix: Update method signature or cast

4. `MainWindow.xaml.Scanning.cs` line 221
   - Issue: PostgreSQLDataStore → DataStore conversion
   - Fix: Update method parameter type

5. `MainWindow.xaml.Tools.cs` lines 63, 212
   - Issue: IEnumerable<PostgreSQL.ImageView> → IEnumerable<Models.ImageView>
   - Fix: Use PostgreSQL models or create adapter

### Missing Methods (4 errors)
1. **CreateBackup** - Settings.xaml.cs line 204
2. **TryRestoreBackup** - Settings.xaml.cs line 237

**Note**: Backup/Restore for PostgreSQL may not be needed if using `pg_dump/pg_restore` externally. Could stub these methods with NotImplementedException or implement pg_dump calls.

## Quick Fix Strategy (30-45 minutes)

### Phase 1: Ambiguity Resolution (15 min)
```csharp
// Add to affected files:
using PgAlbum = Diffusion.Database.PostgreSQL.Models.Album;
using PgImageView = Diffusion.Database.PostgreSQL.ImageView;
using PgUsedPrompt = Diffusion.Database.PostgreSQL.UsedPrompt;
using PgImagePath = Diffusion.Database.PostgreSQL.Models.ImagePath;
```

### Phase 2: Type Conversions (15 min)
- Add explicit casts where needed: `(int)longValue`
- Update method signatures to accept PostgreSQLDataStore
- Fix FolderService int→bool issues (likely property name mismatch)

### Phase 3: Stub Missing Methods (5 min)
```csharp
public void CreateBackup(string path)
{
    throw new NotImplementedException("Use pg_dump for PostgreSQL backups");
}

public bool TryRestoreBackup(string path)
{
    throw new NotImplementedException("Use pg_restore for PostgreSQL restores");
}
```

### Phase 4: Final Build & Test (5-10 min)
- Clean build
- Launch application
- Test basic operations (scan folder, search images, view metadata)

## Files Modified (38 files)

### New Files Created (7)
1. `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.Image.cs`
2. `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.BulkOperations.cs`
3. `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.Node.cs`
4. `Diffusion.Database.PostgreSQL/PostgreSQLDataStore.Query.cs`
5. `Diffusion.Database.PostgreSQL/Models/HashMatch.cs`
6. `Diffusion.Database.PostgreSQL/Models/UpdateViewed.cs`
7. `Diffusion.Database.PostgreSQL/Models/Query.cs`

### Modified Files (31)
- ServiceLocator.cs - Primary database switch
- MainWindow.xaml.cs - Database initialization
- MainWindow.xaml.Events.cs - Dispose pattern
- WelcomeWindow.xaml.cs - Reference updates
- TaggingWindow.xaml.cs - Reference updates
- Settings.xaml.cs - Type updates
- UnavailableFilesWindow.xaml.cs - Type updates
- Preview.xaml.cs - Type updates
- Prompts.xaml.cs - Type updates
- ThumbnailView.xaml.cs - Type updates
- Search.xaml.cs - Type updates, Sorting/Paging aliases
- FolderService.cs - Type updates, using statements
- ScanningService.cs - Type updates, CFGScale fix
- DatabaseWriterService.cs - Model namespace update
- PostgreSQLDataStore.cs - Base infrastructure
- PostgreSQLDataStore.Search.cs - CountSize.Deconstruct, NSFW alias
- PostgreSQLDataStore.Image.cs - Null safety fixes
- ImagePath.cs - Added FolderId, Unavailable properties
- ImageEntity.cs - NSFW alias added
- docker-compose.yml - Port configuration

## Testing Checklist

After compilation succeeds:
- [ ] Launch application
- [ ] Connect to PostgreSQL (verify Docker running)
- [ ] Add a watched folder
- [ ] Scan images
- [ ] Search for images
- [ ] View image details
- [ ] Tag images (favorite, rating, NSFW)
- [ ] Create album
- [ ] Add images to album
- [ ] Test semantic search (if embeddings populated)
- [ ] Verify SQLite thumbnail cache working

## Known Limitations

1. **Backup/Restore**: Not implemented - use native PostgreSQL tools
2. **Performance**: Bulk operations may need tuning for 1M+ images
3. **Model Compatibility**: Some UI components still expect SQLite model types
4. **Migration Path**: No automatic data migration from SQLite to PostgreSQL (would need separate tool)

## Next Steps After Migration

1. Test thoroughly with real image dataset
2. Add performance monitoring
3. Optimize bulk insert operations if needed
4. Consider adding PostgreSQL-specific features:
   - Full-text search on prompts (tsvector)
   - Spatial queries if location data added
   - Advanced similarity search optimizations
5. Document PostgreSQL setup for users
6. Create migration guide for existing SQLite users
