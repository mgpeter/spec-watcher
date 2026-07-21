using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class HeadlessRunnerTests
{
    private static WatchSettings Settings(string format = "json", string? failOn = null, int? minProgress = null) => new()
    {
        RepoPath = Directory.GetCurrentDirectory(),
        Format = format,
        FailOn = failOn,
        MinProgress = minProgress,
    };

    private static SpecRow Row(string folder, SpecStatus status, int done, int total, bool hasTasks = true) =>
        new("Name", "desc", status, status.ToString(), null, done, total, hasTasks, folder, "/x/" + folder);

    private static ScanResult Ok(params SpecRow[] rows) =>
        new(rows.ToImmutableArray(), DateTimeOffset.UtcNow);

    private static ScanResult Errored() =>
        new(ImmutableArray<SpecRow>.Empty, DateTimeOffset.UtcNow, "Specs directory not found: /nope");

    [Fact]
    public void Clean_Pass_Returns_0()
    {
        var writer = new StringWriter();
        var code = HeadlessRunner.Run(Settings(), Ok(Row("a", SpecStatus.Complete, 3, 3)), writer);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Scan_Error_Returns_1()
    {
        var writer = new StringWriter();
        var code = HeadlessRunner.Run(Settings(), Errored(), writer);
        Assert.Equal(1, code);
    }

    [Fact]
    public void Scan_Error_Beats_Gate_Returns_1()
    {
        // gate requested, but a scan error takes precedence (partial data untrusted)
        var writer = new StringWriter();
        var code = HeadlessRunner.Run(Settings(failOn: "planning"), Errored(), writer);
        Assert.Equal(1, code);
    }

    [Fact]
    public void Gate_Failure_Returns_2()
    {
        var writer = new StringWriter();
        var code = HeadlessRunner.Run(Settings(failOn: "planning"), Ok(Row("a", SpecStatus.Planning, 0, 1)), writer);
        Assert.Equal(2, code);
    }

    [Fact]
    public void Gate_Pass_Returns_0()
    {
        var writer = new StringWriter();
        var code = HeadlessRunner.Run(Settings(failOn: "planning"), Ok(Row("a", SpecStatus.Complete, 1, 1)), writer);
        Assert.Equal(0, code);
    }

    [Fact]
    public void MinProgress_Shortfall_Returns_2()
    {
        var writer = new StringWriter();
        var code = HeadlessRunner.Run(Settings(minProgress: 80), Ok(Row("a", SpecStatus.InProgress, 2, 5)), writer);
        Assert.Equal(2, code);
    }

    [Fact]
    public void Json_Format_Writes_Json_To_Writer()
    {
        var writer = new StringWriter();
        HeadlessRunner.Run(Settings("json"), Ok(Row("a", SpecStatus.Planning, 0, 1)), writer);
        var text = writer.ToString();
        Assert.Contains("\"tool\": \"spec-watcher\"", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Md_Format_Writes_Markdown_To_Writer()
    {
        var writer = new StringWriter();
        HeadlessRunner.Run(Settings("md"), Ok(Row("a", SpecStatus.Planning, 0, 1)), writer);
        Assert.Contains("| Name | Status | Progress | Folder |", writer.ToString());
    }

    [Fact]
    public void Table_Format_Reuses_Static_Table()
    {
        var writer = new StringWriter();
        HeadlessRunner.Run(Settings("table"), Ok(Row("2026-07-20-x", SpecStatus.Planning, 0, 3)), writer);
        var text = writer.ToString();
        Assert.Contains("Name", text);
        Assert.Contains("Status", text);
        Assert.Contains("Progress", text);
        Assert.Contains("Folder", text);
        Assert.Contains("2026-07-20-x", text);
    }
}
