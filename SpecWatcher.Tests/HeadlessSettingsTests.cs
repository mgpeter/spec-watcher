using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class HeadlessSettingsTests
{
    private static WatchSettings Base() => new() { RepoPath = Directory.GetCurrentDirectory() };

    // ---- FormatKind parsing ---------------------------------------------

    [Theory]
    [InlineData("table", OutputFormat.Table)]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("md", OutputFormat.Markdown)]
    [InlineData("JSON", OutputFormat.Json)]
    [InlineData("Md", OutputFormat.Markdown)]
    public void FormatKind_Parses_Case_Insensitively(string format, OutputFormat expected)
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), Format = format };
        Assert.Equal(expected, settings.FormatKind);
    }

    [Fact]
    public void FormatKind_Defaults_To_Table()
    {
        Assert.Equal(OutputFormat.Table, Base().FormatKind);
    }

    // ---- FailOnStatuses parsing -----------------------------------------

    [Theory]
    [InlineData("planning", SpecStatus.Planning)]
    [InlineData("in-progress", SpecStatus.InProgress)]
    [InlineData("in_progress", SpecStatus.InProgress)]
    [InlineData("wip", SpecStatus.InProgress)]
    [InlineData("complete", SpecStatus.Complete)]
    [InlineData("done", SpecStatus.Complete)]
    [InlineData("unknown", SpecStatus.Unknown)]
    [InlineData("IN PROGRESS", SpecStatus.InProgress)]
    public void FailOnStatuses_Maps_Tokens(string token, SpecStatus expected)
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), FailOn = token };
        Assert.Equal(new[] { expected }, settings.FailOnStatuses);
    }

    [Fact]
    public void FailOnStatuses_Parses_Comma_List_And_Dedupes()
    {
        var settings = new WatchSettings
        {
            RepoPath = Directory.GetCurrentDirectory(),
            FailOn = "planning, in-progress, planning",
        };
        Assert.Equal(new[] { SpecStatus.Planning, SpecStatus.InProgress }, settings.FailOnStatuses);
    }

    [Fact]
    public void FailOnStatuses_Empty_When_Not_Set()
    {
        Assert.Empty(Base().FailOnStatuses);
    }

    // ---- HasGate / WantsHeadless ----------------------------------------

    [Fact]
    public void HasGate_False_With_No_Gate_Flags()
    {
        Assert.False(Base().HasGate);
    }

    [Fact]
    public void HasGate_True_With_FailOn()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), FailOn = "planning" };
        Assert.True(settings.HasGate);
    }

    [Fact]
    public void HasGate_True_With_MinProgress()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), MinProgress = 80 };
        Assert.True(settings.HasGate);
    }

    [Fact]
    public void WantsHeadless_False_For_Plain_Table_No_Gate()
    {
        Assert.False(Base().WantsHeadless);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("md")]
    public void WantsHeadless_True_For_NonTable_Format(string format)
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), Format = format };
        Assert.True(settings.WantsHeadless);
    }

    [Fact]
    public void WantsHeadless_True_For_Once()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), Once = true };
        Assert.True(settings.WantsHeadless);
    }

    [Fact]
    public void WantsHeadless_True_When_Gate_Set()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), MinProgress = 50 };
        Assert.True(settings.WantsHeadless);
    }

    // ---- Validate -------------------------------------------------------

    [Fact]
    public void Validate_Rejects_Unknown_Format()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), Format = "xml" };
        Assert.False(settings.Validate().Successful);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_Rejects_MinProgress_Out_Of_Range(int value)
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), MinProgress = value };
        Assert.False(settings.Validate().Successful);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(100)]
    public void Validate_Accepts_MinProgress_In_Range(int value)
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), MinProgress = value };
        Assert.True(settings.Validate().Successful);
    }

    [Fact]
    public void Validate_Rejects_Unknown_FailOn_Token()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), FailOn = "planning,bogus" };
        Assert.False(settings.Validate().Successful);
    }

    [Fact]
    public void Validate_Accepts_Valid_FailOn_List()
    {
        var settings = new WatchSettings
        {
            RepoPath = Directory.GetCurrentDirectory(),
            FailOn = "planning,in-progress,done",
        };
        Assert.True(settings.Validate().Successful);
    }

    [Fact]
    public void Validate_Preserves_Interval_Check()
    {
        var settings = new WatchSettings { RepoPath = Directory.GetCurrentDirectory(), IntervalSeconds = 0 };
        Assert.False(settings.Validate().Successful);
    }
}
