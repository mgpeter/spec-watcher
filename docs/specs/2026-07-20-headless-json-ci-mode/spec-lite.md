A non-interactive path for spec-watcher: `--once` scans the specs folder a single time, emits the
board to stdout as `--format table|json|md`, and exits — no TUI, no services, no state. CI-gate flags
(`--fail-on planning,in-progress` and `--min-progress N`) set a distinct non-zero exit code (2) when
specs don't meet the bar, versus 1 for a scan error and 0 for a clean pass. It formalizes the
existing piped/non-interactive static-table fallback into a documented machine surface, and defines
the JSON emitter (stable, camelCase field names) as the single source a README badge or dashboard can
reuse. Built entirely on the current one-shot `SpecScanner.ScanAsync` and `SpecRow`/`ScanResult`
shapes; the interactive TUI is untouched.
