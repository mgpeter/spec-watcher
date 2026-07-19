# Product Roadmap

## Phase 1: Core MVP (Shipped)

**Goal:** A working full-screen watcher that renders a repo's specs live.
**Success Criteria:** Point at car-tracker and see all specs with correct name, description, status badge, and progress; refresh on demand; quit cleanly.

### Features

- [x] .NET 10 CLI scaffold with Spectre.Console + Spectre.Console.Cli - `XS`
- [x] Spec parser: name, description, status, checkbox progress from `spec.md` / `spec-lite.md` / `tasks.md` - `S`
- [x] Directory scanner with graceful missing-folder handling - `XS`
- [x] Full-screen TUI: title / table / footer Layout in the alternate screen buffer - `M`
- [x] Non-blocking background timer scan (default 60s) + on-demand refresh (R) + quit (Q) - `S`
- [x] Status badges, progress bars, and status-count summary footer - `S`
- [x] CLI options: repo path, `--specs-path`, `--interval`; non-interactive fallback - `XS`
- [x] Arrow-key navigation with a selected-row cursor and scrolling viewport - `S`
- [x] Detail view: full `spec.md` + `tasks.md` checklist, scrollable, Enter/Esc - `M`
- [x] Mouse support (Windows): wheel scroll + left-click to open, via `ReadConsoleInput` - `M`

### Dependencies

- .NET 10 SDK
- Spectre.Console 0.51.x

## Phase 2: Usability & Robustness

**Goal:** Make the watcher pleasant for daily, long-running use.
**Success Criteria:** Sort/filter specs and navigate long lists without leaving the terminal.

### Features

- [ ] Column sort toggles (by date, status, progress, name) - `S`
- [ ] Status/keyword filtering (e.g. show only In progress) - `S`
- [ ] Text search / jump-to-spec in the list - `S`
- [ ] FileSystemWatcher for near-instant updates in addition to the timer - `S`
- [ ] Mouse support on non-Windows terminals via ANSI SGR mouse mode - `M`
- [ ] Parser unit tests over car-tracker fixtures (freeform status, missing tasks.md) - `S`

### Dependencies

- Phase 1 complete

## Phase 3: Reach & Polish

**Goal:** Broaden where and how it can be used.
**Success Criteria:** Works against multiple repos and integrates into workflows.

### Features

- [ ] Watch multiple repos / specs roots in one view - `M`
- [ ] Open a spec in the default editor from the table - `S`
- [ ] Configurable field mappings for non-Agent-OS spec formats - `M`
- [ ] Theme options (color schemes, compact mode) - `S`
- [ ] Packaged distribution (dotnet tool / single-file executable) - `S`

### Dependencies

- Phase 2 complete
