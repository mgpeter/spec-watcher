Read the `.git` already in the watched repo (locally, no network or auth) to make the board branch-
and commit-aware: on each off-thread scan, resolve the current branch and a bounded, HEAD-sha-cached
`git log` over the specs dir, map commits to spec folders and the current branch to the active spec,
mark it with a `◈ current` badge, add a **Last commit** column ("2h ago · a1b2c3d"), and list a
spec's recent touching commits in its detail view. Fully passive and read-only; degrades cleanly to
today's behavior when the specs dir isn't in a git repo, `git` isn't installed, or history is too big.
