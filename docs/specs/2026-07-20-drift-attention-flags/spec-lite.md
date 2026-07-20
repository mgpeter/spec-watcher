Add an on-screen attention layer to the list view: a compact per-row glyph flagging specs whose
declared status contradicts their checkbox progress (`⚠ drift`) or that have gone quiet (`◍ idle`),
plus a `!` key that jumps the selection between only the flagged rows and a `⚠ N` footer count. The
glyph carries the meaning (colour is never the sole signal) and the whole layer is dismissible via
`--no-flags` or a runtime toggle. It consumes the `DriftState`/`DriftReason` produced by the sibling
spec `2026-07-20-spec-drift-detection` and degrades to an inert, blank state when that data is absent.
