# Spec Requirements Document

> Spec: Spec Drift Detection
> Created: 2026-07-20
> Status: Planning

## Overview

Teach spec-watcher to detect when a spec's **declared `> Status:` contradicts its real checkbox
progress**, and optionally when an in-progress spec has gone **idle** on disk. The result is a
per-spec `DriftState` + human-readable `DriftReason` exposed on `SpecRow`, so the board can call
out the mission's core pain — "the written status drifts from reality" — instead of leaving it
buried in the files.

This spec is the **analytics engine only**. It computes and exposes the drift data; it does not
render it. The on-screen glyph, colouring, and `!`-to-jump navigation are owned by the sibling
spec **`2026-07-20-drift-attention-flags`** (ux-nav), which consumes the `DriftState`/`DriftReason`
contract defined here.

## User Stories

- As a **tech lead** skimming a repo, I want specs whose status is a lie (marked *Complete* with
  unchecked tasks, or still *Planning* while tasks are already being ticked) flagged automatically,
  so I don't have to open each file to trust the board.
- As a **solo developer**, I want to know which *In progress* spec I've stopped touching, so a
  forgotten spec doesn't silently rot at 40%.
- As the **ux-nav feature**, I want a stable, already-computed drift value on each `SpecRow`, so I
  can render an attention flag without re-deriving the logic.

## Spec Scope

1. **Contradiction detection (always on)** — compare normalised `Status` against derived checkbox
   progress (`Done`/`Total`/`HasTasks`) and classify each spec as `None`, `Overstated`, or
   `Understated`.
2. **Idle detection (opt-in)** — capture the newest file mtime in a spec folder and flag an
   `In progress` spec with no changes for longer than a configurable threshold as `Idle`. Off by
   default (guarded by a CLI flag) per the fresh-clone mtime kill-check.
3. **Data contract** — add `DriftState Drift`, `string? DriftReason`, and `DateTimeOffset?
   LastModifiedUtc` to `SpecRow`; add a pure, testable `DriftAnalyzer`.
4. **Wiring** — compute drift in `SpecScanner` (which already has `now`) so every published
   `ScanResult` carries drift; add the `--drift-idle-days` option to `WatchSettings`.

## Out of Scope

- Any rendering: glyphs, colours, badges, the `!`-jump key, footer counts (→ sibling ux-nav spec).
- Blocked/stuck detection from `StatusRaw` keywords (separate intel idea #4).
- Trend/history persistence, velocity, or "what changed since last look" (intel #6/#7).
- Using git history for activity dates (integrations area); this spec uses filesystem mtime only.

## Expected Deliverable

1. `SpecRow` exposes `Drift`, `DriftReason`, and `LastModifiedUtc`; `DriftState` enum added to
   `Models.cs`.
2. A pure `DriftAnalyzer.Analyze(row, now, options)` with unit tests covering every classification
   and boundary, all green.
3. `--drift-idle-days <N>` enables idle detection (absent/0 = contradiction-only); default runs
   detect no idle drift and stay noise-free on fresh clones.
