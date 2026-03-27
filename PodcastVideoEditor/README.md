# Podcast Video Editor

Windows WPF app for editing podcast timelines and rendering video outputs with visualizers, images, and FFmpeg.

## Release Status
- Windows x64 release packaging is based on `dotnet publish` plus Inno Setup.
- Installer distribution targets GitHub Releases.
- App updates are notification-only in V1. The app opens the download page for a newer installer instead of patching itself.

## System Requirements
- Windows 10 or Windows 11, 64-bit
- .NET runtime is bundled in release builds
- FFmpeg is bundled in release builds

## Download And Install
- End users should install from the latest `PodcastVideoEditor-Setup-vX.Y.Z.exe` asset in GitHub Releases.
- See [docs/INSTALL-UPDATE-GUIDE.md](docs/INSTALL-UPDATE-GUIDE.md) for install, upgrade, uninstall, and troubleshooting details.

## Development Build
Prereqs: Windows and .NET 8 SDK.

```powershell
dotnet build src/PodcastVideoEditor.sln -c Release
dotnet test src/PodcastVideoEditor.Core.Tests/PodcastVideoEditor.Core.Tests.csproj -c Release
dotnet test src/PodcastVideoEditor.Ui.Tests/PodcastVideoEditor.Ui.Tests.csproj -c Release
```

## Build A Release Package
Local release packaging expects `ffmpeg.exe` and `ffprobe.exe` to be available either:
- under `third_party/ffmpeg/bin`
- via `-FfmpegBinDir`
- or on the machine `PATH`

Run:

```powershell
.\scripts\Build-Release.ps1 -Version 1.0.0
```

Artifacts are written to `artifacts/release/packages`.

## Tech Stack
- .NET 8
- WPF (MVVM)
- SkiaSharp
- FFmpeg
- SQLite + EF Core

## Repo Layout
- `docs/` architecture, operations, and release documentation
- `installer/` Inno Setup installer script
- `scripts/` release automation
- `src/` application source code
- `third_party/ffmpeg/` local staging location for FFmpeg binaries during release packaging

## Key Docs
- [docs/INSTALL-UPDATE-GUIDE.md](docs/INSTALL-UPDATE-GUIDE.md)
- [docs/RELEASE-PLAYBOOK.md](docs/RELEASE-PLAYBOOK.md)
- [docs/arch.md](docs/arch.md)
- [docs/decisions.md](docs/decisions.md)
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
