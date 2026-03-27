# Release Playbook

## Release Inputs
- Stable release tags must use `vMAJOR.MINOR.PATCH`, for example `v1.0.0`.
- The canonical app version lives in `src/Directory.Build.props`.
- GitHub Releases is the distribution source of truth for installer downloads and update notifications.

## Pre-Release Checklist
1. Confirm `dotnet build PodcastVideoEditor/src/PodcastVideoEditor.sln -c Release` passes.
2. Confirm both test projects pass:
   - `dotnet test PodcastVideoEditor/src/PodcastVideoEditor.Core.Tests/PodcastVideoEditor.Core.Tests.csproj -c Release`
   - `dotnet test PodcastVideoEditor/src/PodcastVideoEditor.Ui.Tests/PodcastVideoEditor.Ui.Tests.csproj -c Release`
3. Confirm bundled FFmpeg binaries are available locally or on the CI runner.
4. Smoke test on at least one existing install and one clean machine.
5. Review `THIRD_PARTY_NOTICES.md` for the exact FFmpeg redistribution choice used in the release.

## Local Build Flow
1. Update `src/Directory.Build.props` if the version is changing.
2. Stage `ffmpeg.exe` and `ffprobe.exe` under `third_party/ffmpeg/bin`, or pass `-FfmpegBinDir`.
3. Run:

```powershell
.\scripts\Build-Release.ps1 -Version 1.0.0
```

4. Collect artifacts from `artifacts/release/packages/`:
   - `PodcastVideoEditor-Setup-vX.Y.Z.exe`
   - `PodcastVideoEditor-win-x64-vX.Y.Z.zip`
   - `SHA256SUMS.txt`

## GitHub Release Flow
1. Commit the release-ready state.
2. Create and push a tag such as `v1.0.0`.
3. GitHub Actions workflow `.github/workflows/release.yml` will:
   - install Inno Setup and FFmpeg on the Windows runner
   - build the solution
   - test both test projects
   - publish a self-contained `win-x64` app
   - bundle FFmpeg sidecar binaries
   - build the installer EXE
   - create checksums
   - upload packages to GitHub Releases

## Smoke Test Matrix
- Clean machine with no .NET runtime and no FFmpeg in `PATH`.
- Existing machine upgrading from the previous installer.
- Launch app, open an existing project, run a render, and confirm settings survive upgrade.
- Trigger `Help -> Check for Updates` against a known newer tag.

## Rollback
1. If a release is bad, unpublish or edit the GitHub Release immediately.
2. Publish a replacement patch version rather than reusing the same tag.
3. Tell users to reinstall the last known good setup EXE.
4. Keep `%APPDATA%\PodcastVideoEditor` intact unless the defect is explicitly data-related.

## Notes
- V1 update behavior is notification-only. The app never overwrites itself.
- Before broader public rollout, add code signing for both the installer and the app binaries to reduce SmartScreen friction.
