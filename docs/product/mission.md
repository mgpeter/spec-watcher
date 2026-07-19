# Product Mission

## Pitch

spec-watcher is a full-screen terminal dashboard that helps developers and technical leads keep a live eye on a repository's specs by continuously watching the specs folder and displaying every spec's name, description, status, and task progress in one sleek, always-current table.

## Users

### Primary Customers

- **Solo developers using Agent OS specs:** People who drive their work through the date-prefixed `docs/specs` convention and want a single glance to see what is planned, in progress, and done.
- **Technical leads / maintainers:** People overseeing a repo who need a passive, at-a-glance progress board without opening dozens of markdown files or standing up a web tool.

### User Personas

**Repo Owner** (25-55 years old)
- **Role:** Developer / Tech Lead
- **Context:** Works across one or more repos that use the Agent OS spec workflow (`spec.md`, `spec-lite.md`, `tasks.md` per spec folder).
- **Pain Points:** Spec status is scattered across many files; the `Status:` line goes stale; hard to see overall progress at a glance.
- **Goals:** Instantly see what is planned/in-progress/complete; keep a passive monitor running while coding; refresh on demand after editing specs.

## The Problem

### Spec status is invisible until you open every file

A repo can accumulate dozens of spec folders, each with a status buried in a blockquote header and real progress tracked as checkboxes in a separate `tasks.md`. Reviewing overall state means opening file after file. There is no single, current view.

**Our Solution:** Point spec-watcher at a repo and get a live, whole-terminal table of every spec with its status badge and task-completion bar.

### The written status drifts from reality

The `> Status:` line is edited by hand and often lags behind the actual checkbox progress in `tasks.md`.

**Our Solution:** spec-watcher shows the declared status *and* the derived checkbox completion side by side, and treats an all-checked task list as complete even when the status line is stale.

### Heavyweight tools are overkill

Standing up a web app or board to track a handful of markdown specs is disproportionate effort.

**Our Solution:** A single self-contained CLI that takes over the terminal, watches on a timer, and needs zero setup or services.

## Differentiators

### Purpose-built for the Agent OS spec convention

Unlike a generic markdown viewer or a project board, spec-watcher understands the exact `spec.md` / `spec-lite.md` / `tasks.md` layout and extracts name, description, status, and checkbox progress automatically. This results in a zero-configuration, accurate view of a repo's specs.

### Passive, non-blocking, always current

Unlike opening files manually, spec-watcher watches on a background timer that never blocks the UI and refreshes on demand, so the board is always current while you keep working.

## Key Features

### Core Features

- **Live spec table:** Name, short description, status, task progress, and folder for every spec in one view.
- **Repo targeting:** Point at any repo path; watches `docs/specs` by default.
- **Customizable specs source:** Override the watched folder with `--specs-path`.
- **Non-blocking timer watch:** Re-scans on a configurable interval (default 60s) off the UI thread.
- **On-demand refresh:** Press `R` to rescan immediately.

### Experience Features

- **Full-screen takeover:** Uses the terminal's alternate screen buffer and restores it cleanly on exit.
- **Status badges:** Color-coded Planning / In progress / Complete.
- **Progress bars:** Per-spec checkbox completion as a bar with percentage and counts.
- **Summary footer:** Totals by status plus key hints.
- **Graceful degradation:** Prints a single static table when output is piped or the terminal is non-interactive.
