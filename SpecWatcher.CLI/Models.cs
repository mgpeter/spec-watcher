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
    string FullPath)
{
    /// <summary>Task completion as a fraction 0..1, or null when there are no tasks.</summary>
    public double? Progress => Total > 0 ? (double)Done / Total : null;
}

/// <summary>Raw spec.md / tasks.md text loaded on demand for the detail view.</summary>
public sealed record SpecDetail(string? SpecText, string? TasksText);

/// <summary>An immutable snapshot of one scan, published to the UI thread.</summary>
public sealed record ScanResult(
    ImmutableArray<SpecRow> Rows,
    DateTimeOffset CompletedAt,
    string? Error = null)
{
    public static readonly ScanResult Empty =
        new(ImmutableArray<SpecRow>.Empty, DateTimeOffset.MinValue);
}
