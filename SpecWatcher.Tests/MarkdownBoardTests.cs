using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class MarkdownBoardTests
{
    private static WatchSettings Settings(string? failOn = null, int? minProgress = null) => new()
    {
        RepoPath = Directory.GetCurrentDirectory(),
        FailOn = failOn,
        MinProgress = minProgress,
    };

    private static SpecRow Row(string folder, SpecStatus status, int done, int total, bool hasTasks = true, string name = "Some Spec") =>
        new(name, "desc", status, status.ToString(), null, done, total, hasTasks, folder, "/x/" + folder);

    private static ScanResult Result(params SpecRow[] rows) =>
        new(rows.ToImmutableArray(), DateTimeOffset.UtcNow);

    [Fact]
    public void Renders_Github_Table_Header_And_Separator()
    {
        var md = MarkdownBoard.Render(Settings(), Result(Row("2026-07-20-x", SpecStatus.Planning, 0, 3)), null);
        Assert.Contains("| Name | Status | Progress | Folder |", md);
        Assert.Contains("| --- | --- | --- | --- |", md);
    }

    [Fact]
    public void Renders_Data_Row_And_Summary_Line()
    {
        var md = MarkdownBoard.Render(Settings(), Result(Row("2026-07-20-x", SpecStatus.InProgress, 2, 5, name: "My Spec")), null);
        Assert.Contains("| My Spec |", md);
        Assert.Contains("2026-07-20-x", md);
        Assert.Contains("40% (2/5)", md);
        Assert.Contains("1 spec", md);      // summary line references the count
    }

    [Fact]
    public void Escapes_Pipe_In_Cells()
    {
        var md = MarkdownBoard.Render(Settings(), Result(Row("f", SpecStatus.Planning, 0, 1, name: "A | B")), null);
        Assert.Contains("A \\| B", md);
    }

    [Fact]
    public void No_Tasks_Shows_Dash_Progress()
    {
        var md = MarkdownBoard.Render(Settings(), Result(Row("f", SpecStatus.Planning, 0, 0, hasTasks: false)), null);
        Assert.Contains("| — |", md);
    }

    [Fact]
    public void Gate_Section_When_Present()
    {
        var settings = Settings(failOn: "planning");
        var result = Result(Row("a", SpecStatus.Planning, 0, 1));
        var gate = GateEvaluator.Evaluate(result, settings);

        var md = MarkdownBoard.Render(settings, result, gate);

        Assert.Contains("Gate", md);
        Assert.Contains("a", md);
    }

    [Fact]
    public void Ends_With_Newline()
    {
        var md = MarkdownBoard.Render(Settings(), Result(), null);
        Assert.EndsWith("\n", md);
    }
}
