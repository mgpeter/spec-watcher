using System.Collections.Immutable;
using System.Text.Json;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class BoardJsonTests
{
    private static WatchSettings Settings(string? failOn = null, int? minProgress = null) => new()
    {
        RepoPath = Directory.GetCurrentDirectory(),
        SpecsPath = Path.Combine("docs", "specs"),
        FailOn = failOn,
        MinProgress = minProgress,
    };

    private static SpecRow Row(
        string folder, SpecStatus status, string statusRaw, int done, int total,
        bool hasTasks = true, DateOnly? created = null, string name = "Some Spec") =>
        new(name, "A description with & ampersand", status, statusRaw, created, done, total, hasTasks, folder, "/repo/docs/specs/" + folder);

    private static ScanResult Result(params SpecRow[] rows) =>
        new(rows.ToImmutableArray(), new DateTimeOffset(2026, 7, 20, 14, 3, 5, TimeSpan.Zero));

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Emits_Stable_Top_Level_Fields()
    {
        var json = BoardJson.Serialize(Settings(), Result(), null);
        var root = Root(json);

        Assert.Equal("spec-watcher", root.GetProperty("tool").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Equal("2026-07-20T14:03:05Z", root.GetProperty("generatedAt").GetString());
        Assert.True(root.TryGetProperty("specsDir", out _));
        Assert.Equal(0, root.GetProperty("specCount").GetInt32());
        Assert.True(root.TryGetProperty("summary", out _));
        Assert.True(root.TryGetProperty("specs", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public void Trailing_Newline_And_Indented()
    {
        var json = BoardJson.Serialize(Settings(), Result(), null);
        Assert.EndsWith("\n", json);
        Assert.Contains("\n  \"tool\"", json); // 2-space indented
    }

    [Fact]
    public void SpecsDir_Uses_Forward_Slashes()
    {
        var json = BoardJson.Serialize(Settings(), Result(), null);
        var specsDir = Root(json).GetProperty("specsDir").GetString();
        Assert.DoesNotContain("\\", specsDir);
    }

    [Fact]
    public void Spec_Fields_Are_CamelCase_And_Kebab_Status()
    {
        var row = Row("2026-07-20-x", SpecStatus.InProgress, "In progress", 3, 10, created: new DateOnly(2026, 7, 20));
        var json = BoardJson.Serialize(Settings(), Result(row), null);
        var spec = Root(json).GetProperty("specs")[0];

        Assert.Equal("Some Spec", spec.GetProperty("name").GetString());
        Assert.Equal("2026-07-20-x", spec.GetProperty("folder").GetString());
        Assert.Equal("in-progress", spec.GetProperty("status").GetString());
        Assert.Equal("In progress", spec.GetProperty("statusRaw").GetString());
        Assert.Equal("2026-07-20", spec.GetProperty("created").GetString());
        Assert.Equal(3, spec.GetProperty("done").GetInt32());
        Assert.Equal(10, spec.GetProperty("total").GetInt32());
        Assert.True(spec.GetProperty("hasTasks").GetBoolean());
        Assert.Equal(0.3, spec.GetProperty("progress").GetDouble(), 5);
        Assert.DoesNotContain("\\", spec.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData(SpecStatus.Planning, "planning")]
    [InlineData(SpecStatus.InProgress, "in-progress")]
    [InlineData(SpecStatus.Complete, "complete")]
    [InlineData(SpecStatus.Unknown, "unknown")]
    public void Status_Serializes_As_Kebab(SpecStatus status, string expected)
    {
        var json = BoardJson.Serialize(Settings(), Result(Row("f", status, "raw", 0, 1)), null);
        Assert.Equal(expected, Root(json).GetProperty("specs")[0].GetProperty("status").GetString());
    }

    [Fact]
    public void Progress_Null_When_No_Tasks()
    {
        var row = Row("f", SpecStatus.Planning, "Planning", 0, 0, hasTasks: false);
        var json = BoardJson.Serialize(Settings(), Result(row), null);
        Assert.Equal(JsonValueKind.Null, Root(json).GetProperty("specs")[0].GetProperty("progress").ValueKind);
    }

    [Fact]
    public void Created_Null_When_Absent()
    {
        var row = Row("f", SpecStatus.Planning, "Planning", 0, 1, created: null);
        var json = BoardJson.Serialize(Settings(), Result(row), null);
        Assert.Equal(JsonValueKind.Null, Root(json).GetProperty("specs")[0].GetProperty("created").ValueKind);
    }

    [Fact]
    public void Summary_Counts_By_Status()
    {
        var result = Result(
            Row("a", SpecStatus.Planning, "Planning", 0, 1),
            Row("b", SpecStatus.Planning, "Planning", 0, 1),
            Row("c", SpecStatus.Complete, "Complete", 1, 1),
            Row("d", SpecStatus.Unknown, "", 0, 0, hasTasks: false));

        var json = BoardJson.Serialize(Settings(), result, null);
        var summary = Root(json).GetProperty("summary");

        Assert.Equal(2, summary.GetProperty("planning").GetInt32());
        Assert.Equal(0, summary.GetProperty("inProgress").GetInt32());
        Assert.Equal(1, summary.GetProperty("complete").GetInt32());
        Assert.Equal(1, summary.GetProperty("unknown").GetInt32());
        Assert.Equal(4, Root(json).GetProperty("specCount").GetInt32());
    }

    [Fact]
    public void Gate_Null_When_No_Gate()
    {
        var json = BoardJson.Serialize(Settings(), Result(Row("a", SpecStatus.Planning, "Planning", 0, 1)), null);
        Assert.Equal(JsonValueKind.Null, Root(json).GetProperty("gate").ValueKind);
    }

    [Fact]
    public void Gate_Block_Populated_When_Present()
    {
        var settings = Settings(failOn: "planning,in-progress", minProgress: 80);
        var result = Result(Row("a", SpecStatus.Planning, "Planning", 0, 1));
        var outcome = GateEvaluator.Evaluate(result, settings);

        var json = BoardJson.Serialize(settings, result, outcome);
        var gate = Root(json).GetProperty("gate");

        Assert.Equal(new[] { "planning", "in-progress" }, gate.GetProperty("failOn").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(80, gate.GetProperty("minProgress").GetInt32());
        Assert.False(gate.GetProperty("passed").GetBoolean());
        var violation = gate.GetProperty("violations")[0];
        Assert.Equal("a", violation.GetProperty("folder").GetString());
        Assert.Equal("status", violation.GetProperty("reason").GetString());
        Assert.Equal("planning", violation.GetProperty("detail").GetString());
    }

    [Fact]
    public void Error_Populated_On_Scan_Error()
    {
        var errored = new ScanResult(ImmutableArray<SpecRow>.Empty, DateTimeOffset.UtcNow, "Specs directory not found: /nope");
        var json = BoardJson.Serialize(Settings(), errored, null);
        var root = Root(json);

        Assert.Equal("Specs directory not found: /nope", root.GetProperty("error").GetString());
        Assert.Empty(root.GetProperty("specs").EnumerateArray());
        Assert.Equal(0, root.GetProperty("specCount").GetInt32());
    }

    [Fact]
    public void Ampersand_Not_Escaped()
    {
        var json = BoardJson.Serialize(Settings(), Result(Row("f", SpecStatus.Planning, "Planning", 0, 1, name: "A & B")), null);
        Assert.Contains("A & B", json);
        Assert.DoesNotContain("\\u0026", json);
    }
}
