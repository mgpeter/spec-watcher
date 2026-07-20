# Spec Tasks

## Tasks

- [ ] 1. Decoupling seam: consume `DriftState` null-safely
  - [ ] 1.1 Write tests for `DriftOf`/`IsFlagged` covering None, Drift, Idle, and a `SpecRow` built
        without drift data (must read as None, never throw)
  - [ ] 1.2 Add `DriftOf`, `ReasonOf`, and `IsFlagged` helpers in `WatchCommand.cs` behind the single
        engine-contact seam
  - [ ] 1.3 Confirm the app compiles and runs with a `SpecRow` that lacks `Drift` (fallback branch)
  - [ ] 1.4 Verify tests pass

- [ ] 2. Per-row flag glyph column
  - [ ] 2.1 Write tests for `FlagField` (glyph + colour per `DriftState`; blank of exact `FlagW` width
        for None) and for the recomputed `nameW` (columns stay aligned; no wrap at min width)
  - [ ] 2.2 Add `FlagW`, update `BuildList` name-width math, and add the header cell in `HeaderLine`
  - [ ] 2.3 Insert `FlagField` into `RowLine` after the caret; keep selected-row background wrapping
  - [ ] 2.4 Verify tests pass and glyphs render aligned on selected and unselected rows

- [ ] 3. `!`-jump navigation
  - [ ] 3.1 Write tests for `JumpToNextFlagged`: forward wrap, no-flags no-op, single-flag lands on
        itself, selection stays in-range, viewport follows
  - [ ] 3.2 Implement `JumpToNextFlagged` reusing `EnsureSelectedVisible`
  - [ ] 3.3 Bind the key in `HandleListKey` (`!` via extended `InputEvent`, or the documented `N`
        fallback) and note the chosen binding
  - [ ] 3.4 Verify tests pass

- [ ] 4. Footer count, hint, and detail reason
  - [ ] 4.1 Write tests for the flagged-count string, the conditional `!` hint (only when flagged > 0),
        and the detail-footer reason line
  - [ ] 4.2 Update `BuildFooter` list branch (count + hint) and detail branch (reason)
  - [ ] 4.3 Verify tests pass

- [ ] 5. Toggle / dismiss + accessibility posture
  - [ ] 5.1 Write tests for `_flagsVisible == false`: blank glyphs, no count/hint, `!` no-ops (board
        identical to today) and for `--no-flags` initialising the state off
  - [ ] 5.2 Add `_flagsVisible` state, the runtime toggle key, and the `--no-flags` option on
        `WatchSettings`
  - [ ] 5.3 Confirm glyph-first encoding (distinct `⚠`/`◍`) so meaning survives without colour
  - [ ] 5.4 Verify tests pass

- [ ] 6. Integration verification
  - [ ] 6.1 Write/adjust an end-to-end render test over fixtures with mixed drift/idle/none rows
  - [ ] 6.2 Manually verify against a real repo (e.g. car-tracker) in both flags-on and `--no-flags`
        modes, and in degraded mode (no `DriftState`)
  - [ ] 6.3 Confirm mouse click still opens the correct row (hit-testing unchanged) and the full suite
        is green
