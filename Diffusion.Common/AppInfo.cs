using System;
using System.IO;

namespace Diffusion.Common;

public static class AppInfo
{
    private const string AppName = "DiffusionToolkit";
    public static string AppDir { get; }
    public static SemanticVersion Version => SemanticVersionHelper.GetLocalVersion();
    public static string AppDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiffusionToolkit");

    public static string DatabasePath { get; }
    
    /// <summary>
    /// PostgreSQL connection string. Priority order:
    /// 1. Environment variable DIFFUSION_DB_CONNECTION
    /// 2. Settings file DatabaseConnectionString
    /// 3. Default local Docker connection
    /// </summary>
    public static string PostgreSQLConnectionString => GetPostgreSQLConnectionString();
    
    private static string? _cachedConnectionString;
    
    private static string GetPostgreSQLConnectionString()
    {
        // Return cached value if available
        if (_cachedConnectionString != null)
            return _cachedConnectionString;
            
        // 1. Check environment variable first (most secure)
        var envConnection = Environment.GetEnvironmentVariable("DIFFUSION_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            _cachedConnectionString = envConnection;
            return envConnection;
        }
        
        // 2. Try to load from connection string file (for portable mode)
        var connStringPath = Path.Combine(AppDir, "database.connection");
        if (File.Exists(connStringPath))
        {
            try
            {
                var fileConnection = File.ReadAllText(connStringPath).Trim();
                if (!string.IsNullOrWhiteSpace(fileConnection))
                {
                    _cachedConnectionString = fileConnection;
                    return fileConnection;
                }
            }
            catch { /* Fall through to default */ }
        }
        
        // 3. Check AppData location for non-portable mode
        var appDataConnPath = Path.Combine(AppDataPath, "database.connection");
        if (File.Exists(appDataConnPath))
        {
            try
            {
                var fileConnection = File.ReadAllText(appDataConnPath).Trim();
                if (!string.IsNullOrWhiteSpace(fileConnection))
                {
                    _cachedConnectionString = fileConnection;
                    return fileConnection;
                }
            }
            catch { /* Fall through to default */ }
        }
        
        // 4. Default for local Docker development (matches docker-compose.yml)
        _cachedConnectionString = DefaultConnectionString;
        return DefaultConnectionString;
    }
    
    /// <summary>
    /// Updates the cached connection string (called when settings change)
    /// </summary>
    public static void SetConnectionString(string connectionString)
    {
        _cachedConnectionString = connectionString;
    }
    
    /// <summary>
    /// Clears cached connection string to force reload
    /// </summary>
    public static void ClearConnectionStringCache()
    {
        _cachedConnectionString = null;
    }
    
    /// <summary>
    /// Default connection string for local Docker PostgreSQL
    /// </summary>
    public const string DefaultConnectionString = "Host=localhost;Port=5436;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025";

    public static string SettingsPath { get; }

    public static bool IsPortable { get; }

    static AppInfo()
    {
        AppDir = AppDomain.CurrentDomain.BaseDirectory;

        if (AppDir.EndsWith("\\"))
        {
            AppDir = AppDir.Substring(0, AppDir.Length - 1);
        }

        DatabasePath = Path.Combine(AppInfo.AppDir, "diffusion-toolkit.db");

        IsPortable = true;

        SettingsPath = Path.Combine(AppInfo.AppDir, "config.json");

        if (!File.Exists(SettingsPath))
        {
            IsPortable = false;
            SettingsPath = Path.Combine(AppInfo.AppDataPath, "config.json");
            DatabasePath = Path.Combine(AppInfo.AppDataPath, "diffusion-toolkit.db");
        }

    }


}