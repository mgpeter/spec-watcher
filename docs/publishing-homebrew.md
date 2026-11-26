# Publishing spec-watcher to Homebrew

spec-watcher is installed on macOS from the tap **`mgpeter/homebrew-tap`**:

```sh
brew install mgpeter/tap/spec-watcher
```

The formula installs the same self-contained, single-file binary as the winget package, built for
`osx-arm64` and `osx-x64` and shipped as tarballs on the GitHub Release. Users need no .NET runtime.

A tap is just a public GitHub repo named `homebrew-<name>`; there is no Homebrew account, review or
approval. `brew install mgpeter/tap/spec-watcher` taps it automatically on first use.

## Pieces

| Piece | Where |
| --- | --- |
| Tap repo | [`mgpeter/homebrew-tap`](https://github.com/mgpeter/homebrew-tap) |
| Formula (generated) | `Formula/spec-watcher.rb` in the tap |
| Formula generator | [`packaging/homebrew/render-formula.sh`](../packaging/homebrew/render-formula.sh) |
| Token setup | [`packaging/homebrew/set-tap-token.sh`](../packaging/homebrew/set-tap-token.sh) |
| macOS build | `build-macos` job in [`release.yml`](../.github/workflows/release.yml) |
| Tap update | `homebrew` job in [`release.yml`](../.github/workflows/release.yml) |

The formula is never edited by hand. To change its description, test or layout, edit
`render-formula.sh`; the next release overwrites the tap's copy.

## One-time setup 👤

Run this from the repo root:

```sh
packaging/homebrew/set-tap-token.sh
```

It opens the token page with the fields pre-filled, asks you to paste the token with input hidden,
checks it with GitHub and stores it as the **`HOMEBREW_TAP_TOKEN`** Actions secret on
`mgpeter/spec-watcher`. The token never reaches shell history or disk. On the token page set:

- Repository access: *Only select repositories* → `mgpeter/homebrew-tap`
- Permissions: *Contents* → **Read and write** (nothing else)

The built-in `GITHUB_TOKEN` cannot push to another repo, which is why this token is needed.

Fine-grained tokens expire; when it does, the `homebrew` job fails at checkout. Run the script
again with a new token, then re-run the failed job.

## Cut a release

Same tag as winget, see [publishing-winget.md](publishing-winget.md#cut-a-release). On `vX.Y.Z`:

1. `build-macos` publishes both macOS binaries on a macOS runner and tars each as
   `spec-watcher-X.Y.Z-<rid>.tar.gz`. Building on macOS matters: the SDK ad-hoc signs the binary
   there, and an unsigned arm64 binary is killed on launch.
2. `release` attaches the tarballs to the GitHub Release alongside the Windows zips.
3. `homebrew` hashes the tarballs, writes `Formula/spec-watcher.rb` and pushes it to the tap as
   `Update spec-watcher to X.Y.Z`. `brew upgrade spec-watcher` picks it up straight away.

## Gatekeeper and signing

The binaries are ad-hoc signed, not signed with an Apple Developer ID or notarised. That is fine for
a formula: Homebrew does not quarantine formula downloads, so Gatekeeper never checks them. It would
**not** be fine for a cask (casks are quarantined), or for users who download the tarball in a
browser. Moving to a cask or homebrew-core later needs a Developer ID and notarisation first.

## Local build and test

Build a tarball and try the formula from a throwaway local tap:

```sh
v=1.2.0
dotnet publish SpecWatcher.CLI/SpecWatcher.Console.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true -p:Version=$v -o /tmp/sw/pub
tar -czf /tmp/sw/spec-watcher-$v-osx-arm64.tar.gz -C /tmp/sw/pub spec-watcher
cp /tmp/sw/spec-watcher-$v-osx-arm64.tar.gz /tmp/sw/spec-watcher-$v-osx-x64.tar.gz   # placeholder

sha=$(shasum -a 256 /tmp/sw/spec-watcher-$v-osx-arm64.tar.gz | cut -d' ' -f1)
brew tap-new --no-git local/swtest
packaging/homebrew/render-formula.sh $v $sha $sha file:///tmp/sw \
  > "$(brew --repository local/swtest)/Formula/spec-watcher.rb"

brew install --formula local/swtest/spec-watcher
brew test local/swtest/spec-watcher
brew audit --strict --formula local/swtest/spec-watcher

brew uninstall spec-watcher && brew untap local/swtest
```
