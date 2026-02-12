# Code Review – Thay đổi chưa commit (TP-004 Multi-track)

**Ngày:** 2026-02-12  
**Phạm vi:** Multi-track timeline (ST-1 → ST-5), build.bat, docs, migration, UI.

---

## 1. Tổng quan

- **Build:** Đổi solution từ `PodcastVideoEditor.slnx` → `PodcastVideoEditor.sln` (đúng).
- **Docs:** active.md, state.md, worklog.md, MULTI-TRACK-TIMELINE-DESIGN.md cập nhật rõ ràng, thứ tự ST và session notes đầy đủ.
- **Core:** Track model, Segment.TrackId, Project.Tracks, migration, ProjectService CRUD + ReplaceSegmentsOfTrackAsync, TimelineViewModel chuyển sang Tracks + per-track collision, TimelineView layout N tracks.
- **UI:** MainWindow thêm row Script panel; TimelineView refactor 2 cột (header + timeline), ItemsControl Tracks; SegmentEditorPanel chỉnh margin/header.

---

## 2. Điểm tốt

- **Models:** `Track.cs` và `SegmentKind.cs` rõ ràng, XML comment đủ. Quan hệ Project → Track → Segment và cascade delete cấu hình đúng trong `AppDbContext`.
- **Migration:** Data migration trong `Up()` tạo 3 track mặc định mỗi project và gán segment cũ vào Visual 1, sau đó `AlterColumn` TrackId NOT NULL. `Down()` đúng thứ tự (nullable → drop FK → drop table → drop index → drop column).
- **ProjectService:** ReplaceSegmentsOfTrackAsync validate project/trackId, gán ProjectId/TrackId cho segment mới, xóa segment cũ trong track rồi thêm mới, có logging. CRUD Track đầy đủ. CreateProjectAsync tạo 3 track mặc định. ReplaceSegmentsAsync được đánh dấu Obsolete đúng cách.
- **TimelineViewModel:** Collision chỉ trong cùng track (CheckCollisionInTrack), Add segment vào visual track, Apply script vào text track, Clear/Delete/Duplicate theo SelectedTrack, UpdateSegmentTiming/TrySnapToBoundary nhận track. SelectSegment cập nhật SelectedTrack.
- **TimelineView:** TrackHeightConverter (text/audio 48px, visual 100px), layout 2 cột, ItemsControl Tracks với header + segment canvas từng track, code-behind UpdateSegmentLayout/UpdateSegmentSelection duyệt theo Tracks. Playhead và scroll tách cột header (fixed) và nội dung (scroll ngang) hợp lý.
- **TrackHeightConverter:** One-way converter, fallback 48px, code gọn.

---

## 3. Vấn đề cần sửa

### 3.1 (Bug) Sau “Áp dụng script”, timeline vẫn hiển thị segment cũ

- **Nguyên nhân:** `ReplaceSegmentsOfTrackAsync` chỉ cập nhật DB. `CurrentProject` và `CurrentProject.Tracks[].Segments` vẫn là bản in-memory cũ. `ApplyScriptAsync` gọi `LoadTracksFromProject()` nhưng nguồn dữ liệu vẫn là `_projectViewModel.CurrentProject` chưa reload từ DB.
- **Hậu quả:** User áp dụng script xong, text track trên UI vẫn là segment cũ.
- **Cách sửa:** Sau khi gọi `ReplaceSegmentsOfTrackAsync` trong `ProjectViewModel.ReplaceSegmentsAndSaveAsync`, reload project từ DB và gán lại `CurrentProject`:

```csharp
// ProjectViewModel.ReplaceSegmentsAndSaveAsync, sau await _projectService.ReplaceSegmentsOfTrackAsync(...):
var refreshed = await _projectService.GetProjectAsync(CurrentProject.Id);
if (refreshed != null)
    CurrentProject = refreshed;
```

Sau đó `TimelineViewModel.ApplyScriptAsync` gọi `LoadTracksFromProject()` như hiện tại sẽ dùng dữ liệu mới.

---

### 3.2 Segment.TrackId nullable trong code, NOT NULL trong DB

- **Hiện trạng:** `Segment.TrackId` là `string?`. Sau migration, cột DB là NOT NULL.
- **Gợi ý:** Giữ nullable trong C# vẫn an toàn cho dữ liệu cũ (đã migrate). Nếu muốn đồng bộ với DB, có thể đổi thành `string` và đảm bảo mọi chỗ tạo segment đều gán TrackId (AddSegmentAtPlayhead, ApplyScriptAsync, DuplicateSelectedSegment đã gán). Khi save, nếu có segment null TrackId sẽ lỗi từ EF/DB — có thể thêm validation trong ProjectService trước khi SaveChanges.

---

### 3.3 Migration: định dạng Id của Track (SQLite vs C#)

- **Hiện trạng:** Migration dùng `lower(hex(randomblob(16)))` (32 ký tự hex); C# tạo track mới bằng `Guid.NewGuid().ToString()` (format có dấu gạch).
- **Ý nghĩa:** Id vẫn unique; chỉ khác format giữa project tạo từ migration (hex) và project tạo mới từ app (GUID). Không gây lỗi, chỉ cần lưu ý nếu có so sánh/parse Id.
- **Tùy chọn:** Nếu muốn thống nhất, có thể sinh Id trong migration bằng format giống GUID (SQLite không có built-in Guid, cần chuỗi 36 ký tự); không bắt buộc cho hoạt động hiện tại.

---

### 3.4 ClearAllSegments / DeleteSelectedSegment không persist DB

- **Hiện trạng:** ClearAllSegments chỉ `SelectedTrack.Segments.Clear()`, DeleteSelectedSegment chỉ `SelectedTrack.Segments.Remove(SelectedSegment)`. Thay đổi chỉ ở in-memory, chưa gọi service để lưu DB.
- **Hậu quả:** Sau khi mở lại project hoặc reload, segment đã xóa/clear vẫn còn.
- **Gợi ý:** Sau khi clear/remove, gọi ProjectService (ví dụ method kiểu UpdateSegmentAsync/RemoveSegmentAsync hoặc SaveProjectAsync) để persist. Hoặc ghi rõ trong docs/ST-6, ST-7 là “chưa persist” và để task sau.

---

### 3.5 LoadTracksFromProject: ép Track.Segments sang ObservableCollection

- **Code:** `if (track.Segments is not ObservableCollection<Segment>) track.Segments = new ObservableCollection<Segment>(track.Segments ?? Array.Empty<Segment>());`
- **Nhận xét:** Đúng cho binding WPF (thay đổi collection cần notify). Lưu ý: đang thay collection trên entity load từ EF; khi save project sau này cần đảm bảo EF vẫn track đúng (thường vẫn OK vì reference segment không đổi). Ổn cho MVP.

---

### 3.6 TimelineView: ScrollViewer lồng nhau

- **Hiện trạng:** OuterScroller (VerticalScrollBarVisibility="Auto", HorizontalScrollBarVisibility="Disabled"), bên trong cột 1 có TimelineScroller (horizontal). Document trong worklog đã mô tả.
- **Lưu ý:** Trên một số bản Windows/WPF, scroll lồng có thể gây hành vi chuột kỳ lạ (zoom/pan). Nếu sau này có vấn đề, có thể cân nhắc một ScrollViewer với scroll cả hai chiều hoặc sync scroll bằng code.

---

## 4. Gợi ý cải thiện (không chặn commit)

- **ReplaceSegmentsOfTrackAsync:** Có thể cập nhật luôn `project.Tracks` (tìm track theo trackId và gán lại Segments từ `list`) để in-memory đồng bộ ngay mà không cần reload project. Hiện tại fix bằng reload (mục 3.1) là đủ.
- **Segment:** Dùng `SegmentKind.Visual` / `SegmentKind.Text` thay cho string `"visual"` / `"text"` ở vài chỗ (AddSegmentAtPlayhead, ApplyScriptAsync) để tránh typo.
- **Track type:** Tương tự, có thể dùng constant cho `"text"`, `"visual"`, `"audio"` (ví dụ trong TrackHeightConverter và TrackType).
- **GetSegmentsForTrack:** Trả về `Array.Empty<Segment>()` hoặc `new List<Segment>()` đều được; có thể thống nhất một kiểu (ví dụ `Enumerable.Empty<Segment>().ToList()` hoặc list rỗng) để API nhất quán.

---

## 5. File mới (chưa track)

- **Track.cs, SegmentKind.cs, RenderHelper.cs, TrackHeightConverter.cs:** Nội dung ổn, comment đủ, không có lỗi logic hiển nhiên.
- **Migration 20260212034910_AddMultiTrackSupport.cs:** Logic Up/Down và data migration đúng; chỉ lưu ý mục 3.3 (định dạng Id) nếu muốn thống nhất sau này.
- **MANUAL-TEST-TP004.md, editor-preview-and-image-api-plan.md, multitrack-segment-plan.md, segment-timeline-alignment-fix.md:** Chưa đọc chi tiết; nên giữ và commit cùng nhóm docs nếu thuộc TP-004.

---

## 6. Kết luận

- **Nên sửa trước khi commit:** Mục 3.1 (reload project sau ReplaceSegmentsOfTrackAsync) để “Áp dụng script” cập nhật đúng timeline.
- **Nên làm sớm:** Mục 3.4 (persist khi Clear/Delete segment) nếu muốn dữ liệu không mất sau reload.
- **Có thể để sau:** 3.2 (nullable TrackId), 3.3 (định dạng Id), 3.5, 3.6 và các gợi ý mục 4.

Sau khi sửa 3.1 (và tùy chọn 3.4), có thể commit với message dạng:  
`feat(timeline): multi-track support ST-1–ST-5 (models, migration, services, ViewModel, TimelineView)`
