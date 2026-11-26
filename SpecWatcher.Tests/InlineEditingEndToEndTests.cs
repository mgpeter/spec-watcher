using System.Collections.Immutable;
using SpecWatcher;
using SpecWatcher.Input;
using Xunit;

namespace SpecWatcher.Tests;

/// <summary>
/// Drives a real <see cref="WatchCommand"/> with real input events over real files on disk: open a
/// spec, tab to an element, act on it, and check both what landed in the file and what the board now
/// shows. This is the only place the instance wiring (keys → editor → re-parse → publish) is tested.
///
/// No console is involved: the viewport is pinned to 120x30 so hit-testing does not depend on the
/// size the test host happens to report.
/// </summary>
public class InlineEditingEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _specDir;

    private const string SpecMd = "# Spec\n\n> Spec: Editing\n> Created: 2026-08-24\n> Status: Planning\n\n## Overview\n\nA spec.\n";
    private const string TasksMd = "# Spec Tasks\n\n## Tasks\n\n- [ ] 1. First\n  - [ ] 1.1 Nested\n- [ ] 2. Second\n";

    public InlineEditingEndToEndTests()
    {
        _specDir = Path.Combine(_root, "2026-08-24-editing");
        Directory.CreateDirectory(_specDir);
        File.WriteAllText(Path.Combine(_specDir, "spec.md"), SpecMd);
        File.WriteAllText(Path.Combine(_specDir, "tasks.md"), TasksMd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string TasksPath => Path.Combine(_specDir, "tasks.md");
    private string SpecPath => Path.Combine(_specDir, "spec.md");

    private static WatchSettings Settings(bool readOnly = false) =>
        new() { RepoPath = Directory.GetCurrentDirectory(), ReadOnly = readOnly };

    /// <summary>A command with the detail view already open on the fixture spec.</summary>
    private (WatchCommand Command, ScanResult Data) Open(bool readOnly = false)
    {
        var command = new WatchCommand { ViewportOverride = (120, 30) };
        command.ApplySettings(Settings(readOnly));

        var row = SpecParser.Parse(_specDir);
        var data = new ScanResult(ImmutableArray.Create(row), DateTimeOffset.Now);

        command.HandleInput(InputEvent.FromKey(ConsoleKey.Enter), data);   // list → detail
        Assert.True(command.InDetailView);
        return (command, data);
    }

    private static void Press(WatchCommand command, ScanResult data, ConsoleKey key, bool shift = false) =>
        command.HandleInput(InputEvent.FromKey(key, '\0', shift), data);

    // ---- ticking ---------------------------------------------------------

    [Fact]
    public void Tab_Then_Space_Ticks_The_First_Task_On_Disk()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);       // status
        Press(command, data, ConsoleKey.Tab);       // first checkbox
        Press(command, data, ConsoleKey.Spacebar);

        Assert.Equal("# Spec Tasks\n\n## Tasks\n\n- [x] 1. First\n  - [ ] 1.1 Nested\n- [ ] 2. Second\n",
            File.ReadAllText(TasksPath));
    }

    [Fact]
    public void Ticking_Updates_The_Board_Immediately()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Spacebar);

        var row = Assert.Single(command.Latest.Rows);
        Assert.Equal(1, row.Done);
        Assert.Equal(3, row.Total);
    }

    [Fact]
    public void Ticking_Twice_Unticks_And_Leaves_The_File_As_It_Started()
    {
        var (command, data) = Open();
        var before = File.ReadAllBytes(TasksPath);

        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Spacebar);
        Press(command, data, ConsoleKey.Spacebar);

        Assert.Equal(before, File.ReadAllBytes(TasksPath));
    }

    [Fact]
    public void Focus_Survives_The_Rebuild_That_Follows_A_Tick()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Tab);
        var focused = command.FocusIndex;
        Press(command, data, ConsoleKey.Spacebar);

        Assert.Equal(focused, command.FocusIndex);
    }

    [Fact]
    public void Ticking_Every_Task_Raises_Drift_Against_A_Planning_Status()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);                 // status
        for (var i = 0; i < 3; i++)
        {
            Press(command, data, ConsoleKey.Tab);
            Press(command, data, ConsoleKey.Spacebar);
        }

        var row = Assert.Single(command.Latest.Rows);
        Assert.Equal(3, row.Done);
        Assert.Equal(DriftState.Understated, row.Drift);      // all checked, still "Planning"
    }

    // ---- the status dropdown ---------------------------------------------

    [Fact]
    public void Space_On_The_Status_Opens_The_Picker_And_Escape_Cancels_It()
    {
        var (command, data) = Open();
        var before = File.ReadAllBytes(SpecPath);

        Press(command, data, ConsoleKey.Tab);                 // status
        Press(command, data, ConsoleKey.Spacebar);
        Assert.True(command.PickerOpen);

        Press(command, data, ConsoleKey.Escape);
        Assert.False(command.PickerOpen);
        Assert.Equal(before, File.ReadAllBytes(SpecPath));    // cancelling writes nothing
    }

    [Fact]
    public void Choosing_Complete_Rewrites_The_Status_Line()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);                 // status
        Press(command, data, ConsoleKey.Spacebar);            // open, sitting on "Planning"
        Press(command, data, ConsoleKey.DownArrow);           // In progress
        Press(command, data, ConsoleKey.DownArrow);           // Complete
        Press(command, data, ConsoleKey.Enter);

        Assert.False(command.PickerOpen);
        Assert.Contains("> Status: Complete", File.ReadAllText(SpecPath));
        Assert.Equal(SpecStatus.Complete, Assert.Single(command.Latest.Rows).Status);
    }

    [Fact]
    public void The_Picker_Swallows_Keys_That_Would_Otherwise_Quit_Or_Navigate()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Spacebar);
        Press(command, data, ConsoleKey.Q);

        Assert.True(command.Running);
        Assert.True(command.PickerOpen);
        Assert.True(command.InDetailView);
    }

    // ---- escape ladder ---------------------------------------------------

    [Fact]
    public void Escape_Clears_Focus_Before_It_Leaves_The_Detail_View()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);
        Assert.True(command.FocusIndex >= 0);

        Press(command, data, ConsoleKey.Escape);
        Assert.Equal(-1, command.FocusIndex);
        Assert.True(command.InDetailView);

        Press(command, data, ConsoleKey.Escape);
        Assert.False(command.InDetailView);
    }

    // ---- read-only -------------------------------------------------------

    [Fact]
    public void ReadOnly_Ignores_Tab_And_Space_Entirely()
    {
        var (command, data) = Open(readOnly: true);
        var tasksBefore = File.ReadAllBytes(TasksPath);
        var specBefore = File.ReadAllBytes(SpecPath);

        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Spacebar);
        Press(command, data, ConsoleKey.Tab);
        Press(command, data, ConsoleKey.Enter);

        Assert.Equal(-1, command.FocusIndex);
        Assert.False(command.PickerOpen);
        Assert.Equal(tasksBefore, File.ReadAllBytes(TasksPath));
        Assert.Equal(specBefore, File.ReadAllBytes(SpecPath));
    }

    [Fact]
    public void ReadOnly_Ignores_A_Click_On_A_Checkbox_Glyph()
    {
        var (command, data) = Open(readOnly: true);
        var before = File.ReadAllBytes(TasksPath);

        ClickCheckboxGlyph(command, data);

        Assert.Equal(before, File.ReadAllBytes(TasksPath));
        Assert.Equal(-1, command.FocusIndex);
    }

    // ---- mouse -----------------------------------------------------------

    [Fact]
    public void A_Click_On_A_Checkbox_Glyph_Ticks_It()
    {
        var (command, data) = Open();

        ClickCheckboxGlyph(command, data);

        Assert.Contains("- [x] 1. First", File.ReadAllText(TasksPath));
    }

    [Fact]
    public void A_Click_On_The_Task_Text_Focuses_Without_Ticking()
    {
        var (command, data) = Open();
        var before = File.ReadAllBytes(TasksPath);
        var (screenY, glyphX) = CheckboxScreenCell(command);

        command.HandleInput(InputEvent.Down(glyphX + 6, screenY), data);
        command.HandleInput(InputEvent.Up(glyphX + 6, screenY), data);

        Assert.True(command.FocusIndex >= 0);
        Assert.Equal(before, File.ReadAllBytes(TasksPath));
    }

    [Fact]
    public void Wheel_Scrolling_Still_Works_In_The_Detail_View()
    {
        // The default fixture is shorter than any viewport, so give this one a document to scroll.
        var filler = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"Line {i}."));
        File.WriteAllText(Path.Combine(_specDir, "spec.md"), SpecMd + "\n" + filler + "\n");

        var (command, data) = Open();
        Assert.Equal(0, command.DetailScroll);

        command.HandleInput(InputEvent.Wheel(-3, 5, 10), data);   // wheel down

        Assert.True(command.DetailScroll > 0);
    }

    // ---- someone else edits the file mid-session --------------------------

    [Fact]
    public void An_External_Edit_Between_Render_And_Keypress_Is_Refused()
    {
        var (command, data) = Open();
        Press(command, data, ConsoleKey.Tab);       // status
        Press(command, data, ConsoleKey.Tab);       // first checkbox

        // An editor elsewhere rewrites the line we are focused on.
        const string rewritten = "# Spec Tasks\n\n## Tasks\n\n- [ ] 1. Renamed elsewhere\n  - [ ] 1.1 Nested\n- [ ] 2. Second\n";
        File.WriteAllText(TasksPath, rewritten);

        Press(command, data, ConsoleKey.Spacebar);

        // Nothing is written: the line we would have ticked is not the line on disk any more.
        Assert.Equal(rewritten, File.ReadAllText(TasksPath));
    }

    [Fact]
    public void An_External_Status_Change_Is_Refused_By_The_Picker()
    {
        var (command, data) = Open();
        Press(command, data, ConsoleKey.Tab);       // status
        Press(command, data, ConsoleKey.Spacebar);  // open the picker

        File.WriteAllText(Path.Combine(_specDir, "spec.md"), SpecMd.Replace("> Status: Planning", "> Status: Blocked"));

        Press(command, data, ConsoleKey.DownArrow);
        Press(command, data, ConsoleKey.Enter);

        Assert.Contains("> Status: Blocked", File.ReadAllText(SpecPath));
    }

    // ---- existing behaviour must be untouched -----------------------------

    [Fact]
    public void Drag_To_Copy_Still_Selects_Rather_Than_Focusing()
    {
        var (command, data) = Open();
        var before = File.ReadAllBytes(TasksPath);
        var (screenY, glyphX) = CheckboxScreenCell(command);

        // A press on the glyph, then a move: a drag, not a click, so it must not tick anything.
        command.HandleInput(InputEvent.Down(glyphX, screenY), data);
        command.HandleInput(InputEvent.Drag(glyphX + 8, screenY), data);
        command.HandleInput(InputEvent.Up(glyphX + 8, screenY), data);

        Assert.Equal(before, File.ReadAllBytes(TasksPath));
        Assert.Equal(-1, command.FocusIndex);
    }

    [Fact]
    public void Backspace_Still_Leaves_The_Detail_View_In_One_Key()
    {
        var (command, data) = Open();

        Press(command, data, ConsoleKey.Tab);            // focused, so Escape would take two presses
        Press(command, data, ConsoleKey.Backspace);

        Assert.False(command.InDetailView);
    }

    [Fact]
    public void List_View_Keys_Are_Unchanged()
    {
        var command = new WatchCommand();
        command.ApplySettings(Settings());
        var data = new ScanResult(ImmutableArray.Create(SpecParser.Parse(_specDir)), DateTimeOffset.Now);

        Press(command, data, ConsoleKey.Tab);            // no meaning in the list
        Assert.False(command.InDetailView);

        Press(command, data, ConsoleKey.Spacebar);       // still opens the selected spec
        Assert.True(command.InDetailView);

        Press(command, data, ConsoleKey.Q);
        Assert.False(command.Running);
    }

    /// <summary>Screen cell (Y, X) of the first checkbox's mark glyph in the open detail view.</summary>
    private static (int ScreenY, int GlyphX) CheckboxScreenCell(WatchCommand command)
    {
        var element = command.DetailElements.First(e => e.Kind == DetailElementKind.Checkbox);
        var plain = command.DetailPlain[element.ContentLine];
        return (element.ContentLine + WatchCommand.DetailContentTop, WatchCommand.GlyphColumn(plain) + 2);
    }

    private static void ClickCheckboxGlyph(WatchCommand command, ScanResult data)
    {
        var (screenY, glyphX) = CheckboxScreenCell(command);
        command.HandleInput(InputEvent.Down(glyphX, screenY), data);
        command.HandleInput(InputEvent.Up(glyphX, screenY), data);
    }
}
