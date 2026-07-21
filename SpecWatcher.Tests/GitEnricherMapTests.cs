using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// Pure tests for <see cref="GitEnricher"/>'s parse + mapping, driven by canned
/// <c>%x1e/%x1f</c>-delimited <c>git log --name-only</c> strings — no real repo required.
/// </summary>
public class GitEnricherMapTests
{
    private const char RS = '';   // record separator (%x1e)
    private const char US = '';   // unit separator (%x1f)

    private static string Record(string sha, string shortSha, string isoDate, string author, string subject, params string[] paths)
    {
        var header = $"{RS}{sha}{US}{shortSha}{US}{isoDate}{US}{author}{US}{subject}";
        return header + "\n" + string.Join("\n", paths) + "\n\n";
    }

    private static SpecRow Row(string folder) =>
        new("Name " + folder, "", SpecStatus.Planning, "Planning", null, 0, 0, false, folder, @"C:\repo\docs\specs\" + folder);

    [Fact]
    public void ParseBuckets_BucketsByFirstPathSegment_NewestFirst()
    {
        var log =
            Record("aaa111aaa111", "aaa111a", "2026-07-20T10:00:00+00:00", "Ada", "newer on alpha",
                "docs/specs/2026-07-19-alpha/spec.md") +
            Record("bbb222bbb222", "bbb222b", "2026-07-19T10:00:00+00:00", "Bo", "on beta",
                "docs/specs/2026-07-18-beta/tasks.md") +
            Record("ccc333ccc333", "ccc333c", "2026-07-18T10:00:00+00:00", "Cy", "older on alpha",
                "docs/specs/2026-07-19-alpha/tasks.md");

        var buckets = GitEnricher.ParseBuckets(log, "docs/specs");

        Assert.Equal(2, buckets.Count);
        var alpha = buckets["2026-07-19-alpha"];
        Assert.Equal(2, alpha.Length);
        Assert.Equal("aaa111a", alpha[0].ShortSha);   // newest first
        Assert.Equal("ccc333c", alpha[1].ShortSha);
        Assert.Single(buckets["2026-07-18-beta"]);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero), alpha[0].When);
        Assert.Equal("Ada", alpha[0].Author);
        Assert.Equal("newer on alpha", alpha[0].Subject);
    }

    [Fact]
    public void ParseBuckets_CountsCommitOncePerSpec_EvenWhenItTouchesManyFiles()
    {
        var log = Record("d1", "d1short", "2026-07-20T10:00:00+00:00", "Ada", "big",
            "docs/specs/2026-07-19-alpha/spec.md",
            "docs/specs/2026-07-19-alpha/tasks.md",
            "docs/specs/2026-07-19-alpha/sub-specs/technical-spec.md");

        var buckets = GitEnricher.ParseBuckets(log, "docs/specs");

        Assert.Single(buckets["2026-07-19-alpha"]);
    }

    [Fact]
    public void ParseBuckets_SkipsPathsOutsideSpecsDir()
    {
        var log = Record("e1", "e1short", "2026-07-20T10:00:00+00:00", "Ada", "mixed",
            "src/Program.cs",
            "docs/specs/2026-07-19-alpha/spec.md",
            "README.md");

        var buckets = GitEnricher.ParseBuckets(log, "docs/specs");

        Assert.Single(buckets);
        Assert.True(buckets.ContainsKey("2026-07-19-alpha"));
    }

    [Fact]
    public void ParseBuckets_EmptyLog_YieldsNoBuckets()
    {
        Assert.Empty(GitEnricher.ParseBuckets("", "docs/specs"));
    }

    [Fact]
    public void Map_AttachesLastCommitAndRecentCommits()
    {
        var log =
            Record("aaa111aaa111", "aaa111a", "2026-07-20T10:00:00+00:00", "Ada", "newer on alpha",
                "docs/specs/2026-07-19-alpha/spec.md") +
            Record("ccc333ccc333", "ccc333c", "2026-07-18T10:00:00+00:00", "Cy", "older on alpha",
                "docs/specs/2026-07-19-alpha/tasks.md");
        var rows = new[] { Row("2026-07-19-alpha"), Row("2026-07-18-beta") };

        var enriched = GitEnricher.Map(log, branch: "main", "docs/specs", rows);

        var alpha = enriched.Single(r => r.Folder == "2026-07-19-alpha");
        Assert.NotNull(alpha.LastCommit);
        Assert.Equal("aaa111a", alpha.LastCommit!.ShortSha);
        Assert.Equal(2, alpha.RecentCommits.Length);

        var beta = enriched.Single(r => r.Folder == "2026-07-18-beta");
        Assert.Null(beta.LastCommit);
        Assert.True(beta.RecentCommits.IsDefaultOrEmpty);
    }

    [Fact]
    public void Map_BranchSlugMatch_MarksCurrent()
    {
        var log = Record("a", "aShort", "2026-07-20T10:00:00+00:00", "Ada", "x",
            "docs/specs/2026-07-19-starter-check-selection/spec.md");
        var rows = new[]
        {
            Row("2026-07-19-starter-check-selection"),
            Row("2026-07-18-other-thing"),
        };

        var enriched = GitEnricher.Map(log, branch: "feat/starter-check-selection", "docs/specs", rows);

        Assert.True(enriched.Single(r => r.Folder == "2026-07-19-starter-check-selection").IsCurrent);
        Assert.False(enriched.Single(r => r.Folder == "2026-07-18-other-thing").IsCurrent);
    }

    [Fact]
    public void Map_DetachedOrNoSlug_FallsBackToNewestTouchedSpec()
    {
        var log =
            Record("aaa", "aaaShrt", "2026-07-20T10:00:00+00:00", "Ada", "newest touches alpha",
                "docs/specs/2026-07-19-alpha/spec.md") +
            Record("bbb", "bbbShrt", "2026-07-10T10:00:00+00:00", "Bo", "older touches beta",
                "docs/specs/2026-07-18-beta/spec.md");
        var rows = new[] { Row("2026-07-19-alpha"), Row("2026-07-18-beta") };

        // Branch encodes no spec slug → fallback to the spec of the newest commit.
        var enriched = GitEnricher.Map(log, branch: "main", "docs/specs", rows);

        Assert.True(enriched.Single(r => r.Folder == "2026-07-19-alpha").IsCurrent);
        Assert.Equal(1, enriched.Count(r => r.IsCurrent));
    }

    [Fact]
    public void Map_NullBranch_NoHistory_MarksNothingCurrent()
    {
        var rows = new[] { Row("2026-07-19-alpha") };
        var enriched = GitEnricher.Map("", branch: null, "docs/specs", rows);
        Assert.DoesNotContain(enriched, r => r.IsCurrent);
    }

    [Fact]
    public void MatchBranchFolder_PrefersLongestMatch()
    {
        var folders = new[] { "2026-07-19-check", "2026-07-19-starter-check-selection" };
        var match = GitEnricher.MatchBranchFolder("feat/starter-check-selection", folders);
        Assert.Equal("2026-07-19-starter-check-selection", match);
    }

    [Fact]
    public void MatchBranchFolder_MatchesFullDatedSlugToo()
    {
        var folders = new[] { "2026-07-19-alpha" };
        Assert.Equal("2026-07-19-alpha", GitEnricher.MatchBranchFolder("2026-07-19-alpha", folders));
    }

    [Fact]
    public void MatchBranchFolder_NoMatch_ReturnsNull()
    {
        var folders = new[] { "2026-07-19-alpha", "2026-07-18-beta" };
        Assert.Null(GitEnricher.MatchBranchFolder("main", folders));
        Assert.Null(GitEnricher.MatchBranchFolder(null, folders));
    }
}
