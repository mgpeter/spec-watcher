# Technical Specification

This is the technical specification for the spec detailed in
`docs/specs/2026-08-24-inline-spec-editing/spec.md`.

## Approach

Three additions, layered so each is testable on its own:

1. **`SpecEditor`** (new file, `SpecWatcher.CLI/SpecEditor.cs`) — a pure, static, file-level write API
   with no UI knowledge. Surgical line replacement with optimistic concurrency: the caller passes the
   line it believes is there, and the write is refused if disk disagrees.
2. **Detail element model** — `BuildDetailLines` additionally emits an ordered
   `ImmutableArray<DetailElement>` describing which content lines are interactive, what kind they are,
   and which source-file line they came from. This is the only bridge between rendered lines and file
   coordinates.
3. **Focus + overlay in `WatchCommand`** — a focused-element index, focus-aware rendering, an in-`Live`
   dropdown composited into the detail panel's visible lines, and click hit-testing for both.

No new packages. Spectre's `SelectionPrompt` is deliberately not used: it takes over the console and
blocks the render loop, which is incompatible with the existing `Live` + background-scan + custom
`IInputSource` architecture (see the "In-Live overlay" decision in `spec.md`).

## 1. `SpecEditor` (new file)

```csharp
namespace SpecWatcher;

/// <summary>Outcome of an attempted write. Never throws for expected failures.</summary>
public enum EditResult { Ok, NotFound, Conflict, Unwritable, NoStatusLine }

public static partial class SpecEditor
{
    /// <summary>Flip the mark of the checkbox on <paramref name="lineIndex"/> of tasks.md.</summary>
    public static EditResult ToggleCheckbox(string tasksPath, int lineIndex, string expectedLine, bool toChecked);

    /// <summary>Set the `> Status:` value in spec.md, preserving the line's prefix and indentation.</summary>
    public static EditResult SetStatus(string specPath, string newValue, string? expectedLine);
}
```

Mechanics, shared by both:

- Read the file as raw text (`File.ReadAllText`), detect the BOM and the dominant newline (`\r\n` vs
  `\n`) up front.
- Locate the target line's character span by scanning for `\n`; **replace only that span**. Everything
  else, including trailing whitespace, mixed line endings elsewhere and the final newline, is left
  byte-identical.
- Compare the current line against `expectedLine` (ordinal, after `TrimEnd`) before writing. A mismatch
  returns `Conflict` and writes nothing — this is how an external edit between render and keypress is
  caught.
- Write with `File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8BOM: hadBom))`, so
  the UTF-8 posture from the v1.0.0 console fix is preserved.
- `IOException` / `UnauthorizedAccessException` → `Unwritable`. Missing file → `NotFound`.

Checkbox rewriting reuses the existing mark regex shape (`^\s*[-*]\s*\[(?<mark>[ xX])\]`): only the
character inside the brackets changes, so indentation, bullet character and text are untouched.

Status rewriting finds the first blockquote line whose key is `Status` using the same rule as
`SpecParser.FindBlockquoteField`, then rebuilds it as `{prefix}Status: {newValue}` where `prefix` is
the original text up to and including `> `. When no such line exists, insert one immediately after the
last consecutive `>` line of the header block; when there is no blockquote block at all, return
`NoStatusLine` and let the UI report it rather than guessing at the document shape.

## 2. Detail element model

Added to `Models.cs`:

```csharp
public enum DetailElementKind { Checkbox, Status }

/// <param name="ContentLine">Index into the detail line arrays (what the user sees).</param>
/// <param name="SourceFile">Absolute path of the file the line came from.</param>
/// <param name="SourceLine">0-based line index within that file.</param>
/// <param name="SourceText">The raw line as read, used as the optimistic-concurrency token.</param>
/// <param name="IsChecked">Current mark, for Checkbox elements only.</param>
public sealed record DetailElement(
    DetailElementKind Kind,
    int ContentLine,
    string SourceFile,
    int SourceLine,
    string SourceText,
    bool IsChecked = false);
```

`BuildDetailLines` changes signature to
`(string[] Styled, string[] Plain, ImmutableArray<DetailElement> Elements)`:

- While iterating `spec.md`, count the source line index **including** lines that are skipped from the
  render, and emit a `Status` element for the `> Status:` line.
- While iterating `tasks.md`, do the same and emit a `Checkbox` element for every line matching
  `DetailCheckbox()`. The existing `# Spec Tasks` heading skip must not desynchronise the source
  counter — increment it before the `continue`.
- Elements are appended in content order, so the array is already sorted by `ContentLine`.

`WatchCommand` stores it in `_detailElements` alongside `_detailLines` / `_detailPlain`, and resets
`_focusIndex = -1` (nothing focused) whenever a detail document is (re)built.

## 3. Focus, keys and rendering

New state on `WatchCommand`:

```csharp
private ImmutableArray<DetailElement> _detailElements = ImmutableArray<DetailElement>.Empty;
private int _focusIndex = -1;              // -1 = nothing focused
private bool _pickerOpen;
private int _pickerIndex;
private string[] _pickerChoices = Array.Empty<string>();
private OverlayBox _pickerBox;             // content-coord rect, computed on open
private string? _editToast;                // reuses the existing toast slot/timer shape
private long _editToastUntilTick;
```

### Keys (detail view, editing enabled)

| Key | Behaviour |
| --- | --- |
| `Tab` / `Shift+Tab` | Move `_focusIndex` to the next/previous element, wrapping; scroll the viewport so its `ContentLine` is visible |
| `Space` / `Enter` | Checkbox focused → toggle. Status focused → open the picker |
| `Esc` | Picker open → close without writing. Otherwise → clear focus if set, else leave the detail view (existing behaviour) |
| `↑` `↓` `PgUp` `PgDn` `Home` `End` | Unchanged scrolling, except while the picker is open, where `↑↓` move `_pickerIndex` |

`Esc` gains one extra hop before leaving the view; this is the only change to existing key semantics.
`Space` in the **list** view already opens the detail view and is untouched.

### Rendering

`_detailLines` stays the pure, unfocused styled document. Focus is applied in `BuildDetail` at draw
time, exactly like the existing selection highlight, by re-styling the focused element's line:

```csharp
// focused element line: caret + reverse-video body, so focus reads without colour alone
visibleLines.Add($"[black on grey62]{Markup.Escape(_detailPlain[idx])}[/]");
```

The picker is composited over the visible lines rather than layered by Spectre (which has no z-order).
For each overlay row, take the plain text of the covered line, splice at the overlay's column span, and
rebuild the markup as `escape(left) + overlayRowMarkup + escape(right)`. Styling of the covered region
is intentionally lost while the overlay is up.

```csharp
private readonly record struct OverlayBox(int Top, int Left, int Width, int Height)
{
    public bool Contains(int line, int col) =>
        line >= Top && line < Top + Height && col >= Left && col < Left + Width;
}
```

`_pickerBox` is computed once when the picker opens (anchored just below the status line's screen row,
clamped into the viewport) and is read by both the renderer and the hit-tester, so they cannot drift.

### Mouse

- `MouseDown` while `_pickerOpen`: inside `_pickerBox` → set `_pickerIndex` from the row; outside →
  close the picker. Text selection is suppressed for the whole gesture.
- `MouseUp` that is a plain click (existing click-vs-drag rule) in the detail view: map the screen cell
  to a content coord with the existing `MapToContent`, then look up an element whose `ContentLine`
  matches. A click anywhere on a checkbox line focuses it; a click within the first two visible columns
  (the glyph) also toggles it.
- Drag-to-copy is unchanged whenever the picker is closed.

### Footer

The detail footer gains `Tab focus · Space tick` hints when editing is enabled, `[grey]read-only[/]`
when it is not, and reuses the existing transient-toast slot for edit outcomes
(`✔ ticked`, `⚠ file changed on disk`, `⚠ read-only file`).

## 4. Applying an edit

```
toggle/set → SpecEditor.<Write>()
           → EditResult.Ok  → SpecParser.Parse(row.FullPath)   (re-parse just this folder)
                            → patch _latest.Rows with the new row (ImmutableArray.SetItem)
                            → rebuild the detail document, preserving _focusIndex and _detailScroll
                            → Interlocked.Increment(ref _editEpoch)
           → anything else  → footer toast, no state change; on Conflict also TriggerRefresh()
```

**Scanner race.** The UI thread now publishes to `_latest` as well as the scan loop, so a scan that
read the files *before* an edit could publish *after* it and resurrect the stale row. Guard with an
edit epoch: the scan loop captures `Volatile.Read(ref _editEpoch)` before it starts and compares after
it finishes; if the epoch moved, it publishes as normal and then immediately re-triggers itself, so
the board converges within one scan instead of one interval. The epoch is a plain `int` touched only
by `Interlocked` / `Volatile`, keeping the existing lock-free model.

Re-parsing one folder is a handful of small file reads on the UI thread; at detail-view scale this is
below a frame and does not warrant moving off-thread. Git enrichment is **not** re-run for the patched
row — the previous row's `LastCommit` / `RecentCommits` are carried over unchanged.

## 5. `--read-only`

On `WatchSettings`:

```csharp
[CommandOption("--read-only")]
[Description("Disable all writes; the board becomes a pure viewer.")]
[DefaultValue(false)]
public bool ReadOnly { get; init; }

/// <summary>Editing is on unless suppressed, and never in a headless run.</summary>
public bool EditingEnabled => !ReadOnly && !WantsHeadless;
```

When `EditingEnabled` is false: `Tab` does nothing, `Space`/`Enter` on an element does nothing, the
focus marker never draws, the picker cannot open, and the footer shows `read-only`. `SpecEditor` is
never reached, so the headless/JSON/gate paths are structurally incapable of writing.

## Testing notes

- `SpecEditor` is the bulk of the value and is pure file I/O: test against temp-directory fixtures for
  CRLF vs LF, BOM vs no BOM, no trailing newline, indented and `*`-bulleted checkboxes, an absent
  `Status:` line, a conflicting line, and a read-only file.
- `BuildDetailLines` element extraction is testable through `InternalsVisibleTo` (already configured):
  assert `ContentLine`/`SourceLine` alignment, especially across the skipped `# Spec Tasks` heading.
- Focus movement, `OverlayBox.Contains` and the click-to-element lookup are pure functions of state and
  should be tested without a console.
