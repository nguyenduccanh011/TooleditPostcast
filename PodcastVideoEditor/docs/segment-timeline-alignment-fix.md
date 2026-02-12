# Phân tích & xử lý: Segment không ngang hàng với Timeline

**Ngày:** 2026-02-12  
**Ngữ cảnh:** TP-004 Multi-track Timeline, manual test (MANUAL-TEST-TP004.md).  
**Vấn đề:** Segment trên track không khớp vị trí với ruler (thước thời gian) và playhead.

---

## 1. Phân tích nguyên nhân

### 1.1 Công thức vị trí (đã đúng)

- **Ruler:** `TimelineRulerControl` vẽ tick tại `pixelX = timeSeconds * PixelsPerSecond` (TimelineRulerControl.cs).
- **Segment:** `UpdateSegmentLayout()` đặt `Canvas.SetLeft(presenter, _viewModel.TimeToPixels(segment.StartTime))` với `TimeToPixels(t) = t * PixelsPerSecond` (TimelineViewModel, TimelineView.xaml.cs).
- **Playhead:** `Canvas.SetLeft(PlayheadLine, _viewModel.TimeToPixels(PlayheadPosition))`.

→ Cùng một nguồn `PixelsPerSecond` và cùng công thức `time * PixelsPerSecond`, nên **tọa độ theo thời gian là thống nhất**.

### 1.2 Nguyên nhân gốc: layout cột timeline

- **Trước sửa:** Cột 1 của Grid timeline dùng `Width="{Binding TimelineContentWidth}"` với `TimelineContentWidth = TimelineWidth + 56`.
- Hệ quả:
  - Ô cột 1 rộng **TimelineWidth + 56** (thừa 56px).
  - Ruler và Track Canvas đều có `Width=TimelineWidth`, nằm trong ô rộng hơn → tùy alignment có thể lệch nhau hoặc lệch với playhead nếu WPF phân bổ khoảng trống khác nhau giữa ruler row và track row (ví dụ stretch/center).
- **Track row:** ItemsControl span 2 cột; mỗi track row có cột 1 là `*` → nhận phần còn lại = (56 + (TimelineWidth+56)) - 56 = **TimelineWidth + 56**. Canvas segment có `Width=TimelineWidth` → ô và canvas không cùng độ rộng, dễ gây cảm giác “segment không ngang hàng” với vạch ruler/playhead.

### 1.3 Các yếu tố phụ (đã kiểm tra)

- Binding `Width` của Track Canvas: `RelativeSource AncestorType=UserControl` → lấy `TimelineWidth` từ ViewModel; khi DataContext chưa sẵn sàng có thể lúc đầu 0/NaN → đã thêm **FallbackValue=800** để tránh layout lệch lúc khởi tạo.
- `UpdateSegmentLayout` đã được gọi khi Loaded, khi đổi `PixelsPerSecond`, `TimelineWidth` và khi Tracks thay đổi → đủ cho cập nhật vị trí segment.

---

## 2. Kế hoạch xử lý đã thực hiện

### 2.1 Thống nhất độ rộng cột timeline với ruler/segment

- **Thay:** Cột 1 của Grid timeline: `Width="{Binding TimelineContentWidth}"` → **`Width="{Binding TimelineWidth}"`**.
- **Kết quả:**
  - Ruler và Playhead nằm trong ô cột 1 đúng bằng **TimelineWidth**.
  - ItemsControl và mỗi track row có tổng rộng = 56 + TimelineWidth → cột 1 của track row = **TimelineWidth**.
  - Segment Canvas nằm trong ô đúng bằng TimelineWidth → **ruler, playhead và segment dùng chung một trục thời gian và cùng độ rộng**, segment sẽ ngang hàng với timeline.

### 2.2 Căn chỉnh rõ ràng

- **Ruler:** Giữ `Width="{Binding TimelineWidth}"`, thêm **HorizontalAlignment="Left"** trên `TimelineRulerControl` để ruler luôn bắt đầu từ mép trái ô.
- **Track Canvas:** Giữ binding `Width` tới `TimelineWidth` (với FallbackValue=800), thêm **HorizontalAlignment="Stretch"** để canvas luôn fill đúng ô TimelineWidth.

### 2.3 Fallback cho binding

- Track Canvas: **FallbackValue=800** cho binding `DataContext.TimelineWidth` để khi DataContext chưa sẵn sàng (ví dụ lúc template mới áp dụng), canvas vẫn có độ rộng hợp lý, tránh segment bị vẽ lệch hoặc không hiển thị đúng ngay từ đầu.

---

## 3. Kiểm tra lại (sau khi sửa)

- Chạy app, mở project có segment (ví dụ Apply script + Add segment trên Visual 1).
- **Ruler:** Vạch 0:00, 0:05, 0:10, … trùng với vị trí tương ứng trên track.
- **Playhead:** Kéo playhead (ruler hoặc timeline) → đường playhead trùng với mép trái/phải segment tại đúng thời điểm.
- **Segment:** Kéo segment trái/phải → start/end của segment khớp với số giây trên ruler và với playhead khi seek.

Nếu vẫn lệch: kiểm tra thêm (1) có control nào khác trong ô cột 1 có margin/padding, (2) Zoom (Ctrl+Wheel) có làm `TimelineWidth`/`PixelsPerSecond` thay đổi đúng và `UpdateSegmentLayout` được gọi.

---

## 4. Tóm tắt thay đổi code

| File | Thay đổi |
|------|----------|
| `TimelineView.xaml` | Cột 1: `TimelineContentWidth` → `TimelineWidth`; Ruler: thêm `HorizontalAlignment="Left"`; Track Canvas: thêm `HorizontalAlignment="Stretch"` và `FallbackValue=800` cho binding Width. |

**TimelineContentWidth** trong ViewModel vẫn giữ (dùng cho logic khác nếu cần); chỉ không dùng cho độ rộng cột timeline nữa để đảm bảo ruler và segment cùng một hệ tọa độ.
