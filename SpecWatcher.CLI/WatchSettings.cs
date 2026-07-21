using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SpecWatcher;

/// <summary>The board output format for the headless (one-shot) path.</summary>
public enum OutputFormat
{
    /// <summary>The existing Spectre static table (default).</summary>
    Table,

    /// <summary>The stable, machine-readable JSON schema.</summary>
    Json,

    /// <summary>A GitHub-flavored Markdown table.</summary>
    Markdown,
}

public sealed class WatchSettings : CommandSettings
{
    [CommandArgument(0, "[REPO_PATH]")]
    [Description("Repository root to watch. Defaults to the current directory.")]
    public string RepoPath { get; init; } = Directory.GetCurrentDirectory();

    [CommandOption("-s|--specs-path <PATH>")]
    [Description("Specs folder, relative to the repo (or absolute). Default: docs/specs")]
    public string SpecsPath { get; init; } = Path.Combine("docs", "specs");

    [CommandOption("-i|--interval <SECONDS>")]
    [Description("Auto-rescan interval in seconds. Default: 60")]
    [DefaultValue(60)]
    public int IntervalSeconds { get; init; } = 60;

    [CommandOption("--drift-idle-days <DAYS>")]
    [Description("Flag In-progress specs untouched for this many days as idle. 0 = off. Default: 0")]
    [DefaultValue(0)]
    public int DriftIdleDays { get; init; }

    [CommandOption("--no-flags")]
    [Description("Hide the drift/idle attention layer (glyphs, count, hint, jump).")]
    [DefaultValue(false)]
    public bool NoFlags { get; init; }

    [CommandOption("--once")]
    [Description("Scan once, emit the board to stdout, and exit. Never enters the TUI.")]
    [DefaultValue(false)]
    public bool Once { get; init; }

    [CommandOption("-f|--format <FORMAT>")]
    [Description("Output format: table (default), json, or md. json/md imply --once.")]
    [DefaultValue("table")]
    public string Format { get; init; } = "table";

    [CommandOption("--fail-on <STATUSES>")]
    [Description("Comma list of status keywords; any matching spec fails the gate (exit 2).")]
    public string? FailOn { get; init; }

    [CommandOption("--min-progress <N>")]
    [Description("0–100; any spec with tasks below N% complete fails the gate (exit 2).")]
    public int? MinProgress { get; init; }

    /// <summary>The absolute specs directory (SpecsPath may itself be absolute).</summary>
    public string ResolvedSpecsDir =>
        Path.GetFullPath(Path.IsPathRooted(SpecsPath) ? SpecsPath : Path.Combine(RepoPath, SpecsPath));

    /// <summary>Drift options derived from the CLI flags.</summary>
    public DriftOptions ToDriftOptions() => new(DriftIdleDays);

    /// <summary>The parsed <see cref="OutputFormat"/> (defaults to <see cref="OutputFormat.Table"/>).</summary>
    public OutputFormat FormatKind => ParseFormat(Format) ?? OutputFormat.Table;

    /// <summary>The normalized statuses parsed from <c>--fail-on</c> (empty when unset).</summary>
    public IReadOnlyList<SpecStatus> FailOnStatuses => ParseFailOn(FailOn);

    /// <summary>True when at least one gate flag (<c>--fail-on</c> / <c>--min-progress</c>) is set.</summary>
    public bool HasGate => FailOnStatuses.Count > 0 || MinProgress is not null;

    /// <summary>
    /// Headless when the user asked for it OR a non-table format / gate flag makes the TUI
    /// meaningless. The caller still ORs in the existing non-interactive capability check.
    /// </summary>
    public bool WantsHeadless => Once || FormatKind != OutputFormat.Table || HasGate;

    public override ValidationResult Validate()
    {
        if (IntervalSeconds < 1)
            return ValidationResult.Error("--interval must be at least 1 second.");
        if (DriftIdleDays < 0)
            return ValidationResult.Error("--drift-idle-days must be 0 or greater.");
        if (!Directory.Exists(RepoPath))
            return ValidationResult.Error($"Repo path not found: {RepoPath}");
        if (ParseFormat(Format) is null)
            return ValidationResult.Error("--format must be table, json, or md.");
        if (MinProgress is { } mp && mp is < 0 or > 100)
            return ValidationResult.Error("--min-progress must be between 0 and 100.");
        if (!string.IsNullOrWhiteSpace(FailOn))
        {
            foreach (var token in FailOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!TryMapStatus(token, out _))
                    return ValidationResult.Error($"--fail-on: unknown status '{token}'");
        }
        return ValidationResult.Success();
    }

    /// <summary>Parse a format keyword (case-insensitive); null when unrecognized.</summary>
    internal static OutputFormat? ParseFormat(string? format) =>
        (format ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "table" => OutputFormat.Table,
            "json" => OutputFormat.Json,
            "md" => OutputFormat.Markdown,
            _ => null,
        };

    /// <summary>
    /// Map a single <c>--fail-on</c> token to a <see cref="SpecStatus"/> (case-insensitive,
    /// hyphen/underscore/space tolerant), reusing <see cref="SpecParser.NormaliseStatus"/>'s vocabulary.
    /// </summary>
    internal static bool TryMapStatus(string token, out SpecStatus status)
    {
        var t = token.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        switch (t)
        {
            case "planning": status = SpecStatus.Planning; return true;
            case "in-progress" or "wip": status = SpecStatus.InProgress; return true;
            case "complete" or "done": status = SpecStatus.Complete; return true;
            case "unknown": status = SpecStatus.Unknown; return true;
            default: status = SpecStatus.Unknown; return false;
        }
    }

    private static IReadOnlyList<SpecStatus> ParseFailOn(string? failOn)
    {
        if (string.IsNullOrWhiteSpace(failOn)) return Array.Empty<SpecStatus>();
        var list = new List<SpecStatus>();
        foreach (var token in failOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TryMapStatus(token, out var s) && !list.Contains(s))
                list.Add(s);
        return list;
    }
}
