# Active Task Pack - Multi-track Timeline

## Current Phase: Multi-track Timeline (TP-004)

**Duration Target:** Week 9-10 (per state.md)  
**Task Pack:** TP-004-MULTI-TRACK-TIMELINE  
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
**Status:** ⏳ **NOT STARTED**

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
**Status:** ⏳ **NOT STARTED**

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
ST-1 → ST-2 → ST-3 → ST-4 → ST-5 → ST-6 → ST-7
```
- **Sequential:** Mỗi ST phụ thuộc vào predecessor (data model → db → service → viewmodel → UI → header → panel).
- **Không parallel:** dữ liệu thay đổi, cần migrate đúng, service cập nhật rồi mới viewmodel.

---

## Current Work Status

### Phase 3 Progress (TP-003)
- [x] ST-1: 100% (Audio track in timeline) ✅
- [x] ST-2: 100% (Timeline sync precision) ✅
- [x] ST-3: 100% (Script import/display — paste-only) ✅

**Phase 2 (TP-002):** ✅ Đã đóng (ST-7–ST-12 done). Chi tiết lưu trong worklog/state.

### Multi-track Timeline Progress (TP-004)
- [x] ST-1: (Data Models Track & Segment) ✅
- [x] ST-2: (Migration) ✅
- [x] ST-3: (ProjectService & DatabaseService) ✅
- [x] ST-4: (TimelineViewModel) ✅
- [x] ST-5: (TimelineView UI Layout) ✅
- [ ] ST-6: (Track Header & Selection) — **CURRENT** (P2 priority)
- [ ] ST-7: (SegmentEditorPanel Compatibility) — P3 priority

---

## Next Action

**Current Subtask:** TP-004 ST-6 — Track Header UI & Selection Logic.

**Resume Instructions:**
- ST-6: Implement track header UI with icons & selection:
  - Track header template: Unicode icon ("T" text, "V" visual, "🔊" audio) + Track.Name
  - Selection styling: Click header/track area → SelectedTrack binding + highlight effect
  - Lock/visibility toggles (MVP): Buttons or click handlers for IsLocked/IsVisible (full ST-6 features)
  - Test: Click empty area in track → select track, click segment → select segment + track
- Build xác nhận succeeds (0 errors).

---

Last updated: 2026-02-12 Session 16 (ST-1 through ST-5 complete, P2 UI layer ✅)

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
- **P1 (Foundation):** 100% complete (ST-1 through ST-4)
  - Data models, migration, services, ViewModel logic all multi-track enabled
- **P2 (UI):** 50% complete (ST-5 complete, ST-6 ready)
  - ST-5 ✅ Layout complete (N tracks rendered, segment canvases working)
  - ST-6 🔜 Track headers (icons, lock, visibility, selection) — **NEXT**

### Build Status
✅ **0 Errors** | All changes compile successfully | Ready to resume ST-6

---

## Resuming Next Session

**Next Subtask:** TP-004 ST-6 — Track Header UI & Selection Logic

**Quick Start:**
1. Open `docs/active.md` to review ST-6 AC
2. Create track header template in TimelineView.xaml:
   - Add Unicode icons: "T" (text), "V" (visual), "🔊" (audio)
   - Display Track.Name + icons in header cells
   - Implement SelectedTrack binding for selection highlight
3. Add lock/visibility toggle buttons (MVP simple buttons)
4. Update ViewModel commands if needed for track selection
5. Test: Load project → click track header → verify SelectedTrack updates

**Files to Modify:**
- `Ui/Views/TimelineView.xaml` — Track header template enhancement
- `Ui/ViewModels/TimelineViewModel.cs` — Track selection command (if needed)
- Consider: `Ui/Views/TimelineView.xaml.cs` — Click handlers for header selection

**Expected Output:**
- ST-6 AC all met ✅
- Build: 0 Errors
- Visual: Tracks show icons + names, selection highlights track row

**Estimated Duration:** 1-1.5 hours

---
