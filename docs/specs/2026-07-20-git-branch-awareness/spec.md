# Spec Requirements Document

> Spec: Git Branch Awareness
> Created: 2026-07-20
> Status: Planning

## Overview

Make spec-watcher aware of the `.git` that already sits in the watched repo, so the board answers
the one question a static status table can't: *which spec am I actually working on right now, and
which have gone quiet.* On each scan, the watcher reads the repo's current branch and recent commit
history (locally, no network, no auth), maps them to specs, marks the active spec with a `◈ current`
badge, adds a **Last commit** column ("2h ago · a1b2c3d"), and lists the recent commits that touched
a spec in its detail view. This stays fully inside the passive/offline ethos — it only *reads* git.

## User Stories

- **As a developer with many specs open**, I want the spec I'm currently on a branch for to be
  visibly marked, so I can find my place at a glance without scanning folder names.
- **As a tech lead reviewing a repo**, I want to see when each spec was last touched by a commit, so
  I can spot specs that are declared "In progress" but have actually gone quiet.
- **As someone reading a single spec**, I want to see the recent commits that touched its folder, so
  I understand its momentum without leaving the terminal for `git log`.
- **As a user whose specs live outside a git repo (or who has no `git` installed)**, I want the tool
  to keep working exactly as before, with git columns simply absent — never an error, never a hang.

## Spec Scope

1. **Repo/branch detection** — on scan, resolve the git worktree root containing the specs dir and
   read the current branch name and HEAD sha, off the UI thread.
2. **Commit → spec mapping** — a single bounded `git log` over the specs dir buckets recent commits
   by top-level spec folder; each spec gets its most-recent touching commit and a short recent list.
3. **Branch → spec mapping** — match the current branch slug against spec folder slugs to pick the
   `◈ current` spec; when the branch name doesn't encode a slug, fall back to the spec touched by the
   newest commit on the current branch.
4. **HEAD-sha cache** — cache the expensive `git log` result keyed by (HEAD sha + branch name); reuse
   it on scans where neither changed, so we don't shell out to `git log` on every timer tick.
5. **Rendering** — a new **Last commit** list column, a `◈ current` badge on the active row, and a
   "Recent commits" section appended to the detail view.
6. **Graceful degradation** — no git repo, no `git` on PATH, or a `git log` that exceeds a timeout
   all degrade to today's behavior (git fields null/absent), never blocking or erroring the scan.

## Out of Scope

- Any network, remote, forge (GitHub/GitLab), PR, or CI awareness — that is a separate, later spec.
- Writing to git (commits, checkout, branch creation) — this feature is strictly read-only.
- Blame on the `Status:` line, per-status transition timelines, or velocity/ETA analytics (intel).
- A configurable "stale after N days" threshold or a `⚠ stale` badge (a follow-on; this spec only
  surfaces the raw "last commit" recency).
- Bundling a git library or native binaries; adding a package-manager dependency on `git`.

## Expected Deliverable

1. Running spec-watcher against a git repo shows a `◈ current` badge on the spec matching the checked-
   out branch and a **Last commit** column with a human relative time + short sha for every spec that
   git history has touched.
2. Opening a spec's detail view shows a "Recent commits" section listing recent commits (short sha,
   relative time, author, subject) that touched that spec folder.
3. Running against a non-git directory, or with `git` unavailable, renders exactly as today with the
   git column/section absent — verified by tests — and scans are never slower than one bounded,
   cached `git log` per HEAD change.
