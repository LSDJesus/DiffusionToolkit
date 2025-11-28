using System;
using System.IO;

namespace Diffusion.Common;

/// <summary>
/// Log severity levels
/// </summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public class Logger
{
    private static readonly object _lock = new object();
    
    /// <summary>
    /// Minimum log level to write. Messages below this level are ignored.
    /// Default is Debug (all messages logged).
    /// </summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;
    
    /// <summary>
    /// Log a message at the specified level
    /// </summary>
    public static void Log(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;
        
        var levelStr = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO"
        };
        
        lock (_lock)
        {
            File.AppendAllText("DiffusionToolkit.log", $"{DateTime.Now:G} [{levelStr}]: {message}\r\n");
        }
    }

    /// <summary>
    /// Log a message (backward compatible, logs at Info level)
    /// </summary>
    public static void Log(string message)
    {
        Log(LogLevel.Info, message);
    }

    /// <summary>
    /// Log an exception at Error level
    /// </summary>
    public static void Log(Exception exception)
    {
        Log(LogLevel.Error, exception.ToString());
    }
    
    /// <summary>
    /// Log a debug message (verbose, for development)
    /// </summary>
    public static void LogDebug(string message) => Log(LogLevel.Debug, message);
    
    /// <summary>
    /// Log an informational message
    /// </summary>
    public static void LogInfo(string message) => Log(LogLevel.Info, message);
    
    /// <summary>
    /// Log a warning message
    /// </summary>
    public static void LogWarn(string message) => Log(LogLevel.Warn, message);
    
    /// <summary>
    /// Log an error message
    /// </summary>
    public static void LogError(string message) => Log(LogLevel.Error, message);
    
    /// <summary>
    /// Log an error with exception details
    /// </summary>
    public static void LogError(string message, Exception exception)
    {
        Log(LogLevel.Error, $"{message}: {exception}");
    }
}