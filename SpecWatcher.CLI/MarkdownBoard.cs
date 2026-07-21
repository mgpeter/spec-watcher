using System.Collections.Immutable;
using System.Text;

namespace SpecWatcher;

/// <summary>
/// Renders the board as a GitHub-flavored Markdown table plus a summary line — plain text, safe to
/// paste into a wiki, PR description, or status email. No ANSI / Spectre markup.
/// </summary>
public static class MarkdownBoard
{
    /// <summary>Render the board to a Markdown string ending in a trailing newline.</summary>
    public static string Render(WatchSettings settings, ScanResult result, GateOutcome? gate)
    {
        var rows = result.Rows.IsDefaultOrEmpty ? ImmutableArray<SpecRow>.Empty : result.Rows;
        var sb = new StringBuilder();

        sb.Append("# spec-watcher\n\n");

        if (result.Error is { } err)
            sb.Append($"> Error: {Cell(err)}\n\n");

        var planning = rows.Count(r => r.Status == SpecStatus.Planning);
        var inProgress = rows.Count(r => r.Status == SpecStatus.InProgress);
        var complete = rows.Count(r => r.Status == SpecStatus.Complete);
        var unknown = rows.Count(r => r.Status == SpecStatus.Unknown);
        sb.Append($"**{rows.Length} spec{(rows.Length == 1 ? "" : "s")}** — ")
          .Append($"{planning} planning, {inProgress} in progress, {complete} complete, {unknown} unknown\n\n");

        sb.Append("| Name | Status | Progress | Folder |\n");
        sb.Append("| --- | --- | --- | --- |\n");
        if (rows.Length == 0)
            sb.Append("| _no specs found_ |  |  |  |\n");
        else
            foreach (var r in rows)
                sb.Append($"| {Cell(r.Name)} | {StatusLabel(r)} | {Progress(r)} | {Cell(r.Folder)} |\n");

        if (gate is not null)
        {
            sb.Append('\n');
            sb.Append($"**Gate:** {(gate.Passed ? "passed" : "failed")}\n");
            foreach (var v in gate.Violations)
                sb.Append($"- `{Cell(v.Folder)}` — {v.Reason}: {Cell(v.Detail)}\n");
        }

        return sb.ToString();
    }

    private static string Progress(SpecRow row)
    {
        if (!row.HasTasks || row.Total == 0) return "—";
        var pct = (int)Math.Round((row.Progress ?? 0) * 100);
        return $"{pct}% ({row.Done}/{row.Total})";
    }

    private static string StatusLabel(SpecRow row) => row.Status switch
    {
        SpecStatus.Planning => "Planning",
        SpecStatus.InProgress => "In progress",
        SpecStatus.Complete => "Complete",
        _ => string.IsNullOrWhiteSpace(row.StatusRaw) ? "unknown" : Cell(row.StatusRaw),
    };

    /// <summary>Escape a table cell: pipes must be backslash-escaped and newlines collapsed to spaces.</summary>
    private static string Cell(string s) =>
        s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
}
