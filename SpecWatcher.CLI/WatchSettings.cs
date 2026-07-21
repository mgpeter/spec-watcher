using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SpecWatcher;

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

    /// <summary>The absolute specs directory (SpecsPath may itself be absolute).</summary>
    public string ResolvedSpecsDir =>
        Path.GetFullPath(Path.IsPathRooted(SpecsPath) ? SpecsPath : Path.Combine(RepoPath, SpecsPath));

    /// <summary>Drift options derived from the CLI flags.</summary>
    public DriftOptions ToDriftOptions() => new(DriftIdleDays);

    public override ValidationResult Validate()
    {
        if (IntervalSeconds < 1)
            return ValidationResult.Error("--interval must be at least 1 second.");
        if (DriftIdleDays < 0)
            return ValidationResult.Error("--drift-idle-days must be 0 or greater.");
        if (!Directory.Exists(RepoPath))
            return ValidationResult.Error($"Repo path not found: {RepoPath}");
        return ValidationResult.Success();
    }
}
