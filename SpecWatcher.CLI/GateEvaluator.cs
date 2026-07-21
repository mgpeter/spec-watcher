namespace SpecWatcher;

/// <summary>A single reason a spec failed a CI gate.</summary>
/// <param name="Folder">The offending spec's folder slug.</param>
/// <param name="Reason"><c>status</c> (a <c>--fail-on</c> match) or <c>min-progress</c> (a shortfall).</param>
/// <param name="Detail">Human detail: the normalized status, or e.g. <c>"40% &lt; 80%"</c>.</param>
public sealed record GateViolation(string Folder, string Reason, string Detail);

/// <summary>The result of evaluating the CI gate over a scan.</summary>
/// <param name="Passed">True when no spec violated any requested gate.</param>
/// <param name="Violations">Every violation, aggregated across <c>--fail-on</c> and <c>--min-progress</c>.</param>
public sealed record GateOutcome(bool Passed, IReadOnlyList<GateViolation> Violations)
{
    /// <summary>A clean pass with no violations (also the "no gate requested" result).</summary>
    public static readonly GateOutcome Pass = new(true, Array.Empty<GateViolation>());
}

/// <summary>
/// Pure evaluation of the CI gate: <c>(ScanResult, gate options) -&gt; GateOutcome</c>. No I/O.
/// Specs with no tasks are exempt from <c>--min-progress</c> (no progress signal).
/// </summary>
public static class GateEvaluator
{
    /// <summary>Evaluate the gate using a settings object's parsed gate flags.</summary>
    public static GateOutcome Evaluate(ScanResult result, WatchSettings settings) =>
        Evaluate(result, settings.FailOnStatuses, settings.MinProgress);

    /// <summary>Evaluate the gate against explicit gate inputs.</summary>
    public static GateOutcome Evaluate(ScanResult result, IReadOnlyList<SpecStatus> failOnStatuses, int? minProgress)
    {
        // No gate requested → trivially passes.
        if (failOnStatuses.Count == 0 && minProgress is null)
            return GateOutcome.Pass;

        var violations = new List<GateViolation>();
        if (!result.Rows.IsDefaultOrEmpty)
        {
            foreach (var row in result.Rows)
            {
                if (failOnStatuses.Count > 0 && failOnStatuses.Contains(row.Status))
                    violations.Add(new GateViolation(row.Folder, "status", StatusKeyword.ToKebab(row.Status)));

                if (minProgress is { } min && row.HasTasks && row.Total > 0 && row.Progress is { } p && p * 100 < min)
                {
                    var pct = (int)Math.Round(p * 100);
                    violations.Add(new GateViolation(row.Folder, "min-progress", $"{pct}% < {min}%"));
                }
            }
        }

        return new GateOutcome(violations.Count == 0, violations);
    }
}

/// <summary>The stable kebab-case keyword for a <see cref="SpecStatus"/> (shared by the gate and JSON emitters).</summary>
internal static class StatusKeyword
{
    public static string ToKebab(SpecStatus status) => status switch
    {
        SpecStatus.Planning => "planning",
        SpecStatus.InProgress => "in-progress",
        SpecStatus.Complete => "complete",
        _ => "unknown",
    };
}
