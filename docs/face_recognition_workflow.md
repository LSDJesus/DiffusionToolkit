# Face Recognition Workflow - Implementation Status

## Current Implementation ✅

### 1. Face Detection & Storage
- ✅ Faces detected and stored with ArcFace embeddings (512D vectors)
- ✅ Similarity search via pgvector (`FindSimilarFaces()`)
- ✅ Default similarity threshold: **0.6** (60% match)
- ✅ Database schema supports face groups (`face_group` table)

### 2. Faces Tab in Preview
- ✅ Displays detected faces in MetadataPanel
- ✅ Shows face thumbnails (120x120px from database BLOBs)
- ✅ Shows confidence & quality scores
- ✅ **Editable name field** for each face (TextBox with UpdateSourceTrigger)
- ✅ Shows cluster assignment if face is grouped

### 3. Face Gallery Page
- ✅ Registered route: `facegallery/#{groupId}`
- ✅ NavigatorService has `NavigateToFaceGallery(groupId)` method
- ✅ Displays all faces in a group
- ✅ Shows group statistics (face count, avg confidence, avg quality)
- ✅ Editable group name at top

## Missing Components ❌

### 1. **Click Navigation from Face Thumbnail → Gallery**
**Status:** NOT IMPLEMENTED  
**What's needed:** Add MouseDown handler to face Border in MetadataPanel.xaml

```xml
<!-- Current (line 316-317 in MetadataPanel.xaml) -->
<Border BorderBrush="{DynamicResource SecondaryBrush}" BorderThickness="1" 
        Margin="5" Padding="5" Background="#20000000">

<!-- Needs to be: -->
<Border BorderBrush="{DynamicResource SecondaryBrush}" BorderThickness="1" 
        Margin="5" Padding="5" Background="#20000000"
        MouseDown="FaceThumbnail_OnMouseDown" Cursor="Hand">
```

**Code-behind needed:**
```csharp
private void FaceThumbnail_OnMouseDown(object sender, MouseButtonEventArgs e)
{
    if (e.ClickCount == 2 && sender is Border border && border.DataContext is Models.FaceViewModel face)
    {
        // Find similar faces and navigate to gallery
        _ = NavigateToSimilarFacesAsync(face);
    }
}
```

### 2. **Auto-Clustering on Detection**
**Status:** NOT IMPLEMENTED  
**What's needed:** After face detection, automatically search for similar faces and create/assign groups

**Location:** `BackgroundFaceDetectionService.cs` (already has placeholder logic at line 405)

**Threshold Configuration:**
- Currently hardcoded: `threshold = 0.6f` in `FindSimilarFaces()`
- **Need to add:** Setting in `Settings.cs` for user configuration
- **Recommended range:** 0.5 (loose) to 0.8 (strict)

```csharp
// Add to Settings.cs
public float FaceRecognitionThreshold { get; set; } = 0.65f; // Default 65%
```

### 3. **Confirm/Reject Match UI in FaceGallery**
**Status:** NOT IMPLEMENTED  
**What's needed:** Add buttons to each face thumbnail in gallery

```xml
<!-- Add to FaceGallery.xaml face item template -->
<StackPanel Orientation="Horizontal" Margin="0,5,0,0">
    <Button Content="✓ Confirm" Width="55" Height="25" 
            Click="ConfirmMatch_Click" Tag="{Binding Id}"
            Background="LimeGreen"/>
    <Button Content="✗ Reject" Width="55" Height="25" Margin="5,0,0,0"
            Click="RejectMatch_Click" Tag="{Binding Id}"
            Background="OrangeRed"/>
</StackPanel>
```

**Code-behind:**
```csharp
private async void ConfirmMatch_Click(object sender, RoutedEventArgs e)
{
    var faceId = (int)((Button)sender).Tag;
    // Confirm face belongs to this group
    await dataStore.AssignFaceToCluster(faceId, _groupId);
    // Refresh gallery
    await LoadGroupAsync(_groupId);
}

private async void RejectMatch_Click(object sender, RoutedEventArgs e)
{
    var faceId = (int)((Button)sender).Tag;
    // Remove from this group (set face_group_id = NULL)
    await dataStore.RemoveFaceFromCluster(faceId);
    // Remove from UI
    var face = Faces.FirstOrDefault(f => f.Id == faceId);
    if (face != null) Faces.Remove(face);
}
```

### 4. **Auto-Naming Groups**
**Status:** PARTIALLY IMPLEMENTED  
**Current:** Groups default to "Group {id}" or "Unknown"  
**What's needed:** Smart auto-naming strategy

**Option A: Count-based**
```csharp
string autoName = $"Person {groupNumber}"; // e.g., "Person 1", "Person 2"
```

**Option B: Image-based**
```csharp
// Use first image filename as hint
string autoName = Path.GetFileNameWithoutExtension(firstImagePath);
```

**Option C: Hybrid**
```csharp
// Start with "Person N", user edits to real name later
string autoName = $"Person {DateTime.Now:yyyyMMddHHmmss}";
```

### 5. **Name Editing Persistence**
**Status:** PARTIALLY IMPLEMENTED  
**Current:** TextBox binds to `ClusterLabel` property  
**Missing:** Save handler when user edits name

```csharp
// Add to MetadataPanel.xaml.cs or create event handler
private async void FaceName_LostFocus(object sender, RoutedEventArgs e)
{
    if (sender is TextBox textBox && textBox.DataContext is Models.FaceViewModel face)
    {
        // If face has cluster, update cluster name
        if (face.ClusterId.HasValue)
        {
            await ServiceLocator.DataStore.UpdateClusterLabel(face.ClusterId.Value, face.ClusterLabel);
        }
        else
        {
            // Create new cluster for this face
            var clusterId = await ServiceLocator.DataStore.CreateFaceClusterAsync(face.ClusterLabel, isManual: true);
            await ServiceLocator.DataStore.AssignFaceToCluster(face.Id, clusterId);
            face.ClusterId = clusterId;
        }
    }
}
```

## Recommended Implementation Order

1. **Add Click Navigation** (5 minutes)
   - Add MouseDown to MetadataPanel face thumbnails
   - Navigate to FaceGallery or show similar faces popup

2. **Add Settings for Threshold** (10 minutes)
   - Add `FaceRecognitionThreshold` to Settings.cs
   - Add UI slider in Settings page
   - Use setting in `FindSimilarFaces()` calls

3. **Implement Confirm/Reject Buttons** (15 minutes)
   - Add buttons to FaceGallery.xaml
   - Implement click handlers
   - Add `RemoveFaceFromCluster()` to PostgreSQLDataStore

4. **Auto-Clustering on Detection** (20 minutes)
   - Wire up similarity search after detection
   - Auto-create groups for similar faces
   - Use threshold from settings

5. **Name Editing Persistence** (10 minutes)
   - Add LostFocus handler to TextBox
   - Save cluster name to database
   - Create cluster if needed

## Similarity Threshold Guide

| Threshold | Description | Use Case |
|-----------|-------------|----------|
| 0.50-0.55 | Very loose | Catch all possible matches (high false positives) |
| 0.60-0.65 | **Balanced (recommended)** | Good mix of recall/precision |
| 0.70-0.75 | Strict | Only confident matches (may miss some) |
| 0.80+ | Very strict | Identical twins level (high false negatives) |

**Current Default:** 0.6 (60%)  
**ArcFace Typical:** 0.6-0.7 for same person

## Database Methods Available

### Already Implemented ✅
```csharp
// Face detection
Task<List<FaceDetectionEntity>> GetFacesForImage(int imageId)
Task<byte[]?> GetFaceCrop(int faceId)

// Similarity search
Task<List<(int faceId, int imageId, float similarity)>> FindSimilarFaces(
    float[] embedding, float threshold = 0.6f, int limit = 100)

// Clustering
Task<int> CreateFaceClusterAsync(string? label = null, bool isManual = false)
Task AssignFaceToCluster(int faceId, int clusterId)
Task UpdateClusterLabel(int clusterId, string label)
Task<FaceGroupEntity?> GetFaceGroupAsync(int groupId)
```

### Need to Add ❌
```csharp
// Remove face from cluster
Task RemoveFaceFromCluster(int faceId)
{
    await connection.ExecuteAsync(
        $"UPDATE {Table("face_detection")} SET face_group_id = NULL WHERE id = @faceId",
        new { faceId });
}

// Get faces by cluster
Task<List<FaceDetectionEntity>> GetFacesInCluster(int clusterId, int limit = 500)

// Search for ungrouped similar faces
Task<List<FaceDetectionEntity>> FindUngroupedSimilarFaces(
    float[] embedding, float threshold, int limit)
```

## Example Workflow (End-to-End)

1. **User views image** → Faces tab appears (auto-loads from DB)
2. **User double-clicks face** → Searches for similar faces using embedding
3. **Gallery opens** showing all matches above threshold (e.g., 15 similar faces found)
4. **User reviews gallery:**
   - Click ✓ Confirm on correct matches → Added to group
   - Click ✗ Reject on false positives → Removed from results
5. **User edits group name** at top: "Unknown" → "Jane Doe"
6. **All confirmed faces** now labeled "Jane Doe" across entire database
7. **Future detections** auto-match to "Jane Doe" if similarity > threshold

## Settings UI Suggestion

Add to Settings page:
```xml
<Label>Face Recognition Similarity Threshold</Label>
<Slider Minimum="0.5" Maximum="0.9" Value="{Binding FaceRecognitionThreshold}" 
        TickFrequency="0.05" IsSnapToTickEnabled="True"/>
<TextBlock>
    <Run Text="Current: "/>
    <Run Text="{Binding FaceRecognitionThreshold, StringFormat={}{0:P0}}"/>
    <LineBreak/>
    <Run FontSize="10" Foreground="Gray" 
         Text="Lower = more matches (higher false positives), Higher = stricter matching"/>
</TextBlock>
```

Want me to implement any of these missing pieces?
