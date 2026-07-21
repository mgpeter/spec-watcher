# Spec Summary (Lite)

Publish spec-watcher to winget as `UsualExpat.SpecWatcher` under the publisher Usual Expat Limited,
delivered as a self-contained single-file portable executable (`win-x64` + `win-arm64`) via tagged
GitHub Releases. A GitHub Actions workflow builds the binaries and auto-opens the `microsoft/winget-pkgs`
pull request on each version tag, so `winget install UsualExpat.SpecWatcher` installs and updates the
tool with no .NET runtime required on the user's machine.
