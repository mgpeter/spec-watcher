using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using SpecWatcher.Input;

namespace SpecWatcher;

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
    private int _detailScroll;

    // Mouse text selection (Herdr-style): the app keeps the mouse and draws its own highlight, so
    // the constant live-refresh can't erase it. Coordinates are content coords for the active view
    // (Line = row/detail-line index, Col = char offset into that line's plain text).
    private (int Line, int Col)? _selAnchor;   // set on mouse-down
    private (int Line, int Col)? _selHead;     // updated on drag
    private int _downScreenY;                  // screen Y of the last mouse-down (for click dispatch)
    private string? _copyToast;                // transient "copied N chars" footer message
    private long _copyToastUntilTick;

    public override async Task<int> ExecuteAsync(CommandContext context, WatchSettings settings)
    {
        var caps = AnsiConsole.Profile.Capabilities;
        _flagsVisible = !settings.NoFlags;

        // Non-interactive (piped / redirected / dumb terminal): one static table, then exit.
        if (!caps.Ansi || !caps.Interactive)
        {
            if (AnsiConsole.Profile.Width < 100)
                AnsiConsole.Profile.Width = 120;

            var result = await SpecScanner.ScanAsync(settings.ResolvedSpecsDir, DateTimeOffset.Now, settings.ToDriftOptions());
            if (result.Error is { } err) AnsiConsole.MarkupLine($"[red]{Markup.Escape(err)}[/]");
            AnsiConsole.Write(BuildStaticTable(result));
            return result.Error is null ? 0 : 1;
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

    private void HandleInput(InputEvent evt, ScanResult data)
    {
        switch (evt.Kind)
        {
            case InputKind.Key:
                ClearSelection();
                // Attention layer: `!` jumps between flagged rows, `F` toggles the whole layer.
                if (evt.KeyChar == '!') { if (_view == View.List) JumpToNextFlagged(data); break; }
                if (evt.Key == ConsoleKey.F) { _flagsVisible = !_flagsVisible; break; }
                if (_view == View.List) HandleListKey(evt.Key, data);
                else HandleDetailKey(evt.Key);
                break;

            case InputKind.MouseWheel:
                ClearSelection();
                if (_view == View.List) MoveSelection(-evt.WheelNotches, data);   // wheel up = earlier rows
                else ScrollDetail(-evt.WheelNotches * 3);
                break;

            case InputKind.MouseDown:
                _downScreenY = evt.Row;
                var down = MapToContent(evt.Column, evt.Row, data);
                _selAnchor = down;
                _selHead = down;   // anchor == head → nothing highlighted yet
                break;

            case InputKind.MouseDrag:
                if (_selAnchor is not null && MapToContent(evt.Column, evt.Row, data) is { } head)
                    _selHead = head;
                break;

            case InputKind.MouseUp:
                if (SelectionRange() is not null)
                    CopySelection(data);                       // a real drag → auto-copy, keep highlight
                else
                {
                    if (_view == View.List) ClickRow(_downScreenY, data);   // a plain click → open row
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

    private void HandleDetailKey(ConsoleKey key)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow: ScrollDetail(-1); break;
            case ConsoleKey.DownArrow: ScrollDetail(+1); break;
            case ConsoleKey.PageUp: ScrollDetail(-DetailViewportHeight()); break;
            case ConsoleKey.PageDown: ScrollDetail(+DetailViewportHeight()); break;
            case ConsoleKey.Home: _detailScroll = 0; break;
            case ConsoleKey.End: _detailScroll = DetailMaxScroll(); break;
            case ConsoleKey.Escape or ConsoleKey.Backspace or ConsoleKey.LeftArrow: _view = View.List; break;
            case ConsoleKey.R: TriggerRefresh(); break;
            case ConsoleKey.Q: _running = false; break;
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
        (_detailLines, _detailPlain) = BuildDetailLines(row, SpecParser.LoadDetail(row.FullPath));
        _detailScroll = 0;
        _view = View.Detail;
        ClearSelection();
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

    /// <summary>Map a screen cell to a content coord (line index + char offset) for the active view, or null off-content.</summary>
    private (int Line, int Col)? MapToContent(int x, int y, ScanResult data)
    {
        if (_view == View.Detail)
        {
            const int contentTop = TitleHeight + 1;                       // panel top border
            if (y < contentTop) return null;
            var slice = y - contentTop;
            if (slice >= DetailViewportHeight()) return null;
            var start = Math.Clamp(_detailScroll, 0, DetailMaxScroll());
            var idx = start + slice;
            if (idx < 0 || idx >= _detailPlain.Length) return null;
            var col = Math.Clamp(x - 2, 0, _detailPlain[idx].Length);      // panel left border + padding
            return (idx, col);
        }

        if (data.Rows.IsDefaultOrEmpty || y < ListContentTop) return null;
        var rowSlice = y - ListContentTop;
        if (rowSlice >= _visibleRows) return null;
        var rowIdx = _scrollOffset + rowSlice;
        if (rowIdx < 0 || rowIdx >= data.Rows.Length) return null;
        var plain = RowPlain(data.Rows[rowIdx], rowIdx == _selectedIndex, ComputeNameW(), _flagsVisible);
        return (rowIdx, Math.Clamp(x, 0, plain.Length));
    }

    /// <summary>Plain visible text of every content line in the active view, indexed by content line.</summary>
    private string[] CurrentPlainLines(ScanResult data)
    {
        if (_view == View.Detail) return _detailPlain;
        if (data.Rows.IsDefaultOrEmpty) return Array.Empty<string>();
        var nameW = ComputeNameW();
        var arr = new string[data.Rows.Length];
        for (var i = 0; i < arr.Length; i++)
            arr[i] = RowPlain(data.Rows[i], i == _selectedIndex, nameW, _flagsVisible);
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
        {
            _copyToast = $"copied {text.Length} char{(text.Length == 1 ? "" : "s")}";
            _copyToastUntilTick = Environment.TickCount64 + 1200;
        }
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
            var result = await SpecScanner.ScanAsync(settings.ResolvedSpecsDir, DateTimeOffset.Now, settings.ToDriftOptions(), ct);
            Volatile.Write(ref _latest, result);
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

        return new Panel(new Markup($"[bold cyan]spec-watcher[/]  {where}   {state}"))
            .Expand().Border(BoxBorder.Rounded).BorderColor(Color.Blue);
    }

    // The list is rendered as fixed-width, single-line rows (one screen line per spec) so that
    // nothing wraps — this keeps row height deterministic for viewport math and mouse hit-testing,
    // and lets Progress have a proper, non-collapsing width.
    private IRenderable BuildList(ScanResult r)
    {
        var nameW = ComputeNameW();
        var lines = new List<string>(_visibleRows + 1) { HeaderLine(nameW, _flagsVisible) };

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
                    var plain = RowPlain(r.Rows[i], i == _selectedIndex, nameW, _flagsVisible);
                    var (from, to) = LineSpan(s, i, plain.Length);
                    lines.Add(HighlightLine(plain, from, to));
                }
                else
                {
                    lines.Add(RowLine(r.Rows[i], i == _selectedIndex, nameW, _flagsVisible));
                }
            }
        }

        return new Markup(string.Join('\n', lines));
    }

    // Fixed-width name column: fills the row after the other columns and their single-space gaps.
    // Leave a 1-column right margin so an exactly-full line can never wrap. When the attention layer
    // is on, the flag column (glyph + gap) takes an extra FlagW+1 cells.
    private int ComputeNameW() => NameWidth(SafeWindowWidth(), _flagsVisible);

    internal static int NameWidth(int windowWidth, bool flagsVisible)
    {
        var width = Math.Max(60, windowWidth) - 1;
        var flagExtra = flagsVisible ? FlagW + 1 : 0;
        return Math.Max(16, width - CaretW - StatusW - ProgressW - FolderW - 4 - flagExtra);
    }

    private static string HeaderLine(int nameW, bool flagsVisible)
    {
        var text =
            Pad(" ", CaretW) + " " +
            (flagsVisible ? Pad(" ", FlagW) + " " : "") +
            Pad("NAME", nameW) + " " +
            Pad("STATUS", StatusW) + " " +
            Pad("PROGRESS", ProgressW) + " " +
            Pad("FOLDER", FolderW);
        return $"[grey62]{Markup.Escape(text)}[/]";
    }

    private static string RowLine(SpecRow row, bool selected, int nameW, bool flagsVisible)
    {
        var caret = selected ? "[aqua]❯[/]" : " ";
        var flag = flagsVisible ? FlagField(row.Drift).Markup + " " : "";
        var name = (selected ? "[bold white]" : "[grey85]") + Markup.Escape(Pad(row.Name, nameW)) + "[/]";
        var status = StatusColor(row.Status) + Markup.Escape(Pad(StatusText(row), StatusW)) + "[/]";
        var (progMarkup, progPlain) = ProgressField(row);
        var prog = progPlain.Length < ProgressW ? progMarkup + new string(' ', ProgressW - progPlain.Length) : progMarkup;
        var folder = "[grey42]" + Markup.Escape(Pad(row.Folder, FolderW)) + "[/]";

        var line = $"{caret} {flag}{name} {status} {prog} {folder}";
        return selected ? $"[on grey23]{line}[/]" : line;
    }

    /// <summary>Plain visible text of a list row, mirroring <see cref="RowLine"/>'s exact column layout.</summary>
    private static string RowPlain(SpecRow row, bool selected, int nameW, bool flagsVisible)
    {
        var caret = selected ? "❯" : " ";
        var flag = flagsVisible ? FlagField(row.Drift).Plain + " " : "";
        var (_, progPlain) = ProgressField(row);
        var prog = progPlain.Length < ProgressW ? progPlain + new string(' ', ProgressW - progPlain.Length) : progPlain;
        return $"{caret} {flag}{Pad(row.Name, nameW)} {Pad(StatusText(row), StatusW)} {prog} {Pad(row.Folder, FolderW)}";
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
            else
            {
                visibleLines.Add(_detailLines[idx]);
            }
        }
        var body = new Markup(string.Join('\n', visibleLines));

        var title = _detailRow is { } d ? $"[bold]{Markup.Escape(d.Name)}[/]" : "spec";
        var more = DetailMaxScroll() > 0 ? $"  [grey]({start + 1}-{Math.Min(_detailLines.Length, start + height)}/{_detailLines.Length})[/]" : "";
        return new Panel(body)
            .Header($" {title}{more} ")
            .Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey35);
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
        if (mouse && _copyToast is { } toast && Environment.TickCount64 < _copyToastUntilTick)
        {
            keys = $"[green]✔ {Markup.Escape(toast)}[/]";
        }
        else
        {
            var mouseHint = mouse ? "[grey] · wheel · drag-copy[/]" : "";
            var jumpHint = _view == View.List && flagged > 0 ? "   [bold]![/] [grey]flagged[/]" : "";
            keys = _view == View.Detail
                ? $"[bold]↑↓[/] [grey]scroll[/]   [bold]Esc[/] [grey]back[/]{mouseHint}   [bold]Q[/] [grey]quit[/]"
                : $"[bold]↑↓[/] [grey]nav[/]   [bold]Enter[/] [grey]open[/]{jumpHint}{mouseHint}   [bold]R[/] [grey]refresh[/]   [bold]Q[/] [grey]quit[/]";
        }

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().RightAligned());
        grid.AddRow(new Markup(left), new Markup(keys));
        return new Panel(grid).Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey35);
    }

    // ---- static (non-interactive) table ---------------------------------

    private static IRenderable BuildStaticTable(ScanResult r)
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

    private static (string[] Styled, string[] Plain) BuildDetailLines(SpecRow row, SpecDetail detail)
    {
        var styled = new List<string>();
        var plain = new List<string>();

        void Add(string s, string p) { styled.Add(s); plain.Add(p); }
        void AddMd(string raw) { var (s, p) = StyleMarkdownLine(raw); styled.Add(s); plain.Add(p); }

        Add($"[grey]{Markup.Escape(row.FullPath)}[/]", row.FullPath);
        Add(string.Empty, string.Empty);

        if (detail.SpecText is { } spec)
        {
            foreach (var raw in spec.Replace("\r\n", "\n").Split('\n'))
                AddMd(raw);
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
            foreach (var raw in tasks.Replace("\r\n", "\n").Split('\n'))
            {
                // Skip the "# Spec Tasks" heading noise; keep everything else.
                if (raw.TrimStart().StartsWith("# ")) continue;
                AddMd(raw);
            }
        }
        else
        {
            Add("[grey italic]no tasks.md[/]", "no tasks.md");
        }

        return (styled.ToArray(), plain.ToArray());
    }

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
