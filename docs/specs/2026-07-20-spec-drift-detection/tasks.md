# Spec Tasks

These are the tasks to be completed for the spec detailed in
`docs/specs/2026-07-20-spec-drift-detection/spec.md`.

> Reference implementation notes: `docs/specs/2026-07-20-spec-drift-detection/sub-specs/technical-spec.md`

## Tasks

- [x] 1. Data contract: `DriftState` + `SpecRow` fields
  - [x] 1.1 Add a test asserting `SpecRow` exposes `Drift` (default `None`), `DriftReason` (default
        null), and `LastModifiedUtc` (default null), and that existing construction still compiles.
  - [x] 1.2 Add `enum DriftState { None, Overstated, Understated, Idle }` to `Models.cs`.
  - [x] 1.3 Extend the `SpecRow` record with `LastModifiedUtc`, `Drift`, `DriftReason` (safe defaults).
  - [x] 1.4 Run the tests and confirm green.

- [x] 2. `DriftAnalyzer` — contradiction detection (always-on)
  - [x] 2.1 Create `SpecWatcher.Tests` (xUnit) and write cases for: Complete-but-unchecked →
        `Overstated`; all-checked-but-Planning/InProgress → `Understated`; Planning-with-some-checked
        → `Understated`; healthy matches → `None`; no-tasks and empty-tasks → `None`; Unknown → `None`.
        Assert `DriftReason` text for each non-None case.
  - [x] 2.2 Implement `DriftAnalyzer.Analyze` + `DriftOptions` with the classification order from the
        technical spec (contradiction before idle), idle disabled (`IdleDays = 0`).
  - [x] 2.3 Run the tests and confirm green.

- [x] 3. Idle detection (opt-in, mtime-gated)
  - [x] 3.1 Write tests injecting `now` + `LastModifiedUtc`: InProgress older than threshold →
        `Idle`; within threshold → `None`; `IdleDays = 0` → never `Idle`; null mtime → never `Idle`;
        future mtime → `None`; contradiction+idle together → contradiction wins.
  - [x] 3.2 Implement the idle branch in `DriftAnalyzer.Analyze` behind `options.IdleDays > 0`.
  - [x] 3.3 Capture newest `LastModifiedUtc` in `SpecParser.Parse` (guarded file stat) with a test
        over fixture folders; leave `Drift`/`DriftReason` untouched by `Parse`.
  - [x] 3.4 Run the tests and confirm green.

- [x] 4. Wire into the scan pipeline + CLI flag
  - [x] 4.1 Write a test that `SpecScanner.ScanAsync` populates `Drift`/`DriftReason` on returned rows
        given `DriftOptions`, and that `WatchSettings` validates `--drift-idle-days` (rejects negatives).
  - [x] 4.2 Thread `DriftOptions` through `ScanAsync`; compute drift per row via `row with { … }`.
  - [x] 4.3 Add `--drift-idle-days` + `ToDriftOptions()` to `WatchSettings`; pass it from both
        `WatchCommand` scan call sites.
  - [x] 4.4 Run the full test suite and confirm green; manually run against car-tracker with and
        without `--drift-idle-days` to sanity-check no false positives on a fresh clone.
