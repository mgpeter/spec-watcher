using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using SpecWatcher.Input;

namespace SpecWatcher;

/// <summary>What the UI does after a write attempt.</summary>
internal enum EditFollowUp
{
    /// <summary>The write failed in a way that leaves both disk and screen as they were.</summary>
    None,

    /// <summary>The write landed; re-read this spec and fold it back into the board.</summary>
    Reload,

    /// <summary>Disk has moved on underneath us; ask for a full rescan.</summary>
    Rescan,
}

/// <summary>A rectangle of content cells covered by an overlay, in detail-document coordinates.</summary>
/// <param name="Top">First content line covered.</param>
/// <param name="Left">First content column covered.</param>
/// <param name="Width">Width in cells.</param>
/// <param name="Height">Height in lines.</param>
internal readonly record struct OverlayBox(int Top, int Left, int Width, int Height)
{
    public bool Contains(int line, int col) =>
        line >= Top && line < Top + Height && col >= Left && col < Left + Width;
}

/// <summary>
/// What Escape should unwind next in the detail view. It peels one layer at a time instead of
/// always leaving the view outright.
/// </summary>
internal enum EscapeAction
{
    /// <summary>An overlay is up; close it and keep the view and focus as they were.</summary>
    ClosePicker,

    /// <summary>An element is focused; drop focus but stay in the detail view.</summary>
    ClearFocus,

    /// <summary>Nothing left to unwind; go back to the list.</summary>
    LeaveDetail,
}

/// <summary>
/// The watch command: a full-screen, self-refreshing, navigable list of specs with a per-spec
/// detail view.
///
/// Threading model (lock-free):
///  - The UI thread owns the Layout and is the sole caller of Live.Refresh(). It drains input
///    events and reads the latest published <see cref="ScanResult"/>.
///  - A background scan loop parses specs off the UI thread and publishes via Volatile.Write.
///  - An <see cref="IInputSource"/> reader thread enqueues keyboard/mouse events.
/// </summary>
public sealed partial class WatchCommand : AsyncCommand<WatchSettings>
{
    private static readonly string[] Spinner =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    // Fixed vertical chrome, used for viewport math and mouse hit-testing.
    private const int TitleHeight = 3;
    private const int FooterHeight = 3;
    private const int ListHeaderHeight = 1;                  // column-label row at the top of the list body
    private const int ListContentTop = TitleHeight + ListHeaderHeight;   // absolute Y of the first data row

    // Fixed list column widths (visible cells). Name flexes to fill the rest.
    private const int CaretW = 1;
    private const int FlagW = 1;                            // drift/idle attention glyph
    private const int StatusW = 13;
    private const int ProgressW = 24;
    private const int LastCommitW = 18;                     // "just now · a1b2c3d" — git only
    private const int FolderW = 34;

    private enum View { List, Detail }

    private ScanResult _latest = ScanResult.Empty;          // published via Volatile.Write/Read
    private volatile bool _isScanning;
    private volatile bool _running = true;
    private long _nextScanAtTicks;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);

    // UI state (owned by the UI thread only).
    private View _view = View.List;
    private bool _flagsVisible = true;                      // drift/idle attention layer on/off
    private int _selectedIndex;
    private int _scrollOffset;
    private int _visibleRows = 1;
    private SpecRow? _detailRow;
    private string[] _detailLines = Array.Empty<string>();     // styled markup, one per line
    private string[] _detailPlain = Array.Empty<string>();     // visible plain text, parallel to _detailLines
    private ImmutableArray<DetailElement> _detailElements = ImmutableArray<DetailElement>.Empty;
    private int _focusIndex = -1;                              // index into _detailElements; -1 = nothing focused
    private int _detailScroll;

    // Mouse text selection (Herdr-style): the app keeps the mouse and draws its own highlight, so
    // the constant live-refresh can't erase it. Coordinates are content coords for the active view
    // (Line = row/detail-line index, Col = char offset into that line's plain text).
    private (int Line, int Col)? _selAnchor;   // set on mouse-down
    private (int Line, int Col)? _selHead;     // updated on drag
    private int _downScreenX;                  // screen X of the last mouse-down (for click dispatch)
    private int _downScreenY;                  // screen Y of the last mouse-down (for click dispatch)
    private string? _toast;                    // transient footer message, pre-styled markup
    private long _toastUntilTick;

    // ---- editing ---------------------------------------------------------

    private bool _editingEnabled = true;
    private DriftOptions _driftOptions;
    private int _editEpoch;                    // bumped on every successful write (Interlocked)

    private bool _pickerOpen;
    private bool _swallowMouseUp;              // the release that ends a picker gesture
    private int _pickerIndex;
    private ImmutableArray<string> _pickerChoices = ImmutableArray<string>.Empty;
    private OverlayBox _pickerBox;             // geometry shared by the renderer and the hit-tester

    // ---- test surface ----------------------------------------------------
    //
    // The instance wiring (keys → editor → re-parse → publish) is only meaningful end to end, so a
    // narrow read-only window onto the UI state is exposed rather than duplicating the logic in a
    // test double. Nothing here is used by the app itself.

    internal ScanResult Latest => Volatile.Read(ref _latest);
    internal bool InDetailView => _view == View.Detail;
    internal bool Running => _running;
    internal int FocusIndex => _focusIndex;
    internal int DetailScroll => _detailScroll;
    internal bool PickerOpen => _pickerOpen;
    internal string[] DetailPlain => _detailPlain;
    internal ImmutableArray<DetailElement> DetailElements => _detailElements;

    /// <summary>Absolute screen Y of the detail panel's first content line.</summary>
    internal const int DetailContentTop = TitleHeight + 1;

    /// <summary>Adopt the startup options. Shared by <see cref="ExecuteAsync"/> and the tests.</summary>
    internal void ApplySettings(WatchSettings settings)
    {
        _flagsVisible = !settings.NoFlags;
        _driftOptions = settings.ToDriftOptions();
        _editingEnabled = settings.EditingEnabled;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WatchSettings settings)
    {
        var caps = AnsiConsole.Profile.Capabilities;
        ApplySettings(settings);

        // Headless (one-shot) path: the user asked for it (--once / --format json|md / a gate flag) OR
        // output is non-interactive (piped / redirected / dumb terminal). Scan once, emit the chosen
        // format to stdout, and exit with the gate/exit-code contract. Never enters the TUI.
        if (settings.WantsHeadless || !caps.Ansi || !caps.Interactive)
        {
            var result = await SpecScanner.ScanAsync(settings.ResolvedSpecsDir, DateTimeOffset.Now, settings.ToDriftOptions());
            return HeadlessRunner.Run(settings, result, Console.Out);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // let the loop unwind so the alt-screen and console mode are restored
            _running = false;
        };

        var scanLoop = Task.Run(() => ScanLoopAsync(settings, cts.Token), cts.Token);
        using var input = IInputSource.Create();
        var layout = BuildLayout();

        void RunUi() => AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(live =>
            {
                var frame = 0;
                while (_running)
                {
                    var data = Volatile.Read(ref _latest);
                    _visibleRows = Math.Max(1, SafeWindowHeight() - TitleHeight - FooterHeight - ListHeaderHeight);

                    while (input.TryRead(out var evt))
                        HandleInput(evt, data);

                    ClampSelection(data);
                    UpdateLayout(layout, data, settings, input.SupportsMouse, frame++);
                    live.Refresh();
                    Thread.Sleep(30);
                }
            });

        if (caps.AlternateBuffer)
            AnsiConsole.AlternateScreen(RunUi);
        else
            RunUi();

        cts.Cancel();
        try { await scanLoop; } catch (OperationCanceledException) { }
        return 0;
    }

    // ---- input handling --------------------------------------------------

    internal void HandleInput(InputEvent evt, ScanResult data)
    {
        switch (evt.Kind)
        {
            case InputKind.Key:
                ClearSelection();
                // The picker is modal: it consumes every key so none leak through to the view below.
                if (_pickerOpen) { HandlePickerKey(evt.Key, data); break; }
                // Attention layer: `!` jumps between flagged rows, `F` toggles the whole layer.
                if (evt.KeyChar == '!') { if (_view == View.List) JumpToNextFlagged(data); break; }
                if (evt.Key == ConsoleKey.F) { _flagsVisible = !_flagsVisible; break; }
                if (_view == View.List) HandleListKey(evt.Key, data);
                else HandleDetailKey(evt.Key, evt.Shift, data);
                break;

            case InputKind.MouseWheel:
                ClearSelection();
                if (_pickerOpen) break;                                           // don't scroll out from under it
                if (_view == View.List) MoveSelection(-evt.WheelNotches, data);   // wheel up = earlier rows
                else ScrollDetail(-evt.WheelNotches * 3);
                break;

            case InputKind.MouseDown:
                // Handle the picker on press and swallow the release, so dismissing it by clicking
                // away cannot also land as a click on the view underneath.
                if (_pickerOpen) { HandlePickerClick(evt.Column, evt.Row, data); _swallowMouseUp = true; break; }
                _downScreenX = evt.Column;
                _downScreenY = evt.Row;
                var down = MapToContent(evt.Column, evt.Row, data);
                _selAnchor = down;
                _selHead = down;   // anchor == head → nothing highlighted yet
                break;

            case InputKind.MouseDrag:
                if (_pickerOpen) break;
                if (_selAnchor is not null && MapToContent(evt.Column, evt.Row, data) is { } head)
                    _selHead = head;
                break;

            case InputKind.MouseUp:
                if (_swallowMouseUp) { _swallowMouseUp = false; break; }
                if (SelectionRange() is not null)
                    CopySelection(data);                       // a real drag → auto-copy, keep highlight
                else
                {
                    // A plain click: open a row in the list, or focus an element in the detail view.
                    if (_view == View.List) ClickRow(_downScreenY, data);
                    else FocusAt(_downScreenX, _downScreenY, data);
                    ClearSelection();
                }
                break;

            case InputKind.Resize:
                break;   // viewport is recomputed each frame from the window size
        }
    }

    private void HandleListKey(ConsoleKey key, ScanResult data)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow: MoveSelection(-1, data); break;
            case ConsoleKey.DownArrow: MoveSelection(+1, data); break;
            case ConsoleKey.PageUp: MoveSelection(-_visibleRows, data); break;
            case ConsoleKey.PageDown: MoveSelection(+_visibleRows, data); break;
            case ConsoleKey.Home: MoveSelection(int.MinValue / 2, data); break;
            case ConsoleKey.End: MoveSelection(int.MaxValue / 2, data); break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar: OpenDetail(data); break;
            case ConsoleKey.R: TriggerRefresh(); break;
            case ConsoleKey.Q or ConsoleKey.Escape: _running = false; break;
        }
    }

    private void HandleDetailKey(ConsoleKey key, bool shift, ScanResult data)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow: ScrollDetail(-1); break;
            case ConsoleKey.DownArrow: ScrollDetail(+1); break;
            case ConsoleKey.PageUp: ScrollDetail(-DetailViewportHeight()); break;
            case ConsoleKey.PageDown: ScrollDetail(+DetailViewportHeight()); break;
            case ConsoleKey.Home: _detailScroll = 0; break;
            case ConsoleKey.End: _detailScroll = DetailMaxScroll(); break;
            // The focus cursor only exists to act on things, so read-only mode has no use for it.
            case ConsoleKey.Tab when _editingEnabled: MoveFocus(FocusDelta(shift)); break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar: ActivateFocused(data); break;
            case ConsoleKey.Escape: HandleEscape(); break;
            case ConsoleKey.Backspace or ConsoleKey.LeftArrow: _view = View.List; break;
            case ConsoleKey.R: TriggerRefresh(); break;
            case ConsoleKey.Q: _running = false; break;
        }
    }

    /// <summary>Act on the focused element: tick a task, or open the status dropdown.</summary>
    private void ActivateFocused(ScanResult data)
    {
        if (!_editingEnabled) return;
        switch (FocusedElement?.Kind)
        {
            case DetailElementKind.Checkbox: ToggleFocusedCheckbox(data); break;
            case DetailElementKind.Status: OpenPicker(); break;
        }
    }

    /// <summary>
    /// Escape unwinds one layer at a time rather than always leaving the view, so a focused element
    /// can be dismissed without losing your place in a long spec.
    /// </summary>
    private void HandleEscape()
    {
        switch (NextEscapeAction(_pickerOpen, hasFocus: _focusIndex >= 0))
        {
            case EscapeAction.ClosePicker: _pickerOpen = false; break;
            case EscapeAction.ClearFocus: _focusIndex = -1; break;
            case EscapeAction.LeaveDetail: _view = View.List; break;
        }
    }

    private void MoveSelection(int delta, ScanResult data)
    {
        var count = data.Rows.IsDefaultOrEmpty ? 0 : data.Rows.Length;
        if (count == 0) return;
        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, count - 1);
        EnsureSelectedVisible(count);
    }

    private void EnsureSelectedVisible(int count)
    {
        if (_selectedIndex < _scrollOffset)
            _scrollOffset = _selectedIndex;
        else if (_selectedIndex >= _scrollOffset + _visibleRows)
            _scrollOffset = _selectedIndex - _visibleRows + 1;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, count - _visibleRows));
    }

    private void ClickRow(int clickY, ScanResult data)
    {
        var slice = clickY - ListContentTop;
        if (slice < 0 || slice >= _visibleRows) return;                 // clicked chrome/footer
        var index = _scrollOffset + slice;
        if (data.Rows.IsDefaultOrEmpty || index < 0 || index >= data.Rows.Length) return;
        _selectedIndex = index;
        OpenDetail(data);
    }

    private void OpenDetail(ScanResult data)
    {
        if (data.Rows.IsDefaultOrEmpty) return;
        var row = data.Rows[Math.Clamp(_selectedIndex, 0, data.Rows.Length - 1)];
        _detailRow = row;
        LoadDetailDocument(row);
        _detailScroll = 0;
        _view = View.Detail;
        ClearSelection();
    }

    /// <summary>
    /// Re-read a spec from disk and rebuild the detail document. Focus is dropped because element
    /// indices only mean anything against the document they were built with.
    /// </summary>
    private void LoadDetailDocument(SpecRow row)
    {
        (_detailLines, _detailPlain, _detailElements) =
            BuildDetailLines(row, SpecParser.LoadDetail(row.FullPath));
        _focusIndex = -1;
    }

    /// <summary>The focused interactive element, or null when nothing is focused.</summary>
    private DetailElement? FocusedElement =>
        _focusIndex >= 0 && _focusIndex < _detailElements.Length ? _detailElements[_focusIndex] : null;

    // ---- focus cursor ----------------------------------------------------
    //
    // Focus is an index into _detailElements, independent of the scroll position. Every rule is a
    // pure function of state so it can be tested without a console; the instance methods below only
    // supply the current viewport.

    /// <summary>Which way Tab moves: forward, or backward when Shift is held.</summary>
    internal static int FocusDelta(bool shift) => shift ? -1 : +1;

    /// <summary>
    /// The next focused element index, wrapping in both directions. Returns -1 when there is nothing
    /// to focus; entering from unfocused (or a stale index) lands on the first or last element
    /// depending on direction.
    /// </summary>
    internal static int NextFocusIndex(ImmutableArray<DetailElement> elements, int current, int delta)
    {
        if (elements.IsDefaultOrEmpty) return -1;
        var count = elements.Length;
        if (current < 0 || current >= count)
            return delta >= 0 ? 0 : count - 1;
        return ((current + delta) % count + count) % count;
    }

    /// <summary>Index of the element rendered on <paramref name="contentLine"/>, or -1.</summary>
    internal static int ElementAtContentLine(ImmutableArray<DetailElement> elements, int contentLine)
    {
        if (elements.IsDefaultOrEmpty) return -1;
        for (var i = 0; i < elements.Length; i++)
            if (elements[i].ContentLine == contentLine) return i;
        return -1;
    }

    /// <summary>The scroll offset that brings <paramref name="contentLine"/> into view, moving as
    /// little as possible. Mirrors <see cref="EnsureSelectedVisible"/> for the detail viewport.</summary>
    internal static int ScrollToShow(int contentLine, int scroll, int viewportHeight, int totalLines)
    {
        if (contentLine < scroll) scroll = contentLine;
        else if (contentLine >= scroll + viewportHeight) scroll = contentLine - viewportHeight + 1;
        return Math.Clamp(scroll, 0, Math.Max(0, totalLines - viewportHeight));
    }

    /// <summary>Which layer Escape unwinds next.</summary>
    internal static EscapeAction NextEscapeAction(bool pickerOpen, bool hasFocus) =>
        pickerOpen ? EscapeAction.ClosePicker
        : hasFocus ? EscapeAction.ClearFocus
        : EscapeAction.LeaveDetail;

    /// <summary>Style the focused element's line. Banded rather than prefixed, so the visible column
    /// count still matches <see cref="_detailPlain"/> and drag-selection maths stays correct.
    /// A different band colour to the drag selection, so both can be on screen at once.</summary>
    internal static string FocusLine(string plain) => $"[black on deepskyblue1]{Markup.Escape(plain)}[/]";

    private void MoveFocus(int delta)
    {
        _focusIndex = NextFocusIndex(_detailElements, _focusIndex, delta);
        if (FocusedElement is { } element)
            _detailScroll = ScrollToShow(
                element.ContentLine, _detailScroll, DetailViewportHeight(), _detailLines.Length);
    }

    /// <summary>
    /// Focus whatever element sits on the clicked screen cell; a miss clears focus. Landing squarely
    /// on a checkbox glyph also toggles it, so a task can be ticked in one click.
    /// </summary>
    private void FocusAt(int x, int y, ScanResult data)
    {
        if (!_editingEnabled) return;
        if (DetailCell(x, y) is not { } at) return;
        _focusIndex = ElementAtContentLine(_detailElements, at.Line);

        if (FocusedElement is not { Kind: DetailElementKind.Checkbox }) return;
        if (at.Line < 0 || at.Line >= _detailPlain.Length) return;
        if (IsGlyphHit(_detailPlain[at.Line], at.Col)) ToggleFocusedCheckbox(data);
    }

    /// <summary>Column of a checkbox line's mark glyph (its first non-space char), or -1.</summary>
    internal static int GlyphColumn(string plain)
    {
        for (var i = 0; i < plain.Length; i++)
            if (plain[i] != ' ') return i;
        return -1;
    }

    /// <summary>Whether a click column landed on the mark glyph (or the cell beside it).</summary>
    internal static bool IsGlyphHit(string plain, int col)
    {
        var glyph = GlyphColumn(plain);
        return glyph >= 0 && col >= glyph && col <= glyph + 1;
    }

    // ---- applying an edit -------------------------------------------------

    /// <summary>What to do after a write attempt.</summary>
    internal static EditFollowUp FollowUpFor(EditResult result) => result switch
    {
        EditResult.Ok => EditFollowUp.Reload,
        EditResult.Conflict => EditFollowUp.Rescan,
        _ => EditFollowUp.None,
    };

    /// <summary>Footer message for a write attempt, already styled.</summary>
    internal static string EditToastMarkup(EditResult result, string okText) => result switch
    {
        EditResult.Ok => $"[green]✔ {Markup.Escape(okText)}[/]",
        EditResult.Conflict => "[orange1]⚠ file changed on disk[/]",
        EditResult.Unwritable => "[orange1]⚠ read-only file[/]",
        EditResult.NotFound => "[orange1]⚠ file missing[/]",
        EditResult.NoStatusLine => "[orange1]⚠ no Status: line in spec.md[/]",
        _ => "[orange1]⚠ edit failed[/]",
    };

    /// <summary>Replace a spec in the board by path, leaving the rest of the rows untouched.</summary>
    internal static ImmutableArray<SpecRow> PatchRow(ImmutableArray<SpecRow> rows, SpecRow updated)
    {
        if (rows.IsDefaultOrEmpty) return rows;
        for (var i = 0; i < rows.Length; i++)
            if (string.Equals(rows[i].FullPath, updated.FullPath, StringComparison.OrdinalIgnoreCase))
                return rows.SetItem(i, updated);
        return rows;
    }

    /// <summary>
    /// Fold a freshly parsed row back onto the one it replaces: drift is recomputed from the new
    /// counts, and git enrichment is carried over rather than re-run (it costs a subprocess and
    /// cannot have changed as a result of our own write).
    /// </summary>
    internal static SpecRow MergeReparsed(SpecRow fresh, SpecRow previous, DateTimeOffset now, DriftOptions options)
    {
        var (state, reason) = DriftAnalyzer.Analyze(fresh, now, options);
        return fresh with
        {
            Drift = state,
            DriftReason = reason,
            IsCurrent = previous.IsCurrent,
            LastCommit = previous.LastCommit,
            RecentCommits = previous.RecentCommits,
        };
    }

    /// <summary>Where focus lands after the document is rebuilt under it.</summary>
    internal static int PreserveFocus(int previous, int elementCount) =>
        previous < 0 || elementCount <= 0 ? -1 : Math.Min(previous, elementCount - 1);

    private void ToggleFocusedCheckbox(ScanResult data)
    {
        if (!_editingEnabled) return;
        if (FocusedElement is not { Kind: DetailElementKind.Checkbox } element) return;

        var result = SpecEditor.ToggleCheckbox(
            element.SourceFile, element.SourceLine, element.SourceText, !element.IsChecked);
        ApplyEdit(result, data, element.IsChecked ? "unticked" : "ticked");
    }

    private void ApplyEdit(EditResult result, ScanResult data, string okText)
    {
        ShowToast(EditToastMarkup(result, okText));
        switch (FollowUpFor(result))
        {
            case EditFollowUp.Reload: ReloadAfterEdit(data); break;
            case EditFollowUp.Rescan: TriggerRefresh(); break;
        }
    }

    /// <summary>
    /// Re-read the edited spec and publish it, keeping the user exactly where they were. The epoch
    /// bump tells an in-flight scan that it may be holding a pre-edit read of this folder.
    /// </summary>
    private void ReloadAfterEdit(ScanResult data)
    {
        if (_detailRow is not { } previous) return;

        var fresh = MergeReparsed(SpecParser.Parse(previous.FullPath), previous, DateTimeOffset.Now, _driftOptions);
        _detailRow = fresh;
        Volatile.Write(ref _latest, data with { Rows = PatchRow(data.Rows, fresh) });
        Interlocked.Increment(ref _editEpoch);

        var focus = _focusIndex;
        var scroll = _detailScroll;
        LoadDetailDocument(fresh);
        _focusIndex = PreserveFocus(focus, _detailElements.Length);
        _detailScroll = Math.Clamp(scroll, 0, DetailMaxScroll());
    }

    private void ShowToast(string markup)
    {
        _toast = markup;
        _toastUntilTick = Environment.TickCount64 + 1600;
    }

    // ---- status dropdown --------------------------------------------------
    //
    // Drawn inside the Live loop rather than via AnsiConsole.Prompt, which would take the console
    // over and stall the render loop and the background scan. Geometry is computed once on open and
    // read by both the renderer and the hit-tester, so the two cannot disagree about where it is.

    private const string PickerTitle = " Status ";
    private const int PickerLeft = 2;

    /// <summary>The known statuses, plus the spec's current value when it is none of them.</summary>
    internal static ImmutableArray<string> StatusChoices(string currentRaw)
    {
        var choices = ImmutableArray.CreateBuilder<string>();
        choices.Add("Planning");
        choices.Add("In progress");
        choices.Add("Complete");

        var current = (currentRaw ?? string.Empty).Trim();
        if (current.Length > 0 && SpecParser.NormaliseStatus(current) == SpecStatus.Unknown)
            choices.Add(current);

        return choices.ToImmutable();
    }

    /// <summary>The choice to open on: whichever one the spec already says.</summary>
    internal static int InitialChoiceIndex(ImmutableArray<string> choices, string currentRaw)
    {
        var current = (currentRaw ?? string.Empty).Trim();
        if (current.Length == 0) return 0;

        for (var i = 0; i < choices.Length; i++)
            if (string.Equals(choices[i], current, StringComparison.OrdinalIgnoreCase))
                return i;

        var status = SpecParser.NormaliseStatus(current);
        if (status != SpecStatus.Unknown)
            for (var i = 0; i < choices.Length; i++)
                if (SpecParser.NormaliseStatus(choices[i]) == status)
                    return i;

        return 0;
    }

    internal static int NextChoiceIndex(int count, int current, int delta) =>
        count <= 0 ? 0 : ((current + delta) % count + count) % count;

    /// <summary>
    /// Where the box lands: below the status line, flipped above it when there is no room, and never
    /// outside the visible slice of the document.
    /// </summary>
    internal static OverlayBox PickerBox(int anchorLine, ImmutableArray<string> choices, int scroll, int viewportHeight)
    {
        var height = choices.Length + 2;                                  // choices plus the border
        var textW = Math.Max(PickerTitle.Length, choices.Max(c => c.Length));
        var width = textW + 5;                                            // "│▸ " + text + " │"

        var top = anchorLine + 1;
        if (top + height > scroll + viewportHeight)
            top = anchorLine - height;                                    // no room below, hang above
        top = Math.Clamp(top, scroll, Math.Max(scroll, scroll + viewportHeight - height));

        return new OverlayBox(top, PickerLeft, width, height);
    }

    /// <summary>The box as plain rows, each exactly <paramref name="width"/> cells wide.</summary>
    internal static string[] PickerRows(ImmutableArray<string> choices, int selected, int width)
    {
        var inner = width - 2;
        var rows = new string[choices.Length + 2];

        rows[0] = "┌" + PickerTitle + new string('─', Math.Max(0, inner - PickerTitle.Length)) + "┐";
        for (var i = 0; i < choices.Length; i++)
        {
            var marker = i == selected ? "▸" : " ";
            rows[i + 1] = "│" + marker + " " + Pad(choices[i], Math.Max(0, inner - 2)) + "│";
        }
        rows[^1] = "└" + new string('─', Math.Max(0, inner)) + "┘";

        return rows;
    }

    /// <summary>
    /// Lay one overlay row over a content line at a column span. The covered text is escaped, so a
    /// spec containing literal Spectre markup cannot break the overlay.
    /// </summary>
    internal static string Splice(string plain, int left, string overlayPlain, string overlayStyle)
    {
        var padded = plain.Length < left ? plain.PadRight(left) : plain;
        var right = Math.Min(padded.Length, left + overlayPlain.Length);
        return Markup.Escape(padded[..left]) +
               $"[{overlayStyle}]{Markup.Escape(overlayPlain)}[/]" +
               Markup.Escape(padded[right..]);
    }

    private void OpenPicker()
    {
        if (!_editingEnabled) return;
        if (FocusedElement is not { Kind: DetailElementKind.Status } element) return;

        var current = SpecParser.BlockquoteValue(element.SourceText);
        _pickerChoices = StatusChoices(current);
        _pickerIndex = InitialChoiceIndex(_pickerChoices, current);
        _pickerBox = PickerBox(element.ContentLine, _pickerChoices, _detailScroll, DetailViewportHeight());
        _pickerOpen = true;
    }

    private void CommitPicker(ScanResult data)
    {
        _pickerOpen = false;
        if (FocusedElement is not { Kind: DetailElementKind.Status } element) return;
        if (_pickerIndex < 0 || _pickerIndex >= _pickerChoices.Length) return;

        var choice = _pickerChoices[_pickerIndex];
        ApplyEdit(SpecEditor.SetStatus(element.SourceFile, choice, element.SourceText), data, $"status → {choice}");
    }

    /// <summary>Keys while the picker is up. Everything is swallowed so no stray key escapes it.</summary>
    private void HandlePickerKey(ConsoleKey key, ScanResult data)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow: _pickerIndex = NextChoiceIndex(_pickerChoices.Length, _pickerIndex, -1); break;
            case ConsoleKey.DownArrow: _pickerIndex = NextChoiceIndex(_pickerChoices.Length, _pickerIndex, +1); break;
            case ConsoleKey.Home: _pickerIndex = 0; break;
            case ConsoleKey.End: _pickerIndex = Math.Max(0, _pickerChoices.Length - 1); break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar: CommitPicker(data); break;
            case ConsoleKey.Escape: _pickerOpen = false; break;
        }
    }

    /// <summary>A click on a choice picks and commits it; anywhere else dismisses the box.</summary>
    private void HandlePickerClick(int x, int y, ScanResult data)
    {
        if (DetailCell(x, y) is not { } cell || !_pickerBox.Contains(cell.Line, cell.Col))
        {
            _pickerOpen = false;
            return;
        }

        var choice = cell.Line - _pickerBox.Top - 1;                       // skip the top border
        if (choice < 0 || choice >= _pickerChoices.Length) return;         // a border cell: ignore

        _pickerIndex = choice;
        CommitPicker(data);
    }

    // ---- drift attention layer (consumes the DriftState engine) ---------
    //
    // Single engine-contact seam: every read of drift goes through these helpers, so the layer
    // degrades to an inert, blank state if the drift contract is ever absent or turned off.

    private DriftState DriftOf(SpecRow row) => _flagsVisible ? row.Drift : DriftState.None;

    private string? ReasonOf(SpecRow row) => _flagsVisible ? row.DriftReason : null;

    private bool IsFlagged(SpecRow row) => DriftOf(row) != DriftState.None;

    /// <summary>Attention glyph (styled markup + plain, both exactly <see cref="FlagW"/> wide) for a row.</summary>
    internal static (string Markup, string Plain) FlagField(DriftState state) => state switch
    {
        DriftState.Overstated or DriftState.Understated => ("[yellow]⚠[/]", "⚠"),
        DriftState.Idle => ("[grey]◍[/]", "◍"),
        _ => (" ", " "),
    };

    /// <summary>Next flagged row index after <paramref name="current"/> (wrapping), or -1 when none.</summary>
    internal static int NextFlaggedIndex(ImmutableArray<SpecRow> rows, int current)
    {
        if (rows.IsDefaultOrEmpty) return -1;
        var n = rows.Length;
        for (var step = 1; step <= n; step++)
        {
            var idx = ((current + step) % n + n) % n;
            if (rows[idx].Drift != DriftState.None) return idx;
        }
        return -1;
    }

    private void JumpToNextFlagged(ScanResult data)
    {
        if (!_flagsVisible) return;
        var idx = NextFlaggedIndex(data.Rows, _selectedIndex);
        if (idx < 0) return;                               // nothing flagged → no-op
        _selectedIndex = idx;
        EnsureSelectedVisible(data.Rows.Length);
    }

    private int FlaggedCount(ScanResult data) =>
        _flagsVisible && !data.Rows.IsDefaultOrEmpty ? data.Rows.Count(IsFlagged) : 0;

    /// <summary>Trailing drift-reason segment for the detail footer, or empty when not flagged.</summary>
    private string DetailReason(SpecRow row)
    {
        var reason = ReasonOf(row);
        if (string.IsNullOrEmpty(reason)) return "";
        return $"   {FlagField(row.Drift).Markup} [grey]{Markup.Escape(reason)}[/]";
    }

    // ---- mouse text selection -------------------------------------------

    private void ClearSelection()
    {
        _selAnchor = null;
        _selHead = null;
    }

    /// <summary>Normalised (Lo ≤ Hi) selection range in content coords, or null when nothing is selected.</summary>
    private ((int Line, int Col) Lo, (int Line, int Col) Hi)? SelectionRange()
    {
        if (_selAnchor is not { } a || _selHead is not { } h || a == h) return null;
        var aBeforeH = a.Line < h.Line || (a.Line == h.Line && a.Col < h.Col);
        return aBeforeH ? (a, h) : (h, a);
    }

    /// <summary>The [from,to) selected column span for a given content line, given its plain length.</summary>
    private static (int From, int To) LineSpan(((int Line, int Col) Lo, (int Line, int Col) Hi) sel, int line, int len)
    {
        int from, to;
        if (sel.Lo.Line == sel.Hi.Line) { from = sel.Lo.Col; to = sel.Hi.Col; }
        else if (line == sel.Lo.Line) { from = sel.Lo.Col; to = len; }
        else if (line == sel.Hi.Line) { from = 0; to = sel.Hi.Col; }
        else { from = 0; to = len; }
        from = Math.Clamp(from, 0, len);
        to = Math.Clamp(to, 0, len);
        return (from, to);
    }

    /// <summary>Re-render one plain body line with the selected column span shown as a highlight band.</summary>
    private static string HighlightLine(string plain, int from, int to)
    {
        if (to <= from) return Markup.Escape(plain);
        return Markup.Escape(plain[..from]) +
               $"[black on grey62]{Markup.Escape(plain[from..to])}[/]" +
               Markup.Escape(plain[to..]);
    }

    /// <summary>
    /// Map a screen cell to a detail-document cell without clamping the column to the line's length.
    /// Overlay hit-testing needs the true column: clamping would report a click beside a short line
    /// as landing before the overlay that visually covers it.
    /// </summary>
    private (int Line, int Col)? DetailCell(int x, int y)
    {
        const int contentTop = TitleHeight + 1;                           // panel top border
        if (y < contentTop) return null;
        var slice = y - contentTop;
        if (slice >= DetailViewportHeight()) return null;
        var line = Math.Clamp(_detailScroll, 0, DetailMaxScroll()) + slice;
        return (line, x - 2);                                             // panel left border + padding
    }

    /// <summary>Map a screen cell to a content coord (line index + char offset) for the active view, or null off-content.</summary>
    private (int Line, int Col)? MapToContent(int x, int y, ScanResult data)
    {
        if (_view == View.Detail)
        {
            if (DetailCell(x, y) is not { } cell) return null;
            if (cell.Line < 0 || cell.Line >= _detailPlain.Length) return null;
            return (cell.Line, Math.Clamp(cell.Col, 0, _detailPlain[cell.Line].Length));
        }

        if (data.Rows.IsDefaultOrEmpty || y < ListContentTop) return null;
        var rowSlice = y - ListContentTop;
        if (rowSlice >= _visibleRows) return null;
        var rowIdx = _scrollOffset + rowSlice;
        if (rowIdx < 0 || rowIdx >= data.Rows.Length) return null;
        var plain = RowPlain(data.Rows[rowIdx], rowIdx == _selectedIndex, ComputeNameW(data.GitAvailable), _flagsVisible, data.GitAvailable);
        return (rowIdx, Math.Clamp(x, 0, plain.Length));
    }

    /// <summary>Plain visible text of every content line in the active view, indexed by content line.</summary>
    private string[] CurrentPlainLines(ScanResult data)
    {
        if (_view == View.Detail) return _detailPlain;
        if (data.Rows.IsDefaultOrEmpty) return Array.Empty<string>();
        var nameW = ComputeNameW(data.GitAvailable);
        var arr = new string[data.Rows.Length];
        for (var i = 0; i < arr.Length; i++)
            arr[i] = RowPlain(data.Rows[i], i == _selectedIndex, nameW, _flagsVisible, data.GitAvailable);
        return arr;
    }

    private void CopySelection(ScanResult data)
    {
        if (SelectionRange() is not { } sel) return;
        var lines = CurrentPlainLines(data);

        var sb = new StringBuilder();
        for (var idx = sel.Lo.Line; idx <= sel.Hi.Line && idx < lines.Length; idx++)
        {
            var (from, to) = LineSpan(sel, idx, lines[idx].Length);
            sb.Append(lines[idx][from..to].TrimEnd());   // trim right-pad on fixed-width list rows
            if (idx != sel.Hi.Line) sb.Append('\n');
        }

        var text = sb.ToString();
        if (SystemClipboard.TryCopy(text))
            ShowToast($"[green]✔ copied {text.Length} char{(text.Length == 1 ? "" : "s")}[/]");
    }

    private void ClampSelection(ScanResult data)
    {
        var count = data.Rows.IsDefaultOrEmpty ? 0 : data.Rows.Length;
        _selectedIndex = count == 0 ? 0 : Math.Clamp(_selectedIndex, 0, count - 1);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, count - _visibleRows));
    }

    private void ScrollDetail(int delta) =>
        _detailScroll = Math.Clamp(_detailScroll + delta, 0, DetailMaxScroll());

    private int DetailViewportHeight() => Math.Max(1, SafeWindowHeight() - TitleHeight - FooterHeight - 2);
    private int DetailMaxScroll() => Math.Max(0, _detailLines.Length - DetailViewportHeight());

    private void TriggerRefresh()
    {
        try { _refreshSignal.Release(); }
        catch (SemaphoreFullException) { /* already pending */ }
    }

    private static int SafeWindowHeight()
    {
        try { return Math.Max(10, Console.WindowHeight); }
        catch { return 30; }
    }

    private static int SafeWindowWidth()
    {
        try { return Math.Max(40, Console.WindowWidth); }
        catch { return 120; }
    }

    // ---- scan loop -------------------------------------------------------

    private async Task ScanLoopAsync(WatchSettings settings, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(settings.IntervalSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RunOneScan(settings, ct);
                var next = DateTimeOffset.UtcNow + interval;
                Volatile.Write(ref _nextScanAtTicks, next.UtcTicks);

                var remaining = next - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                    await _refreshSignal.WaitAsync(remaining, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task RunOneScan(WatchSettings settings, CancellationToken ct)
    {
        _isScanning = true;
        try
        {
            // An edit that lands mid-scan may already be baked into this result, or may not be —
            // the read happened at an unknown point. Rather than guess, note the epoch before
            // starting and rescan if it moved, so the board converges within one scan.
            var epoch = Volatile.Read(ref _editEpoch);

            var result = await SpecScanner.ScanAsync(settings.ResolvedSpecsDir, DateTimeOffset.Now, settings.ToDriftOptions(), ct);
            Volatile.Write(ref _latest, result);

            if (Volatile.Read(ref _editEpoch) != epoch) TriggerRefresh();
        }
        finally
        {
            _isScanning = false;
        }
    }

    // ---- layout / rendering ---------------------------------------------

    private static Layout BuildLayout() =>
        new Layout("Root").SplitRows(
            new Layout("Title").Size(TitleHeight),
            new Layout("Body"),
            new Layout("Footer").Size(FooterHeight));

    private void UpdateLayout(Layout root, ScanResult r, WatchSettings s, bool mouse, int frame)
    {
        root["Title"].Update(BuildTitle(r, s, frame));
        root["Body"].Update(_view == View.Detail ? BuildDetail() : BuildList(r));
        root["Footer"].Update(BuildFooter(r, s, mouse));
    }

    private Panel BuildTitle(ScanResult r, WatchSettings s, int frame)
    {
        string state;
        if (r.Error is { } err)
            state = $"[red]✖ {Markup.Escape(err)}[/]";
        else if (_isScanning)
            state = $"[yellow]{Spinner[frame % Spinner.Length]} scanning…[/]";
        else if (r.CompletedAt == DateTimeOffset.MinValue)
            state = "[grey]starting…[/]";
        else
        {
            var updated = $"[grey]updated {r.CompletedAt.LocalDateTime:HH:mm:ss}[/]";
            var nextTicks = Volatile.Read(ref _nextScanAtTicks);
            if (nextTicks > 0)
            {
                var secs = (int)Math.Ceiling((new DateTimeOffset(nextTicks, TimeSpan.Zero) - DateTimeOffset.UtcNow).TotalSeconds);
                updated += $" [grey]· next in {Math.Max(0, secs)}s[/]";
            }
            state = updated;
        }

        var where = _view == View.Detail && _detailRow is { } d
            ? $"[grey]{Markup.Escape(d.Folder)}[/]"
            : $"[grey]{Markup.Escape(s.ResolvedSpecsDir)}[/]";

        var branch = r.GitAvailable && r.CurrentBranch is { } b
            ? $"   [grey]on[/] [gold1]◈ {Markup.Escape(b)}[/]"
            : "";

        return new Panel(new Markup($"[bold cyan]spec-watcher[/]  {where}   {state}{branch}"))
            .Expand().Border(BoxBorder.Rounded).BorderColor(Color.Blue);
    }

    // The list is rendered as fixed-width, single-line rows (one screen line per spec) so that
    // nothing wraps — this keeps row height deterministic for viewport math and mouse hit-testing,
    // and lets Progress have a proper, non-collapsing width.
    private IRenderable BuildList(ScanResult r)
    {
        var showCommit = r.GitAvailable;
        var nameW = ComputeNameW(showCommit);
        var lines = new List<string>(_visibleRows + 1) { HeaderLine(nameW, _flagsVisible, showCommit) };

        if (r.Rows.IsDefaultOrEmpty)
        {
            var msg = r.Error is null ? "no specs found" : "scan error";
            lines.Add($"  [grey italic]{Markup.Escape(msg)}[/]");
        }
        else
        {
            var sel = _view == View.List ? SelectionRange() : null;
            var count = r.Rows.Length;
            var start = Math.Clamp(_scrollOffset, 0, Math.Max(0, count - _visibleRows));
            var end = Math.Min(count, start + _visibleRows);
            for (var i = start; i < end; i++)
            {
                if (sel is { } s && i >= s.Lo.Line && i <= s.Hi.Line)
                {
                    var plain = RowPlain(r.Rows[i], i == _selectedIndex, nameW, _flagsVisible, showCommit);
                    var (from, to) = LineSpan(s, i, plain.Length);
                    lines.Add(HighlightLine(plain, from, to));
                }
                else
                {
                    lines.Add(RowLine(r.Rows[i], i == _selectedIndex, nameW, _flagsVisible, showCommit));
                }
            }
        }

        return new Markup(string.Join('\n', lines));
    }

    // Fixed-width name column: fills the row after the other columns and their single-space gaps.
    // Leave a 1-column right margin so an exactly-full line can never wrap. When the attention layer
    // is on, the flag column (glyph + gap) takes an extra FlagW+1 cells.
    private int ComputeNameW(bool showCommit) => NameWidth(SafeWindowWidth(), _flagsVisible, showCommit);

    internal static int NameWidth(int windowWidth, bool flagsVisible, bool showCommit = false)
    {
        var width = Math.Max(60, windowWidth) - 1;
        var flagExtra = flagsVisible ? FlagW + 1 : 0;
        var commitExtra = showCommit ? LastCommitW + 1 : 0;
        return Math.Max(16, width - CaretW - StatusW - ProgressW - FolderW - 4 - flagExtra - commitExtra);
    }

    internal static string HeaderLine(int nameW, bool flagsVisible, bool showCommit)
    {
        var text =
            Pad(" ", CaretW) + " " +
            (flagsVisible ? Pad(" ", FlagW) + " " : "") +
            Pad("NAME", nameW) + " " +
            Pad("STATUS", StatusW) + " " +
            Pad("PROGRESS", ProgressW) + " " +
            (showCommit ? Pad("LAST COMMIT", LastCommitW) + " " : "") +
            Pad("FOLDER", FolderW);
        return $"[grey62]{Markup.Escape(text)}[/]";
    }

    private static string RowLine(SpecRow row, bool selected, int nameW, bool flagsVisible, bool showCommit)
    {
        var caret = CaretMarkup(row, selected);
        var flag = flagsVisible ? FlagField(row.Drift).Markup + " " : "";
        var name = (selected ? "[bold white]" : "[grey85]") + Markup.Escape(Pad(row.Name, nameW)) + "[/]";
        var status = StatusColor(row.Status) + Markup.Escape(Pad(StatusText(row), StatusW)) + "[/]";
        var (progMarkup, progPlain) = ProgressField(row);
        var prog = progPlain.Length < ProgressW ? progMarkup + new string(' ', ProgressW - progPlain.Length) : progMarkup;
        var commit = showCommit ? "[grey]" + Markup.Escape(Pad(LastCommitText(row), LastCommitW)) + "[/] " : "";
        var folder = "[grey42]" + Markup.Escape(Pad(row.Folder, FolderW)) + "[/]";

        var line = $"{caret} {flag}{name} {status} {prog} {commit}{folder}";
        return selected ? $"[on grey23]{line}[/]" : line;
    }

    /// <summary>Plain visible text of a list row, mirroring <see cref="RowLine"/>'s exact column layout.</summary>
    internal static string RowPlain(SpecRow row, bool selected, int nameW, bool flagsVisible, bool showCommit)
    {
        var caret = CaretPlain(row, selected);
        var flag = flagsVisible ? FlagField(row.Drift).Plain + " " : "";
        var (_, progPlain) = ProgressField(row);
        var prog = progPlain.Length < ProgressW ? progPlain + new string(' ', ProgressW - progPlain.Length) : progPlain;
        var commit = showCommit ? Pad(LastCommitText(row), LastCommitW) + " " : "";
        return $"{caret} {flag}{Pad(row.Name, nameW)} {Pad(StatusText(row), StatusW)} {prog} {commit}{Pad(row.Folder, FolderW)}";
    }

    // The caret column is shared by two independent states: the `◈ current` badge (branch match)
    // takes precedence as a distinct accent glyph; a merely-selected row still shows `❯` and is also
    // lit by the row-background highlight in RowLine, so no selection cue is lost.
    private static string CaretMarkup(SpecRow row, bool selected) =>
        row.IsCurrent ? "[gold1]◈[/]" : selected ? "[aqua]❯[/]" : " ";

    private static string CaretPlain(SpecRow row, bool selected) =>
        row.IsCurrent ? "◈" : selected ? "❯" : " ";

    /// <summary>The Last-commit cell text: "<relative time> · <short sha>", or "—" when git has none.</summary>
    private static string LastCommitText(SpecRow row) =>
        row.LastCommit is { } c ? $"{RelativeTime(c.When, DateTimeOffset.Now)} · {c.ShortSha}" : "—";

    /// <summary>Compact relative time: <c>just now / Nm / Nh / Nd / Nw ago</c>. Pure, so it is unit-testable.</summary>
    internal static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var d = now - when;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return $"{(int)(d.TotalDays / 7)}w ago";
    }

    private static string StatusText(SpecRow row) => row.Status switch
    {
        SpecStatus.Planning => "◐ Planning",
        SpecStatus.InProgress => "● In progress",
        SpecStatus.Complete => "✔ Complete",
        _ => string.IsNullOrWhiteSpace(row.StatusRaw) ? "· unknown" : "? " + row.StatusRaw,
    };

    private static string StatusColor(SpecStatus status) => status switch
    {
        SpecStatus.Planning => "[yellow]",
        SpecStatus.InProgress => "[deepskyblue1]",
        SpecStatus.Complete => "[green]",
        _ => "[grey]",
    };

    /// <summary>Progress bar + percentage + counts as both styled markup and its plain visible text.</summary>
    private static (string Markup, string Plain) ProgressField(SpecRow row)
    {
        if (!row.HasTasks || row.Total == 0)
            return ("[grey]—[/]", "—");

        const int barWidth = 10;
        var pct = row.Progress ?? 0;
        var filled = Math.Min(barWidth, (int)Math.Round(pct * barWidth));
        var colour = pct >= 1.0 ? "green" : pct > 0 ? "deepskyblue1" : "grey";
        var barPlain = new string('▓', filled) + new string('░', barWidth - filled);
        var bar = $"[{colour}]{new string('▓', filled)}[/][grey27]{new string('░', barWidth - filled)}[/]";
        var pctText = $"{(int)Math.Round(pct * 100)}%";
        var counts = $"{row.Done}/{row.Total}";
        return ($"{bar} [grey]{pctText} {counts}[/]", $"{barPlain} {pctText} {counts}");
    }

    /// <summary>Truncate (with … ) or right-pad raw text to an exact visible width.</summary>
    private static string Pad(string s, int width)
    {
        if (s.Length == width) return s;
        if (s.Length < width) return s.PadRight(width);
        return width <= 1 ? s[..width] : s[..(width - 1)] + "…";
    }

    private IRenderable BuildDetail()
    {
        var height = DetailViewportHeight();
        var start = Math.Clamp(_detailScroll, 0, DetailMaxScroll());
        var sel = _view == View.Detail ? SelectionRange() : null;

        var focusLine = FocusedElement?.ContentLine ?? -1;

        var visibleLines = new List<string>(height);
        for (var i = 0; i < height && start + i < _detailLines.Length; i++)
        {
            var idx = start + i;
            if (sel is { } s && idx >= s.Lo.Line && idx <= s.Hi.Line)
            {
                var plain = _detailPlain[idx];
                var (from, to) = LineSpan(s, idx, plain.Length);
                visibleLines.Add(HighlightLine(plain, from, to));
            }
            else if (idx == focusLine)
            {
                // Focus is applied here, not baked into _detailLines, so the stored document stays
                // the pure render and focus can move without rebuilding it.
                visibleLines.Add(FocusLine(_detailPlain[idx]));
            }
            else
            {
                visibleLines.Add(_detailLines[idx]);
            }
        }

        if (_pickerOpen) OverlayPicker(visibleLines, start);
        var body = new Markup(string.Join('\n', visibleLines));

        var title = _detailRow is { } d ? $"[bold]{Markup.Escape(d.Name)}[/]" : "spec";
        var more = DetailMaxScroll() > 0 ? $"  [grey]({start + 1}-{Math.Min(_detailLines.Length, start + height)}/{_detailLines.Length})[/]" : "";
        return new Panel(body)
            .Header($" {title}{more} ")
            .Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey35);
    }

    /// <summary>
    /// Draw the picker over the already-rendered lines. Spectre has no z-order, so the box is
    /// spliced into the visible line strings; the styling of what it covers is lost while it is up.
    /// </summary>
    private void OverlayPicker(List<string> visibleLines, int scrollStart)
    {
        var rows = PickerRows(_pickerChoices, _pickerIndex, _pickerBox.Width);

        for (var i = 0; i < rows.Length; i++)
        {
            var slot = _pickerBox.Top + i - scrollStart;                  // viewport row for this box row
            if (slot < 0 || slot >= visibleLines.Count) continue;

            var contentLine = _pickerBox.Top + i;
            var beneath = contentLine < _detailPlain.Length ? _detailPlain[contentLine] : string.Empty;
            var style = i - 1 == _pickerIndex ? "black on deepskyblue1" : "white on grey19";
            visibleLines[slot] = Splice(beneath, _pickerBox.Left, rows[i], style);
        }
    }

    /// <summary>
    /// The detail view's key hints, tailored to what is focused so the next useful key is always
    /// on screen rather than something the user has to know.
    /// </summary>
    internal static string DetailKeyHints(DetailElementKind? focused, bool pickerOpen, bool editing, bool mouse)
    {
        if (pickerOpen)
            return "[bold]↑↓[/] [grey]pick[/]   [bold]Enter[/] [grey]set[/]   [bold]Esc[/] [grey]cancel[/]";

        var hints = new List<string> { "[bold]↑↓[/] [grey]scroll[/]" };

        if (editing)
        {
            hints.Add("[bold]Tab[/] [grey]focus[/]");
            if (focused == DetailElementKind.Checkbox) hints.Add("[bold]Space[/] [grey]tick[/]");
            if (focused == DetailElementKind.Status) hints.Add("[bold]Space[/] [grey]status[/]");
        }

        else
        {
            hints.Add("[grey italic]read-only[/]");
        }

        hints.Add("[bold]Esc[/] [grey]back[/]");
        if (mouse) hints.Add("[grey]wheel · drag-copy[/]");
        hints.Add("[bold]Q[/] [grey]quit[/]");

        return string.Join("   ", hints);
    }

    private Panel BuildFooter(ScanResult r, WatchSettings s, bool mouse)
    {
        var flagged = FlaggedCount(r);

        string left;
        if (_view == View.Detail)
        {
            left = _detailRow is { } d
                ? $"{StatusBadge(d)}   {ProgressCell(d)}{DetailReason(d)}"
                : "[grey]detail[/]";
        }
        else if (r.Rows.IsDefaultOrEmpty)
        {
            left = "[grey]0 specs[/]";
        }
        else
        {
            var planning = r.Rows.Count(x => x.Status == SpecStatus.Planning);
            var progress = r.Rows.Count(x => x.Status == SpecStatus.InProgress);
            var complete = r.Rows.Count(x => x.Status == SpecStatus.Complete);
            left =
                $"[bold]{r.Rows.Length}[/] specs   " +
                $"[yellow]◐ {planning}[/]   [deepskyblue1]● {progress}[/]   [green]✔ {complete}[/]   " +
                (flagged > 0 ? $"[orange1]⚠ {flagged}[/]   " : "") +
                $"[grey]{_selectedIndex + 1}/{r.Rows.Length}[/]";
        }

        string keys;
        if (_toast is { } toast && Environment.TickCount64 < _toastUntilTick)
        {
            keys = toast;                                   // already styled by ShowToast
        }
        else if (_view == View.Detail)
        {
            keys = DetailKeyHints(FocusedElement?.Kind, _pickerOpen, _editingEnabled, mouse);
        }
        else
        {
            var mouseHint = mouse ? "[grey] · wheel · drag-copy[/]" : "";
            var jumpHint = flagged > 0 ? "   [bold]![/] [grey]flagged[/]" : "";
            keys = $"[bold]↑↓[/] [grey]nav[/]   [bold]Enter[/] [grey]open[/]{jumpHint}{mouseHint}   " +
                   $"[bold]R[/] [grey]refresh[/]   [bold]Q[/] [grey]quit[/]";
        }

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().RightAligned());
        grid.AddRow(new Markup(left), new Markup(keys));
        return new Panel(grid).Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey35);
    }

    // ---- static (non-interactive) table ---------------------------------

    internal static IRenderable BuildStaticTable(ScanResult r)
    {
        var table = new Table().Expand().Border(TableBorder.Rounded).BorderColor(Color.Grey35);
        table.AddColumn(new TableColumn("[bold]Name[/]"));
        table.AddColumn(new TableColumn("[bold]Status[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Progress[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Folder[/]"));

        if (r.Rows.IsDefaultOrEmpty)
        {
            table.AddRow("[grey]—[/]", r.Error is null ? "[grey italic]no specs found[/]" : "[red]scan error[/]", "", "");
            return table;
        }

        foreach (var row in r.Rows)
            table.AddRow(
                new Markup($"[bold]{Markup.Escape(row.Name)}[/]"),
                new Markup(StatusBadge(row)),
                new Markup(ProgressCell(row)),
                new Markup($"[grey42]{Markup.Escape(row.Folder)}[/]"));

        return table;
    }

    // ---- shared cell helpers --------------------------------------------

    private static string StatusBadge(SpecRow row) => row.Status switch
    {
        SpecStatus.Planning => "[yellow]◐ Planning[/]",
        SpecStatus.InProgress => "[deepskyblue1]● In progress[/]",
        SpecStatus.Complete => "[green]✔ Complete[/]",
        _ => string.IsNullOrWhiteSpace(row.StatusRaw)
            ? "[grey]· unknown[/]"
            : $"[grey]? {Markup.Escape(row.StatusRaw)}[/]",
    };

    private static string ProgressCell(SpecRow row)
    {
        if (!row.HasTasks || row.Total == 0)
            return "[grey]—[/]";

        const int width = 10;
        var pct = row.Progress ?? 0;
        var filled = Math.Min(width, (int)Math.Round(pct * width));
        var colour = pct >= 1.0 ? "green" : pct > 0 ? "deepskyblue1" : "grey";
        var bar = $"[{colour}]{new string('▓', filled)}[/][grey27]{new string('░', width - filled)}[/]";
        return $"{bar} [grey]{(int)Math.Round(pct * 100),3}% {row.Done}/{row.Total}[/]";
    }

    // ---- detail document build ------------------------------------------

    [GeneratedRegex(@"^(?<indent>\s*)[-*]\s*\[(?<mark>[ xX])\]\s?(?<text>.*)$")]
    private static partial Regex DetailCheckbox();

    /// <summary>
    /// Render a spec's detail document, and alongside it the interactive elements it contains.
    /// Source line indices are counted over every line of the source file, including lines dropped
    /// from the render, so an element always points at the line the user is looking at.
    /// </summary>
    internal static (string[] Styled, string[] Plain, ImmutableArray<DetailElement> Elements)
        BuildDetailLines(SpecRow row, SpecDetail detail)
    {
        var styled = new List<string>();
        var plain = new List<string>();
        var elements = ImmutableArray.CreateBuilder<DetailElement>();

        void Add(string s, string p) { styled.Add(s); plain.Add(p); }
        void AddMd(string raw) { var (s, p) = StyleMarkdownLine(raw); styled.Add(s); plain.Add(p); }

        Add($"[grey]{Markup.Escape(row.FullPath)}[/]", row.FullPath);
        Add(string.Empty, string.Empty);

        if (detail.SpecText is { } spec)
        {
            var specPath = Path.Combine(row.FullPath, "spec.md");
            var lines = SplitLines(spec);
            var haveStatus = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var contentLine = styled.Count;
                AddMd(lines[i]);

                if (haveStatus) continue;
                if (SpecParser.BlockquoteKey(lines[i]) is not { } key) continue;
                if (!key.Equals("Status", StringComparison.OrdinalIgnoreCase)) continue;
                elements.Add(new DetailElement(DetailElementKind.Status, contentLine, specPath, i, lines[i]));
                haveStatus = true;
            }
        }
        else
        {
            Add("[grey italic]no spec.md[/]", "no spec.md");
        }

        Add(string.Empty, string.Empty);
        Add("[grey]──────── Tasks ────────[/]", "──────── Tasks ────────");
        Add(string.Empty, string.Empty);

        if (detail.TasksText is { } tasks)
        {
            var tasksPath = Path.Combine(row.FullPath, "tasks.md");
            var lines = SplitLines(tasks);
            for (var i = 0; i < lines.Length; i++)
            {
                // Skip the "# Spec Tasks" heading noise; keep everything else. The source index
                // still advances, so element coordinates stay aligned with the file.
                if (lines[i].TrimStart().StartsWith("# ")) continue;

                var contentLine = styled.Count;
                AddMd(lines[i]);

                var box = DetailCheckbox().Match(lines[i].TrimEnd());
                if (box.Success)
                    elements.Add(new DetailElement(
                        DetailElementKind.Checkbox, contentLine, tasksPath, i, lines[i],
                        box.Groups["mark"].Value is "x" or "X"));
            }
        }
        else
        {
            Add("[grey italic]no tasks.md[/]", "no tasks.md");
        }

        // Recent commits (git-aware). Omitted entirely when history has not touched this spec.
        if (!row.RecentCommits.IsDefaultOrEmpty)
        {
            Add(string.Empty, string.Empty);
            Add("[grey]──────── Recent commits ────────[/]", "──────── Recent commits ────────");
            Add(string.Empty, string.Empty);

            var now = DateTimeOffset.Now;
            foreach (var c in row.RecentCommits)
            {
                var rel = RelativeTime(c.When, now);
                var plainLine = $"{c.ShortSha}  {rel}  {c.Author} — {c.Subject}";
                var styledLine =
                    $"[gold1]{Markup.Escape(c.ShortSha)}[/]  [grey]{Markup.Escape(rel)}[/]  " +
                    $"[aqua]{Markup.Escape(c.Author)}[/] [grey]—[/] {Markup.Escape(c.Subject)}";
                Add(styledLine, plainLine);
            }
        }

        return (styled.ToArray(), plain.ToArray(), elements.ToImmutable());
    }

    /// <summary>Split source text into lines the way <see cref="SpecEditor"/> counts them.</summary>
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Style one markdown line, returning both the styled markup and its plain visible text.</summary>
    private static (string Styled, string Plain) StyleMarkdownLine(string raw)
    {
        var line = raw.TrimEnd();
        if (line.Length == 0) return (string.Empty, string.Empty);

        var checkbox = DetailCheckbox().Match(line);
        if (checkbox.Success)
        {
            var indent = checkbox.Groups["indent"].Value;
            var done = checkbox.Groups["mark"].Value is "x" or "X";
            var glyph = done ? "✔" : "○";
            var box = done ? "[green]✔[/]" : "[grey]○[/]";
            var raw2 = checkbox.Groups["text"].Value;
            var text = Markup.Escape(raw2);
            var styled = done ? $"[grey]{text}[/]" : text;
            return ($"{indent}{box} {styled}", $"{indent}{glyph} {raw2}");
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("### ")) return ($"[bold]{Markup.Escape(trimmed[4..])}[/]", trimmed[4..]);
        if (trimmed.StartsWith("## ")) return ($"[bold aqua]{Markup.Escape(trimmed[3..])}[/]", trimmed[3..]);
        if (trimmed.StartsWith("# ")) return ($"[bold underline aqua]{Markup.Escape(trimmed[2..])}[/]", trimmed[2..]);
        if (trimmed.StartsWith('>')) { var t = trimmed[1..].TrimStart(); return ($"[grey]{Markup.Escape(t)}[/]", t); }
        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
        {
            var indent = line[..(line.Length - trimmed.Length)];
            return ($"{indent}[grey]•[/] {Markup.Escape(trimmed[2..])}", $"{indent}• {trimmed[2..]}");
        }
        return (Markup.Escape(line), line);
    }
}
