# Podcast Video Editor

Windows WPF app to edit podcast audio and render videos with visualizers, images, and timelines.

## Status
- Planning and design docs are in `docs/`
- Implementation is in progress

## Tech stack
- .NET 8
- WPF (MVVM)
- SkiaSharp (visualizer rendering)
- FFmpeg (render pipeline)
- SQLite + EF Core (project storage)

## Repo layout
- `docs/` architecture, decisions, and issues
- `src/` application source code

## Build
Prereqs: Windows, .NET 8 SDK, FFmpeg in PATH.

```
dotnet build src/PodcastVideoEditor.slnx
```

## Docs
- `docs/arch.md`
- `docs/decisions.md`
- `docs/code_rules.md`
- `docs/issues.md`
- `docs/reference-sources.md`
