using Relay.Core.Performance;
using Relay.Core.Settings;
using Xunit;

namespace Relay.Tests;

public class UsageFormatTests
{
    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(500, "1 KB")]
    [InlineData(840 * 1024, "840 KB")]
    [InlineData(126L * 1024 * 1024, "126 MB")]
    [InlineData(1536L * 1024 * 1024, "1.5 GB")]
    [InlineData(12L * 1024 * 1024 * 1024, "12 GB")]
    public void Bytes(long value, string expected) => Assert.Equal(expected, UsageFormat.Bytes(value));

    [Fact]
    public void CpuPercentIsShareOfWholeMachine()
    {
        // 1 s of CPU over 1 s on 4 cores = 25 %.
        Assert.Equal(25, UsageFormat.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4), 3);
        Assert.Equal(0, UsageFormat.CpuPercent(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(0, UsageFormat.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.Equal(100, UsageFormat.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 1));
    }

    [Fact]
    public void MemorySaverIsOnByDefault()
    {
        var s = new RelaySettings();
        Assert.Equal(60, s.DiscardTabsAfterMinutes);
        Assert.True(s.DiscardTabsAfterMinutes > s.SleepTabsAfterMinutes);
        Assert.Equal(60, s.Clone().DiscardTabsAfterMinutes);
    }
}
