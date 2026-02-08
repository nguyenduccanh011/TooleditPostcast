# Podcast Video Editor

Windows WPF app to edit podcast audio and render videos with visualizers, images, and timelines.

## Status
- ✅ Phase 1 (Core Engine & Audio) - COMPLETE
- 🚀 Phase 2 (Canvas Editor & Visualizer) - IN PROGRESS (ST-7, ST-8 done)
- Planning and design docs are in `docs/`

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

## Getting Started
- **Phase 1 Complete:** See [docs/QUICK-START-PHASE2.md](docs/QUICK-START-PHASE2.md) for next steps
- **Phase 2 Planning:** See [docs/phase2-plan.md](docs/phase2-plan.md) for detailed requirements

## Docs
- `docs/arch.md` - Architecture design
- `docs/decisions.md` - Architecture decision records
- `docs/code_rules.md` - Coding standards
- `docs/issues.md` - Known issues and blockers
- `docs/reference-sources.md` - Reference links
- **`docs/phase2-plan.md`** - Phase 2 detailed planning (Canvas, Visualizer, Timeline)
- **`docs/QUICK-START-PHASE2.md`** - Phase 2 quick start guide
- `docs/active.md` - Current task pack status
- `docs/state.md` - Project state and timeline
- `docs/worklog.md` - Session history
