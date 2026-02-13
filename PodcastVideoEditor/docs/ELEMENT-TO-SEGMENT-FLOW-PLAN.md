# Kế hoạch triển khai: Element gắn Segment (luồng kiểu CapCut)

**Ngày:** 2026-02-12  
**Liên quan:** TP-005 ST-3 (preview composite), ELEMENTS-SEGMENTS-AND-PROPERTIES-PANEL-DESIGN.md, Phase 6 / issue #12.

---

## 1. Mục tiêu

- Element (Text, Title, Image, Logo, Visualizer) **gắn với Segment** trên Timeline; **thời gian xuất hiện** = khoảng [StartTime, EndTime] của segment.
- **Tạo element mới:** chủ yếu qua Timeline (chọn loại → click/drag trên track → tạo Segment + Element có SegmentId), không chỉ thêm trực tiếp lên Canvas.
- **Canvas:** chỉ hiển thị và chỉnh vị trí/kích thước/style; preview chỉ vẽ elements của các segment đang active tại playhead.
- **Một nguồn dữ liệu:** Project.Elements (Core) và Segment; UI (CanvasViewModel) sync load/save với Project.

---

## 2. Thay đổi Model & DB

### 2.1 Core – Element (Entity)

- **File:** `Core/Models/Element.cs`
- **Thêm:**
  - `SegmentId` (string?, nullable): FK tới Segment. Null = element “global”, luôn hiện (hoặc chưa gắn segment).
- **Quan hệ:** Element N–1 Segment (optional). Segment có thể có nhiều Element (1–N) qua collection nếu cần; hoặc chỉ truy vấn `Elements.Where(e => e.SegmentId == segment.Id)`.

### 2.2 Core – Segment

- Không bắt buộc thêm navigation `Elements` ngay; có thể truy vấn ngược từ Element.SegmentId.

### 2.3 EF Core Migration

- **Tên gợi ý:** `AddElementSegmentId`
- **Nội dung:** Thêm cột `SegmentId` (nullable) vào bảng Elements; FK tới Segments(Id); index trên SegmentId để query nhanh.

### 2.4 UI Model – CanvasElement

- **File:** `Core/Models/CanvasElement.cs` (hoặc nơi định nghĩa CanvasElement)
- **Thêm:** `SegmentId` (string?, nullable) để:
  - Khi load từ Project: gán từ Element.SegmentId.
  - Khi preview: lọc “active elements” = elements có SegmentId trong tập segment active tại playhead, cộng elements có SegmentId == null (global).

---

## 3. Luồng nghiệp vụ chi tiết

### 3.1 Luồng 1: Tạo element mới (panel → Timeline)

| Bước | Hành động | Thay đổi code / ghi chú |
|------|-----------|--------------------------|
| 1 | User chọn loại element (Text, Title, Image…) trên toolbar/panel | Giữ nút hiện tại; thêm trạng thái “PendingElementType” trong TimelineViewModel (hoặc Editor state) để biết đang chờ chọn vị trí trên Timeline. |
| 2 | User click (hoặc drag) trên một track trên Timeline | TimelineView: xử lý MouseDown/Drag trên vùng track (không phải segment). Xác định TrackId, StartTime = playhead hoặc vị trí click; EndTime = StartTime + defaultDuration (vd 5s) hoặc theo độ dài kéo. |
| 3 | Tạo Segment mới | Gọi ProjectService (hoặc TimelineViewModel) tạo Segment: TrackId, Kind (text/visual theo track), StartTime, EndTime, Text mặc định nếu text track. |
| 4 | Tạo Element gắn Segment | Tạo entity Element (Core): ProjectId, Type, X/Y/Width/Height/ZIndex mặc định, **SegmentId = segment.Id**. Add vào Project.Elements; persist (ProjectService.AddElementAsync hoặc tương đương). |
| 5 | Sync UI | Timeline: refresh Segments của track → segment mới hiện. Canvas: tạo CanvasElement từ Element (có SegmentId); thêm vào CanvasViewModel.Elements (hoặc vào “all elements” rồi preview chỉ vẽ khi active). Chọn SelectedSegment = segment mới, SelectedElement = element mới; panel Properties mở đúng form. |

**Chỗ cần sửa/thêm:**

- `TimelineViewModel`: PendingElementType, command/handler “AddElementToTrack(TrackId, startTime, endTime, elementType)”.
- `ProjectService`: AddSegmentAsync (đã có hoặc dùng AddSegment), AddElementAsync(projectId, element với SegmentId).
- `TimelineView.xaml/.cs`: Khi PendingElementType != null, click trên track row → gọi AddElementToTrack với tọa độ thời gian.
- Load project: map Project.Elements → CanvasElement (SegmentId, X, Y, …); đổ vào CanvasViewModel (hoặc nguồn duy nhất “all elements” để filter theo playhead).

### 3.2 Luồng 2: Chỉnh sửa – Canvas (vị trí/style) vs Timeline (thời gian)

| Ngữ cảnh | Hành động | Dữ liệu cập nhật |
|----------|-----------|-------------------|
| Canvas | Kéo/resize element, đổi font/màu trong Properties | Chỉ cập nhật Element (Core): X, Y, Width, Height, PropertiesJson. Không đổi StartTime/EndTime (do Segment nắm). |
| Timeline | Kéo đầu/cuối segment, di chuyển segment | Cập nhật Segment.StartTime, Segment.EndTime. Preview tự đổi “active” theo playhead. |

**Chỗ cần sửa:**

- Save project: từ CanvasViewModel.Elements (CanvasElement) ghi ngược vào Project.Elements (Element), giữ SegmentId. Đã có UpdateProjectAsync thì đảm bảo Elements được cập nhật (replace hoặc diff).
- Timeline resize/drag segment: đã có; chỉ cần đảm bảo preview đọc lại active segments theo playhead.

### 3.3 Luồng 3: Gắn element có sẵn vào segment

- User chọn element trên Canvas (hoặc danh sách) → chọn lệnh “Attach to segment” / “Gắn vào segment” → chọn segment trên Timeline (hoặc dropdown).
- Cập nhật Element.SegmentId = segment.Id; persist. CanvasElement.SegmentId sync tương ứng.
- Nếu element trước đó đã có SegmentId thì đổi sang segment mới (chuyển clip).

**Chỗ cần sửa:**

- Property panel hoặc context menu: nút/lệnh “Attach to segment”; dialog hoặc dropdown chọn Segment (từ Tracks/Segments).
- ProjectService: UpdateElementAsync(element) hoặc patch SegmentId.

### 3.4 Luồng 4: Preview composite theo playhead (TP-005 ST-3)

- **Input:** PlayheadPosition (t), Tracks, Segments, Elements (có SegmentId).
- **Bước:**
  1. Với mỗi track, lấy segment active: StartTime ≤ t < EndTime.
  2. Thu thập SegmentIds active (+ null cho global).
  3. Lấy danh sách Elements thỏa: SegmentId ∈ active SegmentIds hoặc SegmentId == null.
  4. Sắp xếp theo z-order (ZIndex, track order).
  5. Vẽ lần lượt: nền segment visual (ảnh) rồi overlay elements (text, title, image, …).

**Chỗ cần sửa:**

- TimelineViewModel (hoặc service) cung cấp API: `GetActiveSegmentsAtTime(double t)` → list segment (hoặc dict trackId → segment).
- CanvasViewModel (hoặc PreviewViewModel): subscribe PlayheadPosition; tại mỗi t gọi GetActiveSegmentsAtTime; lọc Elements có SegmentId trong tập đó hoặc null; set “layer vẽ” hoặc ObservableCollection cho View (Viewbox/Canvas) chỉ chứa elements active.
- CanvasView: binding nguồn từ “ActiveElements” thay vì toàn bộ Elements khi đang play/preview; hoặc vẫn binding Elements nhưng ItemsControl filter bằng converter theo playhead (phức tạp hơn) — ưu tiên nguồn “ActiveElements” từ ViewModel.

---

## 4. Thứ tự triển khai đề xuất

| Thứ tự | Nội dung | Phụ thuộc |
|--------|----------|-----------|
| 1 | **Model + Migration:** Element.SegmentId, CanvasElement.SegmentId, migration AddElementSegmentId | — |
| 2 | **ProjectService:** AddElementAsync, UpdateElementAsync (SegmentId); Load project include Elements; Save ghi Elements (từ Canvas hoặc từ nguồn thống nhất). | 1 |
| 3 | **Load/Save Canvas:** Khi Open project, map Project.Elements → CanvasElement (SegmentId, X, Y, …) đổ vào CanvasViewModel. Khi Save, ghi CanvasViewModel.Elements (có SegmentId) → Project.Elements. | 1, 2 |
| 4 | **Preview composite (ST-3):** GetActiveSegmentsAtTime(t); lọc elements theo SegmentId + thời gian; CanvasViewModel hiển thị “ActiveElements” theo playhead; subscribe PlayheadPosition. | 1, 2, 3 |
| 5 | **Timeline “Add element”:** PendingElementType; click track → tạo Segment + Element (SegmentId); refresh UI, chọn segment + element. | 1, 2, 3 |
| 6 | **Attach element to segment:** UI + UpdateElement(SegmentId). | 2, 3 |

**Mục tiêu thực hiện từng bước:** Bước 1–2 đã xong. Bước 3 = Load/Save Canvas (mở project thấy đúng elements từ DB; Save ghi Canvas → Project.Elements). Bước 4 = Preview composite (playhead đổi → preview đúng segment + elements active). Bước 5 = Add element từ Timeline. Bước 6 = Attach element vào segment. Chi tiết mục tiêu + acceptance criteria: `docs/active.md` mục **Nhiệm vụ tiếp theo**.

Có thể làm 4 (preview) trước 5 (add from timeline) để có trải nghiệm “đã gắn segment thì preview đúng”; sau đó bổ sung 5 và 6.

---

## 5. File cần sửa/đọc (tóm tắt)

| File | Thay đổi |
|------|----------|
| `Core/Models/Element.cs` | Thêm SegmentId (string?, nullable). |
| `Core/Models/CanvasElement.cs` | Thêm SegmentId (string?, nullable). |
| EF Migration | AddElementSegmentId: cột SegmentId, FK, index. |
| `ProjectService.cs` | AddElementAsync, UpdateElementAsync; Load Include Elements; Save Elements. |
| `TimelineViewModel.cs` | GetActiveSegmentsAtTime(double t); PendingElementType, AddElementToTrack. |
| `CanvasViewModel.cs` | Nguồn “ActiveElements” theo playhead (subscribe PlayheadPosition); load/save sync với Project.Elements (map Element ↔ CanvasElement). |
| `TimelineView.xaml/.cs` | Xử lý click/drag trên track khi PendingElementType != null; gọi AddElementToTrack. |
| `MainWindow` / DI | Truyền TimelineViewModel vào CanvasViewModel (hoặc shared state playhead) để Canvas lọc active elements. |

---

*Tài liệu tham chiếu khi implement TP-005 ST-3 và refactor element–segment (Phase 6).*
