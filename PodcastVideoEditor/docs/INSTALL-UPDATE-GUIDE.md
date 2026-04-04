# Installation & Update Guide

## New Installation

1. Download `PodcastVideoEditor-Setup-vX.X.X.exe` from the Releases page.
2. Run the installer and follow the prompts.
3. Launch **Podcast Video Editor** from the Start Menu or Desktop shortcut.

## Portable Installation

1. Download `PodcastVideoEditor-win-x64-vX.X.X.zip`.
2. Extract to any folder (e.g. `C:\Tools\PodcastVideoEditor`).
3. Run `PodcastVideoEditor.Ui.exe` directly — no installation required.

## Updating from a Previous Version

### Installer Version
Run the new installer over the existing installation. Your settings and projects are preserved.

### Portable Version
1. Extract the new ZIP to a **new** folder.
2. Copy your `%APPDATA%\PodcastVideoEditor\` data folder if you want to migrate settings manually (settings migrate automatically on first launch).
3. Replace your shortcut to point to the new folder.

## First Run — AI Settings

1. Open **Settings** (gear icon or `Ctrl+,`).
2. Enter your YesScale API key in the **AI Analysis** section.
3. Optionally configure additional API key profiles and fallback models.

## Requirements

- Windows 10 version 1903 or later (x64)
- .NET 9 runtime (bundled in installer; required separately for portable build)
- FFmpeg is bundled — no separate installation needed

## Data Locations

| Data | Path |
|------|------|
| Settings | `%APPDATA%\PodcastVideoEditor\user_settings.json` |
| Database | `%APPDATA%\PodcastVideoEditor\app.db` |
| Logs | `%APPDATA%\PodcastVideoEditor\Logs\` |
| Renders | `%APPDATA%\PodcastVideoEditor\Renders\` |
