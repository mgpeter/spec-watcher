using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// Tests the git-aware rendering seams: the relative-time formatter, the list width math when the
/// Last-commit column is shown vs. hidden, and that <c>RowPlain</c> stays a fixed-width mirror.
/// </summary>
public class WatchCommandGitLayoutTests
{
    private const int LastCommitW = 18;   // must match WatchCommand.LastCommitW

    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(59 * 60, "59m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(23 * 3600, "23h ago")]
    [InlineData(24 * 3600, "1d ago")]
    [InlineData(6 * 24 * 3600, "6d ago")]
    [InlineData(7 * 24 * 3600, "1w ago")]
    [InlineData(21 * 24 * 3600, "3w ago")]
    public void RelativeTime_FormatsBucketed(int secondsAgo, string expected)
    {
        var when = Now - TimeSpan.FromSeconds(secondsAgo);
        Assert.Equal(expected, WatchCommand.RelativeTime(when, Now));
    }

    [Fact]
    public void RelativeTime_FutureClampsToJustNow()
    {
        Assert.Equal("just now", WatchCommand.RelativeTime(Now + TimeSpan.FromMinutes(5), Now));
    }

    [Fact]
    public void NameWidth_ShowingCommit_ReservesLastCommitColumn()
    {
        const int width = 160;
        var hidden = WatchCommand.NameWidth(width, flagsVisible: true, showCommit: false);
        var shown = WatchCommand.NameWidth(width, flagsVisible: true, showCommit: true);
        Assert.Equal(hidden - (LastCommitW + 1), shown);
    }

    [Fact]
    public void NameWidth_HiddenCommit_MatchesLegacyLayout()
    {
        // With the column hidden the name width is unchanged from the pre-git formula, so a non-git
        // board is byte-identical to before.
        const int width = 140;
        var shown = WatchCommand.NameWidth(width, flagsVisible: false, showCommit: false);
        var expected = Math.Max(16, (Math.Max(60, width) - 1) - 1 - 13 - 24 - 34 - 4);
        Assert.Equal(expected, shown);
    }

    [Fact]
    public void RowPlain_IsFixedWidth_RegardlessOfCommitContent()
    {
        var nameW = WatchCommand.NameWidth(160, flagsVisible: true, showCommit: true);
        var withCommit = RowFor("2026-07-19-alpha", new GitCommit("abc", "abc1234", Now, "Ada", "hi"));
        var noCommit = RowFor("2026-07-19-alpha", null);

        var a = WatchCommand.RowPlain(withCommit, selected: false, nameW, flagsVisible: true, showCommit: true);
        var b = WatchCommand.RowPlain(noCommit, selected: false, nameW, flagsVisible: true, showCommit: true);

        Assert.Equal(a.Length, b.Length);   // fixed-width column → mirror geometry is stable
    }

    [Fact]
    public void RowPlain_MatchesHeaderWidth()
    {
        var nameW = WatchCommand.NameWidth(160, flagsVisible: true, showCommit: true);
        var header = StripGrey(WatchCommand.HeaderLine(nameW, flagsVisible: true, showCommit: true));
        var row = WatchCommand.RowPlain(RowFor("2026-07-19-alpha", null), false, nameW, true, showCommit: true);
        Assert.Equal(header.Length, row.Length);
    }

    [Fact]
    public void RowPlain_HidingCommit_IsShorterByColumn()
    {
        var nameW = WatchCommand.NameWidth(160, flagsVisible: true, showCommit: false);
        var row = RowFor("2026-07-19-alpha", new GitCommit("abc", "abc1234", Now, "Ada", "hi"));
        var withCol = WatchCommand.RowPlain(row, false, nameW, true, showCommit: true);
        var without = WatchCommand.RowPlain(row, false, nameW, true, showCommit: false);
        Assert.Equal(withCol.Length - (LastCommitW + 1), without.Length);
    }

    [Fact]
    public void RowPlain_CurrentRow_ShowsBadgeGlyph()
    {
        var nameW = WatchCommand.NameWidth(160, flagsVisible: true, showCommit: true);
        var current = RowFor("2026-07-19-alpha", null) with { IsCurrent = true };
        var plain = WatchCommand.RowPlain(current, selected: false, nameW, true, showCommit: true);
        Assert.StartsWith("◈", plain);
    }

    private static string StripGrey(string markup)
    {
        const string open = "[grey62]";
        const string close = "[/]";
        var s = markup;
        if (s.StartsWith(open)) s = s[open.Length..];
        if (s.EndsWith(close)) s = s[..^close.Length];
        return s;
    }

    private static SpecRow RowFor(string folder, GitCommit? last) =>
        new("Alpha", "", SpecStatus.Planning, "Planning", null, 0, 0, false, folder, @"C:\repo\" + folder,
            LastCommit: last,
            RecentCommits: last is null ? ImmutableArray<GitCommit>.Empty : ImmutableArray.Create(last));
}
