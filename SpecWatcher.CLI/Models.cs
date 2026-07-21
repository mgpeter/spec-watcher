using System.Collections.Immutable;

namespace SpecWatcher;

/// <summary>The status keyword parsed from a spec's <c>&gt; Status:</c> line.</summary>
public enum SpecStatus
{
    Unknown,
    Planning,
    InProgress,
    Complete,
}

/// <summary>
/// Whether a spec's declared status contradicts its real checkbox progress, or it has gone idle.
/// </summary>
public enum DriftState
{
    /// <summary>No contradiction and (if enabled) not idle.</summary>
    None,

    /// <summary>Status claims more than the boxes show (e.g. Complete but not all checked).</summary>
    Overstated,

    /// <summary>Boxes are ahead of the status (e.g. all checked but still Planning/InProgress).</summary>
    Understated,

    /// <summary>An In-progress spec whose newest file is older than the idle threshold (opt-in).</summary>
    Idle,
}

/// <summary>
/// One parsed spec. Immutable so it can be published across threads safely.
/// </summary>
/// <param name="Name">Human title from <c>&gt; Spec:</c> (fallback: de-slugged folder name).</param>
/// <param name="Description">One-line summary from spec-lite.md / Overview.</param>
/// <param name="Status">Normalised status keyword.</param>
/// <param name="StatusRaw">The full text after <c>Status:</c> (freeform annotations preserved).</param>
/// <param name="Created">Creation date from <c>&gt; Created:</c> or the folder prefix, if any.</param>
/// <param name="Done">Completed checkbox count from tasks.md.</param>
/// <param name="Total">Total checkbox count from tasks.md (0 when no tasks.md).</param>
/// <param name="HasTasks">True when a tasks.md was found.</param>
/// <param name="Folder">The raw folder slug (e.g. 2026-07-19-starter-check-selection).</param>
/// <param name="FullPath">Absolute path to the spec folder (used by the detail view).</param>
/// <param name="LastModifiedUtc">Newest file mtime among spec.md/tasks.md/spec-lite.md (null if none readable).</param>
/// <param name="Drift">Whether the declared status contradicts checkbox reality or has gone idle.</param>
/// <param name="DriftReason">One-line human explanation of the drift, or null when <see cref="Drift"/> is None.</param>
/// <param name="IsCurrent">True when this spec matches the checked-out branch (or the last-touched fallback).</param>
/// <param name="LastCommit">Most recent commit touching this spec folder, or null when git history has none.</param>
/// <param name="RecentCommits">A small, newest-first slice of commits touching this folder (default = empty).</param>
public sealed record SpecRow(
    string Name,
    string Description,
    SpecStatus Status,
    string StatusRaw,
    DateOnly? Created,
    int Done,
    int Total,
    bool HasTasks,
    string Folder,
    string FullPath,
    DateTimeOffset? LastModifiedUtc = null,
    DriftState Drift = DriftState.None,
    string? DriftReason = null,
    bool IsCurrent = false,
    GitCommit? LastCommit = null,
    ImmutableArray<GitCommit> RecentCommits = default)
{
    /// <summary>Task completion as a fraction 0..1, or null when there are no tasks.</summary>
    public double? Progress => Total > 0 ? (double)Done / Total : null;
}

/// <summary>
/// One commit read from the repo's local history, shaped for display. Immutable so it can be
/// published across threads inside a <see cref="SpecRow"/>.
/// </summary>
/// <param name="Sha">The full 40-char commit hash.</param>
/// <param name="ShortSha">The abbreviated hash (git <c>%h</c>).</param>
/// <param name="When">Committer date (git <c>%cI</c>, strict ISO-8601).</param>
/// <param name="Author">Author name (git <c>%an</c>).</param>
/// <param name="Subject">Commit subject line (git <c>%s</c>).</param>
public sealed record GitCommit(
    string Sha, string ShortSha, DateTimeOffset When, string Author, string Subject);

/// <summary>Raw spec.md / tasks.md text loaded on demand for the detail view.</summary>
public sealed record SpecDetail(string? SpecText, string? TasksText);

/// <summary>An immutable snapshot of one scan, published to the UI thread.</summary>
/// <param name="Rows">The parsed spec rows.</param>
/// <param name="CompletedAt">When the scan finished.</param>
/// <param name="Error">A scan-level error message, or null on success.</param>
/// <param name="GitAvailable">True when a git worktree + git binary were usable this scan.</param>
/// <param name="CurrentBranch">Abbreviated current branch name, or null (detached / unavailable).</param>
public sealed record ScanResult(
    ImmutableArray<SpecRow> Rows,
    DateTimeOffset CompletedAt,
    string? Error = null,
    bool GitAvailable = false,
    string? CurrentBranch = null)
{
    public static readonly ScanResult Empty =
        new(ImmutableArray<SpecRow>.Empty, DateTimeOffset.MinValue);
}
