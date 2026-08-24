# Spec Requirements Document

> Spec: Inline Spec Editing
> Created: 2026-08-24
> Status: Planning

## Overview

Turn the detail view from a read-only rendering of `spec.md` / `tasks.md` into a lightly editable
surface: tick and untick task checkboxes, and change a spec's `Status:` through a dropdown, with each
change written straight back to the markdown file. Interactive elements gain a focus cursor driven by
both keyboard and mouse, so a spec can be kept current without leaving the watcher.

The dropdown is drawn as an overlay inside the existing `Live` loop rather than via
`AnsiConsole.Prompt`, so the lock-free UI thread, the background scan timer, mouse hit-testing and the
alternate-screen buffer all keep working while it is open.

## User Stories

### Tick a task off without switching tools

As a developer working through a spec's task list, I want to press Space on a task in the detail view
and have it tick in `tasks.md`, so that I can keep the board honest while I work instead of switching
to an editor for a one-character edit.

The user arrows down through the detail view; the focus cursor lands only on interactive elements
(checkboxes and the status field), skipping prose. Space or Enter toggles the focused checkbox, the
line re-renders as ticked, the file is rewritten in place, and the progress bar in the footer and the
list row both update from the re-parse.

### Fix a stale status

As a tech lead reviewing a flagged spec, I want to open the status dropdown and set the spec to
`Complete`, so that I can clear drift the moment I spot it rather than noting it for later.

With the status field focused, Enter opens a small bordered overlay listing Planning / In progress /
Complete plus the current freeform value if it does not match any of them. Arrow keys or a mouse click
pick an entry; Enter writes it to the `> Status:` line of `spec.md`; Esc cancels with no write.

### Keep the board safe on someone else's repo

As someone pointing spec-watcher at a repository I do not own, I want a `--read-only` switch that
disables every write path, so that I can browse without any risk of touching files.

## Spec Scope

1. **Interactive element model** — the detail document gains a parallel list of focusable elements
   (checkbox lines and the status line), each with its content-line index and source-file coordinates,
   built alongside `_detailLines` / `_detailPlain`.
2. **Focus cursor** — Tab/Shift+Tab and mouse click move focus between interactive elements; the
   focused element is drawn with a distinct marker and colour, and the viewport auto-scrolls to it.
   Plain `↑↓` scrolling behaviour is unchanged.
3. **Checkbox toggling** — Space/Enter on a focused checkbox, or a click directly on its glyph, flips
   `[ ]` ⇄ `[x]` on exactly that line of `tasks.md`, preserving the rest of the file byte for byte.
4. **Status dropdown overlay** — an in-`Live` bordered picker over the detail view offering the three
   known statuses (and the current freeform value when it is none of them), navigable by keyboard and
   mouse, writing the chosen value to the `> Status:` line of `spec.md`.
5. **Immediate write + re-parse** — every edit writes at once and re-parses the spec so the status
   badge, progress bar, drift flag and list row reflect it without waiting for the next timer scan;
   the background scanner must not clobber an in-flight edit.
6. **`--read-only` mode** — disables all writes, hides the focus cursor and edit hints, and is implied
   by the headless (`--once` / non-table / gate) paths.
7. **Write failure handling** — an unwritable or externally changed file surfaces a transient footer
   error and leaves both the file and the on-screen state unchanged.

## Out of Scope

- Editing anything other than checkbox marks and the `> Status:` value; no free text editing of spec
  prose, task wording, or other blockquote fields.
- Typing a new freeform status: the dropdown offers the three known keywords plus the spec's existing
  freeform value as a keep-as-is entry, and nothing else.
- Adding, deleting or reordering tasks.
- Auto-ticking a parent task when all of its subtasks are ticked, or any other inferred cascade.
- Editing from the list view (status stays read-only there); the roadmap's own checkboxes.
- Undo/redo, an edit history, or a buffered save/discard flow.
- Multi-line or rectangular editing, and any change to the existing drag-to-copy selection behaviour.

## Expected Deliverable

1. Opening a spec, tabbing to a task and pressing Space ticks it on screen and in `tasks.md`, and the
   list row's progress bar updates immediately on return.
2. Focusing the status field and choosing `Complete` from the overlay rewrites the `> Status:` line of
   `spec.md`, updates the badge and clears any drift flag, while Esc cancels without a write.
3. Started with `--read-only`, the detail view renders exactly as it does today and no key or click
   modifies a file.
