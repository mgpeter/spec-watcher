# Spec Lite

Add a drift-detection engine that flags when a spec's declared `> Status:` contradicts its real
checkbox progress — *Complete* with unchecked boxes (`Overstated`) or still *Planning* while tasks
are ticked (`Understated`) — plus an opt-in `Idle` state for *In progress* specs untouched on disk
past a threshold. Drift is computed by a pure, unit-tested `DriftAnalyzer` and exposed as
`DriftState Drift` + `string? DriftReason` on `SpecRow`, with file mtime captured as
`LastModifiedUtc`. Idle detection is gated behind `--drift-idle-days` (off by default, so fresh
clones with reset mtimes stay quiet). This is the analytics half only; rendering the flag belongs
to the sibling ux-nav spec `2026-07-20-drift-attention-flags`.
