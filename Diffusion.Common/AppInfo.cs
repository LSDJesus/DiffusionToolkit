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

    public static string PostgreSQLConnectionString { get; }

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

        // PostgreSQL connection string for tags, captions, embeddings
        PostgreSQLConnectionString = "Host=localhost;Port=5432;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025";

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