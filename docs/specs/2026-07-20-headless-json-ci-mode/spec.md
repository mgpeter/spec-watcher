# Spec Requirements Document

> Spec: Headless JSON & CI Mode
> Created: 2026-07-20
> Status: Planning

## Overview

Give spec-watcher a first-class non-interactive mode: scan the specs folder **once**, emit the board
to stdout in a chosen `--format` (`table` | `json` | `md`), and exit. Add CI-gating flags
(`--fail-on`, `--min-progress`) that set a non-zero exit code when specs don't meet a bar. This turns
the tool from a purely interactive TUI into a scriptable machine surface — usable in CI checks,
status dashboards, cron digests, and README badges — without adding services, network calls, or
persisted state. It formalizes the ad-hoc static-table fallback the app already prints when output is
piped (`WatchCommand.ExecuteAsync`, WatchCommand.cs:57-67) into a documented, stable contract.

The `json` emitter is defined here as the **single source of truth** for a machine-readable board.
Integrations idea #5 (README status badge / `spec-status.json`) is intended to consume this exact
JSON rather than define its own — that shape is stable and versioned by this spec.

## User Stories

- **As a maintainer running CI**, I want `spec-watcher --once --fail-on planning,in-progress` to exit
  non-zero when any spec on `main` isn't done, so a merge check can enforce "no unfinished specs
  land."
- **As a tech lead**, I want `spec-watcher --format json` to produce a stable JSON snapshot I can pipe
  into `jq`, a dashboard, or a badge generator, so spec health is visible outside the terminal.
- **As anyone writing docs**, I want `spec-watcher --format md` to print a GitHub-flavored Markdown
  table I can paste into a wiki, PR description, or status email.
- **As a scripter**, I want `--min-progress 80` to fail the run when any spec with tasks is below 80%
  complete, so I can gate on real checkbox progress, not just the declared status.

## Spec Scope

1. **Headless one-shot mode** — `--once` scans once, writes the board, and exits, regardless of
   whether the terminal is interactive. `--format json|md` and any gate flag imply headless too.
2. **Output formats** — `--format table` (the existing Spectre static table, default), `json`
   (stable schema), and `md` (GitHub-flavored Markdown table). Plain text for json/md (no ANSI /
   markup) so redirection is clean.
3. **CI-gate flags** — `--fail-on <statuses>` (comma list of normalized status keywords) and
   `--min-progress <N>` (0–100). Either can trip a distinct non-zero exit code, with the offending
   specs reported.
4. **Exit-code contract** — 0 = OK, 1 = scan error (missing dir / IO), 2 = gate failure. Documented
   and covered by tests.
5. **Reuse the existing scan** — build on `SpecScanner.ScanAsync` (runs once already) and serialize
   the existing `SpecRow` / `ScanResult` shapes; do not re-parse or duplicate scan logic.

## Out of Scope

- Any change to the interactive TUI (list/detail navigation, mouse, timer loop) — untouched.
- Persisted history, snapshots, diffs, or trend/velocity data (that is intel's area).
- Network calls, forge/PR status, notifications, or a long-running server (`--serve`).
- Generating the badge SVG/image itself — this spec only guarantees the JSON that a badge tool reads.
- A `--require-tasks` gate (specs with no `tasks.md` are exempt from `--min-progress` for now; noted
  in the technical spec as a future flag).

## Expected Deliverable

1. `spec-watcher --once` (and `--format json|md`) prints the board to stdout and exits without
   entering the TUI, on interactive and non-interactive terminals alike.
2. `spec-watcher --format json` emits the documented stable schema; `--format md` emits a valid
   GitHub Markdown table; `--format table` is byte-for-byte the current static-table output.
3. `--fail-on` / `--min-progress` produce exit code 2 with the violating specs listed (and in the
   JSON `gate` block); scan errors produce exit code 1; a clean pass produces exit code 0.
4. Unit tests cover flag parsing/validation, the gate engine, and each emitter; all green.
