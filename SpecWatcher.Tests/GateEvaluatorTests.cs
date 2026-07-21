using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

public class GateEvaluatorTests
{
    private static SpecRow Row(string folder, SpecStatus status, int done, int total, bool hasTasks = true) =>
        new("Name", "desc", status, status.ToString(), null, done, total, hasTasks, folder, "/x/" + folder);

    private static ScanResult Result(params SpecRow[] rows) =>
        new(rows.ToImmutableArray(), DateTimeOffset.UtcNow);

    [Fact]
    public void No_Gate_Flags_Passes()
    {
        var outcome = GateEvaluator.Evaluate(Result(Row("a", SpecStatus.Planning, 0, 3)), Array.Empty<SpecStatus>(), null);
        Assert.True(outcome.Passed);
        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void FailOn_Matches_By_Normalized_Status()
    {
        var result = Result(
            Row("a", SpecStatus.Planning, 0, 3),
            Row("b", SpecStatus.Complete, 3, 3));

        var outcome = GateEvaluator.Evaluate(result, new[] { SpecStatus.Planning }, null);

        Assert.False(outcome.Passed);
        var v = Assert.Single(outcome.Violations);
        Assert.Equal("a", v.Folder);
        Assert.Equal("status", v.Reason);
        Assert.Equal("planning", v.Detail);
    }

    [Fact]
    public void FailOn_InProgress_Detail_Is_Kebab()
    {
        var outcome = GateEvaluator.Evaluate(Result(Row("a", SpecStatus.InProgress, 1, 3)), new[] { SpecStatus.InProgress }, null);
        Assert.Equal("in-progress", Assert.Single(outcome.Violations).Detail);
    }

    [Fact]
    public void MinProgress_Violates_Only_Below_Bar()
    {
        var result = Result(
            Row("low", SpecStatus.InProgress, 2, 5),    // 40%
            Row("high", SpecStatus.InProgress, 9, 10)); // 90%

        var outcome = GateEvaluator.Evaluate(result, Array.Empty<SpecStatus>(), 80);

        var v = Assert.Single(outcome.Violations);
        Assert.Equal("low", v.Folder);
        Assert.Equal("min-progress", v.Reason);
        Assert.Equal("40% < 80%", v.Detail);
    }

    [Fact]
    public void MinProgress_At_Bar_Passes()
    {
        var outcome = GateEvaluator.Evaluate(Result(Row("a", SpecStatus.InProgress, 8, 10)), Array.Empty<SpecStatus>(), 80);
        Assert.True(outcome.Passed);
    }

    [Fact]
    public void MinProgress_Exempts_Specs_Without_Tasks()
    {
        // no tasks → no progress signal → exempt from --min-progress
        var outcome = GateEvaluator.Evaluate(Result(Row("a", SpecStatus.Planning, 0, 0, hasTasks: false)), Array.Empty<SpecStatus>(), 80);
        Assert.True(outcome.Passed);
        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void Combined_Gates_Aggregate_Violations()
    {
        var result = Result(
            Row("planning-low", SpecStatus.Planning, 1, 10),   // fails both status + min-progress
            Row("done", SpecStatus.Complete, 3, 3));

        var outcome = GateEvaluator.Evaluate(result, new[] { SpecStatus.Planning }, 80);

        Assert.False(outcome.Passed);
        Assert.Equal(2, outcome.Violations.Count);
        Assert.Contains(outcome.Violations, v => v.Reason == "status");
        Assert.Contains(outcome.Violations, v => v.Reason == "min-progress");
    }

    [Fact]
    public void All_Clean_Passes()
    {
        var result = Result(
            Row("a", SpecStatus.Complete, 3, 3),
            Row("b", SpecStatus.Complete, 5, 5));

        var outcome = GateEvaluator.Evaluate(result, new[] { SpecStatus.Planning, SpecStatus.InProgress }, 80);

        Assert.True(outcome.Passed);
    }

    [Fact]
    public void Evaluate_From_Settings_Uses_Parsed_Gate()
    {
        var settings = new WatchSettings
        {
            RepoPath = Directory.GetCurrentDirectory(),
            FailOn = "planning",
        };
        var outcome = GateEvaluator.Evaluate(Result(Row("a", SpecStatus.Planning, 0, 1)), settings);
        Assert.False(outcome.Passed);
    }
}
