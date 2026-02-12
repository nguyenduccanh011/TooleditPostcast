# Thiết kế chi tiết: Timeline đa track & Segment đa loại

**Mục tiêu:** Timeline nhiều track (kiểu CapCut): track text, track visual, track audio; nhiều track cùng loại; segment nhiều loại; track độc lập (cùng thời điểm có thể có segment trên nhiều track). Nút "Add segment" mặc định = segment visual (chọn ảnh), chuẩn bị cho module Assets.

**Trạng thái:** Đề xuất thiết kế (chưa implement).  
**Tham chiếu:** `state.md` Phase Commitments, `active.md` (multi-track để sau Phase 3), ảnh tham khảo CapCut.

---

## 1. Tổng quan

### 1.1 Nguyên tắc thiết kế

- **Track** = một hàng (lane) trên timeline, có loại (text / visual / audio) và thứ tự hiển thị (z-order).
- **Segment** thuộc đúng **một** track; có `Kind` (visual, text, audio) — nên nhất quán với loại track (có thể ép hoặc cho phép linh hoạt tùy product).
- **Va chạm (collision)** chỉ kiểm tra **trên cùng một track**: hai segment cùng track không được overlap; khác track thì được.
- **Add segment:** mặc định thêm segment **visual** vào một track visual (có thể chọn ảnh từ Assets sau); hỗ trợ mở rộng cho module Assets.

### 1.2 So sánh với hiện tại

| Khía cạnh | Hiện tại | Sau thiết kế |
|-----------|----------|--------------|
| Dữ liệu | `Project.Segments` (flat list) | `Project.Tracks` → mỗi Track có `Segments` (hoặc Segment.TrackId) |
| Timeline UI | 1 hàng segment + 1 hàng audio (waveform) | N hàng, mỗi hàng = 1 track (có thể nhiều track text, nhiều track visual, 1+ audio) |
| Collision | Toàn bộ segments | Chỉ segments **cùng track** |
| Add segment | Thêm vào list chung, không chỉ định track | Thêm vào **track được chọn** (mặc định track visual), **kind = visual** |
| Script apply | Replace toàn bộ segments | Replace segments của **track script/text** (ví dụ track đầu tiên kind=text) |

---

## 2. Data model

### 2.1 Entity: Track

Tạo model mới trong `Core/Models/Track.cs`:

```csharp
/// <summary>
/// A timeline track (lane). Contains segments of a given type.
/// Order = display order (0 = top). Same-type tracks allowed (e.g. Visual 1, Visual 2).
/// </summary>
public class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;
    /// <summary>Display order (0 = top).</summary>
    public int Order { get; set; }
    /// <summary>Track type: "text" | "visual" | "audio".</summary>
    public string TrackType { get; set; } = "visual";
    /// <summary>User-visible name, e.g. "Text 1", "Visual 2".</summary>
    public string Name { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public bool IsVisible { get; set; } = true;

    public Project? Project { get; set; }
    public ICollection<Segment> Segments { get; set; } = [];
}
```

- **TrackType:** `text` (script), `visual` (video/ảnh), `audio` (âm thanh riêng của clip, nếu có).
- **Order:** quyết định thứ tự hàng (0 = trên cùng = lớp phía trước; số lớn = xuống dưới = lớp phía sau). Render: vẽ từ Order lớn → Order nhỏ (back → front).

### 2.2 Cập nhật Segment

Trong `Segment.cs` thêm:

- `TrackId` (string, FK → Track). Bắt buộc: mỗi segment thuộc một track.
- Giữ nguyên: `Kind` ("visual" | "text" | "audio" | ...). Khuyến nghị: **Kind trùng với TrackType** của track chứa segment (để logic render/UI đơn giản). Khi Add segment visual → thêm vào track visual, `Kind = "visual"`.
- **Order** trong segment: thứ tự trong track (nếu cần sort khi cùng track có nhiều segment không overlap — hiện tại có thể sort theo StartTime).

Quan hệ:

- `Project` 1 — N `Track` (cascade delete).
- `Track` 1 — N `Segment` (cascade delete).
- `Segment` N — 1 `Track` (required).

### 2.3 Project

- Thêm: `ICollection<Track> Tracks { get; set; }`.
- **Không bỏ** `Project.Segments` ngay: có thể giữ để backward compatibility trong migration (xem mục 4), sau khi migration xong có thể chỉ đọc qua `Tracks.SelectMany(t => t.Segments)` hoặc deprecate.

### 2.4 Asset (đã có)

- `Segment.BackgroundAssetId` (hoặc asset reference) dùng cho segment **visual** khi chọn ảnh. Module Assets sau sẽ cung cấp: upload ảnh → tạo Asset → gán `BackgroundAssetId` khi tạo/sửa segment visual.

---

## 3. Track mặc định khi tạo project

Khi tạo project mới, tạo sẵn một bộ track mặc định (để UX giống CapCut, không bắt user tạo track từ trắng):

- **Text 1** (TrackType = "text", Order = 0) — script/subtitle.
- **Visual 1** (TrackType = "visual", Order = 1) — ảnh/video nền.
- **Audio** (TrackType = "audio", Order = 2) — có thể “ảo”: không chứa segment clip mà chỉ hiển thị waveform của project audio; hoặc sau này hỗ trợ clip audio riêng.

Gợi ý: **Audio** có thể là track đặc biệt “chỉ hiển thị waveform”, không lưu segment (hoặc ít nhất một track audio ảo). Các track khác đều lưu segment bình thường.

---

## 4. Migration dữ liệu (flat → multi-track)

### 4.1 Migration schema (EF Core)

1. Tạo bảng `Tracks`: Id, ProjectId, Order, TrackType, Name, IsLocked, IsVisible.
2. Thêm cột `Segment.TrackId` (nullable lúc đầu để migration).
3. Migration dữ liệu:
   - Với mỗi Project: tạo 3 track mặc định (Text 1, Visual 1, Audio) với Order 0,1,2.
   - Gán mọi Segment hiện có vào track **Visual 1** (vì hiện tại segment đang dùng như visual/script lẫn lộn): `UPDATE Segments SET TrackId = '<id Visual 1>' WHERE ProjectId = ...`.
4. Sau đó đổi `Segment.TrackId` thành NOT NULL, thêm FK, index.

### 4.2 Backward compatibility

- **Script apply (ST-3):** Hiện tại replace toàn bộ segments. Sau khi có track: replace segments của **track text** (ví dụ track đầu tiên có TrackType = "text"). Các track khác không đổi.
- **Load project cũ (trước migration):** Migration bước trên đưa toàn bộ segment vào Visual 1, nên project cũ vẫn có dữ liệu hợp lệ.

---

## 5. Logic nghiệp vụ

### 5.1 Va chạm (collision)

- **Chỉ kiểm tra overlap giữa các segment trên cùng một track.**
- Công thức: hai segment overlap khi `segmentA.StartTime < segmentB.EndTime && segmentB.StartTime < segmentA.EndTime`.
- Khi Add/Duplicate/Move/Resize: gọi `CheckCollision(segment, trackId)` — chỉ so với `Segments` có cùng `TrackId`.
- TimelineViewModel (hoặc service) cần nhận biết “segment đang thao tác thuộc track nào” (từ binding hoặc context menu).

### 5.2 Add segment

- **Hành vi đề xuất:**
  - User chọn **một track** (click vào track header hoặc track row) → “track đang chọn”.
  - Nút **“Add segment”** = thêm segment vào **track đang chọn** (nếu không chọn thì mặc định track **Visual 1**).
  - Segment mới: `Kind = "visual"`, `StartTime = PlayheadPosition`, `EndTime = PlayheadPosition + 5` (hoặc default duration), `Text = "New Segment"`, `BackgroundAssetId = null`.
  - **Sau này (module Assets):** Sau khi thêm, mở Property panel hoặc dialog “Chọn ảnh” để gán `BackgroundAssetId` từ danh sách Assets (upload ảnh → chọn asset).
- Mở rộng: có thể có “Add text segment” / “Add visual segment” tách nút, nhưng tối thiểu **Add segment = visual** như yêu cầu.

### 5.3 Script apply (Áp dụng script)

- Parse script paste → danh sách (Start, End, Text).
- Xác định **track script:** ví dụ track đầu tiên có `TrackType == "text"` (hoặc track có Name "Text 1").
- **Replace toàn bộ segments của track đó** bằng danh sách segment mới (Kind = "text"); không đụng đến segments của track visual/audio.
- Persist: cập nhật chỉ segments của track text (xóa segment cũ của track đó, thêm segment mới).

### 5.4 Duplicate / Delete / Clear

- **Duplicate:** duplicate trong cùng track; collision check cùng track.
- **Delete:** xóa segment đang chọn (bất kể track).
- **Clear All:** có thể “Clear all segments” (mọi track) hoặc “Clear track” (chỉ track đang chọn). Đề xuất: giữ Clear All = xóa hết segment mọi track; thêm sau “Clear track” nếu cần.

---

## 6. UI Timeline

### 6.1 Thứ tự lớp (z-order) — đã chốt

- **Track trên cùng** = lớp **phía trước** (vẽ sau cùng, hiển thị đè lên).
- **Track dưới cùng** = lớp **phía sau** (vẽ trước).
- Order 0 = hàng **trên cùng** (dưới ruler) = front; Order tăng → xuống dưới = back. Khi render: vẽ từ track dưới lên track trên (back → front).

### 6.2 Cấu trúc layout: cột trái + timeline

- **Cột trái:** Chứa **tiêu đề track** (text, visual, audio) — mỗi hàng track có ô trái: icon, tên track, lock, visibility. Row 0 (ruler) cũng có ô trái (có thể trống hoặc nhãn “Thời gian”). Sau này bổ sung thêm tính năng vào cột này.
- **Cột phải:** Ruler (row 0) + vùng segment từng track (row 1..N) + waveform (row cuối). Cùng scroll ngang.

### 6.3 Cấu trúc hàng (rows)

- **Row 0:** Ruler — ô trái thuộc cột tiêu đề (trống hoặc nhãn), ô phải = thước thời gian.
- **Row 1..N:** Mỗi row = một **Track**:
  - Ô trái: icon TrackType, tên track, lock, visibility (eye).
  - Ô phải: Canvas/ItemsControl hiển thị segments của track đó (`Segment.TrackId == track.Id`).
- **Row cuối (cố định):** Audio waveform (project audio) — track đặc biệt chỉ hiển thị, không chứa segment.

### 6.4 Chiều cao hàng track — đã chốt

- **Cố định**, khác nhau theo loại track:
  - **Text, Audio:** chiều cao **hẹp** (ví dụ 40–48px) — đủ hiển thị label/segment bar.
  - **Visual:** chiều cao **bình thường/lớn hơn** (ví dụ 80–100px) — để hiển thị **thumbnail/visual sơ bộ** ảnh trong segment.
- Sau có thể cho user resize (phase sau).

### 6.5 Binding ViewModel

- **TimelineViewModel:**
  - Thay `ObservableCollection<Segment> Segments` bằng **ObservableCollection<Track> Tracks** (mỗi Track có thể wrap hoặc expose `ObservableCollection<Segment>` cho ItemsControl của từng hàng).
  - Hoặc giữ `Segments` nhưng lọc theo track khi render từng row: `Tracks` là nguồn gốc, mỗi track binding tới `track.Segments`.
- **Selected segment:** vẫn một `SelectedSegment`; cần biết `SelectedTrack` (track chứa segment đang chọn) cho Add/Duplicate/Collision.

### 6.6 Track header (ô trái mỗi hàng)

- Icon: Text = “T”, Visual = “V”/icon ảnh, Audio = icon loa (MVP: Unicode/text; sau dùng icon font).
- Tên: `Track.Name` (e.g. "Text 1", "Visual 1").
- Lock: `Track.IsLocked` — khi lock, không cho kéo/sửa segment trên track đó.
- Visibility: `Track.IsVisible` — ẩn hàng (và có thể bỏ qua khi render).

### 6.7 Add segment và context

- “Track đang chọn”: khi user click vào segment → selected segment + selected track = track của segment đó; khi click vào vùng trống của một track → selected track, selected segment = null.
- Nút **Add:** thêm segment visual vào selected track (hoặc Visual 1 nếu chưa chọn track). Có thể đổi label nút thành “Add visual” để rõ.

---

## 7. Render & Canvas (Phase 5)

- Tại mỗi thời điểm `t`, cần biết “segment đang active” trên từng track (segment mà `StartTime <= t < EndTime`).
- **Z-order (đã chốt):** Track **trên cùng** = lớp phía trước (vẽ sau cùng); track **dưới cùng** = lớp phía sau (vẽ trước). Composition: vẽ từ track dưới lên track trên (Order nhỏ → Order lớn = back → front).
- Render từ Canvas: lấy danh sách segment active theo thời điểm, áp dụng element/background theo thứ tự layer. Chi tiết để Phase 5 (Render Pipeline).

---

## 8. Module Assets (sau)

- Upload ảnh → tạo `Asset`, lưu path (AppData).
- Trong Property panel của segment **visual**: dropdown hoặc browser “Chọn ảnh” → list Assets của project → gán `Segment.BackgroundAssetId`.
- “Add segment” (visual) → tạo segment → mở panel hoặc dialog chọn ảnh ngay (optional). Thiết kế hiện tại (segment visual + BackgroundAssetId) đã sẵn sàng cho bước này.

---

## 9. Thứ tự triển khai đề xuất

1. **Core/Models:** Thêm `Track.cs`, cập nhật `Segment` (TrackId), `Project` (Tracks).
2. **Database:** Migration thêm bảng Tracks, cột Segment.TrackId, dữ liệu mặc định (track + gán segment cũ vào Visual 1).
3. **ProjectService / DatabaseService:** CRUD Track; khi load project include Tracks + Segments; ReplaceSegments → thay bằng “ReplaceSegmentsOfTrack(project, trackId, newSegments)” hoặc tương đương; tạo project mới tạo 3 track mặc định.
4. **TimelineViewModel:** Nguồn dữ liệu = Tracks; collision theo track; Add segment vào selected track (default Visual 1), Kind = "visual"; Apply script → track text.
5. **TimelineView (XAML):** N hàng track (ItemsControl Tracks → mỗi item một row: header + segment canvas); playhead span toàn bộ; scroll đồng bộ.
6. **Segment property panel:** Giữ nguyên; khi có Assets, thêm control chọn ảnh cho segment visual.
7. **Sau:** Module Assets (upload, picker), “Clear track”, thêm track (add/remove track).

---

## 10. Tóm tắt quyết định

| Chủ đề | Quyết định |
|--------|------------|
| Track | Entity mới: Id, ProjectId, Order, TrackType (text/visual/audio), Name, IsLocked, IsVisible. |
| Segment | Thêm TrackId (FK). Giữ Kind. Collision chỉ cùng track. |
| Mặc định project | 3 track: Text 1, Visual 1, Audio (audio có thể chỉ waveform). |
| Add segment | Mặc định segment visual, vào track đang chọn (hoặc Visual 1). Sẵn sàng gán ảnh qua Assets sau. |
| Script apply | Replace segments của track text (Text 1), không đụng track khác. |
| UI | Mỗi track = 1 row (header + segment canvas); collision per-track. |
| **Z-order** | Track trên cùng = lớp phía trước (vẽ sau); track dưới cùng = lớp phía sau (vẽ trước). |
| **Cột trái** | Chứa tiêu đề track (text, visual, audio); ruler row cũng có ô trái; sau bổ sung thêm tính năng. |
| **Chiều cao track** | Cố định, khác nhau: text/audio hẹp (40–48px); visual cao hơn (80–100px) để thumbnail. |

Tài liệu này có thể được đưa vào TP/ST khi bắt đầu phase “Multi-track timeline” (sau Phase 3). Khi implement, cập nhật `docs/decisions.md` với ADR ngắn và tham chiếu file này.

---

## 11. Đã chốt (2026-02-12)

### A, B, C — theo quyết định product

- **A. Z-order:** Track **trên cùng** = lớp **phía trước** (vẽ sau cùng); track **dưới cùng** = lớp **phía sau** (vẽ trước). Order 0 = trên cùng = front.
- **B. Chiều cao track:** Tạm thời **cố định**, **khác nhau** theo loại: track **text** và **audio** **hẹp hơn** (40–48px); track **visual** **bình thường/lớn hơn** (80–100px) để có thể hiển thị visual sơ bộ (thumbnail) ảnh trong track.
- **C. Ruler và cột trái:** **Cột trái** chứa **tiêu đề track** (text, visual, audio); ruler cũng nằm trong layout có cột trái (row 0 có ô trái — trống hoặc nhãn). Sau này bổ sung thêm tính năng vào cột này.

### D–K — theo khuyến nghị

| # | Quyết định |
|---|------------|
| **D** | Hàng waveform (Audio) **cố định dưới cùng**; không chứa segment; không đổi thứ tự. |
| **E** | MVP **không** thêm/xóa track; chỉ 3 track mặc định. Phase sau: Add/Delete track. |
| **F** | MVP icon: **Unicode/Text** (“T”, “V”, 🔊); sau có thể icon font. |
| **G** | **Có** màu segment theo loại (text vs visual) — converter trong DataTemplate. |
| **H** | MVP context menu track: **Lock**, **Visibility**; sau: Rename, Delete, Add track. |
| **I** | **Có** kéo segment sang track khác **cùng TrackType**; cập nhật TrackId + collision check. |
| **J** | Add segment luôn = **visual**; chỉ thêm vào track visual; nếu đang chọn text/audio → target Visual 1 hoặc disable Add + tooltip. |
| **K** | MVP track Audio **chỉ waveform** project, không segment. |

Checklist: A–K đã chốt; mục 6 và 7 đã cập nhật theo quyết định trên.
