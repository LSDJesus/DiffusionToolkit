using Diffusion.Common;
using Xunit;

namespace Diffusion.Tests;

/// <summary>
/// Tests for AppInfo connection string lookup logic
/// </summary>
public class AppInfoTests
{
    [Fact]
    public void PostgreSQLConnectionString_ReturnsDefaultWhenNoOverrides()
    {
        // Clear any cached value and environment variable
        Environment.SetEnvironmentVariable("DIFFUSION_DB_CONNECTION", null);
        AppInfo.ClearConnectionStringCache();
        
        var connectionString = AppInfo.PostgreSQLConnectionString;
        
        // Should return the default connection string (or file-based if exists)
        Assert.NotNull(connectionString);
        Assert.NotEmpty(connectionString);
        Assert.Contains("Host=", connectionString);
    }
    
    [Fact]
    public void PostgreSQLConnectionString_PrefersEnvironmentVariable()
    {
        const string testConnectionString = "Host=testhost;Port=5432;Database=testdb;Username=testuser;Password=testpass";
        
        try
        {
            Environment.SetEnvironmentVariable("DIFFUSION_DB_CONNECTION", testConnectionString);
            AppInfo.ClearConnectionStringCache();
            
            var connectionString = AppInfo.PostgreSQLConnectionString;
            
            Assert.Equal(testConnectionString, connectionString);
        }
        finally
        {
            // Clean up
            Environment.SetEnvironmentVariable("DIFFUSION_DB_CONNECTION", null);
            AppInfo.ClearConnectionStringCache();
        }
    }
    
    [Fact]
    public void PostgreSQLConnectionString_IsCached()
    {
        AppInfo.ClearConnectionStringCache();
        
        // First call
        var first = AppInfo.PostgreSQLConnectionString;
        
        // Second call should return same instance (cached)
        var second = AppInfo.PostgreSQLConnectionString;
        
        Assert.Same(first, second);
    }
    
    [Fact]
    public void ClearConnectionStringCache_AllowsNewLookup()
    {
        const string testConnectionString = "Host=newhost;Port=5432;Database=newdb";
        
        try
        {
            // Get initial value
            AppInfo.ClearConnectionStringCache();
            var initial = AppInfo.PostgreSQLConnectionString;
            
            // Set environment variable
            Environment.SetEnvironmentVariable("DIFFUSION_DB_CONNECTION", testConnectionString);
            
            // Without clearing cache, should still return old value
            var cached = AppInfo.PostgreSQLConnectionString;
            Assert.Equal(initial, cached);
            
            // After clearing, should return new value
            AppInfo.ClearConnectionStringCache();
            var updated = AppInfo.PostgreSQLConnectionString;
            Assert.Equal(testConnectionString, updated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DIFFUSION_DB_CONNECTION", null);
            AppInfo.ClearConnectionStringCache();
        }
    }
}
