# Kế hoạch chi tiết: Multi-Track & Nhiều loại Segment

**Ngày:** 2026-02-11  
**Tham chiếu:** `script-and-timeline-plan.md` mục 2 (Hướng Multi-Track CapCut-like)  
**Mục tiêu:** Nhiều dòng segment trên timeline, nhiều loại segment (media, text, audio) với quy tắc rõ ràng.

---

## 1. Tổng quan

### 1.1 Hiện trạng
- **Một hàng segment** trên timeline; tất cả segment dùng chung collision (không chồng thời gian).
- **Segment** có: `StartTime`, `EndTime`, `Text`, `BackgroundAssetId`, `Kind` (string, đã có cột DB từ migration AddSegmentKind). Giá trị `Kind` mặc định `"visual"`.
- **Audio:** Một track waveform riêng (hàng dưới segment), không phải segment.

### 1.2 Mục tiêu sau khi hoàn thành TP
- **Nhiều loại segment:** Media (ảnh/video nền), Text (script/subtitle), (sau này: Audio clip, Sticker).
- **Nhiều hàng (track):** Mỗi track một hàng; segment khác track được phép chồng thời gian; cùng track không chồng (collision + snap như hiện tại).
- **UI:** Phân biệt trực quan theo loại (màu/icon); có nhãn track (tùy chọn).

---

## 2. Task Pack: TP-004-MULTITRACK-SEGMENTS

### 2.1 Phụ thuộc
- Phase 3 (TP-003) đã xong. Script paste tạo segment với `Text`; cần gán `Kind = "text"` cho segment từ script.

### 2.2 Subtasks (thứ tự thực hiện)

| ST | Tên | Mô tả ngắn | Trạng thái |
|----|-----|-------------|------------|
| **ST-MT1** | Chuẩn hóa Segment Kind & áp dụng script | Định nghĩa hằng Kind (visual, text); script apply gán Kind=text; segment thủ công/Add giữ Kind=visual. | ✅ Done |
| **ST-MT2** | Timeline hiển thị phân biệt theo Kind | Màu/viền hoặc icon khác cho segment visual vs text (vẫn 1 hàng). | ✅ Done |
| **ST-MT3** | Model TrackIndex + collision per track | Thêm TrackIndex (int) vào Segment, migration; collision chỉ trong cùng track. | 📋 Plan |
| **ST-MT4** | UI nhiều hàng timeline theo track | Nhiều Row/ItemsControl theo track; nhãn trái (Media, Text). | 📋 Plan |

---

## 3. Chi tiết từng ST

### 3.1 ST-MT1: Chuẩn hóa Segment Kind & áp dụng script

**Mục tiêu:** Dùng đúng trường `Segment.Kind` đã có; script tạo segment text, thao tác thủ công tạo segment visual.

**Giá trị Kind (chuẩn):**
- `SegmentKind.Visual` = `"visual"`: segment media (ảnh/video nền). **Nút "Add" thêm segment loại Visual** (track Visual).
- `SegmentKind.Text` = `"text"`: segment script/subtitle. Chỉ tạo khi **Áp dụng script** (track Text).
- **Segment audio:** Hiện chưa có. Track "Audio" trên timeline chỉ hiển thị waveform của **một file audio chính** của project, không phải segment rời. Thêm segment loại audio (BGM clip, voice clip) dự kiến mở rộng sau (ST hoặc v1.1).
- (Sau này) `"audio"`, `"sticker"` nếu mở rộng.

**Công việc:**
1. **Core:** Thêm static class hoặc constants `SegmentKind` với `Visual = "visual"`, `Text = "text"`. (Hoặc enum + ToString; DB lưu string.)
2. **Segment.cs:** Giữ `kind = "visual"` mặc định; có thể tham chiếu `SegmentKind.Visual`.
3. **TimelineViewModel.ApplyScript:** Khi tạo segment từ script, gán `Kind = SegmentKind.Text` (hoặc `"text"`).
4. **TimelineViewModel.AddSegmentAtPlayhead / Duplicate:** Giữ `Kind = SegmentKind.Visual` (hoặc không đổi so với hiện tại).
5. **Persist:** ReplaceSegmentsAndSaveAsync đã lưu toàn bộ segment; EF map cột `Kind` → không cần migration mới cho ST-MT1.

**Acceptance criteria:**
- [ ] Có `SegmentKind.Visual` và `SegmentKind.Text` (hoặc tương đương) dùng thống nhất trong code.
- [ ] Áp dụng script → segment mới có `Kind = "text"`.
- [ ] Add segment tại playhead → `Kind = "visual"`.
- [ ] Build 0 lỗi.

---

### 3.2 ST-MT2: Timeline hiển thị phân biệt theo Kind

**Mục tiêu:** User nhìn timeline thấy rõ segment nào là media, nào là text (script) qua màu/viền hoặc icon.

**Công việc:**
1. **TimelineView.xaml:** Phân biệt DataTemplate theo `Kind`:
   - Cách A: DataTrigger trên `Kind` trong một DataTemplate (BorderBrush/Background khác cho `text` vs `visual`).
   - Cách B: DataTemplateSelector với template "SegmentText" và "SegmentVisual".
2. **Gợi ý màu (giữ tông dark):**
   - **Visual (media):** Viền `#43a047` (xanh lá như hiện tại), nền `#2d3a4a`.
   - **Text (script):** Viền `#5c6bc0` (tím/xanh), nền `#37474f`; có thể thêm icon/chữ "T" nhỏ.
3. **Segment Properties:** Không bắt buộc đổi; có thể sau này cho phép đổi Kind từ dropdown (ST sau).

**Acceptance criteria:**
- [ ] Segment có `Kind == "text"` hiển thị khác segment `Kind == "visual"` (màu viền/nền hoặc icon).
- [ ] Chọn, kéo, resize vẫn hoạt động như cũ.
- [ ] Build 0 lỗi.

---

### 3.3 ST-MT3: Model TrackIndex + collision per track

**Mục tiêu:** Mỗi segment thuộc một track (số nguyên). Collision và snap chỉ áp dụng giữa các segment **cùng track**.

**Công việc:**
1. **Segment.cs:** Thêm property `TrackIndex` (int), mặc định `0`. Track 0 = Media, 1 = Text (theo quy ước).
2. **Migration:** Thêm cột `TrackIndex` (integer, default 0). Tên migration ví dụ: `AddSegmentTrackIndex`.
3. **TimelineViewModel:**
   - `CheckCollision(segment, newStart, newEnd)`: chỉ so sánh với segment **cùng TrackIndex**.
   - `TrySnapToBoundary`, `UpdateSegmentTiming`: giữ logic hiện tại nhưng chỉ xét segment cùng track.
   - Khi áp dụng script: gán `TrackIndex = 1` (track text) nếu muốn tách track; hoặc tạm giữ 0 để tương thích (có thể chọn: script → track 1, Add segment → track 0).
4. **Sắp xếp hiển thị:** Segments vẫn trong một collection; sort theo `TrackIndex` rồi `StartTime` khi cần (hoặc ST-MT4 nhóm theo track).

**Quy tắc (nhắc lại):**
- **Cùng track:** Không được chồng thời gian; snap, collision như hiện tại.
- **Khác track:** Được phép chồng (cùng [Start, End] hoặc overlap).

**Acceptance criteria:**
- [ ] Segment có `TrackIndex`; DB migration chạy thành công.
- [ ] Kéo segment chỉ bị chặn bởi segment **cùng TrackIndex**.
- [ ] Segment khác track có thể cùng khoảng thời gian mà không bị snap/chặn.
- [ ] Build 0 lỗi.

---

### 3.4 ST-MT4: UI nhiều hàng timeline theo track

**Mục tiêu:** Timeline có nhiều hàng (row), mỗi hàng một track; nhãn trái (Media, Text).

**Công việc:**
1. **ViewModel:** Có thể dùng `SegmentsByTrack` (Dictionary<int, ObservableCollection<Segment>>) hoặc vẫn một `Segments` nhưng ItemsControl group theo TrackIndex. Cách đơn giản: `ObservableCollection<Segment>[]` hoặc collection of collections theo track.
2. **TimelineView.xaml:** Thay một Canvas + một ItemsControl bằng:
   - Nhiều Row trong Grid (ruler chung; mỗi track một row); hoặc
   - Một ItemsControl với ItemTemplate = một row chứa label + ItemsControl segment của track đó.
3. **Nhãn track:** Cột trái (đã có 56px): mỗi row hiển thị "Media" / "Text" / "Audio" (nếu có).
4. **Playhead:** Vẫn một đường dọc qua tất cả hàng (RowSpan).
5. **Layout:** Chiều cao mỗi hàng segment có thể 48–60px; tổng scroll dọc nếu nhiều track.

**Acceptance criteria:**
- [ ] Có ít nhất 2 hàng segment (track 0, track 1) khi có segment thuộc 2 track.
- [ ] Nhãn track hiển thị rõ (Media / Text).
- [ ] Playhead cắt qua tất cả hàng.
- [ ] Build 0 lỗi.

---

## 4. Thứ tự triển khai đề xuất

1. **ST-MT1** → **ST-MT2**: Không đổi cấu trúc timeline, chỉ chuẩn Kind và hiển thị khác màu/icon. Ít rủi ro, có giá trị ngay.
2. **ST-MT3**: Thêm TrackIndex + collision per track. Cần migration và sửa logic ViewModel.
3. **ST-MT4**: Refactor UI nhiều hàng. Có thể tách nhỏ (ví dụ trước mắt chỉ 2 track cố định: Media, Text).

---

## 5. Tóm tắt quyết định

| Nội dung | Quyết định |
|----------|------------|
| Giá trị Kind | `"visual"` (media), `"text"` (script). Mở rộng sau: `"audio"`, `"sticker"`. |
| Script apply | Segment tạo từ script có `Kind = "text"`. |
| Add segment (thủ công) | `Kind = "visual"`. |
| TrackIndex | Số nguyên, mặc định 0. Track 0 = Media, 1 = Text (quy ước). |
| Collision | Chỉ trong cùng track. Khác track được chồng thời gian. |
| UI giai đoạn 1 (ST-MT2) | Một hàng; phân biệt visual vs text bằng màu/viền (và/hoặc icon). |
| UI giai đoạn 2 (ST-MT4) | Nhiều hàng, mỗi track một hàng, có nhãn. |

---

## 6. File liên quan (implementation)

- **Core:** `Models/Segment.cs`, `SegmentKind` (mới hoặc constants), Migrations.
- **UI:** `TimelineView.xaml`, `TimelineView.xaml.cs`, `TimelineViewModel.cs` (collision, TrackIndex khi có).
- **Script apply:** `TimelineViewModel.ApplyScript` (gán Kind, sau này TrackIndex).

---

Last updated: 2026-02-11
