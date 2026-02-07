# Phase 2 Task Pack - Canvas Editor & Visualizer

**Project:** Podcast Video Editor  
**Phase:** 2 - Canvas Editor & Visualizer  
**Duration Target:** 3-4 weeks (Feb 27 - Mar 27, 2026)  
**Status:** 🚀 READY TO BEGIN

---

## Overview
Xây dựng Canvas Editor cho layout drag-drop, Visualizer spectrum real-time, Timeline Editor để quản lý segments và hình ảnh nền.

### Key Features
- ✅ Drag-drop interface for placing elements (Title, Logo, Image, Visualizer)
- ✅ Real-time spectrum visualizer using SkiaSharp
- ✅ Timeline editor with segment management
- ✅ Property panel for element editing
- ✅ Full integration with Phase 1 services

---

## Subtasks (ST)

### ST-7: Canvas Infrastructure & Drag-Drop
**Objective:** Tạo Canvas XAML element, implement drag-drop framework.

**Requirements:**
- [ ] Install NuGet: GongSolutions.WPF.DragDrop (3.2+)
- [ ] Create `CanvasElement` abstract base class
  - Properties: X, Y, Width, Height, Type (enum), ZIndex, IsSelected
  - Methods: Update(), Validate(), Clone()
  
- [ ] Create element type classes:
  - `TitleElement` - Text, FontFamily, FontSize, Color, Bold, Italic
  - `LogoElement` - ImagePath, Opacity (0-1), ScaleMode (Fit/Fill/Stretch)
  - `VisualizerElement` - ColorPalette, BandCount, Style, Size
  - `ImageElement` - FilePath, Opacity, ScaleMode
  - `TextElement` - Content, FontSize, Color, Alignment

- [ ] Create `CanvasViewModel`:
  - `Elements` (ObservableCollection<CanvasElement>)
  - `SelectedElement` (INotifyPropertyChanged)
  - `CanvasWidth`, `CanvasHeight` (resolution)
  - `GridSize` (for snapping)
  - Commands: 
    - `AddElementCommand<T>()` - generic
    - `DeleteElementCommand`
    - `MoveElementCommand(newX, newY)`
    - `ResizeElementCommand(newWidth, newHeight)`
    - `ChangeZIndexCommand(offset)`

- [ ] Create `CanvasView.xaml`:
  - ItemsControl bound to Elements
  - DataTemplate for each element type
  - Selection rectangle (visual feedback on SelectedElement)
  - Grid background pattern (optional, for alignment guide)
  - GongSolutions drag-drop handlers

**Acceptance Criteria:**
- [ ] Canvas renders with 1920x1080 background (customizable)
- [ ] Can add title element via toolbar button
- [ ] Drag-drop repositions elements (smooth, snap to grid optional)
- [ ] Can delete selected element (Delete key)
- [ ] Properties panel shows selected element
- [ ] Solution compiles (0 errors)

**Effort:** 6 hours  
**Dependencies:** Phase 1 complete

---

### ST-8: Visualizer Service (SkiaSharp)
**Objective:** Real-time spectrum visualizer using SkiaSharp, consume AudioService FFT data.

**Requirements:**
- [ ] Create `VisualizerService` class:
  - Subscribe to `AudioService.FFTDataAvailable` event
  - Process FFT data → frequency bins (32, 64, or 128 bands)
  - Implement smoothing: averaging + decay curve (falling bars effect)
  - Support color palettes (gradients)
  - Method: `RenderFrameAsync(width, height, config)` → `SKBitmap`

- [ ] Create `VisualizerConfig`:
  - `BandCount` (32, 64, 128)
  - `ColorPalette` (enum: Rainbow, Fire, Ocean, Mono)
  - `Style` (enum: Bars, Waveform, Circular)
  - `SmoothingFactor` (0.0-1.0, default 0.7)
  - `MinFreq`, `MaxFreq` (Hz for filtering)
  - `BarWidth`, `BarSpacing` (px)

- [ ] Create `VisualizerViewModel`:
  - `CurrentBitmap` (SKBitmap, INotifyPropertyChanged)
  - `VisualizerConfig` (ObservableProperty for UI binding)
  - `IsActive` (bool, when audio plays)
  - `BitmapSource` (helper for WPF Image.Source binding)
  - Update timer: ~16ms (60fps), non-blocking

- [ ] Implement rendering loop:
  - Background thread (Task.Run, long-running)
  - Lock-free bitmap swaps (avoid deadlocks)
  - Proper disposal of old bitmaps
  - Handle audio pause/stop (freeze frame)

- [ ] Windowing function (optional for Phase 2b):
  - Hann or Hamming window for better FFT visualization

**Acceptance Criteria:**
- [ ] Visualizer renders spectrum bars live
- [ ] Updates smooth with audio playback
- [ ] No memory leaks (bitmap disposal tested with long sessions)
- [ ] 60fps target (or gracefully degrade if slow)
- [ ] Different styles/palettes can be toggled in real-time
- [ ] Latency <100ms from FFT data to visual update
- [ ] No crashes on audio stop/pause

**Effort:** 8 hours  
**Dependencies:** ST-3 (AudioService FFT data available)  
**Blockers:** Issue #3 - Memory management with SkiaSharp bitmaps (must profile)

---

### ST-9: Timeline Editor & Segment Manager
**Objective:** UI for managing segments (start time, duration, background image, transition).

**Requirements:**
- [ ] Create `TimelineView.xaml`:
  - Horizontal ruler at top (timecode: 0:00, 0:05, 0:10, etc.)
  - Track area (white background, grid lines every 5s)
  - Segment blocks (colored rectangles, labeled with start:end time)
  - Playhead (vertical line, red, synced with audio position)
  - Scroll bar (horizontal, for long timelines)
  - MouseDown → select segment, MouseLeft → drag, handles → resize

- [ ] Create `TimelineViewModel`:
  - `Segments` (ObservableCollection<Segment>)
  - `SelectedSegment` (INotifyPropertyChanged)
  - `PlayheadPosition` (double, seconds, synced to AudioPlayerViewModel)
  - `TimelineScale` (double, pixels per second, for zoom)
  - `TotalDuration` (double, bound to audio duration)
  - Commands:
    - `AddSegmentCommand(startTime)` - create at playhead
    - `DeleteSegmentCommand`
    - `UpdateSegmentCommand` - batch update (text, image, transition)
    - `DuplicateSegmentCommand`
    - `ClearAllSegmentsCommand`

- [ ] Create `SegmentEditorPanel.xaml`:
  - TextBox: Segment description/script (multi-line)
  - Button: Pick background image (OpenFileDialog, filter: *.jpg, *.png)
  - ColorPicker: Segment color (for timeline block)
  - ComboBox: Transition type (Cut, Fade, SlideLeft, SlideRight, ZoomIn)
  - Slider: Transition duration (0-3 seconds)
  - TextBlock: Display start/end time (read-only)
  - Button: Delete segment

- [ ] Sync playhead with audio:
  - Bind `TimelineViewModel.PlayheadPosition` to `AudioPlayerViewModel.CurrentPosition`
  - Update every 100ms (or on audio position changed event)
  - Auto-scroll timeline to keep playhead visible

- [ ] Segment validation:
  - Prevent overlapping segments
  - Warn if segment duration < transition duration
  - Auto-snap to grid (5s increments, optional)

**Acceptance Criteria:**
- [ ] Timeline displays all segments with correct timing
- [ ] Can add segment at current playhead position
- [ ] Can drag-resize segment to change duration
- [ ] Playhead follows audio (±50ms acceptable)
- [ ] Select segment → SegmentEditorPanel updates
- [ ] Edit properties → Segment and timeline update
- [ ] Delete segment → removed from timeline
- [ ] Scroll large timelines smoothly

**Effort:** 7 hours  
**Dependencies:** ST-3 (AudioService), Phase 1 complete  
**Optional:** Segment snapping grid, multi-select segments

---

### ST-10: Canvas + Visualizer Integration
**Objective:** VisualizerElement renders live visualizer on canvas during playback.

**Requirements:**
- [ ] Extend `VisualizerElement`:
  - Dependency inject `VisualizerViewModel` (or weak reference)
  - Subscribe to `VisualizerViewModel.CurrentBitmap` changes
  - Render bitmap at (X, Y) with (Width, Height) scaling
  - Respect Z-order (composite after background, before text)

- [ ] Update `CanvasView` rendering:
  - Use `Image` control or custom `Canvas.DrawImage()` for visualizer
  - Bind visualizer bitmap to UI (convert SKBitmap → BitmapSource)
  - Handle resize: scale visualizer bitmap appropriately
  - Performance: throttle updates if rendering lag detected

- [ ] Performance optimization:
  - Profile rendering with dotMemory (target: <500MB RAM for 10-min audio)
  - Implement frame skipping if needed (render every 2nd frame if slow)
  - Cache transformed bitmaps if resizing frequently
  - Log performance metrics (FPS, memory usage) to Serilog

- [ ] Audio sync:
  - Pause audio → visualizer freezes at last frame (expected behavior)
  - Resume audio → visualizer continues smoothly
  - Seek audio → visualizer data doesn't match (acceptable, audio takes priority)

**Acceptance Criteria:**
- [ ] Visualizer element displays on canvas
- [ ] Renders live spectrum during audio playback
- [ ] 60fps target or graceful degradation
- [ ] Memory stable (<500MB for 10-min sessions)
- [ ] Can move/resize visualizer element on canvas
- [ ] Can change visualizer colors/style via property panel
- [ ] No crashes on long sessions

**Effort:** 4 hours  
**Dependencies:** ST-7, ST-8, Phase 1 AudioService

---

### ST-11: Element Property Editor Panel
**Objective:** Property panel UI for editing selected canvas element properties.

**Requirements:**
- [ ] Create `PropertyEditorView.xaml`:
  - ScrollViewer (for many properties)
  - Grid layout (2 columns: label, input)
  - Sections (expandable, optional): Position, Size, Appearance, Advanced

- [ ] Dynamic property fields by element type:
  - **TitleElement**: 
    - Text (TextBox, multi-line)
    - FontFamily (ComboBox: Arial, Verdana, etc.)
    - FontSize (Slider: 12-72pt)
    - Color (ColorPicker)
    - Bold, Italic (CheckBox)
    - Alignment (ComboBox: Left, Center, Right)
  
  - **LogoElement**:
    - ImagePath (TextBox + Browse button)
    - Opacity (Slider: 0-100%)
    - ScaleMode (ComboBox: Fit, Fill, Stretch)
  
  - **VisualizerElement**:
    - ColorPalette (ComboBox: Rainbow, Fire, Ocean, Mono)
    - BandCount (ComboBox: 32, 64, 128)
    - Style (ComboBox: Bars, Waveform, Circular)
  
  - **ImageElement**:
    - FilePath (TextBox + Browse)
    - Opacity (Slider: 0-100%)
    - ScaleMode (ComboBox)
  
  - **Common (all elements)**:
    - X (Slider or TextBox, px)
    - Y (Slider or TextBox, px)
    - Width (Slider or TextBox, px)
    - Height (Slider or TextBox, px)
    - ZIndex (Slider: -10 to +10)
    - Visibility (CheckBox)
    - Name (TextBox, optional)

- [ ] Create `PropertyEditorViewModel`:
  - `SelectedElement` (ObservableProperty, subscribes to CanvasViewModel)
  - Watch property changes → update model → notify CanvasViewModel
  - Validate inputs (X/Y < 0, Width/Height > canvas size, etc.)
  - Commands: 
    - `ResetToDefaultCommand` - restore original values
    - `DeleteElementCommand`
    - `DuplicateElementCommand`

- [ ] Two-way binding:
  - Canvas selects element → panel populates
  - Panel edits property → canvas updates in real-time
  - Deselect element → panel clears

- [ ] Style:
  - Dark theme (match MainWindow)
  - Section headers bold
  - Property labels right-aligned
  - Input fields full width

**Acceptance Criteria:**
- [ ] Property panel displays for any selected element
- [ ] All field types render correctly (TextBox, Slider, ComboBox, ColorPicker)
- [ ] Editing property updates canvas immediately
- [ ] Deselecting element → panel clears
- [ ] Can duplicate/delete from panel
- [ ] Input validation prevents invalid values
- [ ] Solution compiles

**Effort:** 5 hours  
**Dependencies:** ST-7, CanvasViewModel complete

---

## Integration Flow

```
ST-7: Canvas Infrastructure
  ↓
  ├─→ ST-11: Property Editor (reads/writes CanvasElement)
  │
  ├─→ ST-8: Visualizer Service
  │     ↓
  │     ST-10: Canvas + Visualizer (renders visualizer on canvas)
  │
  └─→ ST-9: Timeline Editor (manages Segment collection for RenderConfig)
        ↓
        SegmentEditorPanel (preview background images on timeline)
```

**Deployment Order:**
1. ST-7 first (foundation for all canvas work)
2. ST-8 & ST-11 parallel (independent services)
3. ST-9 parallel (independent from canvas)
4. ST-10 (integration task, last)

---

## Test Plan

### Manual Tests
- [ ] Launch app, open recent project from Phase 1
- [ ] Add Title element: "My Video"
  - [ ] Edit text, font, size, color
  - [ ] Drag to position, resize
  - [ ] Delete and recreate
  
- [ ] Add Visualizer element
  - [ ] Play audio
  - [ ] Spectrum bars update in real-time
  - [ ] Try different colors/styles
  - [ ] Pause/resume audio
  
- [ ] Add Image element
  - [ ] Browse to image file (provide test image)
  - [ ] Position on canvas
  - [ ] Adjust opacity
  
- [ ] Timeline:
  - [ ] Create 3 segments (0-10s, 10-20s, 20-30s)
  - [ ] Assign background image to each
  - [ ] Play audio, watch playhead sync
  - [ ] Drag segment to extend/shorten
  - [ ] Delete middle segment
  
- [ ] Render:
  - [ ] Render project with all elements visible
  - [ ] Verify video contains title, visualizer, images
  - [ ] Check audio sync (play and manually verify timing)

### Performance Tests
- [ ] Canvas with 20+ elements → no lag, <200ms response
- [ ] Timeline with 50+ segments → smooth scrolling
- [ ] Visualizer @ 60fps for 15-min audio → <500MB RAM peak
- [ ] Render 10-min video with visualizer → completes without memory spikes

### Edge Cases
- [ ] Delete selected element → property panel clears automatically
- [ ] Very long title text → word wrap or truncate gracefully
- [ ] Overlapping elements → z-order respected when rendering
- [ ] Segment with no image → display default color (no crash)
- [ ] Audio ends → visualizer freezes (expected)
- [ ] Seek audio during playback → playhead jumps (expected, audio takes priority)
- [ ] Resize canvas area → elements not repositioned (stay absolute)

### Automated Tests (Optional, Phase 2b)
- [ ] CanvasElement.Clone() produces deep copy
- [ ] CanvasViewModel.AddElement() increments count
- [ ] VisualizerService.RenderFrame() returns valid SKBitmap
- [ ] TimelineViewModel.PlayheadPosition tracks audio accurately
- [ ] PropertyEditorViewModel validates input bounds

---

## Phase 2 Deliverables

1. **Canvas Editor**
   - CanvasView.xaml with drag-drop, selection, resizing
   - CanvasViewModel with element management
   - 5 element types (Title, Logo, Visualizer, Image, Text)

2. **Visualizer Service**
   - VisualizerService with FFT processing
   - SkiaSharp rendering (Bars, Waveform, Circular styles)
   - Real-time spectrum visualization

3. **Timeline Editor**
   - TimelineView.xaml with playhead sync
   - TimelineViewModel with segment CRUD
   - SegmentEditorPanel for properties

4. **Property Editor**
   - PropertyEditorView.xaml (dynamic by element type)
   - PropertyEditorViewModel with two-way binding
   - Full element property editing

5. **Integration**
   - VisualizerElement renders on canvas
   - Playhead syncs with audio
   - All services integrated and working together

6. **Documentation**
   - Updated code_rules.md with Canvas/Visualizer patterns
   - API contracts for VisualizerService, TimelineViewModel
   - Known issues (memory management, performance) in issues.md

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| SkiaSharp memory leak (bitmap disposal) | 🔴 BLOCKER | Comprehensive testing, dotMemory profiling before Phase 3 |
| Canvas rendering lag with 20+ elements | 🟡 MEDIUM | Implement virtualization (render only visible), optimize DataTemplate |
| Timeline playhead sync drift (audio vs visual) | 🟡 MEDIUM | Use Stopwatch + high-precision timers, extensive testing |
| GongSolutions.WPF.DragDrop .NET 8 compatibility | 🟡 MEDIUM | Test early (ST-7 day 1), consider alternative (Syncfusion) if needed |
| Visualizer FFT quality (MVP version) | 🟡 MEDIUM | Use basic windowing, plan enhancement for Phase 2b/3 |
| SkiaSharp SKBitmap → WPF BitmapSource conversion performance | 🟡 MEDIUM | Cache conversion, update only on change |

---

## Optional Enhancements (Phase 2b+)

- [ ] Grid snapping (align elements to grid)
- [ ] Undo/Redo (Command pattern history)
- [ ] Multi-select elements (Ctrl+click)
- [ ] Group elements (nest in folders)
- [ ] Element templates (save/load frequently-used layouts)
- [ ] Visualizer FFT windowing (Hann/Hamming for better spectrum)
- [ ] Waveform display in timeline (audio waveform background)
- [ ] Keyframe animation (animate element properties over time)

---

## Success Criteria (Phase 2 Complete)

✅ All 5 subtasks (ST-7 to ST-11) marked DONE  
✅ 0 build errors, <10 warnings  
✅ Manual test plan passed  
✅ Performance targets met (<500MB RAM, 60fps visualizer)  
✅ Visualizer rendering live spectrum correctly  
✅ Canvas + Timeline + Properties working together  
✅ Ready for Phase 3 (Script & Timeline automation)  

---

**Last updated:** 2026-02-07 (PHASE 2 PLANNING COMPLETE)  
**Status:** 🚀 READY TO BEGIN DEVELOPMENT

