# 🌙 Luna-Vault Integration: Diffusion Toolkit UI Specification

**Author:** The Stubborn Architect  
**Date:** January 2025  
**Target:** DiffusionToolkit C# WPF Application  
**Implementation Time:** 2-3 hours  

---

## Executive Summary

Add a "Vault Search" feature to DiffusionToolkit that:
1. Allows users to select a reference image (or crop)
2. Queries the database for visually similar images using pgvector
3. Displays results with similarity scores
4. (Future) Sends selected results to ComfyUI API for generation

---

## UI Location & Entry Points

### Option A: New Top-Level Tab (Recommended)
```
[Toolbar: Settings | Folders | Albums | Tags | ➕ Vault Search]
```

**Why:** Vault Search is a distinct workflow, deserves its own space

### Option B: Context Menu Extension
```
Right-click image → 
  ├─ View Details
  ├─ Edit Tags
  ├─ Auto-Tag...
  ├─ Caption...
  ├─ 🔍 Find Similar (Vault Search)  ← NEW
  └─ Delete
```

**Why:** Natural workflow - "find more like this"

### Option C: Side Panel (Like Metadata Panel)
```
┌─────────────┬────────────────────────────────┐
│ Image Grid  │ [Details] [Tags] [🔍 Vault]   │
│             │                                │
│  [images]   │  Vault Search Panel:          │
│             │    [Reference Image Preview]   │
│             │    K Neighbors: [5]  ⬆️⬇️      │
│             │    Category: [All ▼]           │
│             │    [Search Similar]            │
│             │    Results: (list below)       │
└─────────────┴────────────────────────────────┘
```

**Why:** Keeps context visible, non-intrusive

---

## Primary UI: Vault Search Window

### Window Title
```
Luna Vault Search - Find Similar Images
```

### Layout (WPF XAML Structure)

```
┌────────────────────────────────────────────────────────────────┐
│ Luna Vault Search                                    [─][□][×] │
├────────────────────────────────────────────────────────────────┤
│                                                                │
│  ┌─── Search Configuration ─────────────────────────────────┐ │
│  │                                                           │ │
│  │  Reference Image:                                         │ │
│  │  ┌─────────────────────────┐                             │ │
│  │  │                         │  [Browse...] [From Selected]│ │
│  │  │   [Image Preview]       │  [Crop Region] (future)     │ │
│  │  │      336x336            │                             │ │
│  │  │                         │                             │ │
│  │  └─────────────────────────┘                             │ │
│  │                                                           │ │
│  │  Search Parameters:                                       │ │
│  │  ┌───────────────────────────────────────────────────┐   │ │
│  │  │ K Neighbors:     [5    ] ⬆️⬇️   (1-50)            │   │ │
│  │  │ Min Similarity:  [0.50 ] ⬆️⬇️   (0.0-1.0)         │   │ │
│  │  │ Category Filter: [All Images ▼]                   │   │ │
│  │  │                   └─ Eyes                          │   │ │
│  │  │                   └─ Hands                         │   │ │
│  │  │                   └─ Faces                         │   │ │
│  │  │                   └─ Backgrounds                   │   │ │
│  │  │ Quality (Rating): [Any ▼] (0-10)                  │   │ │
│  │  │ Model Filter:     [All Models ▼]                  │   │ │
│  │  │ Date Range:       [Any ▼]                         │   │ │
│  │  └───────────────────────────────────────────────────┘   │ │
│  │                                                           │ │
│  │  Search Type:                                             │ │
│  │  ◉ Visual Similarity (CLIP-ViT)                           │ │
│  │  ○ Text Embedding (CLIP-L/G)                              │ │
│  │  ○ Combined (Visual + Text)                               │ │
│  │                                                           │ │
│  │                       [Search] [Clear]                    │ │
│  └───────────────────────────────────────────────────────────┘ │
│                                                                │
│  ┌─── Search Results (15 matches) ──────────────────────────┐ │
│  │                                                           │ │
│  │  ┌────────┬────────┬────────┬────────┬────────┐         │ │
│  │  │ [img1] │ [img2] │ [img3] │ [img4] │ [img5] │ ← Row 1 │ │
│  │  │ 0.92   │ 0.89   │ 0.87   │ 0.85   │ 0.83   │         │ │
│  │  │ ☑️      │ ☑️      │ ☑️      │ ☐      │ ☐      │         │ │
│  │  ├────────┼────────┼────────┼────────┼────────┤         │ │
│  │  │ [img6] │ [img7] │ [img8] │ [img9] │ [img10]│ ← Row 2 │ │
│  │  │ 0.81   │ 0.79   │ 0.77   │ 0.75   │ 0.73   │         │ │
│  │  │ ☐      │ ☐      │ ☐      │ ☐      │ ☐      │         │ │
│  │  └────────┴────────┴────────┴────────┴────────┘         │ │
│  │                                                           │ │
│  │  Sort by: [Similarity ▼] [Descending ▼]                  │ │
│  │           └─ Date                                         │ │
│  │           └─ Rating                                       │ │
│  │           └─ File Size                                    │ │
│  │                                                           │ │
│  │  [Select All] [Select None] [Invert Selection]           │ │
│  └───────────────────────────────────────────────────────────┘ │
│                                                                │
│  ┌─── Selected Images (3) ──────────────────────────────────┐ │
│  │                                                           │ │
│  │  Actions:                                                 │ │
│  │  [📋 Copy Prompts]   - Copy all prompts to clipboard     │ │
│  │  [📊 Analyze Common Tags] - Show tag frequency           │ │
│  │  [💾 Export Embeddings] - Save CLIP-L/G as .npy/.json    │ │
│  │  [🎨 Send to ComfyUI] - Generate using vault conditioning│ │
│  │  [📁 Add to Album] - Create "Vault Search: [date]" album │ │
│  │                                                           │ │
│  └───────────────────────────────────────────────────────────┘ │
│                                                                │
│  Status: Ready                                   [Close]       │
└────────────────────────────────────────────────────────────────┘
```

---

## Database Queries

### Query 1: Visual Similarity Search (Primary Use Case)

```sql
-- Get visually similar images using CLIP-ViT (from image_embeddings table)
SELECT 
    i.id,
    i.path,
    i.file_name,
    i.prompt,
    i.model,
    i.rating,
    i.width,
    i.height,
    i.created_date,
    ie.clip_vit_embedding,
    i.clip_l_embedding,
    i.clip_g_embedding,
    1 - (ie.clip_vit_embedding <=> @reference_embedding::vector) as similarity
FROM image i
JOIN image_embeddings ie ON ie.image_id = i.id
WHERE ie.clip_vit_embedding IS NOT NULL
  AND (@category_filter = '' OR i.category = @category_filter)
  AND (@min_rating = 0 OR i.rating >= @min_rating)
  AND (@model_filter = '' OR i.model = @model_filter)
  AND (1 - (ie.clip_vit_embedding <=> @reference_embedding::vector)) >= @min_similarity
ORDER BY ie.clip_vit_embedding <=> @reference_embedding::vector
LIMIT @k_neighbors;
```

### Query 2: Text Embedding Similarity (Alternative)

```sql
-- Find images with similar text embeddings (CLIP-L)
SELECT 
    i.id,
    i.path,
    i.file_name,
    i.prompt,
    i.clip_l_embedding,
    i.clip_g_embedding,
    1 - (i.clip_l_embedding <=> @reference_text_embedding::vector) as similarity
FROM image i
WHERE i.clip_l_embedding IS NOT NULL
  AND (1 - (i.clip_l_embedding <=> @reference_text_embedding::vector)) >= @min_similarity
ORDER BY i.clip_l_embedding <=> @reference_text_embedding::vector
LIMIT @k_neighbors;
```

### Query 3: Combined Search (Visual + Text Weighted)

```sql
-- Hybrid search: weight visual and text similarity
SELECT 
    i.id,
    i.path,
    i.prompt,
    (
        (1 - (ie.clip_vit_embedding <=> @visual_embedding::vector)) * @visual_weight +
        (1 - (i.clip_l_embedding <=> @text_embedding::vector)) * @text_weight
    ) as combined_similarity
FROM image i
JOIN image_embeddings ie ON ie.image_id = i.id
WHERE ie.clip_vit_embedding IS NOT NULL
  AND i.clip_l_embedding IS NOT NULL
ORDER BY combined_similarity DESC
LIMIT @k_neighbors;
```

---

## C# Class Structure

### VaultSearchWindow.xaml.cs

```csharp
public partial class VaultSearchWindow : Window
{
    private readonly DataStore _dataStore;
    private readonly CLIPTextEncoder _clipEncoder;  // Your existing CLIP encoder
    private Image _referenceImage;
    private float[] _referenceEmbedding;
    private List<VaultSearchResult> _searchResults;
    
    public VaultSearchWindow(DataStore dataStore)
    {
        InitializeComponent();
        _dataStore = dataStore;
        _clipEncoder = ServiceLocator.CLIPEncoder;  // Or however you access it
        
        InitializeCategoryFilter();
        InitializeModelFilter();
    }
    
    // Event Handlers
    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        // Open file dialog, load image
        var dialog = new OpenFileDialog();
        // ... standard file dialog code
        
        if (dialog.ShowDialog() == true)
        {
            await LoadReferenceImage(dialog.FileName);
        }
    }
    
    private async void FromSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        // Use currently selected image in main grid
        var selectedImage = MainWindow.GetSelectedImage();
        if (selectedImage != null)
        {
            await LoadReferenceImage(selectedImage.Path);
        }
    }
    
    private async Task LoadReferenceImage(string path)
    {
        // 1. Load image from disk
        _referenceImage = Image.Load<Rgb24>(path);
        
        // 2. Extract CLIP-ViT embedding
        //    (Use your existing CLIPTextEncoder or create CLIPVisionEncoder)
        _referenceEmbedding = await ExtractVisualEmbedding(_referenceImage);
        
        // 3. Update preview
        ReferenceImagePreview.Source = ConvertToBitmapSource(_referenceImage);
        
        // 4. Enable search button
        SearchButton.IsEnabled = true;
    }
    
    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchButton.IsEnabled = false;
        StatusText.Text = "Searching...";
        ProgressBar.Visibility = Visibility.Visible;
        
        try
        {
            // Get parameters from UI
            int k = (int)KNeighborsSlider.Value;
            float minSimilarity = (float)MinSimilaritySlider.Value;
            string categoryFilter = (CategoryFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            int minRating = QualityFilterCombo.SelectedIndex;
            
            // Execute search
            _searchResults = await SearchVault(
                _referenceEmbedding,
                k,
                minSimilarity,
                categoryFilter,
                minRating
            );
            
            // Display results
            DisplayResults(_searchResults);
            
            StatusText.Text = $"Found {_searchResults.Count} matches";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            Logger.Error(ex, "Vault search failed");
        }
        finally
        {
            SearchButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }
    
    private async Task<List<VaultSearchResult>> SearchVault(
        float[] referenceEmbedding,
        int k,
        float minSimilarity,
        string categoryFilter,
        int minRating)
    {
        // Call your DataStore method (see below)
        return await _dataStore.SearchSimilarImages(
            referenceEmbedding,
            k,
            minSimilarity,
            categoryFilter,
            minRating
        );
    }
    
    private void DisplayResults(List<VaultSearchResult> results)
    {
        ResultsItemsControl.ItemsSource = results;
        
        // Or if using DataGrid:
        // ResultsDataGrid.ItemsSource = results;
    }
    
    private void CopyPromptsButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedResults = _searchResults.Where(r => r.IsSelected).ToList();
        if (selectedResults.Any())
        {
            var prompts = string.Join("\n\n---\n\n", selectedResults.Select(r => r.Prompt));
            Clipboard.SetText(prompts);
            StatusText.Text = $"Copied {selectedResults.Count} prompts to clipboard";
        }
    }
    
    private void AnalyzeTagsButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedResults = _searchResults.Where(r => r.IsSelected).ToList();
        if (selectedResults.Any())
        {
            // Open new window showing tag frequency analysis
            var analyzer = new TagAnalysisWindow(selectedResults);
            analyzer.ShowDialog();
        }
    }
    
    private void ExportEmbeddingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Export CLIP-L/G embeddings as numpy or JSON
        var selectedResults = _searchResults.Where(r => r.IsSelected).ToList();
        
        var saveDialog = new SaveFileDialog
        {
            Filter = "NumPy Array (*.npy)|*.npy|JSON (*.json)|*.json"
        };
        
        if (saveDialog.ShowDialog() == true)
        {
            ExportEmbeddings(selectedResults, saveDialog.FileName);
        }
    }
    
    private async void SendToComfyUIButton_Click(object sender, RoutedEventArgs e)
    {
        // Future implementation: Average embeddings and send to ComfyUI API
        var selectedResults = _searchResults.Where(r => r.IsSelected).ToList();
        
        if (!selectedResults.Any())
        {
            MessageBox.Show("Please select at least one image", "No Selection");
            return;
        }
        
        // Average CLIP-L and CLIP-G embeddings
        var avgClipL = AverageEmbeddings(selectedResults.Select(r => r.ClipLEmbedding));
        var avgClipG = AverageEmbeddings(selectedResults.Select(r => r.ClipGEmbedding));
        
        // Open ComfyUI generation dialog (future feature)
        var comfyDialog = new ComfyUIGenerationDialog(avgClipL, avgClipG);
        comfyDialog.ShowDialog();
    }
}
```

### VaultSearchResult.cs (Model)

```csharp
public class VaultSearchResult : INotifyPropertyChanged
{
    public int ImageId { get; set; }
    public string Path { get; set; }
    public string FileName { get; set; }
    public string Prompt { get; set; }
    public string Model { get; set; }
    public int Rating { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CreatedDate { get; set; }
    
    public float Similarity { get; set; }  // 0.0 - 1.0
    
    // Embeddings (for export/ComfyUI)
    public float[] ClipLEmbedding { get; set; }  // [768]
    public float[] ClipGEmbedding { get; set; }  // [1280]
    
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }
    
    public BitmapSource ThumbnailImage { get; set; }
    
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
```

### DataStore Extension (DataStore.VaultSearch.cs)

```csharp
public partial class DataStore
{
    public async Task<List<VaultSearchResult>> SearchSimilarImages(
        float[] referenceEmbedding,
        int k = 5,
        float minSimilarity = 0.5f,
        string categoryFilter = "",
        int minRating = 0,
        string modelFilter = "")
    {
        var results = new List<VaultSearchResult>();
        
        using var conn = await OpenConnectionAsync();
        using var cmd = new NpgsqlCommand();
        cmd.Connection = conn;
        
        // Build query dynamically based on filters
        var whereClauses = new List<string>
        {
            "ie.clip_vit_embedding IS NOT NULL",
            "(1 - (ie.clip_vit_embedding <=> @reference::vector)) >= @minSim"
        };
        
        if (!string.IsNullOrEmpty(categoryFilter))
            whereClauses.Add("i.category = @category");
        
        if (minRating > 0)
            whereClauses.Add("i.rating >= @rating");
        
        if (!string.IsNullOrEmpty(modelFilter))
            whereClauses.Add("i.model = @model");
        
        cmd.CommandText = $@"
            SELECT 
                i.id,
                i.path,
                i.file_name,
                i.prompt,
                i.model,
                i.rating,
                i.width,
                i.height,
                i.created_date,
                i.clip_l_embedding,
                i.clip_g_embedding,
                1 - (ie.clip_vit_embedding <=> @reference::vector) as similarity
            FROM image i
            JOIN image_embeddings ie ON ie.image_id = i.id
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY ie.clip_vit_embedding <=> @reference::vector
            LIMIT @k;
        ";
        
        cmd.Parameters.AddWithValue("reference", NpgsqlTypes.NpgsqlDbType.Unknown, 
            new Pgvector.Vector(referenceEmbedding));
        cmd.Parameters.AddWithValue("minSim", minSimilarity);
        cmd.Parameters.AddWithValue("k", k);
        
        if (!string.IsNullOrEmpty(categoryFilter))
            cmd.Parameters.AddWithValue("category", categoryFilter);
        if (minRating > 0)
            cmd.Parameters.AddWithValue("rating", minRating);
        if (!string.IsNullOrEmpty(modelFilter))
            cmd.Parameters.AddWithValue("model", modelFilter);
        
        using var reader = await cmd.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            var result = new VaultSearchResult
            {
                ImageId = reader.GetInt32(0),
                Path = reader.GetString(1),
                FileName = reader.GetString(2),
                Prompt = reader.GetString(3),
                Model = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Rating = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Width = reader.GetInt32(6),
                Height = reader.GetInt32(7),
                CreatedDate = reader.GetDateTime(8),
                ClipLEmbedding = reader.IsDBNull(9) ? null : 
                    ((Pgvector.Vector)reader.GetValue(9)).ToArray(),
                ClipGEmbedding = reader.IsDBNull(10) ? null : 
                    ((Pgvector.Vector)reader.GetValue(10)).ToArray(),
                Similarity = reader.GetFloat(11)
            };
            
            // Load thumbnail (async in background)
            _ = LoadThumbnailAsync(result);
            
            results.Add(result);
        }
        
        return results;
    }
    
    private async Task LoadThumbnailAsync(VaultSearchResult result)
    {
        try
        {
            // Use your existing thumbnail system
            var thumbnail = await ThumbnailCache.GetThumbnailAsync(result.Path);
            result.ThumbnailImage = thumbnail;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load thumbnail for {result.Path}");
        }
    }
}
```

---

## Key Features Breakdown

### 1. **Reference Image Selection**
- Browse button: Load any image from disk
- "From Selected" button: Use currently selected image in main grid
- Future: Crop tool to search by region (eye, hand, etc.)

### 2. **Search Parameters**
- **K Neighbors:** How many results (1-50)
- **Min Similarity:** Threshold to filter low-quality matches (0.0-1.0)
- **Category Filter:** Pre-labeled categories (eyes, hands, faces, backgrounds)
- **Quality Filter:** Only show images with rating >= X
- **Model Filter:** Restrict to specific models (Pony, Illustrious, etc.)

### 3. **Search Types**
- **Visual Similarity:** CLIP-ViT embeddings (image-to-image)
- **Text Embedding:** CLIP-L/G embeddings (prompt-to-prompt)
- **Combined:** Weighted blend of visual + text

### 4. **Results Display**
- Grid layout with thumbnails
- Similarity score displayed on each
- Checkboxes for multi-select
- Sort options (similarity, date, rating, file size)

### 5. **Actions on Selected Results**
- **Copy Prompts:** Paste into text editor/ComfyUI
- **Analyze Tags:** Show tag frequency (which tags appear most)
- **Export Embeddings:** Save CLIP-L/G as .npy or .json for external use
- **Send to ComfyUI:** (Future) Generate using averaged embeddings
- **Add to Album:** Create vault search album for organization

---

## Future Enhancements

### Phase 2: ComfyUI API Integration

```csharp
public class ComfyUIClient
{
    private readonly HttpClient _client;
    private readonly string _baseUrl = "http://127.0.0.1:8188";
    
    public async Task<string> QueuePrompt(
        float[] avgClipL,
        float[] avgClipG,
        string userPrompt,
        int seed)
    {
        // Build ComfyUI API workflow JSON
        var workflow = new
        {
            prompt = new Dictionary<string, object>
            {
                ["3"] = new  // KSampler node
                {
                    inputs = new
                    {
                        seed = seed,
                        steps = 30,
                        cfg = 7.0,
                        // ... other params
                        positive = new[] { avgClipL, avgClipG },  // Vault conditioning
                        // ... blend with user prompt
                    }
                }
            }
        };
        
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/prompt",
            workflow
        );
        
        return await response.Content.ReadAsStringAsync();
    }
}
```

### Phase 3: Crop Region Tool

```
Allow user to draw rectangle on reference image
  ↓
Extract only that region for embedding
  ↓
Search for images with similar regions (eyes, hands, etc.)
```

### Phase 4: Batch Vault Generation

```
Select 100 images
  ↓
For each: Query vault for top 5 similar
  ↓
Average embeddings
  ↓
Send all 100 to ComfyUI queue with vault conditioning
  ↓
Generate variations that match your aesthetic
```

---

## XAML Bindings (Example)

```xml
<!-- ResultsItemsControl in VaultSearchWindow.xaml -->
<ItemsControl x:Name="ResultsItemsControl">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <WrapPanel Orientation="Horizontal"/>
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Border Width="200" Height="250" Margin="5" 
                    BorderBrush="Gray" BorderThickness="1">
                <StackPanel>
                    <!-- Thumbnail -->
                    <Image Source="{Binding ThumbnailImage}" 
                           Width="180" Height="180"
                           Stretch="Uniform"/>
                    
                    <!-- Similarity Score -->
                    <TextBlock Text="{Binding Similarity, StringFormat=0.000}" 
                               HorizontalAlignment="Center"
                               FontWeight="Bold"
                               Foreground="Green"/>
                    
                    <!-- Checkbox -->
                    <CheckBox Content="Select" 
                              IsChecked="{Binding IsSelected}"
                              HorizontalAlignment="Center"/>
                    
                    <!-- Prompt (truncated) -->
                    <TextBlock Text="{Binding Prompt}" 
                               TextTrimming="CharacterEllipsis"
                               ToolTip="{Binding Prompt}"
                               Margin="5"/>
                </StackPanel>
            </Border>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

---

## Testing Checklist

- [ ] Load reference image from disk
- [ ] Load reference image from selected in main grid
- [ ] Extract CLIP-ViT embedding correctly
- [ ] Query returns results sorted by similarity
- [ ] Thumbnails load asynchronously
- [ ] Similarity scores display correctly (0.0-1.0)
- [ ] Category filter works
- [ ] Quality filter works
- [ ] Multi-select checkboxes work
- [ ] Copy prompts to clipboard
- [ ] Export embeddings as .npy/.json
- [ ] Tag analysis window opens
- [ ] Add to album creates new album

---

## Performance Considerations

### pgvector Index (Critical)

```sql
-- Create IVFFlat index on CLIP-ViT embeddings
CREATE INDEX IF NOT EXISTS idx_clip_vit_cosine 
ON image_embeddings 
USING ivfflat (clip_vit_embedding vector_cosine_ops)
WITH (lists = 100);

-- Stats: 500k images, k=5 query should take < 50ms
```

### Thumbnail Caching
- Load thumbnails asynchronously after query returns
- Don't block UI waiting for image files
- Use your existing thumbnail cache system

### Embedding Extraction
- CLIP-ViT encoding takes ~50-100ms per image
- Show progress bar if processing large reference image
- Consider caching reference embeddings if reused

---

## Implementation Priority

### MVP (2-3 hours)
1. ✅ Basic window with reference image preview
2. ✅ K neighbors slider
3. ✅ Search button → database query
4. ✅ Results grid with thumbnails + similarity scores
5. ✅ Copy prompts button

### Phase 2 (4-6 hours)
6. Category/quality/model filters
7. Multi-select checkboxes
8. Export embeddings
9. Tag analysis window

### Phase 3 (Future)
10. ComfyUI API integration
11. Crop region tool
12. Batch vault generation

---

## Files to Create/Modify

### New Files
- `Windows/VaultSearchWindow.xaml`
- `Windows/VaultSearchWindow.xaml.cs`
- `Models/VaultSearchResult.cs`
- `Database/DataStore.VaultSearch.cs` (partial class)
- `Services/CLIPVisionEncoder.cs` (if not exists)

### Modified Files
- `MainWindow.xaml` - Add menu item or toolbar button
- `MainWindow.xaml.cs` - Add event handler to open VaultSearchWindow
- `Database/DataStore.cs` - Add partial class reference

---

## Summary

**Core Concept:** "Show me images that look like this, so I can see what prompts worked."

**User Flow:**
1. User selects/loads reference image
2. DiffusionToolkit extracts CLIP-ViT embedding
3. pgvector finds top-K most similar images
4. User sees results with similarity scores
5. User copies prompts or exports embeddings
6. (Future) User sends to ComfyUI for generation

**Benefits:**
- Discover forgotten prompt patterns
- Find similar compositions for reference
- Build "style libraries" via albums
- (Future) Generate with vault-enhanced conditioning

---

**Now go to bed. Hit your head on something. Come back tomorrow and build this in 2-3 hours.** 🌙