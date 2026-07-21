using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SpecWatcher;

/// <summary>The repo facts resolved by three cheap <c>rev-parse</c> calls at the start of a scan.</summary>
/// <param name="Root">Absolute worktree root (<c>rev-parse --show-toplevel</c>).</param>
/// <param name="HeadSha">Full HEAD sha (drives the log cache key).</param>
/// <param name="Branch">Abbreviated branch name, or null when detached.</param>
internal readonly record struct GitContext(string Root, string HeadSha, string? Branch);

/// <summary>
/// HEAD-sha cache for the one expensive <c>git log</c>. The buckets are keyed by
/// (root, headSha, branch); the loader runs only when that key changes. A loader that fails
/// (timeout / error) returns null and the previously cached buckets are kept (stale-but-useful)
/// rather than discarded. Instances are cheap and self-contained, which keeps the cache unit-testable.
/// </summary>
internal sealed class GitLogCache
{
    private string? _root;
    private string? _headSha;
    private string? _branch;
    private ImmutableDictionary<string, ImmutableArray<GitCommit>>? _buckets;

    /// <summary>
    /// Return the commit buckets for the given key, invoking <paramref name="load"/> only on a key
    /// change. On a cache hit <paramref name="loaded"/> is false and no subprocess is spawned. When
    /// the loader returns null (the expensive call failed), the last good buckets are returned
    /// unchanged so a transient failure never blanks the board.
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<GitCommit>>? GetOrLoad(
        string root, string headSha, string? branch,
        Func<ImmutableDictionary<string, ImmutableArray<GitCommit>>?> load, out bool loaded)
    {
        if (_buckets is not null && root == _root && headSha == _headSha && branch == _branch)
        {
            loaded = false;
            return _buckets;
        }

        var fresh = load();
        loaded = fresh is not null;
        if (fresh is not null)
        {
            _root = root;
            _headSha = headSha;
            _branch = branch;
            _buckets = fresh;
        }
        // On failure keep the stale buckets and the old key, so the next scan retries the load.
        return _buckets;
    }
}

/// <summary>
/// Turns the repo's local git history into per-spec commit context. Detection and the <c>git log</c>
/// shell-out happen off the UI thread (inside <see cref="SpecScanner.ScanAsync"/>); the mapping from
/// raw log output to enriched rows is a pure function (<see cref="Map"/>) that needs no real repo.
/// </summary>
internal static partial class GitEnricher
{
    private const char RecordSep = '';   // %x1e — delimits commits
    private const char UnitSep = '';     // %x1f — delimits header fields
    private const int MaxCommits = 300;
    private const int RecentPerSpec = 10;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private static readonly GitLogCache Cache = new();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-")]
    private static partial Regex DatePrefix();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    /// <summary>
    /// Enrich <paramref name="rows"/> with git branch/commit context for the repo containing
    /// <paramref name="specsDir"/>. Degrades silently: when there is no repo, no <c>git</c> binary,
    /// or the <c>log</c> fails with nothing cached, the rows are returned unchanged and
    /// <c>GitAvailable</c> is false so rendering matches the no-git layout exactly.
    /// </summary>
    public static (ImmutableArray<SpecRow> Rows, bool GitAvailable, string? Branch) Enrich(
        IReadOnlyList<SpecRow> rows, string specsDir, CancellationToken ct)
    {
        var immutableRows = rows as ImmutableArray<SpecRow>? ?? rows.ToImmutableArray();

        if (DetectContext(specsDir, ct) is not { } ctx)
            return (immutableRows, false, null);

        var prefix = RelativePrefix(ctx.Root, specsDir);

        var buckets = Cache.GetOrLoad(ctx.Root, ctx.HeadSha, ctx.Branch, () =>
        {
            var stdout = RunLog(ctx.Root, prefix, ct);
            return stdout is null ? null : ParseBuckets(stdout, prefix);
        }, out _);

        if (buckets is null)
            return (immutableRows, false, null);   // repo present but no usable log and nothing cached

        var enriched = Apply(immutableRows, buckets, ctx.Branch);
        return (enriched, true, ctx.Branch);
    }

    // ---- repo detection (three cheap rev-parse calls) --------------------

    /// <summary>Resolve the worktree containing <paramref name="specsDir"/>, or null when unavailable.</summary>
    internal static GitContext? DetectContext(string specsDir, CancellationToken ct)
    {
        var top = GitRunner.Run(new[] { "-C", specsDir, "rev-parse", "--show-toplevel" }, CallTimeout, ct);
        if (!top.Ok) return null;
        var root = top.StdOut.Trim();
        if (root.Length == 0) return null;

        var head = GitRunner.Run(new[] { "-C", root, "rev-parse", "HEAD" }, CallTimeout, ct);
        if (!head.Ok) return null;
        var sha = head.StdOut.Trim();
        if (sha.Length == 0) return null;

        var branchResult = GitRunner.Run(new[] { "-C", root, "rev-parse", "--abbrev-ref", "HEAD" }, CallTimeout, ct);
        var branch = branchResult.Ok ? branchResult.StdOut.Trim() : null;
        if (string.IsNullOrEmpty(branch) || branch == "HEAD") branch = null;   // detached HEAD

        return new GitContext(root, sha, branch);
    }

    /// <summary>Run the one bounded <c>git log</c> over the specs dir; null on failure/timeout.</summary>
    private static string? RunLog(string root, string prefix, CancellationToken ct)
    {
        var pathspec = prefix.Length == 0 ? "." : prefix;
        var args = new[]
        {
            "-C", root, "log", "--no-color",
            "-n", MaxCommits.ToString(CultureInfo.InvariantCulture),
            "--since=180.days", "--name-only",
            "--pretty=format:%x1e%H%x1f%h%x1f%cI%x1f%an%x1f%s",
            "--", pathspec,
        };
        var result = GitRunner.Run(args, CallTimeout, ct);
        return result.Ok ? result.StdOut : null;
    }

    /// <summary>The specs dir expressed relative to the worktree root, forward-slashed (empty when equal).</summary>
    internal static string RelativePrefix(string root, string specsDir)
    {
        var rel = Path.GetRelativePath(root, specsDir).Replace('\\', '/').Trim('/');
        return rel is "." ? string.Empty : rel;
    }

    // ---- pure mapping ----------------------------------------------------

    /// <summary>
    /// Pure end-to-end map used by tests: parse raw <c>git log</c> output, bucket commits by spec
    /// folder, and stamp <c>IsCurrent</c>/<c>LastCommit</c>/<c>RecentCommits</c> onto the rows.
    /// </summary>
    /// <param name="logStdout">Raw <c>%x1e/%x1f</c>-delimited, <c>--name-only</c> log output.</param>
    /// <param name="branch">Current branch (null when detached) for the <c>IsCurrent</c> decision.</param>
    /// <param name="specsDirPrefix">Specs dir relative to the repo root (e.g. <c>docs/specs</c>).</param>
    /// <param name="rows">The freshly-scanned rows to enrich (bucketed by <see cref="SpecRow.Folder"/>).</param>
    internal static ImmutableArray<SpecRow> Map(
        string logStdout, string? branch, string specsDirPrefix, IReadOnlyList<SpecRow> rows) =>
        Apply(rows, ParseBuckets(logStdout, specsDirPrefix), branch);

    /// <summary>
    /// Parse raw log output into folder → newest-first commits. Paths outside the specs dir are
    /// skipped; a commit touching several files in one spec is counted once for that spec.
    /// </summary>
    internal static ImmutableDictionary<string, ImmutableArray<GitCommit>> ParseBuckets(
        string logStdout, string specsDirPrefix)
    {
        var builders = new Dictionary<string, ImmutableArray<GitCommit>.Builder>(StringComparer.Ordinal);
        var prefix = specsDirPrefix.Replace('\\', '/').Trim('/');

        foreach (var record in logStdout.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n');
            if (lines.Length == 0) continue;

            var fields = lines[0].TrimEnd('\r').Split(UnitSep);
            if (fields.Length < 5) continue;
            if (!TryParseWhen(fields[2], out var when)) continue;

            var commit = new GitCommit(fields[0], fields[1], when, fields[3], fields[4]);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < lines.Length; i++)
            {
                var path = lines[i].TrimEnd('\r').Trim();
                if (path.Length == 0) continue;
                if (SpecFolderOf(path, prefix) is not { } folder) continue;
                if (!seen.Add(folder)) continue;

                if (!builders.TryGetValue(folder, out var b))
                    builders[folder] = b = ImmutableArray.CreateBuilder<GitCommit>();
                b.Add(commit);   // git emits newest-first, so append preserves order
            }
        }

        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<GitCommit>>(StringComparer.Ordinal);
        foreach (var (folder, b) in builders)
            result[folder] = b.ToImmutable();
        return result.ToImmutable();
    }

    /// <summary>First path segment under the specs prefix (the spec folder), or null when outside it.</summary>
    private static string? SpecFolderOf(string path, string prefix)
    {
        var p = path.Replace('\\', '/');
        string rel;
        if (prefix.Length == 0)
        {
            rel = p;
        }
        else
        {
            if (!p.StartsWith(prefix + "/", StringComparison.Ordinal)) return null;
            rel = p[(prefix.Length + 1)..];
        }
        var slash = rel.IndexOf('/');
        var segment = slash < 0 ? rel : rel[..slash];
        return segment.Length == 0 ? null : segment;
    }

    /// <summary>Attach buckets to rows and resolve the single <c>IsCurrent</c> spec.</summary>
    internal static ImmutableArray<SpecRow> Apply(
        IReadOnlyList<SpecRow> rows,
        IReadOnlyDictionary<string, ImmutableArray<GitCommit>> buckets,
        string? branch)
    {
        var folders = rows.Select(r => r.Folder).ToArray();
        var currentFolder = MatchBranchFolder(branch, folders) ?? NewestTouchedFolder(rows, buckets);

        var builder = ImmutableArray.CreateBuilder<SpecRow>(rows.Count);
        foreach (var row in rows)
        {
            var bucket = buckets.TryGetValue(row.Folder, out var b) ? b : ImmutableArray<GitCommit>.Empty;
            var last = bucket.IsDefaultOrEmpty ? null : bucket[0];
            var recent = bucket.IsDefaultOrEmpty
                ? ImmutableArray<GitCommit>.Empty
                : (bucket.Length > RecentPerSpec ? bucket[..RecentPerSpec] : bucket);

            builder.Add(row with
            {
                IsCurrent = row.Folder == currentFolder,
                LastCommit = last,
                RecentCommits = recent,
            });
        }
        return builder.ToImmutable();
    }

    /// <summary>Fallback: the spec whose newest commit is the most recent of all (null when no history).</summary>
    private static string? NewestTouchedFolder(
        IReadOnlyList<SpecRow> rows, IReadOnlyDictionary<string, ImmutableArray<GitCommit>> buckets)
    {
        string? best = null;
        DateTimeOffset bestWhen = DateTimeOffset.MinValue;
        foreach (var row in rows)   // rows are newest-folder-first, so ties keep the newest folder
        {
            if (!buckets.TryGetValue(row.Folder, out var bucket) || bucket.IsDefaultOrEmpty) continue;
            var when = bucket[0].When;
            if (best is null || when > bestWhen)
            {
                best = row.Folder;
                bestWhen = when;
            }
        }
        return best;
    }

    /// <summary>
    /// The spec folder encoded in <paramref name="branch"/>, or null. A folder matches when its slug
    /// (or its date-stripped bare slug) is a contiguous token-subsequence of the branch; the longest
    /// match wins, ties broken toward the newest folder (callers pass folders newest-first).
    /// </summary>
    internal static string? MatchBranchFolder(string? branch, IReadOnlyList<string> folders)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var hay = Tokens(branch);
        if (hay.Length == 0) return null;

        string? best = null;
        var bestLen = 0;
        foreach (var folder in folders)
        {
            var full = Tokens(folder);
            var bare = Tokens(DatePrefix().Replace(folder, string.Empty));
            var len = 0;
            if (IsContiguousSubsequence(hay, full)) len = Math.Max(len, full.Length);
            if (IsContiguousSubsequence(hay, bare)) len = Math.Max(len, bare.Length);
            if (len > bestLen)   // strictly greater keeps the first (newest) folder on ties
            {
                bestLen = len;
                best = folder;
            }
        }
        return best;
    }

    private static string[] Tokens(string s) =>
        NonAlnum().Split(s.ToLowerInvariant()).Where(t => t.Length > 0).ToArray();

    private static bool IsContiguousSubsequence(string[] hay, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > hay.Length) return false;
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (hay[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private static bool TryParseWhen(string iso, out DateTimeOffset when) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when);
}
