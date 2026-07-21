using System.Collections.Immutable;

namespace SpecWatcher;

/// <summary>Enumerates spec folders under a directory and parses each into a <see cref="SpecRow"/>.</summary>
public static class SpecScanner
{
    /// <summary>
    /// Scan <paramref name="specsDir"/> off the caller's thread. A folder counts as a spec when it
    /// contains a spec.md (or, leniently, a spec-lite.md / tasks.md). Newest folder first.
    /// A missing directory is reported via <see cref="ScanResult.Error"/> rather than throwing.
    /// </summary>
    public static Task<ScanResult> ScanAsync(
        string specsDir, DateTimeOffset now, DriftOptions driftOptions = default, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!Directory.Exists(specsDir))
                return new ScanResult(ImmutableArray<SpecRow>.Empty, now, $"Specs directory not found: {specsDir}");

            try
            {
                var folders = Directory.EnumerateDirectories(specsDir)
                    .Where(IsSpecFolder)
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var rows = ImmutableArray.CreateBuilder<SpecRow>(folders.Length);
                foreach (var folder in folders)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = SpecParser.Parse(folder);
                    var (state, reason) = DriftAnalyzer.Analyze(row, now, driftOptions);
                    rows.Add(row with { Drift = state, DriftReason = reason });
                }

                // Git enrichment runs off the UI thread here (inside Task.Run), after the folder parse.
                // It degrades silently to the un-enriched rows when git/the repo is unavailable.
                var (enriched, gitAvailable, branch) = GitEnricher.Enrich(rows.ToImmutable(), specsDir, ct);
                return new ScanResult(enriched, now, GitAvailable: gitAvailable, CurrentBranch: branch);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ScanResult(ImmutableArray<SpecRow>.Empty, now, ex.Message);
            }
        }, ct);

    private static bool IsSpecFolder(string dir) =>
        File.Exists(Path.Combine(dir, "spec.md")) ||
        File.Exists(Path.Combine(dir, "spec-lite.md")) ||
        File.Exists(Path.Combine(dir, "tasks.md"));
}
