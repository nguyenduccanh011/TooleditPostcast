# Render Pipeline: Text Missing Bug Analysis & Commercial-Grade Restructuring Plan

**Date:** 2026-03-30  
**Status:** Deep Code Review Complete

---

## 1. ROOT CAUSE ANALYSIS — Tại sao text không xuất hiện trong video render

### Bug #1 (CRITICAL): Thiếu đồng bộ ngược TextOverlayElement.Content → Segment.Text

**File lỗi:** Không có code sync ngược trong toàn bộ codebase.

**Flow hiện tại:**
```
Segment.Text thay đổi → OnSegmentPropertyChanged → TextOverlayElement.Content cập nhật ✅
TextOverlayElement.Content thay đổi → ??? → Segment.Text KHÔNG cập nhật ❌
```

**Impact:** Khi user chỉnh text qua Property Editor trên canvas, chỉ `TextOverlayElement.Content` thay đổi. `Segment.Text` vẫn giữ giá trị cũ (ví dụ: "Title 1"). Tại render time, `BuildRasterizedTextSegments()` đọc từ `segment.Text` → render text cũ/sai.

**Vị trí code:**
- `RenderSegmentBuilder.cs` line ~258: `options.Text = segment.Text` ← đọc text từ Segment, KHÔNG từ Element
- `PropertyEditorViewModel.cs` line ~185: `OnTextElementPropertyChanged` chỉ propagate sang siblings, không sync lại segment
- Không tồn tại code `segment.Text = element.Content;` ở bất kỳ đâu

### Bug #2 (CRITICAL): Lazy element creation → thiếu StyleData khi render

**Flow hiện tại:**
```
User tạo text segment trên timeline (playhead tại t=0s)
  → TextOverlayElement được tạo chỉ cho segment tại t=0s
  → Các segment tại t=5s, t=10s CHƯA có TextOverlayElement

User nhấn Render → SnapshotElementsForRender()
  → Chỉ snapshot elements hiện có (thiếu elements cho segments ở t=5s, t=10s)
  → BuildRasterizedTextSegments() gặp segment KHÔNG có linked element
  → Dùng fallback defaults: 24px Arial, vị trí y=80%, nhỏ và dễ bị che
```

**File lỗi:**
- `CanvasViewModel.Preview.cs` line ~460: `EnsureTextElementForSegment()` chỉ chạy khi playhead đi qua
- `RenderViewModel.cs` line ~415: `SnapshotElementsForRender()` chỉ clone elements hiện có

### Bug #3 (HIGH): Fallback text quá nhỏ/sai vị trí

Khi segment không có linked TextOverlayElement, fallback rendering là:
```csharp
options.FontSize = ScaleFontSize(24, canvasHeight, renderHeight);  // ~24px trên canvas
imgHeight = (int)(renderHeight * 0.1);  // Chỉ 10% chiều cao
overlayY = (int)(renderHeight * 0.8);   // Nằm ở 80% từ trên
overlayX = 0; imgWidth = renderWidth;   // Full width
```

Trên video 1080x1920 (9:16), text chỉ cao 192px với font 24px — có thể bị cắt hoặc không hiện.

### Bug #4 (MEDIUM): Duplicate render pipelines gây nhầm lẫn

Hai pipeline song song:
| Legacy (đang sử dụng) | New (chưa sử dụng) |
|---|---|
| `RenderSegmentBuilder` (static) | `CompositionBuilder` (interface) |
| `FFmpegCommandComposer` (static) | `FFmpegFilterGraphBuilder` (static) |

`RenderViewModel` gọi legacy path. `CompositionBuilder` + `FFmpegFilterGraphBuilder` hoàn chỉnh nhưng không được kết nối.

---

## 2. FULL RENDER FLOW — Trace chi tiết

### Luồng thực thi hiện tại:
```
RenderViewModel.StartRenderAsync()
│
├─ 1. CreateRenderSnapshot(project)
│     ├─ BuildLiveProject(): deep-clone Tracks + Segments từ TimelineViewModel
│     ├─ SnapshotElementsForRender(): clone Elements (chỉ những gì hiện có) ⚠️
│     └─ ElementSegmentRegistry.Build(): tạo O(1) lookup map
│
├─ 2. RenderSegmentBuilder.BuildTimelineVisualSegments()
│     ├─ Lọc: TrackType == "visual" && IsVisible
│     ├─ Mỗi segment → lookup Asset → tạo RenderVisualSegment
│     └─ Sync position từ linked CanvasElement (nếu có)
│
├─ 3. RenderSegmentBuilder.BuildRasterizedTextSegments()     ⚠️ BUG ZONE
│     ├─ Lọc: TrackType == "text" && IsVisible
│     ├─ Mỗi segment → check segment.Text (KHÔNG phải element.Content!)
│     ├─ Lookup linked TextOverlayElement (có thể null!)
│     │   ├─ Có element: dùng styling từ element (font, color, shadow, etc.)
│     │   └─ KHÔNG có element: fallback 24px Arial white, vị trí 80% bottom
│     ├─ TextRasterizer.RenderToFile() → PNG (SkiaSharp)
│     │   ├─ Success: trả RenderVisualSegment với SourcePath = PNG
│     │   └─ Fail: [catch, return null] → segment bị BỊ BỎ QUA THẦM LẶNG ⚠️
│     └─ Parallel.For: rasterize song song (good), nhưng lỗi bị nuốt (bad)
│
├─ 4. Merge: timelineVisualSegments.AddRange(rasterizedTextVisuals)
│
├─ 5. RenderSegmentBuilder.BuildVisualizerSegmentsAsync()
│     └─ Bake visualizer → transparent video
│
├─ 6. Merge: timelineVisualSegments.AddRange(visualizerSegments)
│
├─ 7. RenderConfig { VisualSegments = ALL merged, TextSegments = [] }
│                                                 ↑ Luôn empty (text đã thành PNG)
│
├─ 8. FFmpegCommandComposer.Build(config)
│     ├─ NormalizeRenderConfig() → copy danh sách segments
│     └─ BuildTimelineFFmpegCommand()
│           ├─ Lọc visual: File.Exists(SourcePath) && EndTime > StartTime
│           ├─ PNG overlays: format=rgba, scale, overlay với enable='between(t,...)'
│           ├─ Text drawtext: ĐÃ BỊ VÔ HIỆU HÓA (TextSegments = [])
│           └─ Audio mix: amix pipeline
│
└─ 9. FFmpegService.RenderVideoAsync() → chạy subprocess ffmpeg
```

### Các điểm failure tiềm ẩn:

| # | Điểm | Hậu quả | Khả năng |
|---|---|---|---|
| F1 | `segment.Text` empty | Segment bị skip | Cao nếu user chỉ edit Content |
| F2 | No linked element | Fallback nhỏ/sai vị trí | Cao nếu chưa scroll playhead |
| F3 | TextRasterizer throws | PNG không tồn tại, bị filter out | Trung bình (font issues) |
| F4 | PNG temp file bị xóa | `File.Exists()` fail → bị lọc | Thấp |
| F5 | ZOrder overlapping | Text bị che bởi visual layer | Thấp (unified z-order đã fix) |

---

## 3. SO SÁNH VỚI ỨNG DỤNG THƯƠNG MẠI

### 3.1 Architecture Comparison

| Feature | PodcastVideoEditor | DaVinci Resolve | Premiere Pro | CapCut |
|---|---|---|---|---|
| **Timeline Model** | Track → Segment | Timeline → Track → Clip | Sequence → Track → Item | Timeline → Track → Clip |
| **Text Rendering** | Rasterize to PNG | Node-based (Fusion) | Native text engine | Real-time GPU text |
| **Render Pipeline** | Single-threaded FFmpeg | Multi-GPU + CPU nodes | Mercury Engine (GPU) | GPU accelerated |
| **Intermediate Rep** | RenderConfig (flat) | Composition Tree (DAG) | Graph Pipeline | Composition Graph |
| **Preview ↔ Render** | Separate codepaths ⚠️ | Unified engine | Unified engine | Unified engine |
| **Text ↔ Segment** | Loose coupling ⚠️ | Tight integration | Tight integration | Tight integration |
| **Undo/Redo** | Command pattern ✅ | Graph diffing | Command pattern | Command pattern |
| **Z-Order** | Unified per-track ✅ | Per-node | Per-track | Per-track |

### 3.2 Điểm mạnh hiện tại

1. **Unified z-order system** — Track.Order → global z-order map (tốt)
2. **ElementSegmentRegistry** — O(1) lookup thay vì O(n) FirstOrDefault (tốt)
3. **CompositionBuilder pattern** — intermediate representation giống Resolve (tốt, nhưng chưa dùng)
4. **Parallel text rasterization** — `Parallel.For` cho TextRasterizer (tốt)
5. **Deep-clone snapshot** — isolate render từ UI state (tốt)
6. **Ken Burns motion** — auto-motion cho still images (unique feature)
7. **Track style propagation** — shared style template cho text (tốt)

### 3.3 Điểm yếu so với thương mại

| Điểm yếu | Mức độ | Chi tiết |
|---|---|---|
| **Preview ≠ Render** | CRITICAL | Canvas dùng WPF elements, render dùng SkiaSharp rasterize → mismatch |
| **Loose text binding** | CRITICAL | Element.Content vs Segment.Text không sync 2 chiều |
| **Lazy element creation** | HIGH | Text elements chỉ tạo khi playhead sweep → missing at render |
| **No pre-render validation** | HIGH | Không verify segments/elements trước khi render |
| **Dual pipeline** | HIGH | Legacy RenderSegmentBuilder vs new CompositionBuilder |
| **No GPU acceleration** | MEDIUM | CPU-only SkiaSharp rasterize + FFmpeg encode |
| **Silent error swallowing** | MEDIUM | Parallel.For catch-and-ignore → text biến mất không trace |
| **No render preview** | MEDIUM | Không hiển thị trước kết quả render (commercial editors có "render preview") |
| **Single FFmpeg process** | LOW | Không chunked/distributed rendering |

---

## 4. PHƯƠNG ÁN TÁI CẤU TRÚC — Commercial-Grade

### Phase 1: FIX BUGS (Immediate — 1-2 ngày)

#### 1A. Bidirectional Text Sync
```
File: CanvasViewModel.cs hoặc CanvasViewModel.Preview.cs
Add: PropertyChanged handler cho TextOverlayElement.Content
     → sync ngược về Segment.Text
```
```csharp
// Trong CanvasViewModel, khi subscribe element:
element.PropertyChanged += (s, e) =>
{
    if (e.PropertyName == "Content" && s is TextOverlayElement tov)
    {
        var segment = FindSegmentById(tov.SegmentId);
        if (segment != null && segment.Text != tov.Content)
            segment.Text = tov.Content;
    }
};
```

#### 1B. Pre-render Element Materialization
```
File: RenderViewModel.cs → trong CreateRenderSnapshot()
Add: Duyệt qua TẤT CẢ text segments, gọi GetOrCreateTextElement()
     TRƯỚC KHI snapshot elements
```
```csharp
private RenderSnapshot CreateRenderSnapshot(Project project)
{
    var liveProject = BuildLiveProject(project);
    
    // ★ NEW: Materialize ALL text elements before snapshot
    EnsureAllTextElementsExist(liveProject);
    
    var elements = SnapshotElementsForRender();
    ...
}

private void EnsureAllTextElementsExist(Project project)
{
    foreach (var track in project.Tracks ?? [])
    {
        if (!string.Equals(track.TrackType, "text", StringComparison.OrdinalIgnoreCase))
            continue;
        foreach (var segment in track.Segments ?? [])
        {
            if (!string.IsNullOrWhiteSpace(segment.Text))
                _canvasViewModel?.GetOrCreateTextElement(segment.Id);
        }
    }
}
```

#### 1C. Better Error Reporting in TextRasterizer
```
File: RenderSegmentBuilder.cs → BuildRasterizedTextSegments Parallel.For
Change: Log.Warning → aggregate errors and report to UI
```

### Phase 2: UNIFIED RENDER ENGINE (1-2 tuần)

#### 2A. Switch to CompositionBuilder as primary
```
Current:  RenderViewModel → RenderSegmentBuilder → FFmpegCommandComposer
Target:   RenderViewModel → CompositionBuilder → FFmpegFilterGraphBuilder
```

Benefits:
- CompositionBuilder đã xử lý unified z-order trong single pass
- CompositionPlan là intermediate representation sạch (giống Resolve/Premiere)
- Dễ thêm validation layer giữa build và render

#### 2B. Pre-render Validation Gate
```csharp
public interface IRenderValidator
{
    RenderValidationResult Validate(CompositionPlan plan);
}

public class RenderValidationResult
{
    public bool IsValid { get; init; }
    public List<RenderWarning> Warnings { get; init; } = [];
    public List<RenderError> Errors { get; init; } = [];
}
```

Checks:
- Mọi text segment có linked element (hoặc auto-create)
- Mọi visual segment có asset file tồn tại
- Mọi rasterized PNG tồn tại sau khi tạo
- Không có segment trống (EndTime <= StartTime)
- Z-order không conflict
- Output path writable

#### 2C. Retire Legacy Pipeline
```
Deprecate: RenderSegmentBuilder (giữ lại cho tests/backward compat)
Deprecate: FFmpegCommandComposer (delegate qua FFmpegFilterGraphBuilder)
Primary:   CompositionBuilder → CompositionPlan → FFmpegFilterGraphBuilder
```

### Phase 3: PREVIEW-RENDER PARITY (2-4 tuần)

#### Vấn đề hiện tại:
- Preview: WPF TextBlock/Border → native text rendering
- Render: SkiaSharp TextRasterizer → PNG → FFmpeg overlay
- Font metrics, line wrapping, alignment CÓ THỂ KHÁC

#### Giải pháp: Unified SkiaSharp Canvas

```
Option A (Recommended): SkiaSharp cho cả Preview và Render
  - Thay WPF canvas bằng SKElement (SkiaSharp.Views.WPF)
  - Preview và Render chia sẻ chung rendering code
  - WYSIWYG hoàn toàn (giống Figma approach)

Option B: Direct FFmpeg Preview
  - Render preview frames bằng FFmpeg filter (low-fps)
  - Chính xác 100% nhưng chậm hơn
  - Chỉ phù hợp cho "render preview" mode

Option C (Hybrid):
  - WPF cho interactive editing (dragging, real-time)
  - SkiaSharp render cho static frame preview
  - Toggle giữa 2 mode
```

### Phase 4: PERFORMANCE OPTIMIZATION (2-4 tuần)

#### 4A. Text Rasterization Pipeline
```
Current:  Parallel.For CPU rasterize → sequential PNG write → FFmpeg read
Target:   Streaming pipeline → rasterize + pipe to FFmpeg in memory
```

#### 4B. GPU-Accelerated Text
```
Current:  SkiaSharp CPU → PNG → FFmpeg overlay filter
Target:   Direct drawtext trong FFmpeg (HOẶC) GPU text rasterize
```

FFmpeg `drawtext` filter benchmarks:
- CPU: ~50ms per frame (1080p, 3 text overlays)
- GPU (NVENC with text): ~5ms per frame

#### 4C. Incremental Render
```
Current:  Full re-render mỗi lần
Target:   Cache unchanged segments' outputs
          Chỉ re-render segments bị thay đổi
          Stitch cached + new segments
```

#### 4D. Render Queue Architecture
```csharp
public interface IRenderExecutor
{
    Task<RenderResult> ExecuteAsync(CompositionPlan plan, RenderOptions options, CancellationToken ct);
}

// Strategies:
public class SinglePassFFmpegExecutor : IRenderExecutor { ... }     // Hiện tại
public class ChunkedFFmpegExecutor : IRenderExecutor { ... }        // Chia chunk
public class GpuAcceleratedExecutor : IRenderExecutor { ... }       // GPU encode
public class DistributedExecutor : IRenderExecutor { ... }          // Multi-machine
```

---

## 5. TARGET ARCHITECTURE (Commercial-Grade)

### Module Structure:
```
PodcastVideoEditor.Core/
├── Models/
│   ├── Project, Track, Segment          (data models)
│   ├── CanvasElement hierarchy           (UI models)
│   └── CompositionPlan, CompositionLayer (render IR)
│
├── Services/
│   ├── Composition/
│   │   ├── ICompositionBuilder          (interface)
│   │   ├── CompositionBuilder           (implementation)
│   │   ├── IRenderValidator             (pre-render check)
│   │   └── CompositionValidator         (implementation)
│   │
│   ├── Render/
│   │   ├── IRenderExecutor              (strategy interface)
│   │   ├── FFmpegRenderExecutor         (FFmpeg subprocess)
│   │   ├── FFmpegFilterGraphBuilder     (filter_complex generation)
│   │   └── TextRasterizer               (SkiaSharp text → PNG)
│   │
│   ├── Sync/
│   │   ├── ElementSegmentSync           ★ NEW: bidirectional sync
│   │   ├── ElementSegmentRegistry       (O(1) lookup)
│   │   └── TrackStylePropagation        (shared text styles)
│   │
│   └── Timeline/
│       ├── UndoRedoService
│       └── CollisionDetector
│
PodcastVideoEditor.Ui/
├── ViewModels/
│   ├── RenderViewModel                  (orchestration only)
│   ├── CanvasViewModel                  (preview + editing)
│   └── TimelineViewModel                (timeline state)
│
└── Views/
    ├── CanvasView                       (unified SkiaSharp canvas) ★ Phase 3
    └── TimelineView
```

### Data Flow (Target):
```
User clicks Render
  │
  ├─ RenderViewModel.StartRenderAsync()
  │    │
  │    ├─ 1. EnsureAllElements() ★ NEW — materialize all lazy elements
  │    ├─ 2. ElementSegmentSync.SyncAll() ★ NEW — bidirectional sync
  │    │
  │    ├─ 3. CreateRenderSnapshot() → immutable copy
  │    │
  │    ├─ 4. CompositionBuilder.BuildPlanAsync() → CompositionPlan
  │    │      ├─ Unified single-pass compositing (all track types)
  │    │      ├─ Text rasterization (SkiaSharp → PNG)
  │    │      └─ Visualizer baking (async)
  │    │
  │    ├─ 5. RenderValidator.Validate(plan) ★ NEW — verify everything
  │    │      ├─ Check all files exist
  │    │      ├─ Check z-order integrity
  │    │      ├─ Check segment/element alignment
  │    │      └─ Report warnings + errors to user
  │    │
  │    ├─ 6. FFmpegFilterGraphBuilder.BuildCommand(plan) → FFmpeg command
  │    │
  │    └─ 7. IRenderExecutor.ExecuteAsync(plan) → output MP4
  │
  └─ Complete
```

---

## 6. IMPLEMENTATION PRIORITY

| Priority | Task | Impact | Effort |
|---|---|---|---|
| P0 | Bug #1: Content → Segment.Text sync | Fix text content mismatch | 2h |
| P0 | Bug #2: Pre-render element materialization | Fix missing text elements | 3h |
| P0 | Bug #3: Error logging in TextRasterizer | Diagnose silent failures | 1h |
| P1 | Switch to CompositionBuilder pipeline | Clean code, single source of truth | 1d |
| P1 | Pre-render validation gate | Catch errors before FFmpeg | 1d |
| P1 | Retire legacy RenderSegmentBuilder | Reduce maintenance | 0.5d |
| P2 | Unified SkiaSharp canvas (preview=render) | WYSIWYG fidelity | 2w |
| P2 | Render progress with frame thumbnails | User experience | 3d |
| P3 | GPU-accelerated text rendering | Performance | 1w |
| P3 | Incremental render caching | Performance | 1w |
| P3 | Chunked render executor | Reliability for long videos | 1w |

---

## 7. QUICK FIX CHECKLIST (Có thể sửa ngay)

- [ ] Thêm reverse sync: `TextOverlayElement.Content` → `Segment.Text`
- [ ] Thêm `EnsureAllTextElementsExist()` trước `SnapshotElementsForRender()`
- [ ] Thêm aggregate error log cho `BuildRasterizedTextSegments` Parallel.For
- [ ] Thêm `Log.Information` với count: "Rasterized {N} text segments, {M} succeeded, {F} failed"
- [ ] Thêm `Log.Debug` dump danh sách VisualSegments (bao gồm cả text PNGs) trước khi gửi cho FFmpeg
- [ ] Test: tạo project với 3+ text segments, render, verify tất cả xuất hiện
