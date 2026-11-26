#!/usr/bin/env bash
# Store the token the release workflow uses to open the winget-pkgs PR (from the mgpeter fork of
# microsoft/winget-pkgs), as the WINGET_TOKEN Actions secret on mgpeter/spec-watcher.
#
#   packaging/winget/set-winget-token.sh [--no-open]
#
# winget-releaser needs a classic token with the public_repo and workflow scopes: a fine-grained
# token cannot open a PR against another owner's repo, and syncing the fork with upstream fails
# without workflow whenever microsoft/winget-pkgs has changed its own workflow files.
#
# The token is read with echo off and piped straight into gh, so it never reaches shell history,
# the process list or disk. Re-run it whenever the token expires, then re-run the failed
# "submit to winget" job.
set -euo pipefail

repo="mgpeter/spec-watcher"
secret="WINGET_TOKEN"
create_url="https://github.com/settings/tokens/new?scopes=public_repo,workflow&description=spec-watcher+winget+releaser"

command -v gh >/dev/null || { echo "gh is not installed: brew install gh" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "gh is not logged in: gh auth login" >&2; exit 1; }

cat <<EOF
1. Create a classic token (the link pre-fills the note and scopes):
   $create_url

   Scopes:      public_repo and workflow only (already ticked)
   Expiration:  up to you; re-run this script when it runs out

2. Paste it below.

EOF

if [[ "${1:-}" != "--no-open" ]] && command -v open >/dev/null; then
  open "$create_url"
fi

read -rsp "Token (input hidden): " token
echo
[[ -n "$token" ]] || { echo "No token entered, nothing changed." >&2; exit 1; }
[[ "$token" == ghp_* ]] || echo "Warning: this is not a classic token (no ghp_ prefix)." >&2

# A classic token lists its scopes in the X-OAuth-Scopes response header.
if ! login=$(GH_TOKEN="$token" gh api user --jq .login 2>/dev/null); then
  echo "GitHub rejected the token, nothing changed." >&2
  exit 1
fi
scopes=$(GH_TOKEN="$token" gh api -i user 2>/dev/null | tr -d '\r' | grep -i '^x-oauth-scopes:' | cut -d: -f2- | head -n1)
scopes="${scopes# }"
if [[ ! ",${scopes// /}," =~ ,(public_repo|repo), || ! ",${scopes// /}," =~ ,workflow, ]]; then
  echo "The token needs the public_repo and workflow scopes (it has: ${scopes:-none}), nothing changed." >&2
  exit 1
fi
echo "Token is valid, belongs to $login and has: $scopes."

printf '%s' "$token" | gh secret set "$secret" --repo "$repo"
unset token

gh secret list --repo "$repo" | grep "^$secret" || true
echo "Done. Re-run a failed winget job with: gh run rerun <run-id> --failed"
