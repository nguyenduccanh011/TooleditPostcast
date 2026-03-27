# Install And Update Guide

## System Requirements
- Windows 10 or Windows 11, 64-bit.
- Local user profile with write access to `%LOCALAPPDATA%` and `%APPDATA%`.
- Internet access only when checking for updates or using AI/image search features.
- No separate .NET runtime or FFmpeg install is required for the packaged release.

## Install
1. Download the latest `PodcastVideoEditor-Setup-vX.Y.Z.exe` from GitHub Releases.
2. Run the installer and choose the default per-user location unless you have a specific deployment requirement.
3. Launch the app from the Start menu or the optional desktop shortcut.
4. On first run, the app creates `%APPDATA%\PodcastVideoEditor` and initializes `app.db`, logs, thumbnails, and `user_settings.json`.

## What Gets Installed
- App binaries are installed under `%LOCALAPPDATA%\Programs\PodcastVideoEditor`.
- Bundled `ffmpeg.exe` and `ffprobe.exe` are installed under `tools\ffmpeg` inside the app directory.
- User data remains under `%APPDATA%\PodcastVideoEditor` and is intentionally outside the install folder.

## Upgrade
1. Download the newer setup EXE.
2. Close the running app before launching the installer.
3. Run the new installer over the existing installation.
4. Confirm the app still opens recent projects and preserves AI keys, logs, and render settings.

Upgrades keep `%APPDATA%\PodcastVideoEditor` intact. That includes:
- `app.db`
- `user_settings.json`
- `logs`
- `thumbnails`

## Update Notification
- The app checks GitHub Releases for the latest stable version on startup.
- Manual update checks are available in `Help -> Check for Updates` and on the `Settings` tab.
- V1 does not self-patch. It opens the download page for the newer installer instead.

## FFmpeg Notes
- The packaged release prefers the bundled FFmpeg sidecar.
- If you set a custom FFmpeg path in Settings, that explicit path overrides the bundled default.
- The Validate button only checks the executable path. Click `Save Settings` to persist the custom path for future launches.

## AI And API Keys
- AI and image search keys are entered per user and stored locally in `%APPDATA%\PodcastVideoEditor\user_settings.json`.
- V1 does not proxy requests through a shared backend.
- If you move the app to another machine, configure the keys again unless you intentionally migrate the user settings file.

## Uninstall
- Uninstall removes app binaries from `%LOCALAPPDATA%\Programs\PodcastVideoEditor`.
- User data in `%APPDATA%\PodcastVideoEditor` is kept by default so projects and settings are not lost.
- If a full wipe is required, uninstall the app and then manually remove `%APPDATA%\PodcastVideoEditor`.

## Troubleshooting
- App opens but render fails: confirm `tools\ffmpeg\ffmpeg.exe` exists in the install directory or set a valid custom path in Settings.
- App reports no update even though a tag exists: verify the tag is a stable release like `v1.2.3` and includes uploaded installer assets.
- Settings did not persist: click `Save Settings`; validation alone does not write `user_settings.json`.
- GitHub update check fails: retry later if GitHub rate limits are hit or the network is unavailable.
