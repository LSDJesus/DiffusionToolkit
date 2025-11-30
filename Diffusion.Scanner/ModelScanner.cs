using System;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Diffusion.Common;

namespace Diffusion.IO;

public class ModelScanner
{
    private static readonly string CacheFileName = "model_hash_cache.json";
    private static ConcurrentDictionary<string, CachedHash> _hashCache = new();
    private static bool _cacheLoaded = false;
    private static readonly object _cacheLock = new();

    public static IEnumerable<Model> Scan(string path)
    {
        // Load cache from disk if not already loaded
        LoadCache(path);

        var files = Directory.EnumerateFiles(path, "*.ckpt", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(path, "*.safetensors", SearchOption.AllDirectories));

        var modelsToReturn = new List<Model>();
        var cacheModified = false;

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(path, file);

            if (File.Exists(file))
            {
                string hash = null;
                try
                {
                    hash = GetCachedHash(file, ref cacheModified);
                }
                catch (Exception e)
                {
                    // Hash calculation failed, continue without hash
                }

                modelsToReturn.Add(new Model()
                {
                    Path = relativePath,
                    Filename = Path.GetFileNameWithoutExtension(file),
                    Hash = hash,
                    IsLocal = true
                });
            }
        }

        // Save cache if modified
        if (cacheModified)
        {
            SaveCache(path);
        }

        return modelsToReturn;
    }

    private static string GetCachedHash(string filePath, ref bool cacheModified)
    {
        var fileInfo = new FileInfo(filePath);
        var lastWriteTime = fileInfo.LastWriteTimeUtc.Ticks;
        var cacheKey = filePath.ToLowerInvariant();

        // Check if we have a cached hash that's still valid
        if (_hashCache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.LastWriteTimeTicks == lastWriteTime)
            {
                return cached.Hash;
            }
        }

        // Calculate new hash
        var hash = HashFunctions.CalculateHash(filePath);

        // Update cache
        _hashCache[cacheKey] = new CachedHash
        {
            Hash = hash,
            LastWriteTimeTicks = lastWriteTime
        };
        cacheModified = true;

        return hash;
    }

    private static void LoadCache(string modelPath)
    {
        lock (_cacheLock)
        {
            if (_cacheLoaded)
                return;

            _cacheLoaded = true;

            try
            {
                var cachePath = GetCachePath(modelPath);
                if (File.Exists(cachePath))
                {
                    var json = File.ReadAllText(cachePath);
                    var cached = JsonSerializer.Deserialize<Dictionary<string, CachedHash>>(json);
                    if (cached != null)
                    {
                        _hashCache = new ConcurrentDictionary<string, CachedHash>(cached);
                    }
                }
            }
            catch
            {
                // If cache load fails, start fresh
                _hashCache = new ConcurrentDictionary<string, CachedHash>();
            }
        }
    }

    private static void SaveCache(string modelPath)
    {
        try
        {
            var cachePath = GetCachePath(modelPath);
            var json = JsonSerializer.Serialize(_hashCache.ToDictionary(k => k.Key, v => v.Value));
            File.WriteAllText(cachePath, json);
        }
        catch
        {
            // Ignore cache save failures
        }
    }

    private static string GetCachePath(string modelPath)
    {
        // Store cache in model root folder
        return Path.Combine(modelPath, CacheFileName);
    }

    private class CachedHash
    {
        public string Hash { get; set; }
        public long LastWriteTimeTicks { get; set; }
    }
}