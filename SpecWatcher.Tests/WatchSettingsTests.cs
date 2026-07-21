using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class WatchSettingsTests
{
    private static WatchSettings Settings(int idleDays) =>
        new() { RepoPath = Directory.GetCurrentDirectory(), DriftIdleDays = idleDays };

    [Fact]
    public void DriftIdleDays_Negative_Is_Rejected()
    {
        var result = Settings(-1).Validate();
        Assert.False(result.Successful);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void DriftIdleDays_NonNegative_Is_Accepted(int days)
    {
        Assert.True(Settings(days).Validate().Successful);
    }

    [Fact]
    public void ToDriftOptions_Carries_IdleDays()
    {
        Assert.Equal(new DriftOptions(14), Settings(14).ToDriftOptions());
    }
}
