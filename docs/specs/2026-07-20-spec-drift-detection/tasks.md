# Spec Tasks

These are the tasks to be completed for the spec detailed in
`docs/specs/2026-07-20-spec-drift-detection/spec.md`.

> Reference implementation notes: `docs/specs/2026-07-20-spec-drift-detection/sub-specs/technical-spec.md`

## Tasks

- [ ] 1. Data contract: `DriftState` + `SpecRow` fields
  - [ ] 1.1 Add a test asserting `SpecRow` exposes `Drift` (default `None`), `DriftReason` (default
        null), and `LastModifiedUtc` (default null), and that existing construction still compiles.
  - [ ] 1.2 Add `enum DriftState { None, Overstated, Understated, Idle }` to `Models.cs`.
  - [ ] 1.3 Extend the `SpecRow` record with `LastModifiedUtc`, `Drift`, `DriftReason` (safe defaults).
  - [ ] 1.4 Run the tests and confirm green.

- [ ] 2. `DriftAnalyzer` — contradiction detection (always-on)
  - [ ] 2.1 Create `SpecWatcher.Tests` (xUnit) and write cases for: Complete-but-unchecked →
        `Overstated`; all-checked-but-Planning/InProgress → `Understated`; Planning-with-some-checked
        → `Understated`; healthy matches → `None`; no-tasks and empty-tasks → `None`; Unknown → `None`.
        Assert `DriftReason` text for each non-None case.
  - [ ] 2.2 Implement `DriftAnalyzer.Analyze` + `DriftOptions` with the classification order from the
        technical spec (contradiction before idle), idle disabled (`IdleDays = 0`).
  - [ ] 2.3 Run the tests and confirm green.

- [ ] 3. Idle detection (opt-in, mtime-gated)
  - [ ] 3.1 Write tests injecting `now` + `LastModifiedUtc`: InProgress older than threshold →
        `Idle`; within threshold → `None`; `IdleDays = 0` → never `Idle`; null mtime → never `Idle`;
        future mtime → `None`; contradiction+idle together → contradiction wins.
  - [ ] 3.2 Implement the idle branch in `DriftAnalyzer.Analyze` behind `options.IdleDays > 0`.
  - [ ] 3.3 Capture newest `LastModifiedUtc` in `SpecParser.Parse` (guarded file stat) with a test
        over fixture folders; leave `Drift`/`DriftReason` untouched by `Parse`.
  - [ ] 3.4 Run the tests and confirm green.

- [ ] 4. Wire into the scan pipeline + CLI flag
  - [ ] 4.1 Write a test that `SpecScanner.ScanAsync` populates `Drift`/`DriftReason` on returned rows
        given `DriftOptions`, and that `WatchSettings` validates `--drift-idle-days` (rejects negatives).
  - [ ] 4.2 Thread `DriftOptions` through `ScanAsync`; compute drift per row via `row with { … }`.
  - [ ] 4.3 Add `--drift-idle-days` + `ToDriftOptions()` to `WatchSettings`; pass it from both
        `WatchCommand` scan call sites.
  - [ ] 4.4 Run the full test suite and confirm green; manually run against car-tracker with and
        without `--drift-idle-days` to sanity-check no false positives on a fresh clone.
