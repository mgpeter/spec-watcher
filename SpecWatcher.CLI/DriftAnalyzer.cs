namespace SpecWatcher;

/// <summary>Options controlling drift detection. <c>IdleDays &lt;= 0</c> disables idle detection.</summary>
public readonly record struct DriftOptions(int IdleDays)
{
    public static readonly DriftOptions Default = new(0);
}

/// <summary>
/// Pure, side-effect-free classification of a spec's drift. Contradiction checks (Overstated /
/// Understated) are always on and depend only on data already parsed; idle detection is opt-in and
/// is the only branch that needs <c>now</c> + the file mtime.
/// </summary>
public static class DriftAnalyzer
{
    /// <summary>
    /// Classify <paramref name="row"/> as of <paramref name="now"/>. First match wins; a status
    /// contradiction always beats idle (it is the more actionable signal).
    /// </summary>
    public static (DriftState State, string? Reason) Analyze(SpecRow row, DateTimeOffset now, DriftOptions options)
    {
        var hasReality = row.HasTasks && row.Total > 0;

        // 1. Overstated — declared Complete but not every box is checked.
        if (hasReality && row.Status == SpecStatus.Complete && row.Done < row.Total)
            return (DriftState.Overstated, $"Marked Complete, but {row.Done}/{row.Total} tasks checked");

        // 2. Understated — the boxes are ahead of the declared status.
        if (hasReality)
        {
            if (row.Done == row.Total && row.Status is SpecStatus.Planning or SpecStatus.InProgress)
                return (DriftState.Understated, $"All {row.Total} tasks checked, still marked {StatusText(row.Status)}");
            if (row.Status == SpecStatus.Planning && row.Done > 0)
                return (DriftState.Understated, $"Marked Planning, but {row.Done}/{row.Total} tasks in progress");
        }

        // 3. Idle — only when explicitly enabled, for an In-progress spec untouched past the threshold.
        if (options.IdleDays > 0
            && row.Status == SpecStatus.InProgress
            && row.LastModifiedUtc is { } modified)
        {
            var wholeDays = (int)(now - modified).TotalDays;
            if (wholeDays >= options.IdleDays)
                return (DriftState.Idle, $"In progress, no file change in {wholeDays}d");
        }

        return (DriftState.None, null);
    }

    private static string StatusText(SpecStatus status) => status switch
    {
        SpecStatus.Planning => "Planning",
        SpecStatus.InProgress => "In progress",
        SpecStatus.Complete => "Complete",
        _ => "Unknown",
    };
}
