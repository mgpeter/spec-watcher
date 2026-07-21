# Spec Tasks

These are the tasks to be completed for the spec detailed in
`docs/specs/2026-07-21-winget-publishing/spec.md`.

> Reference: `docs/specs/2026-07-21-winget-publishing/sub-specs/technical-spec.md`
>
> Note: this is a packaging/release spec, so "verify" sub-tasks are validations (a working
> single-file exe, `winget validate`, YAML/workflow lint) rather than unit tests. Steps that require
> external credentials or accounts (the winget-pkgs fork, the `WINGET_TOKEN` PAT, pushing a real tag,
> and the actual PR to `microsoft/winget-pkgs`) are performed by the maintainer — they are documented
> in the runbook and marked 👤 below.

## Tasks

- [x] 1. Self-contained portable build
  - [x] 1.1 Add version + single-file/self-contained publish properties to
        `SpecWatcher.Console.csproj` (AssemblyName `spec-watcher`, `Version` overridable via
        `-p:Version=`, RIDs not hardcoded).
  - [x] 1.2 Produce a `win-x64` single-file self-contained build and confirm the exe runs standalone
        (list, detail, and `--once --format json`) with no .NET runtime dependency.
  - [x] 1.3 Evaluate `PublishTrimmed`: smoke-test a trimmed build; if Spectre.Console rendering
        breaks, keep it untrimmed (the safe default) and record the decision.
  - [x] 1.4 Verify a `win-arm64` publish succeeds (build only) and finalize the publish command/props.

- [x] 2. Release build & artifacts workflow (CI)
  - [x] 2.1 Add `.github/workflows/release.yml` triggered on `v*` tags with a build matrix
        (`win-x64`, `win-arm64`) that publishes the single-file exe and derives the version from the tag.
  - [x] 2.2 Rename outputs to `spec-watcher-<version>-<rid>.exe` and attach them to a GitHub Release
        (`softprops/action-gh-release`).
  - [x] 2.3 Validate the workflow YAML (schema/lint) and confirm the asset URLs match the pattern the
        winget `InstallerUrl`s will use.

- [x] 3. Winget manifests
  - [x] 3.1 Author the three manifests (version / installer / locale.en-US) for
        `UsualExpat.SpecWatcher` under `packaging/winget/` in the repo as the source-of-truth template.
  - [x] 3.2 Set `InstallerType: portable`, `PortableCommandAlias: spec-watcher`, both architectures,
        and the `Publisher: Usual Expat Limited` + license/URL/description/tags locale fields.
  - [x] 3.3 Validate the manifests locally with `winget validate --manifest packaging/winget/...`
        (hashes/URLs are placeholders until the first real release; note this).

- [x] 4. Automated winget submission
  - [x] 4.1 Add a `winget` job to `release.yml` that runs after the release is published and opens the
        winget-pkgs PR via `vedantmgoyal9/winget-releaser` (identifier `UsualExpat.SpecWatcher`,
        installers regex, `token: secrets.WINGET_TOKEN`).
  - [x] 4.2 Document the `WINGET_TOKEN` secret (classic PAT, `public_repo`) and the winget-pkgs fork
        prerequisite; wire the secret reference in the workflow.
  - [x] 4.3 Document the first-version handling (manual `komac`/`wingetcreate` submission if the action
        can't create a brand-new package) so the very first release is unblocked.

- [x] 5. Publishing runbook & docs
  - [x] 5.1 Write `docs/publishing-winget.md`: identity table, prerequisites (👤 fork + PAT + optional
        company URLs), "cut a release" steps (bump version → tag `vX.Y.Z` → push → CI opens PR), and
        local validation commands.
  - [x] 5.2 Add a short "Install" section to `README.md` (`winget install UsualExpat.SpecWatcher`) and
        cross-link the runbook; note code signing as a recommended future step.
  - [x] 5.3 Final review: workflow lints, manifests validate, runbook is complete, and the maintainer
        checklist of 👤 manual steps is clear.
