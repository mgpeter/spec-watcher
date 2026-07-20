# Technical Specification

This is the technical specification for the spec detailed in
`docs/specs/2026-07-20-drift-attention-flags/spec.md`.

## Approach

The attention layer is a thin render + navigation addition to the existing list view. All changes are
confined to `SpecWatcher.CLI/WatchCommand.cs`, plus one new startup option on `WatchSettings.cs` and a
consumed (not defined) field on `SpecRow`. No vertical chrome changes, so viewport math and mouse
hit-testing are untouched.

### Consumed contract (owned by the sibling engine spec)

`2026-07-20-spec-drift-detection` adds to `SpecRow` in `Models.cs`:

```csharp
public enum DriftState { None, Drift, Idle }
// on SpecRow:
DriftState Drift          // None when no contradiction / not enough info
string? DriftReason       // short human reason, e.g. "status says Complete, 4/9 done"
```

This spec **reads** these; it does not add or compute them. To stay decoupled and shippable before the
engine lands, all reads go through one null-safe seam in `WatchCommand.cs`:

```csharp
// Single point of contact with the engine. Until SpecRow carries Drift, this returns None,
// which makes the whole attention layer render inert (blank column, 0 count, ! no-ops).
private static DriftState DriftOf(SpecRow row) => row.Drift;       // change to `=> DriftState.None;` if not yet present
private static string? ReasonOf(SpecRow row) => row.DriftReason;
private static bool IsFlagged(SpecRow row) => DriftOf(row) != DriftState.None;
```

## Files & methods to touch (all in `WatchCommand.cs` unless noted)

### 1. Column constant + Name width — `BuildList`
Add a flag column beside the existing width constants:

```csharp
private const int FlagW = 1;   // one visible glyph cell, placed after the caret
```

In `BuildList`, the Name width currently subtracts `CaretW … FolderW` and 4 gap spaces. Add `FlagW`
and one more gap:

```csharp
var nameW = Math.Max(16, width - CaretW - FlagW - StatusW - ProgressW - FolderW - 5);
```

### 2. Header cell — `HeaderLine`
Insert a blank `FlagW`-wide cell after the caret cell so labels stay aligned:

```csharp
Pad(" ", CaretW) + " " + Pad(" ", FlagW) + " " + Pad("NAME", nameW) + " " + ...
```

### 3. Row glyph — new `FlagField` + `RowLine`
```csharp
// glyph carries meaning; colour is a secondary channel (colour-blind safe).
private static string FlagField(SpecRow row) => DriftOf(row) switch
{
    DriftState.Drift => "[orange1]⚠[/]",
    DriftState.Idle  => "[grey58]◍[/]",
    _                => " ",              // exactly FlagW visible cells
};
```

`RowLine` composes the flag right after the caret (nothing else moves):

```csharp
var flag = _flagsVisible ? FlagField(row) : " ";
var line = $"{caret} {flag} {name} {status} {prog} {folder}";
```

The `⚠`/`◍` are single-width glyphs; keep the `[on grey23]` selected-row background wrapping the whole
line so amber/dim-grey stay legible on the highlight.

### 4. `!`-jump — new `JumpToNextFlagged` + key binding
```csharp
private void JumpToNextFlagged(ScanResult data, int dir = +1)
{
    if (!_flagsVisible || data.Rows.IsDefaultOrEmpty) return;
    var count = data.Rows.Length;
    for (var step = 1; step <= count; step++)
    {
        var i = (((_selectedIndex + dir * step) % count) + count) % count;   // wraps both directions
        if (IsFlagged(data.Rows[i]))
        {
            _selectedIndex = i;
            EnsureSelectedVisible(count);   // reuse existing viewport follow
            return;
        }
    }
    // nothing flagged → no-op; footer already communicates "0 flags"
}
```

Wire it in `HandleListKey`. **Input note:** `HandleListKey` switches on `ConsoleKey`, and `!` is
Shift+`1`. The current `InputEvent` (see `Input/`) surfaces a `ConsoleKey` but not the shift modifier
or raw char. Two acceptable bindings, in order of preference:
- **Preferred:** extend `InputEvent` to carry the `char`/`ConsoleModifiers` already available from
  `ReadConsoleInput`, and match `!`.
- **Fallback (zero input-layer change):** bind an unmodified key such as `N` ("next flag") via
  `case ConsoleKey.D1` is ambiguous, so use `ConsoleKey.N`. Document whichever ships; the spec's intent
  is a single-key hop between flagged rows.

### 5. Footer — `BuildFooter`
In the non-empty **list** branch, after the status counts, append the flagged tally and hint:

```csharp
var flagged = r.Rows.Count(IsFlagged);
if (_flagsVisible && flagged > 0)
    left += $"   [orange1]⚠ {flagged}[/]";
// in the keys string (list view), when flagged > 0:
//   ... + "[bold]![/] [grey]next flag[/]   " ...
```

In the **detail** branch, when the open row is flagged, show its reason:

```csharp
if (_detailRow is { } d && ReasonOf(d) is { Length: > 0 } why)
    left += $"   [orange1]⚠ {Markup.Escape(why)}[/]";
```

### 6. Toggle / accessibility posture
- **State:** add UI-thread field `private bool _flagsVisible = true;` (owned by the UI thread, like
  `_view`). Initialised from settings.
- **Startup option:** add `--no-flags` to `WatchSettings.cs` (bool, default false → flags on). When
  set, `_flagsVisible` starts false. (Spec-only; the engine/CLI wiring is trivial.)
- **Runtime toggle:** in `HandleListKey`, a key (e.g. `F`) flips `_flagsVisible`. When off, glyphs
  render as blank, the footer count/hint disappear, and `!`/jump no-op — the board is byte-identical to
  today.
- **No motion:** flags are static glyphs with no animation, so there is nothing to disable for
  motion-sensitive users; the accessibility lever here is the full on/off toggle plus glyph-first
  encoding (never colour alone).

### 7. Graceful degradation
If `SpecRow` has no `Drift`/`DriftReason` yet, set `DriftOf`/`ReasonOf` to return `None`/`null`
(one-line change at the seam). Result: `FlagField` yields blank for every row, `IsFlagged` is always
false, `JumpToNextFlagged` no-ops, the footer count is 0 and hidden. The layer is inert and the app
behaves exactly as it does today — no exceptions, no layout shift.

## Unaffected / verified-unchanged

- **Mouse hit-testing** (`ClickRow`) and viewport math depend on `ListContentTop` / `ListHeaderHeight`
  (vertical chrome), which this spec does not change — rows keep one screen line each.
- **Non-interactive path** (`BuildStaticTable`) is out of scope; the static table gains no flag column.
- `Description` / `Created` remain unshown.
