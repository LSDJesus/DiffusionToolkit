using Diffusion.Common;
using Xunit;

namespace Diffusion.Tests;

/// <summary>
/// Tests for StringUtility helper methods
/// </summary>
public class StringUtilityTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("short.txt", "short.txt")]
    public void TruncatePath_ShortPaths_ReturnedUnchanged(string? input, string expected)
    {
        var result = StringUtility.TruncatePath(input, 100);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TruncatePath_LongPath_TruncatesWithEllipsis()
    {
        var longPath = @"C:\Users\TestUser\Documents\Projects\VeryLongProjectName\SubFolder\AnotherFolder\DeepFolder\image.jpg";
        
        var result = StringUtility.TruncatePath(longPath, 50);
        
        Assert.StartsWith("...", result);
        // Result should be reasonably short (path truncation finds a good break point)
        Assert.True(result.Length < longPath.Length, $"Result '{result}' should be shorter than input");
        Assert.Contains("image.jpg", result);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("hello", "hello")]
    public void Truncate_ShortText_ReturnedUnchanged(string? input, string expected)
    {
        var result = StringUtility.Truncate(input, 100);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Truncate_LongText_TruncatesWithEllipsis()
    {
        var longText = "This is a very long piece of text that should be truncated";
        
        var result = StringUtility.Truncate(longText, 20);
        
        Assert.Equal(20, result.Length);
        Assert.EndsWith("...", result);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void FormatFileSize_VariousSizes_FormatsCorrectly(long bytes, string expected)
    {
        var result = StringUtility.FormatFileSize(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("invalid", null)]
    public void ParseTimeSpan_InvalidInput_ReturnsNull(string? input, TimeSpan? expected)
    {
        var result = StringUtility.ParseTimeSpan(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("100ms", 100)]
    [InlineData("5s", 5000)]
    [InlineData("2m", 120000)]
    [InlineData("1h", 3600000)]
    public void ParseTimeSpan_ValidInput_ParsesCorrectly(string input, double expectedMs)
    {
        var result = StringUtility.ParseTimeSpan(input);
        
        Assert.NotNull(result);
        Assert.Equal(expectedMs, result.Value.TotalMilliseconds);
    }
}
