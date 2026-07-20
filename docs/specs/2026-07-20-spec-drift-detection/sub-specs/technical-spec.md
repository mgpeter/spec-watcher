# Technical Specification

This is the technical specification for the spec detailed in
`docs/specs/2026-07-20-spec-drift-detection/spec.md`.

## Approach

Drift is a pure function of data spec-watcher already parses plus one new input (file mtime) and the
scan `now`. Keep the computation in a new static, side-effect-free `DriftAnalyzer` — mirroring the
existing `SpecParser` style — so it is trivially unit-testable. `SpecRow` carries the *result*;
`SpecScanner` (which owns `now`) is the single call site that fills it in. `SpecParser.Parse` stays
time-agnostic and only gains the cheap mtime capture.

## Files & Types to Touch

### `Models.cs`
- Add enum:
  ```csharp
  public enum DriftState { None, Overstated, Understated, Idle }
  ```
  - `Overstated` — status claims more than the boxes show (declared `Complete` but not all checked).
  - `Understated` — boxes are ahead of the status (all/most checked but still `Planning`/`InProgress`,
    or any completed checkboxes while `Planning`).
  - `Idle` — an `InProgress` spec whose newest file is older than the idle threshold (opt-in).
- Extend `SpecRow` with three members (append to the positional record; all have safe defaults so
  existing construction in tests/`Parse` stays valid):
  - `DateTimeOffset? LastModifiedUtc` — newest mtime among `spec.md`/`tasks.md`/`spec-lite.md`
    (null if none readable).
  - `DriftState Drift` — defaults to `DriftState.None`.
  - `string? DriftReason` — one-line human explanation (e.g. `"Complete, but 4/9 tasks checked"`),
    null when `Drift == None`.
- Rationale for storing (not computing on the fly): the ux-nav consumer and any future footer count
  read it repeatedly per frame; a stored value avoids re-deriving and keeps the contract explicit.

### New file `DriftAnalyzer.cs`
```csharp
public static class DriftAnalyzer
{
    public static (DriftState State, string? Reason) Analyze(
        SpecRow row, DateTimeOffset now, DriftOptions options);
}

public readonly record struct DriftOptions(int IdleDays)   // IdleDays <= 0 => idle detection off
{
    public static readonly DriftOptions Default = new(0);
}
```
Classification order (first match wins; contradiction beats idle):
1. **Overstated** — `Status == Complete && HasTasks && Total > 0 && Done < Total`.
   Reason: `"Marked Complete, but {Done}/{Total} tasks checked"`.
2. **Understated** —
   - `HasTasks && Total > 0 && Done == Total && Status is Planning or InProgress`, or
   - `Status == Planning && Done > 0`.
   Reason (case A): `"All {Total} tasks checked, still marked {Status}"`;
   (case B): `"Marked Planning, but {Done}/{Total} tasks in progress"`.
3. **Idle** — only when `options.IdleDays > 0 && Status == InProgress && LastModifiedUtc is {} m`
   and `(now - m).TotalDays >= IdleDays`.
   Reason: `"In progress, no file change in {wholeDays}d"`.
4. Otherwise **None**.

`Overstated`/`Understated` need neither `now` nor mtime, so they work on every platform and on fresh
clones. Only `Idle` depends on mtime and is therefore gated.

### `SpecParser.cs`
- In `Parse`, after reading the three files, capture the newest mtime:
  `LastModifiedUtc` = max of `File.GetLastWriteTimeUtc(path)` over the files that exist (wrapped in
  the same try/`IOException`/`UnauthorizedAccessException` swallow pattern as `TryRead`; return null
  on failure). Pass it into the extended `SpecRow`.
- `Parse` does **not** compute `Drift` (no `now`/options here). It leaves `Drift = None`,
  `DriftReason = null`. Note the existing all-checked→`Complete` override at
  `SpecParser.cs:49-50` runs *before* drift and only fires for `Unknown` status — so a spec that was
  auto-promoted to `Complete` will never be `Overstated`, and an explicit `Planning`/`InProgress`
  with a full task list will correctly surface as `Understated`. Preserve this ordering.

### `SpecScanner.cs`
- `ScanAsync` already has `now` and loops `rows.Add(SpecParser.Parse(folder))`. Change to:
  ```csharp
  var row = SpecParser.Parse(folder);
  var (state, reason) = DriftAnalyzer.Analyze(row, now, driftOptions);
  rows.Add(row with { Drift = state, DriftReason = reason });
  ```
- Thread a `DriftOptions` parameter through `ScanAsync` (default `DriftOptions.Default`) so callers
  opt in without breaking the signature.

### `WatchSettings.cs`
- Add:
  ```csharp
  [CommandOption("--drift-idle-days <DAYS>")]
  [Description("Flag In-progress specs untouched for this many days as idle. 0 = off. Default: 0")]
  [DefaultValue(0)]
  public int DriftIdleDays { get; init; }
  ```
- `Validate()`: reject negative values (`--drift-idle-days must be 0 or greater`).
- Expose `DriftOptions ToDriftOptions() => new(DriftIdleDays);` for the two `ScanAsync` call sites in
  `WatchCommand.cs` (lines 63 and 262) to pass through.

## mtime Plumbing Decision

- **Always capture** `LastModifiedUtc` (a stat is cheap and platform-neutral); it is also useful to
  the sibling spec and future ideas.
- **Only interpret it as drift when explicitly enabled** via `--drift-idle-days > 0`. Per the
  kill-check, `git clone`/checkout rewrites mtimes to the checkout time, so an always-on idle flag
  would mark every spec in a fresh clone stale. Default-off keeps the tool honest with zero config;
  the contradiction checks (the reliable signal) are always on.

## Edge Cases

- **No `tasks.md`** (`HasTasks == false`, `Total == 0`): never `Overstated`/`Understated` — there is
  no checkbox reality to contradict. → `None`.
- **`Status == Unknown`**: contradiction checks skip it (nothing was declared to contradict); if it
  had a full task list it was already promoted to `Complete` upstream.
- **`Total == 0` with `HasTasks == true`** (empty tasks.md): treat as no reality signal → `None`
  (the intel linter idea handles "empty tasks.md" separately).
- **Unreadable files / null `LastModifiedUtc`**: idle simply cannot fire; contradiction still works.
- **Clock skew / future mtime**: `(now - m)` negative → below any positive threshold → not idle.
- **Idle vs contradiction on the same spec**: contradiction wins (evaluated first); a spec that is
  both `Complete`-but-unchecked and old reports `Overstated`, the more actionable signal.

## Testing Notes

- No test project exists yet (roadmap Phase 2 lists parser tests as pending). This spec introduces
  `SpecWatcher.Tests` (xUnit) with `DriftAnalyzer` as its first subject — pure inputs, no filesystem.
- `now` and `LastModifiedUtc` are injected values, so idle tests are deterministic without touching
  the disk or the clock.
