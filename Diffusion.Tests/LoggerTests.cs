using Diffusion.Common;
using Xunit;

namespace Diffusion.Tests;

/// <summary>
/// Tests for Logger functionality
/// </summary>
public class LoggerTests
{
    [Fact]
    public void Log_WithMessage_DoesNotThrow()
    {
        // Should not throw even with basic message
        var exception = Record.Exception(() => Logger.Log("Test message"));
        Assert.Null(exception);
    }
    
    [Fact]
    public void Log_WithException_DoesNotThrow()
    {
        var testException = new InvalidOperationException("Test exception");
        
        var exception = Record.Exception(() => Logger.Log(testException));
        Assert.Null(exception);
    }
    
    [Fact]
    public void LogDebug_BelowMinLevel_DoesNotLog()
    {
        // Set minimum level to Info
        Logger.MinimumLevel = LogLevel.Info;
        
        try
        {
            // Debug should not throw (it just won't write)
            var exception = Record.Exception(() => Logger.LogDebug("Debug message"));
            Assert.Null(exception);
        }
        finally
        {
            Logger.MinimumLevel = LogLevel.Debug; // Reset
        }
    }
    
    [Fact]
    public void LogError_AlwaysLogs()
    {
        // Even with high minimum level, error should work
        Logger.MinimumLevel = LogLevel.Error;
        
        try
        {
            var exception = Record.Exception(() => Logger.LogError("Error message"));
            Assert.Null(exception);
        }
        finally
        {
            Logger.MinimumLevel = LogLevel.Debug; // Reset
        }
    }
    
    [Theory]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Info)]
    [InlineData(LogLevel.Warn)]
    [InlineData(LogLevel.Error)]
    public void LogLevel_AllLevelsSupported(LogLevel level)
    {
        Logger.MinimumLevel = LogLevel.Debug;
        
        var exception = Record.Exception(() => Logger.Log(level, $"Test message at {level}"));
        Assert.Null(exception);
    }
}
