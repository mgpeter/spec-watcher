# Publishing spec-watcher to winget

spec-watcher is published to the Windows Package Manager as **`UsualExpat.SpecWatcher`** by
**Usual Expat Limited**. Delivery is a self-contained, single-file portable executable shipped in a
zip via GitHub Releases; a GitHub Actions workflow builds it and opens the winget-pkgs PR on each
`vX.Y.Z` tag.

There is **no winget account or registration** — a package is just YAML manifests merged into the
public [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs) repo via PR. Your identity
is the `UsualExpat` token in the identifier; keep it consistent for all future company packages.

## Package identity

| Field | Value |
| --- | --- |
| PackageIdentifier | `UsualExpat.SpecWatcher` |
| Publisher (display) | `Usual Expat Limited` |
| Command alias | `spec-watcher` |
| License | `MIT` |
| PublisherUrl / PackageUrl | `https://github.com/mgpeter/spec-watcher` |
| PublisherSupportUrl | `https://github.com/mgpeter/spec-watcher/issues` |

Manifest source-of-truth templates live in [`packaging/winget/`](../packaging/winget/). To change the
publisher/support URLs (e.g. to a company website), edit
`packaging/winget/UsualExpat.SpecWatcher.locale.en-US.yaml`.

## One-time setup 👤

These require your credentials/accounts and are done once:

1. **Fork `microsoft/winget-pkgs`** onto the GitHub account that will raise the manifest PRs — your
   personal `mgpeter` account or a dedicated company bot account. (winget-releaser/komac push a branch
   to this fork.)
2. **Create a classic Personal Access Token** on that same account with the **`public_repo`** scope.
3. In the `mgpeter/spec-watcher` repo settings → *Secrets and variables → Actions*, add it as
   **`WINGET_TOKEN`**. (The built-in `GITHUB_TOKEN` cannot push to an external fork, so this PAT is
   required.)

## Cut a release

1. Decide the version `X.Y.Z`. The tag drives everything (`-p:Version` and the winget `PackageVersion`);
   no file edit is needed for a normal release.
2. Tag and push:
   ```sh
   git tag v1.2.3
   git push origin v1.2.3
   ```
3. The `release` workflow then:
   - builds self-contained single-file `win-x64` + `win-arm64` binaries, zips each as
     `spec-watcher-1.2.3-<rid>.zip`,
   - publishes a GitHub Release with those zips,
   - runs `winget-releaser`, which computes the SHA256s and opens/updates the winget-pkgs PR.
4. Watch the PR in `microsoft/winget-pkgs`: automated validation runs; respond to any moderator
   comment. Once merged, `winget install UsualExpat.SpecWatcher` serves the new version and
   `winget upgrade` picks it up.

## First version (v1.0.0) — one-time manual submission 👤

`winget-releaser` (Komac `update`) works by **inheriting the previous version's manifest** and bumping
the version/URLs/hashes — so the package must exist in winget-pkgs once before automation takes over.
Create `UsualExpat.SpecWatcher` **v1.0.0** manually:

1. Tag `v1.0.0` and push so CI builds the release zips (or build locally — see below).
2. Submit the first manifest with real hashes, either:
   - **Komac** (recommended): `komac update UsualExpat.SpecWatcher --version 1.0.0 --urls <zip-x64-url> <zip-arm64-url> --submit` — Komac downloads the zips, computes hashes, and (for a new package) scaffolds the manifest; verify it keeps `InstallerType: zip` + `NestedInstallerType: portable`; **or**
   - **By hand**: copy `packaging/winget/*` into a fork of winget-pkgs under
     `manifests/u/UsualExpat/SpecWatcher/1.0.0/`, fill the two `InstallerSha256` values
     (`Get-FileHash <zip> -Algorithm SHA256`), and open the PR.
3. After v1.0.0 merges, every later `vX.Y.Z` tag is fully automated by the workflow.

## Local build & validation

Build the exact artifact the pipeline ships (self-contained, single-file, **not trimmed** — trimming
breaks Spectre.Console.Cli's reflection-based command binding):

```sh
dotnet publish SpecWatcher.CLI/SpecWatcher.Console.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:Version=1.0.0 -o publish
```

Validate the manifests:

```sh
winget validate --manifest packaging/winget
```

Test a real install in **Windows Sandbox** (proves a clean machine with no .NET runtime): use
winget-pkgs' `Tools/SandboxTest.ps1`, or locally enable and install the manifest (elevated):

```powershell
winget settings --enable LocalManifestFiles
winget install --manifest packaging\winget   # requires real InstallerSha256 + reachable URLs
```

## Optional, recommended later: code signing

The published exe is unsigned, so Windows SmartScreen may warn on first run. Signing with an OV/EV
certificate or **Azure Trusted Signing** (which verifies the Usual Expat Limited legal identity)
clears this. It is **not required** for winget acceptance and is intentionally out of scope for the
initial release; add a signing step to the `build` job when ready.
