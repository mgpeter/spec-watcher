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

    // ---- editing gate ----------------------------------------------------

    private static WatchSettings Editing(
        bool readOnly = false, bool once = false, string format = "table",
        string? failOn = null, int? minProgress = null) =>
        new()
        {
            RepoPath = Directory.GetCurrentDirectory(),
            ReadOnly = readOnly,
            Once = once,
            Format = format,
            FailOn = failOn,
            MinProgress = minProgress,
        };

    [Fact]
    public void Editing_Is_On_By_Default()
    {
        Assert.True(Editing().EditingEnabled);
    }

    [Fact]
    public void ReadOnly_Turns_Editing_Off()
    {
        Assert.False(Editing(readOnly: true).EditingEnabled);
    }

    [Theory]
    [InlineData(true, "table", null, null)]     // --once
    [InlineData(false, "json", null, null)]     // machine-readable output
    [InlineData(false, "md", null, null)]
    [InlineData(false, "table", "planning", null)]   // a gate flag
    [InlineData(false, "table", null, 50)]
    public void Every_Headless_Path_Disables_Editing(bool once, string format, string? failOn, int? minProgress)
    {
        var settings = Editing(once: once, format: format, failOn: failOn, minProgress: minProgress);

        Assert.True(settings.WantsHeadless);
        Assert.False(settings.EditingEnabled);
    }

    [Fact]
    public void ReadOnly_Is_Accepted_By_Validation()
    {
        Assert.True(Editing(readOnly: true).Validate().Successful);
    }
}
