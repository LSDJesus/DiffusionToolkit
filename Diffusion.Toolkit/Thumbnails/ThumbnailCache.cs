using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Diffusion.Database.PostgreSQL;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Thumbnails;

/// <summary>
/// PostgreSQL-backed thumbnail cache with in-memory LRU layer.
/// Replaces SQLite dt_thumbnails.db files scattered across folders.
/// </summary>
public class ThumbnailCache
{
    private static ThumbnailCache? _instance;
    public static ThumbnailCache Instance => _instance ?? throw new InvalidOperationException("ThumbnailCache not initialized");

    private readonly ConcurrentDictionary<string, CacheEntry> _memoryCache;
    private readonly int _maxMemoryItems;
    private readonly int _evictCount;
    private readonly object _lock = new object();

    private ThumbnailCache(int maxMemoryItems, int evictCount)
    {
        _maxMemoryItems = maxMemoryItems;
        _evictCount = evictCount;
        _memoryCache = new ConcurrentDictionary<string, CacheEntry>();
    }

    private static string GetCacheKey(string path, int size) => $"{path}|{size}";

    private PostgreSQLDataStore? DataStore => ServiceLocator.DataStore;

    /// <summary>
    /// Try to get a thumbnail from cache (memory first, then PostgreSQL)
    /// </summary>
    public bool TryGetThumbnail(string path, int size, out BitmapSource? thumbnail)
    {
        thumbnail = null;
        var key = GetCacheKey(path, size);

        // Check memory cache first
        if (_memoryCache.TryGetValue(key, out var entry))
        {
            thumbnail = entry.BitmapSource;
            return true;
        }

        // Check PostgreSQL
        var dataStore = DataStore;
        if (dataStore == null) return false;

        try
        {
            var data = dataStore.GetThumbnail(path, size);
            if (data != null && data.Length > 0)
            {
                var bitmap = BytesToBitmap(data);
                if (bitmap != null)
                {
                    // Add to memory cache
                    AddToMemoryCache(key, bitmap);
                    thumbnail = bitmap;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Database error - return false
        }

        return false;
    }

    /// <summary>
    /// Add thumbnail to both memory cache and PostgreSQL
    /// </summary>
    public void AddThumbnail(string path, int size, BitmapImage bitmapImage)
    {
        if (!File.Exists(path)) return;

        var key = GetCacheKey(path, size);
        
        // Add to memory cache
        AddToMemoryCache(key, bitmapImage);

        // Persist to PostgreSQL
        var dataStore = DataStore;
        if (dataStore == null) return;

        try
        {
            var data = BitmapToBytes(bitmapImage);
            if (data != null && data.Length > 0)
            {
                dataStore.SetThumbnail(path, size, data);
            }
        }
        catch (Exception)
        {
            // Database error - thumbnail still in memory cache
        }
    }

    /// <summary>
    /// Add thumbnail from raw bytes (for embedded previews in model files)
    /// </summary>
    public bool AddThumbnailFromBytes(string path, int size, byte[] imageData)
    {
        if (!File.Exists(path)) return false;

        var dataStore = DataStore;
        if (dataStore == null) return false;

        try
        {
            dataStore.SetThumbnail(path, size, imageData);
            
            // Also add to memory cache
            var bitmap = BytesToBitmap(imageData);
            if (bitmap != null)
            {
                var key = GetCacheKey(path, size);
                AddToMemoryCache(key, bitmap);
            }
            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Check if thumbnail exists in cache (memory or PostgreSQL)
    /// </summary>
    public bool HasThumbnail(string path, int size)
    {
        var key = GetCacheKey(path, size);
        
        // Check memory first
        if (_memoryCache.ContainsKey(key)) return true;

        // Check PostgreSQL
        var dataStore = DataStore;
        if (dataStore == null) return false;

        try
        {
            return dataStore.HasThumbnail(path, size);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Remove thumbnail from cache when file is deleted/moved
    /// </summary>
    public bool Unload(string path)
    {
        // Remove all sizes from memory cache
        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var key in _memoryCache.Keys)
        {
            if (key.StartsWith(path + "|"))
            {
                keysToRemove.Add(key);
            }
        }
        
        foreach (var key in keysToRemove)
        {
            _memoryCache.TryRemove(key, out _);
        }

        // Remove from PostgreSQL
        var dataStore = DataStore;
        if (dataStore == null) return keysToRemove.Count > 0;

        try
        {
            dataStore.DeleteThumbnail(path);
            return true;
        }
        catch (Exception)
        {
            return keysToRemove.Count > 0;
        }
    }

    /// <summary>
    /// Clear memory cache only (PostgreSQL cache persists)
    /// </summary>
    public void Clear()
    {
        _memoryCache.Clear();
    }

    /// <summary>
    /// Clear entire cache including PostgreSQL
    /// </summary>
    public int ClearAll()
    {
        _memoryCache.Clear();

        var dataStore = DataStore;
        if (dataStore == null) return 0;

        try
        {
            return dataStore.ClearThumbnailCache();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void AddToMemoryCache(string key, BitmapSource bitmap)
    {
        lock (_lock)
        {
            // Evict old entries if at capacity
            if (_memoryCache.Count >= _maxMemoryItems)
            {
                var toEvict = _memoryCache
                    .OrderBy(x => x.Value.Created)
                    .Take(_evictCount)
                    .Select(x => x.Key)
                    .ToList();

                foreach (var evictKey in toEvict)
                {
                    _memoryCache.TryRemove(evictKey, out _);
                }
            }
        }

        _memoryCache.TryAdd(key, new CacheEntry
        {
            BitmapSource = bitmap,
            Created = DateTime.UtcNow
        });
    }

    private static byte[]? BitmapToBytes(BitmapImage bitmapImage)
    {
        if (bitmapImage.StreamSource is MemoryStream ms)
        {
            return ms.ToArray();
        }

        // Encode to JPEG if not already a memory stream
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
        
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapImage? BytesToBitmap(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(data);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static void CreateInstance(int maxMemoryItems, int evictCount)
    {
        _instance = new ThumbnailCache(maxMemoryItems, evictCount);
    }
}

/// <summary>
/// In-memory cache entry for thumbnails
/// </summary>
public class CacheEntry
{
    public DateTime Created { get; set; }
    public BitmapSource BitmapSource { get; set; } = null!;
}
