# Technical Specification

This is the technical specification for the spec detailed in
@docs/specs/2026-07-21-winget-publishing/spec.md

## Identity & naming (decided)

- **PackageIdentifier:** `UsualExpat.SpecWatcher` (the `UsualExpat` token is the company's stable
  winget publisher identity — reuse it for any future packages).
- **Publisher (display):** `Usual Expat Limited`.
- **PackageName:** `spec-watcher`. **Command alias:** `spec-watcher`.
- **License:** `MIT` (`LicenseUrl` → the repo `LICENSE`).
- **PublisherUrl / PackageUrl:** `https://github.com/mgpeter/spec-watcher` (or a company URL if
  preferred). **PublisherSupportUrl:** the repo issues page.
- **Moderation note:** winget-pkgs is PR-based, no account/fee. Publishing an own MIT tool under an
  own company name is the normal path; there is no impersonation concern.

## Build: self-contained single-file portable

Produce a framework-independent single exe per architecture, versioned from the tag:

```
dotnet publish SpecWatcher.CLI/SpecWatcher.Console.csproj -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:Version=$VERSION
# repeat with -r win-arm64
```

- `InvariantGlobalization` is already `true` in the csproj (smaller, no ICU) — keep it.
- **Trimming:** `PublishTrimmed` can roughly halve size but Spectre.Console uses reflection; it must be
  smoke-tested (list + detail + headless). If any rendering breaks, ship **untrimmed** (the safe
  default for v1); trimming is an optimization, not a requirement.
- Optional `-p:PublishReadyToRun=true` for faster cold start (larger file) — evaluate, not required.
- **Versioning contract:** the git tag `vX.Y.Z`, the assembly `Version`, and the winget
  `PackageVersion` must all be the same `X.Y.Z`. CI derives `$VERSION` from the tag (strip the `v`).
- **Asset naming:** zip the single exe per arch as `spec-watcher-X.Y.Z-win-x64.zip` /
  `...-win-arm64.zip` (each zip contains one `spec-watcher.exe` at its root) and attach them to the
  GitHub Release. These URLs are the winget `InstallerUrl`s. (A zip + nested portable is used instead
  of a bare exe so the installed command alias is a clean `spec-watcher` rather than the versioned
  asset filename.)

## Winget manifests (schema 1.9+, `InstallerType: portable`)

Three files under `manifests/u/UsualExpat/SpecWatcher/<version>/` in a fork of `microsoft/winget-pkgs`.
Generate them with the tooling below rather than hand-editing; the shapes are:

**`UsualExpat.SpecWatcher.installer.yaml`** (essentials) — zip carrying a nested portable exe, so the
installed alias is `spec-watcher` (a bare-exe portable would alias to the versioned filename, and
`PortableCommandAlias` is only valid under `NestedInstallerFiles`, not on a top-level installer):
```yaml
PackageIdentifier: UsualExpat.SpecWatcher
PackageVersion: X.Y.Z
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: spec-watcher.exe
    PortableCommandAlias: spec-watcher
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/mgpeter/spec-watcher/releases/download/vX.Y.Z/spec-watcher-X.Y.Z-win-x64.zip
    InstallerSha256: <sha256>
  - Architecture: arm64
    InstallerUrl: https://github.com/mgpeter/spec-watcher/releases/download/vX.Y.Z/spec-watcher-X.Y.Z-win-arm64.zip
    InstallerSha256: <sha256>
ManifestType: installer
ManifestVersion: 1.9.0
```

**`UsualExpat.SpecWatcher.locale.en-US.yaml`** — `Publisher: Usual Expat Limited`, `Author`,
`PackageName`, `PublisherUrl`, `PublisherSupportUrl`, `PackageUrl`, `License: MIT`, `LicenseUrl`,
`ShortDescription`, `Description`, `Tags` (e.g. `cli`, `tui`, `specs`, `dotnet`, `agent-os`),
`ReleaseNotesUrl`, `ManifestType: defaultLocale`, `ManifestVersion: 1.9.0`.

**`UsualExpat.SpecWatcher.yaml`** (version) — `PackageIdentifier`, `PackageVersion`,
`DefaultLocale: en-US`, `ManifestType: version`, `ManifestVersion: 1.9.0`.

Portable install semantics: winget downloads the exe and registers a symlink/alias
(`%LOCALAPPDATA%\Microsoft\WinGet\Links`, already on PATH) named per `PortableCommandAlias`, so
`spec-watcher` works in any new shell. `winget uninstall` removes it cleanly.

## Automated submission pipeline

`.github/workflows/release.yml`, triggered on `push` tags matching `v*`:

1. **build** (matrix: `win-x64`, `win-arm64`): checkout → setup .NET 10 → `dotnet publish` (above) →
   rename to `spec-watcher-$VERSION-<rid>.exe` → upload artifacts.
2. **release**: create/att­ach the GitHub Release for the tag with both exes
   (`softprops/action-gh-release`).
3. **winget**: after the release is published, submit to winget-pkgs. Use the
   **`vedantmgoyal9/winget-releaser`** action (Komac-backed) with `identifier: UsualExpat.SpecWatcher`,
   an `installers-regex` matching the two exes, and `token: ${{ secrets.WINGET_TOKEN }}`.

**Required secret `WINGET_TOKEN`:** a classic GitHub PAT with `public_repo` scope, on the account that
owns the fork of `microsoft/winget-pkgs` used to raise PRs. (The default `GITHUB_TOKEN` cannot push to
that external fork.)

**First-version handling:** the very first manifest may need a manual submission (see runbook) if the
action's new-package path is unavailable; every subsequent tag is fully automated. Confirm at
implementation time whether `winget-releaser` creates the initial package or only updates existing
ones, and fall back to a one-time `komac`/`wingetcreate` submission if needed.

## Local validation & test (before/first PR)

- `winget validate --manifest <folder>` — schema/lint.
- `winget install --manifest <folder>` inside **Windows Sandbox** (winget-pkgs ships
  `Tools/SandboxTest.ps1`) — proves a clean install with no .NET runtime present and `spec-watcher` on
  PATH.
- Enable `winget settings` local-manifest support for the manual test.

## Publishing runbook (`docs/`)

A short doc covering: the identity table above; creating/holding the winget-pkgs fork + `WINGET_TOKEN`;
setting `PublisherUrl`/support URLs; the "cut a release" steps (bump version → tag `vX.Y.Z` → push →
CI opens PR → watch validation); and local validation commands. Note code signing as a recommended
future step (Azure Trusted Signing) to clear SmartScreen, explicitly out of scope here.

## External Dependencies (Conditional)

New tooling/infra (no new runtime libraries in the app itself):

- **GitHub Actions** — `actions/setup-dotnet`, `softprops/action-gh-release`,
  `vedantmgoyal9/winget-releaser`. **Justification:** hands-off build + release + winget PR per tag.
- **Komac** (used by the releaser action; or standalone/`wingetcreate` for the manual first submission)
  — generates and submits winget manifests with computed SHA256. **Justification:** manifests should be
  tool-generated for correctness, not hand-maintained.
- **A GitHub PAT (`WINGET_TOKEN`)** and a fork of `microsoft/winget-pkgs`. **Justification:** required
  to raise the manifest PR from CI.
