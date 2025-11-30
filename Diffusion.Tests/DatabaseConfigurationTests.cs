using Diffusion.Common;
using Xunit;

namespace Diffusion.Tests;

/// <summary>
/// Tests for DatabaseConfiguration
/// </summary>
public class DatabaseConfigurationTests
{
    [Fact]
    public void Default_HasReasonableValues()
    {
        var config = DatabaseConfiguration.Default;
        
        // Connection pool
        Assert.True(config.MaxPoolSize > 0);
        Assert.True(config.MinPoolSize > 0);
        Assert.True(config.MaxPoolSize >= config.MinPoolSize);
        
        // Timeouts
        Assert.True(config.CommandTimeoutSeconds > 0);
        Assert.True(config.LongRunningCommandTimeoutSeconds > config.CommandTimeoutSeconds);
        
        // Batch sizes
        Assert.True(config.BulkInsertBatchSize > 0);
        Assert.True(config.CopyBatchSize > 0);
        Assert.True(config.ProgressUpdateInterval > 0);
        
        // Retry
        Assert.True(config.MaxConnectionRetries > 0);
        Assert.True(config.ConnectionRetryDelayMs > 0);
    }

    [Fact]
    public void Default_IsSingleton()
    {
        var config1 = DatabaseConfiguration.Default;
        var config2 = DatabaseConfiguration.Default;
        
        Assert.Same(config1, config2);
    }

    [Fact]
    public void NewInstance_CanBeCustomized()
    {
        var config = new DatabaseConfiguration
        {
            MaxPoolSize = 100,
            MinPoolSize = 10,
            BulkInsertBatchSize = 500
        };
        
        Assert.Equal(100, config.MaxPoolSize);
        Assert.Equal(10, config.MinPoolSize);
        Assert.Equal(500, config.BulkInsertBatchSize);
        
        // Verify default still has original values
        Assert.NotEqual(100, DatabaseConfiguration.Default.MaxPoolSize);
    }
}
