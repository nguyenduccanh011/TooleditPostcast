# Active Task Pack

## Current Phase: TP-005 MVP Visual & Preview

**Task Pack hiện tại:** TP-005-MVP-VISUAL-AND-PREVIEW  
**Bối cảnh:** TP-004 (Multi-track) core đã xong (ST-1..ST-5). Khoảng trống MVP: (1) gắn ảnh/video vào segment visual, (2) preview chọn tỉ lệ khung hình, (3) preview hiển thị theo timeline (visual + text + audio).  
**Prerequisite:** Đã đọc `docs/state.md` mục **MVP Gap & Roadmap**; hiểu multi-track (Tracks, Segment.TrackId, z-order) từ TP-004.

---

## Task Pack: TP-005 MVP Visual & Preview

### Overview
Bù khoảng trống MVP: **segment visual** có thể gắn ảnh (tối thiểu); **preview** cho phép chọn tỉ lệ khung hình và hiển thị composite theo timeline (segment visual, text, audio sync). Nền tảng cho Phase 5 (Render từ Canvas).

### Subtasks (ST)

#### ST-1: Gắn ảnh (và tùy chọn video) vào segment visual — MVP
**Objective:** User có thể gán media (ít nhất ảnh) cho segment visual; dữ liệu lưu qua Asset hoặc path; Segment.BackgroundAssetId được set.
**Status:** ⏳ **NOT STARTED** — **CURRENT**

**Acceptance Criteria:**
- [ ] Có luồng chọn file ảnh (và tùy chọn video) cho segment đang chọn: ví dụ nút "Chọn ảnh" / "Chọn file" trong Segment Property Panel hoặc context menu segment.
- [ ] Khi chọn file: tạo Asset (ProjectId, FilePath, Type=Image/Video) và gán `Segment.BackgroundAssetId = asset.Id`; hoặc MVP đơn giản: lưu path vào Asset rồi gán. Persist (ProjectService / DatabaseService).
- [ ] Segment visual không có ảnh: hiển thị placeholder trong timeline/preview (đã có RenderHelper placeholder; có thể tái dùng).
- [ ] Build succeeds (0 errors).

**Implementation Notes:**
- Asset entity đã có (Core/Models/Asset.cs); cần AssetService (hoặc ProjectService) AddAssetAsync, GetAssetById; DbSet Assets nếu chưa có.
- Segment panel: binding SelectedSegment; khi có segment visual → hiển thị nút "Chọn ảnh" + thumbnail/path hiện tại; chọn file → tạo/lấy Asset → UpdateSegmentAsync(segment với BackgroundAssetId).
- File dialog: OpenFileDialog (WPF) filter ảnh/video; copy file vào AppData project hoặc lưu path tùy quyết định (docs/decisions.md).

**Phụ thuộc:** TP-004 core (Segment.TrackId, Tracks). Không phụ thuộc ST-2/ST-3.

---

#### ST-2: Preview — Chọn tỉ lệ khung hình (aspect ratio)
**Objective:** Preview (canvas/vùng xem) có khung theo tỉ lệ đã chọn (9:16, 16:9, 1:1, 4:5); đồng bộ với project/render settings.
**Status:** ⏳ **NOT STARTED**

**Acceptance Criteria:**
- [ ] Trong Editor: có cách chọn aspect ratio cho preview (dropdown hoặc nút: 9:16, 16:9, 1:1, 4:5). Giá trị lưu vào Project (đã có Project.AspectRatio hoặc RenderSettings) hoặc ViewModel preview.
- [ ] Khung preview (CanvasView hoặc vùng hiển thị tương đương) đổi kích thước/letterbox theo tỉ lệ chọn — không vỡ layout; nội dung scale/fit trong khung.
- [ ] Render settings (Resolution/AspectRatio) có thể đồng bộ với tỉ lệ preview (hoặc tách riêng; MVP tối thiểu preview đúng tỉ lệ).
- [ ] Build succeeds (0 errors).

**Implementation Notes:**
- Project.AspectRatio đã có ("9:16"); RenderViewModel có SelectedAspectRatio. Cần binding aspect ratio từ Project hoặc EditorViewModel xuống Canvas/Preview (CanvasViewModel.CanvasWidth/Height hoặc PreviewFrame aspect).
- XAML: khung preview với AspectRatio constraint (Viewbox, hoặc tính Width/Height từ ratio). Tham khảo `docs/editor-preview-and-image-api-plan.md` mục 2.

**Phụ thuộc:** Không bắt buộc ST-1. Có thể làm song song hoặc sau ST-1.

---

#### ST-3: Preview — Composite theo timeline (visual + text + audio sync)
**Objective:** Tại mỗi thời điểm playhead, preview hiển thị đúng: segment visual (ảnh tương ứng), segment text (subtitle/script), audio đã phát — composite theo track và z-order.
**Status:** ⏳ **NOT STARTED**

**Acceptance Criteria:**
- [ ] Khi playhead thay đổi (play hoặc scrub): xác định "active segments" mỗi track (StartTime ≤ playhead < EndTime); z-order theo track Order (track trên = front).
- [ ] **Visual track:** Vẽ ảnh của segment active (từ Segment.BackgroundAssetId → Asset.FilePath); không có ảnh → placeholder. Một track visual chỉ một segment active tại một thời điểm (không overlap).
- [ ] **Text track:** Hiển thị Text của segment text active (overlay subtitle/script). Có thể dùng layer text trên canvas hoặc TextBlock overlay.
- [ ] **Audio:** Đã có sync play với playhead (TimelineViewModel); không cần đổi nếu đã đúng.
- [ ] Preview cập nhật khi playhead thay đổi (timer hoặc event PlayheadPosition changed); mượt khi play (~30fps đủ).
- [ ] Build succeeds (0 errors). Manual test: load project, gán ảnh segment, play → preview đổi ảnh/text theo timeline.

**Implementation Notes:**
- TimelineViewModel đã có PlayheadPosition, Tracks, SegmentsForTrack (hoặc tương đương). Cần API "GetActiveSegmentsAtTime(double t)" → dict trackId → Segment (hoặc list theo z-order).
- CanvasViewModel hoặc PreviewViewModel: subscribe PlayheadPosition; tại mỗi t lấy active segments → vẽ nền (ảnh visual) + overlay text. Canvas hiện có Elements (title, logo, visualizer) — cần thêm layer "timeline background" từ segment visual + layer "timeline text" từ segment text; hoặc tách PreviewControl riêng chỉ composite timeline.
- Asset path → load bitmap (WPF/Skia) — cache nhỏ theo segment/asset để tránh load lại mỗi frame.

**Phụ thuộc:** ST-1 (có ảnh gán segment). ST-2 (tỉ lệ) có thể độc lập nhưng UX tốt hơn khi có cả hai.

---

### Dependencies TP-005

```
ST-1 (Gắn ảnh segment) ──┬──► ST-3 (Preview composite)
ST-2 (Preview aspect)   ──┘
```
- **ST-1** trước **ST-3** (composite cần ảnh từ segment). **ST-2** có thể song song ST-1 hoặc trước/sau.
- **Thứ tự đề xuất:** ST-1 → ST-2 → ST-3 (hoặc ST-1 → ST-3 rồi ST-2 nếu ưu tiên composite trước tỉ lệ).

---

## Task Pack: TP-004 Multi-track Timeline (đã core xong)

**Duration Target:** Week 9-10 (per state.md)  
**Prerequisite:** Đã đọc & hiểu `docs/MULTI-TRACK-TIMELINE-DESIGN.md` (mục 1-11)

---

## Task Pack: TP-004-MULTI-TRACK-TIMELINE

### Overview
Chuyển đổi timeline từ **flat segments** sang **multi-track architecture** (kiểu CapCut): mỗi track là một hàng (lane) có loại (text, visual, audio) và độc lập về va chạm. Tham chiếu: `docs/MULTI-TRACK-TIMELINE-DESIGN.md` mục 1-11. Nền tảng cho Phase 5 (Render Pipeline) rendering per-track + z-order.

### Subtasks (ST)

#### ST-1: Core Data Models - Track & Segment
**Objective:** Thêm entity `Track` vào codebase; cập nhật `Segment` và `Project` để hỗ trợ multi-track.
**Status:** ✅ **COMPLETE**

**Acceptance Criteria:**
- [ ] File `Core/Models/Track.cs` tạo mới, có properties: Id, ProjectId, Order, TrackType (text/visual/audio), Name, IsLocked, IsVisible, Segments (collection). Đầy đủ comments.
- [ ] `Segment.cs`: Thêm `TrackId` (string, foreign key). Giữ `Kind` và các property khác.
- [ ] `Project.cs`: Thêm `Tracks` collection (ICollection<Track>). Giữ `Segments` (để backward compat).
- [ ] Quan hệ: Project 1–N Track (cascade delete); Track 1–N Segment (cascade delete); Segment N–1 Track (required).
- [ ] Build succeeds (0 errors).

**Implementation Notes:**
- Không tạo migration trong ST-1 (để trong ST-2).
- Track.cs: ID = Guid.NewGuid().ToString(); Order = int (0=top=front).
- Segment.TrackId: bắt buộc cho segment mới; hiện tại để null để migrate dữ liệu (ST-2).

---

#### ST-2: EF Core Migration - Add Tracks Table & Data Migration
**Objective:** Tạo migration để thêm bảng Tracks, TrackId column, và migrate dữ liệu (3 track default + gán segment cũ).
**Status:** ✅ **COMPLETE**

**Acceptance Criteria:**
- [ ] `dotnet ef migrations add AddMultiTrackSupport` → `Migrations/202X_AddMultiTrackSupport.cs` tạo bảng Tracks với schema đúng (Id, ProjectId, Order, TrackType, Name, IsLocked, IsVisible).
- [ ] `Segment.TrackId` thêm nullable column.
- [ ] Data migration trong Up: Với mỗi Project, tạo 3 track (Text 1 Order 0, Visual 1 Order 1, Audio Order 2); gán mọi Segment.TrackId = Visual 1 ID.
- [ ] Down: xóa bảng, remove TrackId column.
- [ ] Sau migration: Segment.TrackId = NOT NULL, add FK constraint, add index.
- [ ] `dotnet ef database update` thành công (0 errors).

**Implementation Notes:**
- Track IDs: `Guid.NewGuid().ToString()`.
- Các ProjectId lấy từ bảng Projects; GroupBy ProjectId để tạo 3 track mỗi project.
- TrackId assignment: `UPDATE Segments SET TrackId = '<Visual 1 Track ID>' WHERE ProjectId = ...`.
- Migration file: viết C# code trong Up/Down methods.

---

#### ST-3: ProjectService & DatabaseService - CRUD Track & Load/Save
**Objective:** Cập nhật services để làm việc với Tracks; CRUD Track; load/save project include Tracks.
**Status:** ✅ **COMPLETE**

**Acceptance Criteria:**
- [ ] `ProjectService`: Thêm methods: `AddTrackAsync(project, track)`, `GetTracksAsync(projectId)`, `GetTrackByIdAsync(trackId)`, `UpdateTrackAsync(track)`, `DeleteTrackAsync(trackId)`. Không xóa segment khi xóa track (MVP: không xóa track; ST để future).
- [ ] `ProjectService.CreateProjectAsync()`: Tạo 3 track default (Text 1, Visual 1, Audio) tự động khi tạo project mới.
- [ ] `ProjectService.LoadProjectAsync(projectId)`: Include Tracks và Segments. Đảm bảo lazy load không bị lỗi.
- [ ] `ProjectService.ReplaceSegmentsAsync()` → thay thành `ReplaceSegmentsOfTrackAsync(project, trackId, newSegments)` (dùng cho Script apply).
- [ ] `DatabaseService`: Ensure DbContext.Tracks, DbContext.Segments query + include Tracks đúng.
- [ ] Build succeeds (0 errors). Xác nhận load/save project không errors (chạy unit test hoặc manual test).

**Implementation Notes:**
- Đồng thời cập nhật `CreateProjectAsync` thay vì tạo project mới bằng ctor.
- `ReplaceSegmentsOfTrackAsync`: xóa mọi segment thuộc track đó, thêm segment mới. Persist database.
- Method async/await; use DbContext SaveChangesAsync.

---

#### ST-4: TimelineViewModel - Logic & State Management
**Objective:** Cập nhật TimelineViewModel để quản lý Tracks (thay Segments); logic collision per-track; Add segment tới track đang chọn.
**Status:** ✅ **COMPLETE**

**Acceptance Criteria:**
- [ ] `TimelineViewModel`: Thay `ObservableCollection<Segment> Segments` bằng `ObservableCollection<Track> Tracks`. Giữ `SelectedSegment`; thêm `SelectedTrack`.
- [ ] Add property: `CollectionsView<Segment> SegmentsForTrack(trackId)` hoặc helper; timeline view dùng để render mỗi track.
- [ ] `AddSegmentCommand`: nhận `SelectedTrack` (default Visual 1), loại = visual, StartTime = playhead, End = playhead + 5s. Collision check chỉ cùng track.
- [ ] `ApplyScriptCommand`: xác định track text (Text 1 hoặc track đầu tiên Kind=text), gọi `ReplaceSegmentsOfTrackAsync(project, track.Id, ...)` → refresh Segments/Tracks từ database.
- [ ] Playhead sync: vẫn 30fps, không đổi. Cần update để lặp qua Tracks khi check Active segment.
- [ ] Build succeeds (0 errors).

**Implementation Notes:**
- INotifyPropertyChanged: OnPropertyChanged("Tracks"), OnPropertyChanged("SelectedTrack").
- AddSegment: if SelectedTrack is null → default Visual 1; if SelectedTrack.TrackType != "visual" → disable/warn.
- Playhead & active segment: iterate Tracks → mỗi track → tìm segment active (StartTime <= playhead < EndTime).

---

#### ST-5: TimelineView UI Layout - N Tracks + Header Column
**Objective:** Thiết kế & code XAML TimelineView để hiển thị N tracks (mỗi track = row), mỗi row có header (cột trái) + segment canvas (cột phải).
**Status:** ✅ **COMPLETE**

**Acceptance Criteria:**
- [ ] Layout: Grid 2 columns (trái = header, phải = timeline); Grid.RowDefinitions = N+2 rows (row 0=ruler, row 1..N=Track 1..N, row cuối=waveform audio).
- [ ] Row 0 (Ruler): header cell trống/label, timeline ruler.
- [ ] Row 1..N (Tracks):  
  - Left cell (cột trái): Track header (icon, name, lock, visibility) — tạm thời TextBlock hoặc StackPanel đơn giản (ví dụ "Text 1", "Visual 1", "Audio").
  - Right cell (cột phải): ItemsControl (hoặc Canvas) render Segments của track đó. Binding: `ItemsSource={Binding SegmentsForTrack(Track.Id)}` hoặc similar.
- [ ] Row cuối (Waveform): Audio track — WaveformCanvas từ ST-1 Phase 3.
- [ ] Height per row: Text/Audio = 48px; Visual = 100px (fixed for MVP). `RowDefinition Height="Auto" / Height="48" / Height="100"` tùy loại.
- [ ] Scroll: ScrollViewer span cột phải; sync ruler + waveform khi scroll (dùng ScrollViewer event, giống ST-9 hiện tại).
- [ ] Build succeeds (0 errors).

**Implementation Notes:**
- XAML: `<Grid>` + `<Grid.ColumnDefinitions>` (left=200px, right=*) + `<Grid.RowDefinitions>` (0=auto ruler, 1..N theo track, last=auto waveform).
- ItemsControl data: Bind `Tracks` → ItemTemplate → mỗi template layout (header + canvas). Hoặc tạo separate `TrackRowView.xaml`.
- Segment canvas mỗi track: giống hiện tại (canvas + items adorner mỗi segment).
- Scroll sync: thêm ScrollChanged event handle nếu cần (hiện tại ST-9 đã có).

---

#### ST-6: Track Header UI & Selection Logic
**Objective:** Implement track header (cột trái mỗi row), icon/tên/lock/visibility; track selection (click segment → select track).
**Status:** ⏸️ **DEFERRED** — Không bắt buộc MVP; làm cuối khi hoàn thiện (polish).

**Acceptance Criteria:**
- [ ] Track header template: icon (Unicode text: "T" text, "V" visual, 🔊 audio), tên track (Text binding Track.Name), lock icon (binding IsLocked, click toggle), visibility eye icon (binding IsVisible, click toggle).
- [ ] Styling: hover highlight; selected track (SelectedTrack binding) hiệu ứng (border, bg color).
- [ ] Selection logic: Click vào header/empty area of track → SelectedTrack = track đó. Click segment → SelectedSegment + SelectedTrack = track của segment.
- [ ] Add segment nút: nút "Add segment" → `AddSegmentCommand`. Disable hoặc tooltip nếu SelectedTrack không phải visual.
- [ ] Context menu (later, MVP): nút "..." hoặc right-click → Lock/Unlock, Show/Hide (toggle IsVisible). MVP có thể bỏ context menu.
- [ ] Build succeeds (0 errors).

**Implementation Notes:**
- Unicode: "T" (U+0054), "V" (U+0056), "🔊" (speaker emoji, U+1F50A).
- Header template: StackPanel horizontal: [icon TextBlock] + [name TextBlock].
- Lock/visibility buttons: click handler → ViewModel.ToggleLockCommand(track) / ToggleVisibilityCommand(track).
- Selection: MouseDown event trên header Border → `SelectedTrack = Track` (binding Command).

---

#### ST-7: Segment Property Panel Compatibility
**Objective:** Đảm bảo Segment Editor Panel hiện tại vẫn bind & hoạt động với multi-track.
**Status:** ⏸️ **DEFERRED** — Làm sau khi muốn hoàn thiện (cùng ST-6).

**Acceptance Criteria:**
- [ ] SegmentEditorPanel binding SelectedSegment — không đổi.
- [ ] Update references chỗ gọi `Segments` → `SelectedTrack.Segments` hoặc ensure context đúng.
- [ ] Khi delete/save segment, update track.Segments (hoặc project.Segments nếu còn ref).
- [ ] Build succeeds (0 errors).
- [ ] Manual test: mở project → chọn segment → edit properties (Start, End, Text) → save → kiểm tra timeline update đúng.

**Implementation Notes:**
- SegmentEditorPanel.cs: SelectedSegment binding từ TimelineViewModel vẫn đúng (không đổi).
- ProjectViewModel.SaveSegmentAsync: call ProjectService method thích hợp (hoặc pass TrackId nếu cần).

---

### Dependencies Between Subtasks

```
ST-1 → ST-2 → ST-3 → ST-4 → ST-5 ✅ | ST-6, ST-7 hoãn (làm sau)
```
- **Core (xong):** ST-1..ST-5 — multi-track data, UI layout đã đủ cho MVP.
- **ST-6, ST-7:** Hoãn — làm sau khi muốn hoàn thiện (Track header + Segment panel).

---

## Current Work Status

### Phase 3 Progress (TP-003)
- [x] ST-1: 100% (Audio track in timeline) ✅
- [x] ST-2: 100% (Timeline sync precision) ✅
- [x] ST-3: 100% (Script import/display — paste-only) ✅

**Phase 2 (TP-002):** ✅ Đã đóng (ST-7–ST-12 done). Chi tiết lưu trong worklog/state.

### Multi-track Timeline Progress (TP-004)
- [x] ST-1..ST-5: ✅ Core done
- [ ] ST-6, ST-7: ⏸️ Hoãn (polish)

### MVP Visual & Preview Progress (TP-005)
- [ ] ST-1: Gắn ảnh vào segment visual — **CURRENT**
- [ ] ST-2: Preview chọn tỉ lệ khung hình
- [ ] ST-3: Preview composite theo timeline (visual + text + audio)

---

## Next Action

**Current Subtask:** TP-005 ST-1 — Gắn ảnh (và tùy chọn video) vào segment visual — MVP.

**Resume Instructions (ST-1):**
- Thêm luồng "Chọn ảnh" cho segment visual: Segment Property Panel hoặc context — nút mở file ảnh/video → tạo Asset (FilePath) → gán Segment.BackgroundAssetId → persist.
- Kiểm tra Asset trong DbContext/ProjectService; nếu chưa có bảng Assets thì migration. Segment panel binding SelectedSegment + hiển thị thumbnail/path + nút Chọn ảnh.
- Build 0 errors; test: tạo segment visual → chọn ảnh → lưu → mở lại project thấy ảnh gán đúng.

**Sau ST-1:** ST-2 (Preview aspect ratio) rồi ST-3 (Preview composite sync). Hoặc ST-1 → ST-3 rồi ST-2.

**TP-004 ST-6/ST-7:** Vẫn hoãn (polish). Phase 5 (Render) sau khi TP-005 xong.

---

Last updated: 2026-02-12 (TP-005 added; ST-1 = next)

---

## Session 16 Summary

**Dates:** 2026-02-12 | **Status:** ✅ Session Paused (ST-5 Complete)

### Completed in This Session
- ✅ **ST-5: TimelineView Multi-track UI Layout** (P2 Priority)
  - Created TrackHeightConverter (TrackType → row height)
  - Refactored TimelineView.xaml with ItemsControl(Tracks) + StackPanel layout
  - Multi-track Grid: 2 columns (header + timeline), 3 rows (ruler + tracks + waveform)
  - Updated CodeBehind: UpdateSegmentLayout, UpdateSegmentSelection for multi-track
  - Build verified: ✅ 0 Errors

### P1 Foundation + P2 UI Progress
- **P1 (Foundation):** 100% (ST-1..ST-4)
- **P2 (UI):** ST-5 ✅ Layout complete. ST-6, ST-7 hoãn — làm sau khi muốn hoàn thiện.

### Build Status
✅ **0 Errors** | TP-004 core done. **Next:** TP-005 ST-1.

---

## Resuming Next Session

**Next:** TP-005 ST-1 — Gắn ảnh vào segment visual (MVP).

**Quick Start ST-1:**
1. Đọc `docs/active.md` → TP-005 ST-1 Acceptance Criteria & Implementation Notes.
2. Kiểm tra Asset trong Core (model + DbContext + migration nếu chưa có bảng).
3. Segment Property Panel (hoặc tương đương): nút "Chọn ảnh" / "Chọn file" → OpenFileDialog (image/video) → tạo Asset → gán Segment.BackgroundAssetId → SaveSegment/UpdateSegment.
4. Build + manual test: chọn segment visual → chọn ảnh → lưu → reload project.

**Sau TP-005:** Phase 5 (Render #10, #11). Phase 4 (AI) hoặc Phase 6 (ST-6/ST-7, #12) tùy ưu tiên.

---
