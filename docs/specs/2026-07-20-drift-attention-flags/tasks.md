# Spec Tasks

## Tasks

- [x] 1. Decoupling seam: consume `DriftState` null-safely
  - [x] 1.1 Write tests for `DriftOf`/`IsFlagged` covering None, Drift, Idle, and a `SpecRow` built
        without drift data (must read as None, never throw)
  - [x] 1.2 Add `DriftOf`, `ReasonOf`, and `IsFlagged` helpers in `WatchCommand.cs` behind the single
        engine-contact seam
  - [x] 1.3 Confirm the app compiles and runs with a `SpecRow` that lacks `Drift` (fallback branch)
  - [x] 1.4 Verify tests pass

- [x] 2. Per-row flag glyph column
  - [x] 2.1 Write tests for `FlagField` (glyph + colour per `DriftState`; blank of exact `FlagW` width
        for None) and for the recomputed `nameW` (columns stay aligned; no wrap at min width)
  - [x] 2.2 Add `FlagW`, update `BuildList` name-width math, and add the header cell in `HeaderLine`
  - [x] 2.3 Insert `FlagField` into `RowLine` after the caret; keep selected-row background wrapping
  - [x] 2.4 Verify tests pass and glyphs render aligned on selected and unselected rows

- [x] 3. `!`-jump navigation
  - [x] 3.1 Write tests for `JumpToNextFlagged`: forward wrap, no-flags no-op, single-flag lands on
        itself, selection stays in-range, viewport follows
  - [x] 3.2 Implement `JumpToNextFlagged` reusing `EnsureSelectedVisible`
  - [x] 3.3 Bind the key in `HandleListKey` (`!` via extended `InputEvent`, or the documented `N`
        fallback) and note the chosen binding
  - [x] 3.4 Verify tests pass

- [x] 4. Footer count, hint, and detail reason
  - [x] 4.1 Write tests for the flagged-count string, the conditional `!` hint (only when flagged > 0),
        and the detail-footer reason line
  - [x] 4.2 Update `BuildFooter` list branch (count + hint) and detail branch (reason)
  - [x] 4.3 Verify tests pass

- [x] 5. Toggle / dismiss + accessibility posture
  - [x] 5.1 Write tests for `_flagsVisible == false`: blank glyphs, no count/hint, `!` no-ops (board
        identical to today) and for `--no-flags` initialising the state off
  - [x] 5.2 Add `_flagsVisible` state, the runtime toggle key, and the `--no-flags` option on
        `WatchSettings`
  - [x] 5.3 Confirm glyph-first encoding (distinct `⚠`/`◍`) so meaning survives without colour
  - [x] 5.4 Verify tests pass

- [x] 6. Integration verification
  - [x] 6.1 Write/adjust an end-to-end render test over fixtures with mixed drift/idle/none rows
  - [x] 6.2 Manually verify against a real repo (e.g. car-tracker) in both flags-on and `--no-flags`
        modes, and in degraded mode (no `DriftState`)
  - [x] 6.3 Confirm mouse click still opens the correct row (hit-testing unchanged) and the full suite
        is green
