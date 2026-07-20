# Technical Specification

This is the technical specification for the spec detailed in
`docs/specs/2026-07-20-git-branch-awareness/spec.md`.

## Approach: shell out to `git` (not a library)

Invoke the system `git` via `System.Diagnostics.Process`, not LibGit2Sharp.

Justification:
- **Zero new dependency, honors the ethos.** LibGit2Sharp drags in platform-specific native binaries
  (~MBs) that fight the roadmap's single-file / self-contained packaging goal. Shelling out adds no
  package and no native asset.
- **The operations are trivial and read-only:** `rev-parse` and one `log`. There is no need for an
  object-database API.
- **`git` is already the user's tool.** Anyone using the Agent OS date-prefixed spec convention in a
  repo has `git`. When they don't (or the specs dir isn't a repo), we degrade — see Fallbacks.

Cost of this choice: a hard dependency on `git` being on `PATH`, mitigated by graceful degradation.

## Where the code lives (grounded in the current build)

- `WatchSettings.cs` — `RepoPath` is already known and `ResolvedSpecsDir` is derived; the `.git` is
  right there. No new option is required for the MVP (the git worktree root is discovered from the
  specs dir at runtime).
- `SpecScanner.cs` — `ScanAsync` already runs the whole scan inside `Task.Run` (off the UI thread).
  Git enrichment is invoked here, after the existing folder parse, so every `git` subprocess runs off
  the UI thread exactly like today's file parsing. A new static helper `GitEnricher` holds the logic.
- `Models.cs` — `SpecRow` (immutable record) gains git fields; a new `GitCommit` record is added; and
  `ScanResult` gains repo-level git context. Immutability is preserved so publishing via
  `Volatile.Write` stays safe.
- `WatchCommand.cs` — the list gains a **Last commit** column and a `◈ current` caret/badge; the
  detail document (`BuildDetailLines`) gains a "Recent commits" section.

## Data model changes (`Models.cs`)

```csharp
public sealed record GitCommit(
    string Sha, string ShortSha, DateTimeOffset When, string Author, string Subject);

// added to SpecRow:
//   bool IsCurrent               — matches current branch (or last-touched fallback)
//   GitCommit? LastCommit        — most recent commit touching this spec folder (null if none)
//   ImmutableArray<GitCommit> RecentCommits   — small, newest-first, for the detail view

// added to ScanResult:
//   bool GitAvailable            — true when a git worktree + git binary were usable this scan
//   string? CurrentBranch        — abbrev branch name, or null (detached / unavailable)
```

`RecentCommits` defaults to `ImmutableArray<GitCommit>.Empty`; `LastCommit` defaults to null so a
non-git scan produces rows identical in behavior to today.

## Git commands (all with `-C <worktreeRoot>`, read-only)

1. **Worktree root / repo detection:**
   `git -C <specsDir> rev-parse --show-toplevel`
   Non-zero exit (or process fails to start) ⇒ not a repo / no git ⇒ `GitAvailable = false`.
2. **Current branch:** `git -C <root> rev-parse --abbrev-ref HEAD` (`HEAD` string ⇒ detached).
3. **HEAD sha:** `git -C <root> rev-parse HEAD` (cheap; drives the cache key).
4. **Recent commits over the specs dir (the one expensive call, bounded):**
   `git -C <root> log --no-color -n 300 --since=180.days --name-only \`
   `  --pretty=format:%x1e%H%x1f%h%x1f%cI%x1f%an%x1f%s -- <specsDirRelativeToRoot>`
   - `%x1e` (record sep) delimits commits; `%x1f` (unit sep) delimits fields; the `--name-only`
     paths follow each header line until the next record sep. These control chars can't appear in
     git field output, so parsing is robust without quoting.
   - `%cI` = committer date, strict ISO-8601 ⇒ parse with `DateTimeOffset.Parse`.

Only **one** `log` subprocess runs per scan (covering all specs), not one per folder — this is the
kill-check's "scope to one bounded call."

## Mapping rules

Let `specSlug` = the spec's folder name (e.g. `2026-07-19-starter-check-selection`) and
`bareSlug` = that name with a leading `YYYY-MM-DD-` prefix stripped.

- **Commit → spec:** for each commit in the `log` output, take each touched path, make it relative to
  the specs dir, and take its **first path segment** — that is the spec folder. Bucket commits under
  that folder (newest-first, as git already emits). A spec's `LastCommit` = bucket head;
  `RecentCommits` = first _k_ (e.g. 10) of the bucket.
- **Branch → spec (primary):** normalize the current branch by lowercasing and splitting on
  non-alphanumeric runs. A spec is `IsCurrent` when its `specSlug` or `bareSlug` (also normalized)
  is a contiguous token-subsequence of the branch — e.g. branch `feat/starter-check-selection`
  matches `2026-07-19-starter-check-selection`. First/longest match wins; ties broken by newest
  spec folder.
- **Branch → spec (fallback):** when the branch is detached, or no spec slug is encoded in it, set
  `IsCurrent` on the spec whose bucket contains the newest commit reachable from HEAD (i.e. the
  top-most commit in the whole `log` output that maps to a spec). This is the kill-check's
  "last-touched-path" fallback. If even that is empty, no row is marked current.

## HEAD-sha cache

`GitEnricher` holds static cache state: `(lastRoot, lastHeadSha, lastBranch, cachedBuckets)`.

- Each scan runs the cheap `rev-parse --show-toplevel` + `rev-parse HEAD` +
  `rev-parse --abbrev-ref HEAD` (all microseconds).
- If `root`, `HEAD sha`, **and** branch are unchanged from the last enrichment, **reuse
  `cachedBuckets`** and skip the expensive `git log` entirely — only re-attach buckets to the
  freshly-scanned `SpecRow`s. HEAD moves on commit/checkout/reset, so the cache invalidates exactly
  when history could have changed.
- Branch is part of the key because `IsCurrent` depends on it even when HEAD is unchanged
  (e.g. switching between two branches at the same commit).

## Off-thread execution & timeouts

- All `git` calls happen inside `SpecScanner.ScanAsync`'s existing `Task.Run`, honoring the passed
  `CancellationToken`. The UI thread never starts a process.
- Each subprocess is wrapped with a timeout (e.g. 3s; the `log` is the only one that can be slow on a
  pathological history). On timeout, kill the process and treat the call as failed for this scan
  (keep the previous cached buckets if any; otherwise degrade to no git info). Read stdout
  asynchronously to avoid pipe-buffer deadlocks.
- `git` is spawned with `RedirectStandardOutput/Error = true`, `UseShellExecute = false`,
  `CreateNoWindow = true`, and a clean working dir via `-C` (never relying on the process CWD).

## Fallbacks (kill-check coverage)

- **Not a git repo / `git` not installed:** `rev-parse --show-toplevel` fails or `Process.Start`
  throws `Win32Exception` ⇒ `GitAvailable = false`, all git fields null/empty. The list **hides** the
  Last commit column and the detail view omits "Recent commits" — output is byte-for-byte today's.
- **Huge history:** bounded by `-n 300 --since=180.days` **and** the per-process timeout; the
  HEAD-sha cache means the bound is paid at most once per HEAD change.
- **Branch doesn't encode the slug:** falls back to last-touched-path mapping (see Mapping rules).
- **Specs dir outside the worktree tree / submodule oddities:** if a spec path can't be made relative
  to the specs dir, that commit is skipped rather than mis-bucketed.

## Rendering (`WatchCommand.cs`)

- **List column:** add `LastCommitW` (~18 cols; e.g. `2h ago · a1b2c3d`) between Progress and Folder,
  recomputing the flexible Name width (`nameW`) accordingly. When `!GitAvailable`, omit the column so
  widths match today. Relative time helper: `just now / Nm / Nh / Nd / Nw ago`.
- **Current badge:** when `row.IsCurrent`, render the caret as `◈` in an accent color (distinct from
  the selection `❯`) and/or prefix the name; also surface the branch in the title/footer
  (`on <branch>`). Selection and current are independent states.
- **Detail view:** in `BuildDetailLines`, after the Tasks section, append `──── Recent commits ────`
  then one line per `RecentCommits` entry: `<shortSha>  <relTime>  <author> — <subject>` (escaped via
  `Markup.Escape`). Omit the whole section when `RecentCommits` is empty.

## Testing notes

Git-dependent logic is isolated behind a thin seam: a `parse` function that turns raw `git log`
stdout + a branch name + the scanned folder list into enriched rows. That pure function is unit-
tested with canned `git log` strings (no real repo needed). Detection/timeout/degradation are tested
by pointing the scanner at a temp non-git directory and asserting rows render exactly as today. A
test project (`SpecWatcher.Tests`, xUnit) is added if one does not yet exist (aligns with the
roadmap's Phase-2 parser-tests item).
