# Template Track Role & Flow

## Goal

Podcast templates should preserve layout intent, not frozen project content.

The template must distinguish:
- fixed project-level layers
- dynamic script/AI-driven layers
- per-segment overrides when needed

## Where the switch belongs

The "fixed or dynamic" decision belongs in **Track Properties**, not Segment Properties, when it applies to the whole lane.

Use Segment Properties only for per-segment exceptions.

Recommended track-level policy fields:
- `TrackRole`
- `SpanMode`

## Recommended roles

- `BrandOverlay`: logo, icon, watermark, fixed brand layers
- `TitleOverlay`: title elements that should stay consistent across the project
- `ScriptText`: text driven by parsed script segments
- `AIContent`: image/visual segments generated or selected by AI
- `Visualizer`: audio-driven spectrum or motion layer
- `BackgroundContent`: long visual background or intro/outro layer

## Recommended span modes

- `ProjectDuration`: keep visible until the end of the project
- `TemplateDuration`: keep the same duration as the template sample
- `SegmentBound`: visible only during the segment's own time range
- `Manual`: never auto-extend; user controls the duration explicitly

## Final flow

### 1. Create template
- User places tracks and elements on the canvas.
- User marks each track by role.
- User chooses span mode for each track.
- Track style and layout are saved.
- Script content and AI-generated content are not saved as fixed content.

### 2. Create project from template
- The template is cloned by track role, not by track index alone.
- Fixed layers are restored as fixed layers.
- Dynamic tracks are restored as editable lanes.
- If the template does not have a suitable AI target track, the system can create one.

### 3. Add audio, script, AI analysis
- Script is parsed into new text segments.
- AI can generate or replace dynamic content segments.
- AI must not replace tracks tagged as `BrandOverlay` or other fixed roles.

### 4. Expand timing
- `ProjectDuration` tracks auto-expand to the farthest project end time.
- `TemplateDuration` tracks keep the original sample length.
- `Manual` tracks stay exactly as the user configured them.

### 5. Render
- Render reads all visible tracks and elements.
- Fixed layers stay visible across the whole project when span mode requires it.
- Dynamic layers appear only within their segment timing.

## Practical rule for the current product

Use this default behavior:
- logo, icon, watermark, and brand visualizer: `BrandOverlay` + `ProjectDuration`
- title: usually `TitleOverlay` + either `ProjectDuration` or `TemplateDuration`
- script text: `ScriptText` + `SegmentBound`
- AI image segments: `AIContent` + `SegmentBound`

## Compatibility rule

If old templates do not have role metadata:
- fall back to existing heuristics
- infer fixed overlay tracks from lock state, naming, and one-segment-at-start behavior
- keep the project usable without forcing migration failure
