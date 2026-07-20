# Spec Tasks

## Tasks

- [ ] 1. Headless CLI flags & validation (`WatchSettings`)
  - [ ] 1.1 Add `SpecWatcher.Tests` (xUnit) referencing `SpecWatcher.CLI`; write failing tests for
        flag parsing (`--once`, `--format`, `--fail-on`, `--min-progress`) and for `Validate()`
        rejecting a bad format, out-of-range `--min-progress`, and an unknown `--fail-on` token.
  - [ ] 1.2 Add the four options to `WatchSettings` plus the typed accessors `FormatKind`,
        `FailOnStatuses`, `HasGate`, `WantsHeadless`, and the `OutputFormat` enum.
  - [ ] 1.3 Extend `Validate()` with the format / min-progress / fail-on checks, preserving the
        existing repo-path and interval validation.
  - [ ] 1.4 Verify all Task 1 tests pass.

- [ ] 2. Gate engine (`GateEvaluator`)
  - [ ] 2.1 Write failing tests: `--fail-on` matches by normalized status; `--min-progress`
        violates only tasked specs below the bar; no-tasks specs are exempt; combined gates aggregate;
        no gate flags → `passed == true`.
  - [ ] 2.2 Implement `GateEvaluator` as a pure function producing `(passed, violations[])` with
        `reason`/`detail` per the technical spec.
  - [ ] 2.3 Verify all Task 2 tests pass.

- [ ] 3. JSON emitter (`BoardJson`) — the stable single-source schema
  - [ ] 3.1 Write failing tests asserting stable field names, camelCase, kebab-case `status`, null
        `progress`/`created` handling, the `summary` counts, the `gate` block, and `error` population
        on a scan-error `ScanResult`.
  - [ ] 3.2 Implement the serializable DTO, the `SpecStatus`→kebab `JsonConverter`, and the
        `System.Text.Json` options (camelCase, indented, no BOM, trailing newline).
  - [ ] 3.3 Verify all Task 3 tests pass.

- [ ] 4. Table & Markdown emitters + `HeadlessRunner`
  - [ ] 4.1 Write failing tests: `md` produces a valid GitHub table + summary line; `table` output is
        unchanged from the current static table; `HeadlessRunner.Run` returns exit codes 0/1/2 for
        clean pass / scan error / gate failure using in-memory `ScanResult`s and a `StringWriter`.
  - [ ] 4.2 Implement the Markdown emitter, wire `table` to the existing `BuildStaticTable`, and
        implement `HeadlessRunner.Run` (emit format, then evaluate gate → exit code).
  - [ ] 4.3 Verify all Task 4 tests pass.

- [ ] 5. Wire into `ExecuteAsync`, document, and verify end-to-end
  - [ ] 5.1 Write failing end-to-end tests over a temp specs folder: `--once` exits without the TUI;
        empty dir → exit 0; missing dir → exit 1; `--fail-on planning` on a planning spec → exit 2.
  - [ ] 5.2 Replace the capability-only branch in `WatchCommand.ExecuteAsync` with the
        `WantsHeadless || !Ansi || !Interactive` decision delegating to `HeadlessRunner`; keep the
        interactive path and the table-only width widening intact.
  - [ ] 5.3 Update `README.md` (options table + a headless/CI usage example) and `Program.cs`
        `AddExample` entries; add a one-line note that `spec-status.json` (integrations #5) consumes
        this JSON.
  - [ ] 5.4 Verify the full test suite is green and `dotnet build -c Release` succeeds.
