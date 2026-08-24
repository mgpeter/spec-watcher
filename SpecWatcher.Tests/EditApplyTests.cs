using System.Collections.Immutable;
using SpecWatcher;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// What happens after <see cref="SpecEditor"/> returns: which follow-up runs, what the footer says,
/// how the patched row is folded back into the board, and what survives a document rebuild.
/// </summary>
public class EditApplyTests
{
    private static SpecRow Row(
        string fullPath = "/specs/a", SpecStatus status = SpecStatus.Planning, int done = 0, int total = 4) =>
        new("A", "desc", status, status.ToString(), null, done, total, true, "a", fullPath);

    // ---- follow-up routing -----------------------------------------------

    [Fact]
    public void Ok_Reloads_The_Document()
    {
        Assert.Equal(EditFollowUp.Reload, WatchCommand.FollowUpFor(EditResult.Ok));
    }

    [Fact]
    public void Conflict_Triggers_A_Rescan_So_The_Board_Catches_Up()
    {
        Assert.Equal(EditFollowUp.Rescan, WatchCommand.FollowUpFor(EditResult.Conflict));
    }

    [Fact]
    public void Other_Failures_Change_Nothing()
    {
        Assert.Equal(EditFollowUp.None, WatchCommand.FollowUpFor(EditResult.Unwritable));
        Assert.Equal(EditFollowUp.None, WatchCommand.FollowUpFor(EditResult.NotFound));
        Assert.Equal(EditFollowUp.None, WatchCommand.FollowUpFor(EditResult.NoStatusLine));
    }

    // ---- toast -----------------------------------------------------------

    [Fact]
    public void Success_Toast_Uses_The_Callers_Verb()
    {
        var toast = WatchCommand.EditToastMarkup(EditResult.Ok, "ticked");

        Assert.Contains("ticked", toast);
        Assert.Contains("green", toast);
    }

    [Fact]
    public void Failure_Toasts_Explain_Themselves_And_Are_Not_Green()
    {
        Assert.Contains("changed on disk", WatchCommand.EditToastMarkup(EditResult.Conflict, "ticked"));
        Assert.Contains("read-only", WatchCommand.EditToastMarkup(EditResult.Unwritable, "ticked"));
        Assert.Contains("Status:", WatchCommand.EditToastMarkup(EditResult.NoStatusLine, "ticked"));

        foreach (var failure in new[] { EditResult.Conflict, EditResult.Unwritable, EditResult.NotFound })
            Assert.DoesNotContain("green", WatchCommand.EditToastMarkup(failure, "ticked"));
    }

    // ---- patching the board ----------------------------------------------

    [Fact]
    public void PatchRow_Replaces_The_Matching_Row_Only()
    {
        var rows = ImmutableArray.Create(Row("/specs/a"), Row("/specs/b"), Row("/specs/c"));
        var updated = Row("/specs/b", SpecStatus.Complete, 4, 4);

        var patched = WatchCommand.PatchRow(rows, updated);

        Assert.Equal(3, patched.Length);
        Assert.Equal(SpecStatus.Complete, patched[1].Status);
        Assert.Equal(SpecStatus.Planning, patched[0].Status);
        Assert.Equal(SpecStatus.Planning, patched[2].Status);
    }

    [Fact]
    public void PatchRow_Leaves_The_Board_Alone_When_The_Spec_Is_Gone()
    {
        var rows = ImmutableArray.Create(Row("/specs/a"));

        var patched = WatchCommand.PatchRow(rows, Row("/specs/missing", SpecStatus.Complete, 4, 4));

        Assert.Equal(rows, patched);
    }

    [Fact]
    public void PatchRow_Handles_An_Empty_Board()
    {
        Assert.True(WatchCommand.PatchRow(ImmutableArray<SpecRow>.Empty, Row()).IsEmpty);
    }

    // ---- re-parse merge --------------------------------------------------

    [Fact]
    public void MergeReparsed_Recomputes_Drift_From_The_Fresh_Counts()
    {
        // Was drifting (Complete but 2/4); ticking the rest should clear the flag.
        var previous = Row(status: SpecStatus.Complete, done: 2, total: 4) with
        {
            Drift = DriftState.Overstated,
            DriftReason = "Marked Complete, but 2/4 tasks checked",
        };
        var fresh = Row(status: SpecStatus.Complete, done: 4, total: 4);

        var merged = WatchCommand.MergeReparsed(fresh, previous, DateTimeOffset.Now, DriftOptions.Default);

        Assert.Equal(DriftState.None, merged.Drift);
        Assert.Null(merged.DriftReason);
        Assert.Equal(4, merged.Done);
    }

    [Fact]
    public void MergeReparsed_Raises_Drift_When_The_Edit_Creates_It()
    {
        var previous = Row(status: SpecStatus.Planning, done: 0, total: 4);
        var fresh = Row(status: SpecStatus.Planning, done: 1, total: 4);

        var merged = WatchCommand.MergeReparsed(fresh, previous, DateTimeOffset.Now, DriftOptions.Default);

        Assert.Equal(DriftState.Understated, merged.Drift);
        Assert.False(string.IsNullOrEmpty(merged.DriftReason));
    }

    [Fact]
    public void MergeReparsed_Carries_Git_Enrichment_Across()
    {
        var commit = new GitCommit("f".PadRight(40, '0'), "f000000", DateTimeOffset.Now, "Ada", "Add thing");
        var previous = Row() with
        {
            IsCurrent = true,
            LastCommit = commit,
            RecentCommits = ImmutableArray.Create(commit),
        };
        var fresh = Row(done: 1);

        var merged = WatchCommand.MergeReparsed(fresh, previous, DateTimeOffset.Now, DriftOptions.Default);

        Assert.True(merged.IsCurrent);
        Assert.Equal(commit, merged.LastCommit);
        Assert.Single(merged.RecentCommits);
    }

    // ---- what survives a rebuild -----------------------------------------

    [Fact]
    public void PreserveFocus_Keeps_The_Same_Element_When_The_Count_Is_Unchanged()
    {
        Assert.Equal(3, WatchCommand.PreserveFocus(previous: 3, elementCount: 8));
    }

    [Fact]
    public void PreserveFocus_Clamps_When_The_Document_Shrank()
    {
        Assert.Equal(1, WatchCommand.PreserveFocus(previous: 5, elementCount: 2));
    }

    [Fact]
    public void PreserveFocus_Stays_Unfocused_And_Survives_An_Emptied_Document()
    {
        Assert.Equal(-1, WatchCommand.PreserveFocus(previous: -1, elementCount: 8));
        Assert.Equal(-1, WatchCommand.PreserveFocus(previous: 2, elementCount: 0));
    }

    // ---- clicking the glyph ----------------------------------------------

    [Fact]
    public void GlyphColumn_Finds_The_Mark_Past_Any_Indent()
    {
        Assert.Equal(0, WatchCommand.GlyphColumn("○ one"));
        Assert.Equal(2, WatchCommand.GlyphColumn("  ✔ 1.1 nested"));
        Assert.Equal(-1, WatchCommand.GlyphColumn("     "));
    }

    [Fact]
    public void A_Click_On_The_Glyph_Toggles_And_A_Click_On_The_Text_Does_Not()
    {
        const string line = "  ○ 1.1 nested";

        Assert.True(WatchCommand.IsGlyphHit(line, 2));    // the glyph itself
        Assert.True(WatchCommand.IsGlyphHit(line, 3));    // the space beside it, a forgiving target
        Assert.False(WatchCommand.IsGlyphHit(line, 1));   // the indent
        Assert.False(WatchCommand.IsGlyphHit(line, 6));   // the task text
    }

    // ---- footer hints ----------------------------------------------------

    [Fact]
    public void Hints_Offer_Focus_When_Nothing_Is_Focused()
    {
        var hints = WatchCommand.DetailKeyHints(null, pickerOpen: false, editing: true, mouse: false);

        Assert.Contains("Tab", hints);
        Assert.Contains("focus", hints);
        Assert.DoesNotContain("tick", hints);
    }

    [Fact]
    public void Hints_Offer_Ticking_When_A_Checkbox_Is_Focused()
    {
        var hints = WatchCommand.DetailKeyHints(
            DetailElementKind.Checkbox, pickerOpen: false, editing: true, mouse: false);

        Assert.Contains("Space", hints);
        Assert.Contains("tick", hints);
    }

    [Fact]
    public void Hints_Offer_Changing_The_Status_When_It_Is_Focused()
    {
        var hints = WatchCommand.DetailKeyHints(
            DetailElementKind.Status, pickerOpen: false, editing: true, mouse: false);

        Assert.Contains("Space", hints);
        Assert.Contains("status", hints);
    }

    [Fact]
    public void Hints_Switch_To_The_Pickers_Own_Keys_While_It_Is_Open()
    {
        var hints = WatchCommand.DetailKeyHints(
            DetailElementKind.Status, pickerOpen: true, editing: true, mouse: false);

        Assert.Contains("pick", hints);
        Assert.Contains("Enter", hints);
        Assert.Contains("cancel", hints);
        Assert.DoesNotContain("quit", hints);        // Q is swallowed while the picker is up
    }

    [Fact]
    public void Hints_Drop_Every_Edit_Affordance_When_Editing_Is_Off()
    {
        var hints = WatchCommand.DetailKeyHints(
            DetailElementKind.Checkbox, pickerOpen: false, editing: false, mouse: false);

        Assert.DoesNotContain("tick", hints);
        Assert.DoesNotContain("Tab", hints);
        Assert.Contains("scroll", hints);
        Assert.Contains("quit", hints);
    }

    [Fact]
    public void Hints_Say_So_When_The_Board_Is_Read_Only()
    {
        Assert.Contains("read-only", WatchCommand.DetailKeyHints(null, false, editing: false, mouse: false));
        Assert.DoesNotContain("read-only", WatchCommand.DetailKeyHints(null, false, editing: true, mouse: false));
    }

    [Fact]
    public void Hints_Mention_The_Mouse_Only_When_It_Works()
    {
        Assert.Contains("wheel", WatchCommand.DetailKeyHints(null, false, true, mouse: true));
        Assert.DoesNotContain("wheel", WatchCommand.DetailKeyHints(null, false, true, mouse: false));
    }
}
