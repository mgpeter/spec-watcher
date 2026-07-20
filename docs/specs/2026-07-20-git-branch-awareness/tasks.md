# Spec Tasks

## Tasks

- [ ] 1. Git invocation seam + repo detection
  - [ ] 1.1 Add a `SpecWatcher.Tests` (xUnit) project if absent; write tests for a `GitRunner` helper
        that shells out via `Process` with `-C`, a timeout, async stdout read, and captures exit code
        (mock/point at a temp dir for the not-a-repo and git-missing paths).
  - [ ] 1.2 Implement `GitRunner`: `UseShellExecute=false`, `CreateNoWindow=true`, redirected
        stdout/err, cancellation + timeout (kill on expiry), and a "git not found / non-zero exit"
        result type instead of throwing.
  - [ ] 1.3 Implement worktree detection: `rev-parse --show-toplevel`, `rev-parse HEAD`,
        `rev-parse --abbrev-ref HEAD`; return a `GitContext(root, headSha, branch)` or "unavailable".
  - [ ] 1.4 Verify tests green (real repo → context resolved; temp non-git dir → unavailable, no throw).

- [ ] 2. `git log` parse + commit→spec / branch→spec mapping (pure)
  - [ ] 2.1 Write tests for a pure `GitEnricher.Map(logStdout, branch, specsDir, folders)` function
        using canned `%x1e/%x1f`-delimited log strings: correct bucketing by first path segment,
        `LastCommit`/`RecentCommits` per spec, ISO date parse, and both branch-match and detached/no-
        match (last-touched) `IsCurrent` selection.
  - [ ] 2.2 Implement the log-output parser (record/unit separators, `--name-only` path lines).
  - [ ] 2.3 Implement commit→spec bucketing and branch-slug normalization + token-subsequence match,
        with the newest-touched-spec fallback.
  - [ ] 2.4 Verify tests green, including edge cases (paths outside specs dir skipped; empty log).

- [ ] 3. HEAD-sha cache + bounded, off-thread invocation
  - [ ] 3.1 Write tests asserting the expensive `git log` runs only when (root, HEAD sha, branch)
        changes and is skipped (cache reused) when unchanged.
  - [ ] 3.2 Implement the static cache keyed by (root, headSha, branch); build the bounded log command
        (`-n 300 --since=180.days --name-only --pretty=…`); reuse cached buckets on a cache hit.
  - [ ] 3.3 Add a timeout/degrade path: on log timeout keep prior buckets (or empty) and mark the scan
        git-degraded without failing the scan.
  - [ ] 3.4 Verify tests green.

- [ ] 4. Model + scanner wiring
  - [ ] 4.1 Write tests for the enriched scan: `SpecRow` gains `IsCurrent`, `LastCommit`,
        `RecentCommits`; `ScanResult` gains `GitAvailable`, `CurrentBranch`; a non-git scan yields
        rows equivalent to today (git fields null/empty).
  - [ ] 4.2 Extend `Models.cs` (add `GitCommit`, new `SpecRow`/`ScanResult` fields, immutable).
  - [ ] 4.3 Invoke `GitEnricher` from `SpecScanner.ScanAsync` after the existing folder parse, inside
        the current `Task.Run`, threading the `CancellationToken`.
  - [ ] 4.4 Verify tests green (git repo enriches; non-git degrades identically to today).

- [ ] 5. Rendering: Last-commit column, `◈ current` badge, detail commits section
  - [ ] 5.1 Write tests for the relative-time formatter (`just now / Nm / Nh / Nd / Nw ago`) and the
        list layout width math when the column is shown vs. hidden (`!GitAvailable`).
  - [ ] 5.2 Add the **Last commit** column + `LastCommitW`, recompute `nameW`, and hide the column
        when git is unavailable so today's layout is preserved.
  - [ ] 5.3 Render the `◈ current` badge (distinct from the selection caret) and surface the branch in
        the title/footer.
  - [ ] 5.4 Append a "Recent commits" section to `BuildDetailLines` (short sha · relative time ·
        author — subject), omitted when empty, escaped via `Markup.Escape`.
  - [ ] 5.5 Verify tests green and manually confirm against the spec-watcher repo itself and a temp
        non-git dir.

- [ ] 6. Robustness pass & final verification
  - [ ] 6.1 Write/confirm tests for the three degradation paths: no git repo, `git` not on PATH, and a
        `git log` timeout — each renders exactly as today with no error and no UI-thread blocking.
  - [ ] 6.2 Confirm no `git` subprocess is ever started on the UI thread (all inside `ScanAsync`).
  - [ ] 6.3 Run the full test suite and confirm all green.
