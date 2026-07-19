# spec-watcher

A full-screen terminal dashboard that watches a repository's specs folder and shows every spec's
**name, status, and task progress** in one live, navigable table. Arrow-key/mouse navigation with a
per-spec detail view that shows the whole spec.

Built for the Agent OS spec convention: one date-prefixed folder per spec under `docs/specs`, each
with a `spec.md` (blockquote header), `spec-lite.md` (one-paragraph summary), and usually a
`tasks.md` (checkbox tree).

List columns: **Name · Status · Progress · Folder**. Press **Enter** (or click a row) to open the
full spec + task checklist.

## Requirements

- .NET 10 SDK

## Run

```sh
# watch ./docs/specs of the current directory
dotnet run --project SpecWatcher.CLI

# watch another repo
dotnet run --project SpecWatcher.CLI -- C:\repos\personal\car-tracker

# custom specs folder and 30s interval
dotnet run --project SpecWatcher.CLI -- C:\repos\personal\car-tracker -s docs/specs -i 30
```

Build a binary:

```sh
dotnet build -c Release
# → SpecWatcher.CLI/bin/Release/net10.0/spec-watcher(.exe)
```

## Options

| Argument / Option        | Default              | Description                                   |
| ------------------------ | -------------------- | --------------------------------------------- |
| `[REPO_PATH]` (position) | current directory    | Repository root to watch.                     |
| `-s, --specs-path`       | `docs/specs`         | Specs folder, relative to the repo or absolute. |
| `-i, --interval`         | `60`                 | Auto-rescan interval in seconds (min 1).      |

## Keys & mouse

**List view**

| Input                    | Action                          |
| ------------------------ | ------------------------------- |
| `↑` / `↓`                | Move selection                  |
| `PgUp` / `PgDn` / `Home` / `End` | Jump                    |
| `Enter`                  | Open the selected spec          |
| **Left-click** a row     | Open that spec                  |
| **Scroll wheel**         | Move through the list           |
| `R`                      | Rescan immediately              |
| `Q` / `Esc`              | Quit                            |

**Detail view**

| Input                    | Action                          |
| ------------------------ | ------------------------------- |
| `↑` / `↓` / `PgUp` / `PgDn` / **wheel** | Scroll the spec  |
| `Esc` / `Backspace` / `←`| Back to the list                |
| `Q`                      | Quit                            |

Mouse (wheel + click) is supported on Windows (Windows Terminal / conhost). On other platforms, or
when input is redirected, the app runs keyboard-only.

## How it reads a spec

- **Name** — the `> Spec:` line in `spec.md` (falls back to the de-slugged folder name).
- **Status** — the `> Status:` keyword → **Planning** / **In progress** / **Complete**. An all-checked
  `tasks.md` is treated as Complete even if the status line is stale.
- **Progress** — completed vs total `- [ ]` / `- [x]` checkboxes in `tasks.md` (shows `—` when absent).
- **Folder** — the raw folder slug, e.g. `2026-07-19-starter-check-selection`.
- **Detail view** — renders the full `spec.md` (Overview, User Stories, Scope, …) plus the `tasks.md`
  checklist, loaded fresh from disk when opened.

## Behavior notes

- The watch timer runs off the UI thread, so scanning never blocks input or redraws.
- Full-screen uses the terminal's alternate screen buffer and restores it on exit.
- On Windows, the console input mode is switched to deliver mouse events (QuickEdit is temporarily
  disabled) and restored on exit.
- When output is piped or the terminal is non-interactive, it prints a single static table and exits.

## Project layout

```
SpecWatcher.slnx            # solution
SpecWatcher.CLI/            # the SpecWatcher.Console project
  SpecWatcher.Console.csproj
  Program.cs               # CommandApp entry point
  WatchSettings.cs         # CLI args/options + validation
  WatchCommand.cs          # Full-screen TUI: list + detail, nav, mouse, timer scan loop
  SpecScanner.cs           # Enumerate + parse spec folders (off-thread)
  SpecParser.cs            # Pure parsing of spec.md / spec-lite.md / tasks.md
  Models.cs                # SpecRow, SpecStatus, ScanResult, SpecDetail (immutable)
  Input/                   # IInputSource: Windows ReadConsoleInput (keys+mouse) + ReadKey fallback
docs/product/              # Mission, tech stack, roadmap, decisions
```
