# Spec Tasks

## Tasks

- [x] 1. `SpecEditor`: surgical, conflict-safe writes
  - [x] 1.1 Write tests for `ToggleCheckbox` over temp fixtures: LF and CRLF, BOM and no BOM, no
        trailing newline, indented and `*`-bulleted boxes, and a line that no longer matches
        `expectedLine` (must return `Conflict` and leave the file byte-identical)
  - [x] 1.2 Write tests for `SetStatus`: existing `> Status:` line rewritten in place, freeform value
        preserved verbatim, missing line inserted after the header block, no blockquote at all →
        `NoStatusLine`
  - [x] 1.3 Add `SpecWatcher.CLI/SpecEditor.cs` with `EditResult`, the raw-text line-span replacement,
        BOM/newline detection, and the optimistic-concurrency check
  - [x] 1.4 Confirm unwritable and missing files return `Unwritable` / `NotFound` rather than throwing
  - [x] 1.5 Verify all tests pass

- [x] 2. Detail element model
  - [x] 2.1 Write tests for `BuildDetailLines` element extraction: one `Status` element from spec.md,
        one `Checkbox` per task, and `ContentLine`/`SourceLine` alignment across the skipped
        `# Spec Tasks` heading and the injected separator lines
  - [x] 2.2 Add `DetailElementKind` and `DetailElement` to `Models.cs`
  - [x] 2.3 Extend `BuildDetailLines` to return `ImmutableArray<DetailElement>`, tracking source line
        indices independently of rendered line indices
  - [x] 2.4 Store `_detailElements` in `OpenDetail` and reset `_focusIndex` on every rebuild
  - [x] 2.5 Verify tests pass and the detail view renders exactly as before

- [x] 3. Focus cursor (keyboard + mouse)
  - [x] 3.1 Write tests for focus movement: Tab/Shift+Tab wrapping, no-elements no-op, viewport
        follows the focused `ContentLine`, and click-to-element lookup by content coord
  - [x] 3.2 Add `_focusIndex`, the Tab/Shift+Tab bindings, and the `Esc`-clears-focus hop in
        `HandleDetailKey`
  - [x] 3.3 Apply the focus style at draw time in `BuildDetail`, leaving `_detailLines` pure
  - [x] 3.4 Route plain clicks in the detail view to focus an element, keeping drag-to-copy intact
  - [x] 3.5 Verify tests pass

- [x] 4. Checkbox toggling end to end
  - [x] 4.1 Write tests for the apply path: `Ok` patches the row via `SetItem` and rebuilds the detail
        document with focus and scroll preserved; `Conflict` leaves state untouched and triggers a
        refresh
  - [x] 4.2 Bind Space/Enter on a focused checkbox, and a click on the glyph columns, to
        `SpecEditor.ToggleCheckbox`
  - [x] 4.3 Add the `_editEpoch` guard so a scan started before an edit re-triggers instead of
        publishing a stale row as final
  - [x] 4.4 Surface outcomes through the existing transient footer toast
  - [x] 4.5 Verify tests pass and ticking updates the badge, progress bar and drift flag immediately

- [x] 5. Status dropdown overlay
  - [x] 5.1 Write tests for `OverlayBox.Contains`, choice-list construction (three known statuses plus
        the existing freeform value only when it normalises to `Unknown`), and picker index movement
  - [x] 5.2 Add the picker state, open/close handling, and key routing that captures `↑↓`/Enter/Esc
        while open
  - [x] 5.3 Composite the picker into `BuildDetail`'s visible lines at `_pickerBox`, anchored below the
        status line and clamped into the viewport
  - [x] 5.4 Hit-test mouse down against `_pickerBox`: inside picks a row, outside closes, and text
        selection is suppressed for the gesture
  - [x] 5.5 Wire Enter to `SpecEditor.SetStatus` through the same apply path as task 4
  - [x] 5.6 Verify tests pass

- [x] 6. `--read-only` and footer hints
  - [x] 6.1 Write tests for `EditingEnabled`: false under `--read-only` and under every headless path
        (`--once`, `--format json/md`, gate flags)
  - [x] 6.2 Add the `--read-only` option and `EditingEnabled` to `WatchSettings`
  - [x] 6.3 Gate Tab, Space/Enter, the picker and the focus marker on `EditingEnabled`; show
        `read-only` in the detail footer (the contextual `Tab focus · Space tick` hints shipped with
        tasks 4-5 via `DetailKeyHints`, which already takes an `editing` flag)
  - [x] 6.4 Verify tests pass and the read-only board renders identically to today

- [x] 7. Integration verification
  - [x] 7.1 Add an end-to-end test over a temp spec folder: open, focus, tick, re-parse, and assert
        both the file content and the resulting `SpecRow`
  - [x] 7.2 Manually verify against a real repo: tick and untick tasks, change status via the dropdown,
        cancel with Esc, and edit a file externally mid-session to see the conflict path
  - [x] 7.3 Confirm mouse wheel, drag-copy, `!` jump and list-view behaviour are all unchanged, and the
        full suite is green
