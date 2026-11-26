#!/usr/bin/env bash
# Store the token the release workflow uses to push Formula/spec-watcher.rb to mgpeter/homebrew-tap,
# as the HOMEBREW_TAP_TOKEN Actions secret on mgpeter/spec-watcher.
#
#   packaging/homebrew/set-tap-token.sh [--no-open]
#
# The token is read with echo off and piped straight into gh, so it never reaches shell history,
# the process list or disk. Re-run it whenever the token expires.
set -euo pipefail

repo="mgpeter/spec-watcher"
tap="mgpeter/homebrew-tap"
secret="HOMEBREW_TAP_TOKEN"
create_url="https://github.com/settings/personal-access-tokens/new?name=spec-watcher+homebrew+tap&description=Release+workflow+pushes+Formula%2Fspec-watcher.rb&target_name=mgpeter&expires_in=366&contents=write"

command -v gh >/dev/null || { echo "gh is not installed: brew install gh" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "gh is not logged in: gh auth login" >&2; exit 1; }

cat <<EOF
1. Create a fine-grained token (the link pre-fills what it can):
   $create_url

   Resource owner:     mgpeter
   Repository access:  Only select repositories -> $tap
   Permissions:        Repository permissions -> Contents -> Read and write
   Expiration:         up to you; re-run this script when it runs out

2. Paste it below.

EOF

if [[ "${1:-}" != "--no-open" ]] && command -v open >/dev/null; then
  open "$create_url"
fi

read -rsp "Token (input hidden): " token
echo
[[ -n "$token" ]] || { echo "No token entered, nothing changed." >&2; exit 1; }
[[ "$token" == github_pat_* ]] || echo "Warning: this is not a fine-grained token (no github_pat_ prefix)." >&2

if ! login=$(GH_TOKEN="$token" gh api user --jq .login 2>/dev/null); then
  echo "GitHub rejected the token, nothing changed." >&2
  exit 1
fi
echo "Token is valid and belongs to $login."

printf '%s' "$token" | gh secret set "$secret" --repo "$repo"
unset token

gh secret list --repo "$repo" | grep "^$secret" || true
echo "Done. The next vX.Y.Z tag will push the formula to $tap."
