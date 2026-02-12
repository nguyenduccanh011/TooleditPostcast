# Phân tích khung thành phần giao diện: CapCut vs Editor (PVE)

Mục đích: Tham khảo bố cục CapCut để cải thiện trang Editor — đặc biệt **khung preview không cuộn**, và tối ưu diện tích cho preview.

---

## 1. CapCut — Các khung thành phần

| Vùng | Mô tả | Cuộn | Ghi chú |
|------|--------|------|--------|
| **Top Bar** | Logo, Menu, Auto saved, tab Media/Audio/Text/…, Share, Export, cửa sổ | Không | Cố định trên cùng. |
| **Left Panel** | Import, Record, tab Media/Subprojects/…, **danh sách media (thumbnails)** | Có (dọc) | Chỉ danh sách media cuộn; khung panel cố định. |
| **Central Top — Preview** | **Player – Timeline 01**, khung xem video | **Không** | Nội dung **scale/fit** trong khung, không scroll ngang/dọc. Có nút Full, Ratio, maximize. |
| **Right Panel** | Details (Name, Path, Color space, Aspect ratio, Resolution, Frame rate…) | Có (dọc) | Nội dung chi tiết có thể dài. |
| **Bottom — Timeline** | Ruler, playhead, nhiều track, clip | Có (ngang) | Chỉ timeline cuộn ngang theo thời gian; chiều dọc cố định theo track. |

**Điểm quan trọng:** Preview là **viewport cố định**, scale nội dung cho vừa (letterbox/pillarbox), **không có thanh cuộn** trên chính khung preview.

---

## 2. PVE Editor hiện tại — Các khung thành phần

| Vùng | XAML / Vị trí | Cuộn | Vấn đề |
|------|----------------|------|--------|
| **Menu** | `Menu` Dock Top | Không | OK. |
| **Tab Editor** | Toàn bộ nội dung trong **ScrollViewer** | **Cả trang Editor cuộn dọc** | Toàn bộ layout bị “đẩy” khi cuộn → preview và timeline cùng bị ép. |
| **Row 0** | Audio player (full width) | Không | Chiếm chiều cao cố định. |
| **Row 1** | Script paste (Expander, mặc định thu gọn) | Không | Khi mở rộng tốn thêm chiều cao. |
| **Row 2** | **Canvas (preview)** + Property panel (phải) | Trước đây: ScrollViewer trong Canvas → **đã đổi thành Viewbox**, không cuộn | **Height="320"** → diện tích preview rất nhỏ. |
| **Row 3** | Timeline + Segment editor | Khung Timeline có scroll riêng (theo thiết kế) | Timeline + Segment cùng một hàng. |
| **Row 4** | Render | Không | Thêm chiều cao. |

**Nguyên nhân preview nhỏ:**
- **Row 2** có `Height="320"` cố định → khung preview bị giới hạn 320px.
- Cả Editor nằm trong **một ScrollViewer** → layout dọc bị chia cho nhiều hàng (Audio, Script, Canvas, Timeline, Render) nên mỗi vùng đều bị thu nhỏ hoặc phải cuộn trang.
- CapCut tách rõ: **trái (media) | giữa (preview) | phải (details)**, preview chiếm **phần lớn chiều cao** vùng giữa; còn PVE đang xếp **dọc** (Audio → Script → Canvas → Timeline → Render) nên preview chỉ còn một “dải” 320px.

---

## 3. So sánh nhanh

| Tiêu chí | CapCut | PVE Editor hiện tại |
|----------|--------|----------------------|
| Preview cuộn | Không (scale to fit) | Đã bỏ cuộn (dùng Viewbox), chỉ còn vấn đề diện tích. |
| Vị trí preview | Giữa màn hình, vùng rộng | Row giữa, Height 320px → nhỏ. |
| Bố cục | Trái – Giữa – Phải + Timeline dưới | Trên – dưới: Audio, Script, Canvas, Timeline, Render. |
| Scroll toàn trang | Không (các panel cố định) | Có (ScrollViewer bọc cả Editor). |

---

## 4. Gợi ý cải thiện (để preview rộng hơn, bớt lộn xộn)

1. **Bỏ ScrollViewer bọc toàn bộ Tab Editor** (hoặc chỉ cuộn một vùng cụ thể nếu cần), dùng **DockPanel hoặc Grid cố định** để Audio, Preview, Timeline, Render có tỉ lệ chiều cao rõ ràng.
2. **Tăng chiều cao vùng preview (Row 2):** ví dụ `Height="*"` (chiếm phần còn lại) hoặc `MinHeight="420"` thay vì cố định 320.
3. **Thu gọn / ẩn bớt khi cần:** Script (Expander), Render (có thể thu gọn hoặc đưa sang tab/dialog) để dành chỗ cho Preview + Timeline.
4. **Xem xét layout 3 cột kiểu CapCut (sau này):** Trái: media/tools | Giữa: preview (lớn) + timeline | Phải: properties/details — khi refactor UI lớn.

---

## 5. Thay đổi đã làm (liên quan preview)

- **CanvasView.xaml:** Đã thay **ScrollViewer** quanh Canvas bằng **Viewbox Stretch="Uniform"** → khung preview **không còn thanh cuộn**, nội dung scale để luôn vừa khung (giống CapCut).

---

*Tài liệu tham khảo khi chỉnh layout Editor (issue #12, Phase 6). Cập nhật: 2026-02-12.*
