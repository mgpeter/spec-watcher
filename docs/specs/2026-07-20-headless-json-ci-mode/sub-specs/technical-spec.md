# Technical Spec — Headless JSON & CI Mode

> Parent: ../spec.md
> Created: 2026-07-20

## Approach

The interactive path in `WatchCommand.ExecuteAsync` (WatchCommand.cs:53-109) already branches on
`AnsiConsole.Profile.Capabilities` and, when output is non-interactive, does exactly what this spec
generalizes: one `SpecScanner.ScanAsync`, print a static table, `return 0/1` (WatchCommand.cs:57-67).
We promote that branch into an explicit, flag-driven **headless runner** and add two more output
formats plus a gate engine.

Structure (all new code lives beside the existing files in `SpecWatcher.CLI`; the TUI is untouched):

- `WatchSettings.cs` — new options + typed accessors + validation (below).
- `HeadlessRunner.cs` *(new)* — pure orchestration: takes a `WatchSettings` + `ScanResult`, writes
  the chosen format to a `TextWriter`, and returns the exit code. Kept free of TUI/Spectre-Live so it
  is unit-testable with a `StringWriter`.
- `BoardJson.cs` *(new)* — builds the serializable DTO from `ScanResult` and the `System.Text.Json`
  options (the stable schema — the single source integrations #5 reuses).
- `GateEvaluator.cs` *(new)* — pure function: `(ScanResult, gate options) -> GateOutcome` (passed +
  list of violations). No I/O.
- `WatchCommand.ExecuteAsync` — at the top, decide headless vs interactive and delegate to
  `HeadlessRunner` when headless; otherwise run today's TUI unchanged.

`table` format reuses the existing `BuildStaticTable` (WatchCommand.cs:461-483) verbatim so current
piped output is byte-for-byte preserved.

## Flags & validation (`WatchSettings.cs`)

New options (Spectre.Console.Cli attributes, matching the existing style):

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `--once` | bool | false | Scan once, emit, exit. Never enters the TUI. |
| `-f, --format <FORMAT>` | string | `table` | `table` \| `json` \| `md` (case-insensitive). |
| `--fail-on <STATUSES>` | string? | null | Comma list of status keywords; any matching spec fails the gate. |
| `--min-progress <N>` | int? | null | 0–100; any spec with tasks below N% fails the gate. |

Typed accessors on the settings (parsed once, reused by runner + tests):

```csharp
public enum OutputFormat { Table, Json, Markdown }

public OutputFormat FormatKind { get; }                 // parsed from Format
public IReadOnlyList<SpecStatus> FailOnStatuses { get; } // parsed from FailOn
public bool HasGate => FailOnStatuses.Count > 0 || MinProgress is not null;

/// Headless when the user asked for it OR a non-table/gate flag makes the TUI meaningless.
/// The caller still ORs in the existing non-interactive capability check.
public bool WantsHeadless => Once || FormatKind != OutputFormat.Table || HasGate;
```

`--fail-on` token mapping (case-insensitive, hyphen/underscore/space tolerant), reusing the
vocabulary already in `SpecParser.NormaliseStatus`:
`planning`→Planning, `in-progress`/`in_progress`/`wip`→InProgress, `complete`/`done`→Complete,
`unknown`→Unknown.

Extend `Validate()` (WatchSettings.cs:26-33), keeping the existing repo/interval checks:

- `Format` must be one of table/json/md → else `ValidationResult.Error("--format must be table, json, or md")`.
- `MinProgress`, when set, must be 0..100 → else error.
- Every `--fail-on` token must map to a known status → else `Error($"--fail-on: unknown status '{token}'")`.

Validation failures are surfaced by Spectre before `ExecuteAsync` runs (its own non-zero exit); the
gate/exit-code contract below applies only to a run that passed validation.

## JSON schema (stable field names)

`--format json` writes one object, `System.Text.Json`, camelCase, two-space indented, UTF-8, no BOM,
trailing newline. Field names are **stable API** — additive changes only.

```json
{
  "tool": "spec-watcher",
  "schemaVersion": 1,
  "version": "1.0.0",
  "generatedAt": "2026-07-20T14:03:05Z",
  "specsDir": "C:/repos/personal/spec-watcher/docs/specs",
  "specCount": 2,
  "summary": { "planning": 1, "inProgress": 0, "complete": 1, "unknown": 0 },
  "gate": {
    "failOn": ["planning", "in-progress"],
    "minProgress": 80,
    "passed": false,
    "violations": [
      { "folder": "2026-07-20-headless-json-ci-mode", "reason": "status", "detail": "planning" }
    ]
  },
  "specs": [
    {
      "name": "Headless JSON & CI Mode",
      "folder": "2026-07-20-headless-json-ci-mode",
      "status": "planning",
      "statusRaw": "Planning",
      "description": "A non-interactive path for spec-watcher…",
      "created": "2026-07-20",
      "done": 0,
      "total": 12,
      "hasTasks": true,
      "progress": 0.0,
      "path": "C:/repos/.../2026-07-20-headless-json-ci-mode"
    }
  ],
  "error": null
}
```

Encoding rules (from `Models.cs`):

- `status` — normalized `SpecStatus` as kebab-case string: `planning` | `in-progress` | `complete` |
  `unknown` (custom `JsonConverter`). `statusRaw` is the preserved freeform text.
- `progress` — `SpecRow.Progress` (0..1) or **null** when `Total == 0` (no tasks). Not rounded.
- `created` — `"yyyy-MM-dd"` or **null**.
- `gate` — omitted-as-`null` when no gate flag was given; otherwise always present with `passed`.
- `error` — `ScanResult.Error` string on scan failure (specs still `[]`), else null.

## Exit-code contract

Evaluated by `HeadlessRunner` after emitting output (so a broken gate still prints the board):

| Code | Meaning |
| --- | --- |
| `0` | Scan succeeded and the gate passed (or no gate was set). |
| `1` | Scan error — specs dir missing or IO failure (`ScanResult.Error != null`). Matches today's non-interactive `return 1`. |
| `2` | Gate failure — at least one `--fail-on` match or `--min-progress` shortfall. |

Precedence: a scan error is `1` even if a gate was requested (we can't trust partial data). Gate is
only evaluated when `ScanResult.Error is null`.

Gate rules (`GateEvaluator`):

- **fail-on**: a spec violates if `FailOnStatuses.Contains(row.Status)`. Reason `status`, detail = the
  spec's normalized status.
- **min-progress**: a spec violates if `row.HasTasks && row.Total > 0 && row.Progress * 100 < N`.
  Reason `min-progress`, detail = e.g. `"40% < 80%"`. Specs with **no tasks are exempt** (no progress
  signal) — a future `--require-tasks` could change this; out of scope here.
- `passed = violations.Count == 0`.

## Composition with existing non-interactive detection

In `ExecuteAsync`, replace the current capability-only branch with:

```csharp
var caps = AnsiConsole.Profile.Capabilities;
bool headless = settings.WantsHeadless || !caps.Ansi || !caps.Interactive;
if (headless)
{
    var result = await SpecScanner.ScanAsync(settings.ResolvedSpecsDir, DateTimeOffset.Now);
    return HeadlessRunner.Run(settings, result, Console.Out); // writes format + returns exit code
}
// …unchanged interactive TUI below…
```

Notes:

- The `AnsiConsole.Profile.Width = 120` widening only applies to the **table** format (so wrapped
  output stays readable when piped); json/md ignore console width entirely.
- json/md are written via the plain `TextWriter` (no Spectre markup), so no escape/markup handling and
  clean redirection. `table` continues to go through `AnsiConsole.Write`, which already degrades to no
  color on a non-ANSI profile.
- `Console.CancelKeyPress`, the alt-screen, and Windows console-mode toggling are interactive-only and
  are never touched in the headless path.

## Edge cases

- **Empty specs dir (exists, no spec folders)** — `ScanResult` with `Error == null`, `Rows` empty:
  `specs: []`, `specCount: 0`, summary all zero, gate `passed: true` (nothing violates) → exit `0`.
- **Missing specs dir** — `ScanResult.Error` set (SpecScanner.cs:16-17): `error` populated,
  `specs: []`, gate skipped → exit `1`.
- **Malformed specs** — parser never throws (SpecParser.Parse); such specs land as `status: unknown`,
  `hasTasks: false`. `--fail-on unknown` can catch them; `--min-progress` skips them.
- **Output redirection / pipes** — already the legacy trigger; still honored via the `!caps.Ansi ||
  !caps.Interactive` OR, now also reachable explicitly with `--once`.
- **`--format json` on an interactive TTY** — still emits plain JSON and exits; never starts the TUI.
- **Both gates set** — violations from fail-on and min-progress are aggregated; exit `2` if either
  produced any.

## Testing

The repo currently has no test project (roadmap Phase 2 lists parser tests as pending), so the first
task adds one (`SpecWatcher.Tests`, xUnit, referencing `SpecWatcher.CLI`). `HeadlessRunner`,
`BoardJson`, and `GateEvaluator` are pure (`TextWriter` / in-memory `ScanResult` inputs), so they are
tested without a real terminal. Fixture `ScanResult`s are constructed directly from `SpecRow` records
— no disk needed for the emitter/gate tests; a couple of end-to-end tests build a temp specs folder to
cover the scan-error and empty-dir exit codes.
