# FFmpeg Bundle Staging

Place `ffmpeg.exe` and `ffprobe.exe` in `third_party/ffmpeg/bin/` when building a local release package.

The release script also accepts `-FfmpegBinDir` or `FFMPEG_BIN_DIR` and will fall back to `Get-Command ffmpeg` if both binaries are available on the machine.

Do not commit third-party binaries unless you have already reviewed the license and redistribution requirements for the exact FFmpeg build you are shipping.
