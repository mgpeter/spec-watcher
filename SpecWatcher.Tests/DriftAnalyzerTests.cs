using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class DriftAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 20, 12, 0, 0, TimeSpan.Zero);

    private static SpecRow Row(
        SpecStatus status, int done, int total, bool hasTasks = true, DateTimeOffset? modified = null) =>
        new("Name", "desc", status, status.ToString(), null, done, total, hasTasks, "2026-07-20-x", "/x", modified);

    // ---- Task 1: data contract defaults ----

    [Fact]
    public void SpecRow_HasSafeDriftDefaults()
    {
        var row = new SpecRow("N", "d", SpecStatus.Planning, "", null, 0, 0, false, "f", "/f");
        Assert.Null(row.LastModifiedUtc);
        Assert.Equal(DriftState.None, row.Drift);
        Assert.Null(row.DriftReason);
    }

    // ---- Task 2: contradiction detection ----

    [Fact]
    public void Complete_But_Unchecked_Is_Overstated()
    {
        var (state, reason) = DriftAnalyzer.Analyze(Row(SpecStatus.Complete, 4, 9), Now, DriftOptions.Default);
        Assert.Equal(DriftState.Overstated, state);
        Assert.Equal("Marked Complete, but 4/9 tasks checked", reason);
    }

    [Theory]
    [InlineData(SpecStatus.Planning)]
    [InlineData(SpecStatus.InProgress)]
    public void AllChecked_But_NotComplete_Is_Understated(SpecStatus status)
    {
        var (state, reason) = DriftAnalyzer.Analyze(Row(status, 5, 5), Now, DriftOptions.Default);
        Assert.Equal(DriftState.Understated, state);
        Assert.Contains("All 5 tasks checked", reason);
    }

    [Fact]
    public void Planning_With_Some_Checked_Is_Understated()
    {
        var (state, reason) = DriftAnalyzer.Analyze(Row(SpecStatus.Planning, 2, 6), Now, DriftOptions.Default);
        Assert.Equal(DriftState.Understated, state);
        Assert.Equal("Marked Planning, but 2/6 tasks in progress", reason);
    }

    [Theory]
    [InlineData(SpecStatus.Complete, 5, 5)]   // healthy complete
    [InlineData(SpecStatus.InProgress, 0, 5)] // in progress, nothing yet
    public void Healthy_Is_None(SpecStatus status, int done, int total)
    {
        var (state, reason) = DriftAnalyzer.Analyze(Row(status, done, total), Now, DriftOptions.Default);
        Assert.Equal(DriftState.None, state);
        Assert.Null(reason);
    }

    [Fact]
    public void NoTasks_Is_None()
    {
        var (state, _) = DriftAnalyzer.Analyze(Row(SpecStatus.Complete, 0, 0, hasTasks: false), Now, DriftOptions.Default);
        Assert.Equal(DriftState.None, state);
    }

    [Fact]
    public void EmptyTasks_Is_None()
    {
        var (state, _) = DriftAnalyzer.Analyze(Row(SpecStatus.Complete, 0, 0, hasTasks: true), Now, DriftOptions.Default);
        Assert.Equal(DriftState.None, state);
    }

    [Fact]
    public void Unknown_Status_Is_None()
    {
        var (state, _) = DriftAnalyzer.Analyze(Row(SpecStatus.Unknown, 3, 5), Now, DriftOptions.Default);
        Assert.Equal(DriftState.None, state);
    }

    // ---- Task 3: idle detection (opt-in) ----

    [Fact]
    public void InProgress_Old_Is_Idle_When_Enabled()
    {
        var row = Row(SpecStatus.InProgress, 2, 5, modified: Now.AddDays(-10));
        var (state, reason) = DriftAnalyzer.Analyze(row, Now, new DriftOptions(7));
        Assert.Equal(DriftState.Idle, state);
        Assert.Equal("In progress, no file change in 10d", reason);
    }

    [Fact]
    public void InProgress_Recent_Is_None()
    {
        var row = Row(SpecStatus.InProgress, 2, 5, modified: Now.AddDays(-2));
        var (state, _) = DriftAnalyzer.Analyze(row, Now, new DriftOptions(7));
        Assert.Equal(DriftState.None, state);
    }

    [Fact]
    public void Idle_Off_By_Default()
    {
        var row = Row(SpecStatus.InProgress, 2, 5, modified: Now.AddDays(-100));
        var (state, _) = DriftAnalyzer.Analyze(row, Now, DriftOptions.Default);
        Assert.Equal(DriftState.None, state);
    }

    [Fact]
    public void Null_Mtime_Never_Idle()
    {
        var row = Row(SpecStatus.InProgress, 2, 5, modified: null);
        var (state, _) = DriftAnalyzer.Analyze(row, Now, new DriftOptions(7));
        Assert.Equal(DriftState.None, state);
    }

    [Fact]
    public void Future_Mtime_Is_None()
    {
        var row = Row(SpecStatus.InProgress, 2, 5, modified: Now.AddDays(+5));
        var (state, _) = DriftAnalyzer.Analyze(row, Now, new DriftOptions(7));
        Assert.Equal(DriftState.None, state);
    }

    [Fact]
    public void Contradiction_Beats_Idle()
    {
        // Complete-but-unchecked AND old: contradiction wins.
        var row = Row(SpecStatus.Complete, 3, 9, modified: Now.AddDays(-100));
        var (state, _) = DriftAnalyzer.Analyze(row, Now, new DriftOptions(7));
        Assert.Equal(DriftState.Overstated, state);
    }
}
