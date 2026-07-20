# Spec Requirements Document

> Spec: Drift Attention Flags
> Created: 2026-07-20
> Status: Planning

## Overview

Add an on-screen **attention layer** to the list view: a compact per-row glyph that flags specs whose
declared status contradicts their real progress (`⚠ drift`) or that have gone quiet (`◍ idle`), a
`!` key that jumps the selection between only the flagged rows, and a footer count so the whole board
answers "what needs my eyes?" at a glance.

This spec owns the **UI only**. The drift/idle computation is a sibling spec,
`2026-07-20-spec-drift-detection` (intel agent), which exposes a `DriftState` (and a short
`DriftReason` string) on `SpecRow`. This layer **consumes** those values and must degrade to an inert,
blank state when they are absent, so it can ship and compile before the engine spec lands.

## User Stories

- As a tech lead leaving spec-watcher open beside my editor, I want specs whose written `Status:`
  disagrees with their checkbox reality to carry a visible `⚠` marker, so I catch drift without
  opening every file.
- As a developer triaging a repo, I want to press `!` to hop straight from one flagged spec to the
  next, so I can review only the ones that need attention instead of arrowing through dozens of rows.
- As a user who finds the extra glyphs noisy (or has a false-positive-prone repo), I want to turn the
  attention layer off, so the board stays a plain status table.
- As a colour-blind user, I want each attention state to have a distinct glyph — not just a colour —
  so the signal is legible regardless of palette.

## Spec Scope

1. **Per-row flag glyph** — a fixed-width leading indicator column between the selection caret and the
   Name: `⚠` (drift), `◍` (idle), or blank (none), driven by `SpecRow.DriftState`.
2. **Status-aware colouring** — amber for drift, dim grey for idle; glyph always carries the meaning so
   colour is never the sole channel. Legible on the selected-row highlight.
3. **`!`-jump navigation** — `!` moves the selection to the next flagged row (wrapping), reusing the
   existing selection + viewport model; a no-op (no crash, no move) when nothing is flagged.
4. **Footer summary** — a flagged count (`⚠ N`) added to the status-count footer, plus a `!` key hint
   shown only when at least one row is flagged.
5. **Detail reason** — when a flagged spec is opened, show its `DriftReason` in the detail footer.
6. **Toggle / dismiss posture** — a `--no-flags` startup option and a runtime toggle key hide the whole
   layer (glyphs, count, hint, jump), per the "conservative, false-positive-safe" kill-check.
7. **Graceful degradation** — when no `DriftState` is available (engine spec not built, or property
   absent), every row reads as "none": blank column, zero count, `!` no-ops. No errors.

## Out of Scope

- **Computing** drift/idle — owned by `2026-07-20-spec-drift-detection`. Do not re-derive here.
- Persisting/animating flags, sound, or desktop notifications.
- Surfacing the parsed-but-unshown `Description` / `Created` fields (the engine may use `Created` for
  idle age, but this UI does not add columns for them).
- Filtering the list to only flagged rows (jump-only here; a filter is a separate roadmap item).

## Expected Deliverable

1. Running against a repo with drift data, flagged specs show `⚠`/`◍` in the list, `!` cycles between
   them, and the footer shows `⚠ N`; opening a flagged spec shows its reason.
2. With `--no-flags` (or the runtime toggle), the board renders exactly as today.
3. Built against a `SpecRow` with no `DriftState`, the app runs unchanged with the layer inert.
