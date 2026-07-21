# Spec Requirements Document

> Spec: Winget Publishing
> Created: 2026-07-21
> Status: Planning

## Overview

Publish spec-watcher to the Windows Package Manager (winget) under the publisher **Usual Expat
Limited**, so anyone can install and update it with `winget install UsualExpat.SpecWatcher`. Delivery
is a self-contained, single-file portable executable distributed via tagged GitHub Releases, with a
GitHub Actions workflow that builds the binaries and opens the winget-pkgs pull request automatically
on every version tag.

## User Stories

### Install from winget

As a Windows developer, I want to run `winget install UsualExpat.SpecWatcher`, so that I get
`spec-watcher` on my PATH in one command without cloning the repo, installing the .NET SDK, or
building anything.

The user runs the install; winget downloads the version's self-contained exe from the GitHub Release,
registers a `spec-watcher` command alias, and the tool runs from any terminal. `winget upgrade` later
pulls the next published version.

### One-tag release for the maintainer

As the maintainer at Usual Expat Limited, I want pushing a `vX.Y.Z` tag to build the release binaries
and open the winget update PR for me, so that shipping a new version is a single action and the
manifest version, assets, and SHA256 hashes are always consistent.

Pushing the tag triggers CI: it publishes `win-x64` and `win-arm64` single-file binaries, attaches
them to a GitHub Release, then runs the winget submission tool which computes hashes and opens (or
updates) the pull request to `microsoft/winget-pkgs`.

### Trustworthy publisher identity

As a technical lead evaluating the tool, I want `winget show UsualExpat.SpecWatcher` to display a
clear publisher (Usual Expat Limited), homepage, license (MIT), and description, so that I can trust
what I'm installing.

## Spec Scope

1. **Self-contained portable build** — a repeatable `dotnet publish` producing a single-file,
   framework-independent `spec-watcher.exe` for `win-x64` and `win-arm64`, versioned from the git tag.
2. **GitHub Release artifacts** — each `vX.Y.Z` tag yields a GitHub Release with the per-architecture
   binaries as downloadable assets at stable URLs (the winget download source).
3. **Winget manifests** — the three-file manifest set (version / installer / locale) for
   `UsualExpat.SpecWatcher` using `InstallerType: portable`, `PortableCommandAlias: spec-watcher`, and
   `Publisher: Usual Expat Limited`.
4. **Automated submission pipeline** — a GitHub Actions workflow that builds, releases, and opens the
   winget-pkgs PR on each tag, using a stored GitHub token; plus the documented one-time first
   submission and required secrets.
5. **Publishing runbook** — `docs/` instructions covering the identifier/publisher identity, the
   token/fork prerequisites, local manifest validation, and how to cut a release.

## Out of Scope

- Code signing / an EV or Azure Trusted Signing certificate (SmartScreen cleanliness) — a recommended
  follow-up, not required for winget acceptance.
- MSIX or MSI installers, Add/Remove Programs entries, or Microsoft Store submission.
- Publishing to other package managers (Scoop, Chocolatey, Homebrew) or as a `dotnet tool`.
- Any change to spec-watcher's runtime behavior, features, or CLI surface.
- Trimming/size optimization beyond what is verified safe with Spectre.Console.

## Expected Deliverable

1. Pushing a `vX.Y.Z` tag produces a GitHub Release with `win-x64` and `win-arm64` self-contained
   single-file binaries, and automatically opens a valid PR to `microsoft/winget-pkgs`.
2. The submitted manifests pass `winget validate` and a Windows Sandbox `winget install --manifest`
   test, installing a working `spec-watcher` command under the Usual Expat Limited publisher.
3. After the PR merges, `winget install UsualExpat.SpecWatcher` installs the tool on a clean Windows
   machine (no .NET runtime present) and `spec-watcher` runs from any terminal; `winget show` displays
   the correct publisher, license, and description.
