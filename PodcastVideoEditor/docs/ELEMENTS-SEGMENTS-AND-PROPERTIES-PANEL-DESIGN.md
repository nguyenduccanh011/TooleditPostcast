# Thiết kế: Elements gắn Segment (thời gian) & Gộp panel Properties

**Ngày:** 2026-02-12  
**Liên quan:** Editor layout, Timeline full bottom, ST-3 preview composite.

---

## 1. Elements và Segment — Thuộc tính thời gian

### Câu hỏi
Elements (Title, Logo, Image, Visualizer, Text) có nên **kéo thả vào segment** trên timeline thay vì chỉ kéo vào preview không? Để element có **thuộc tính thời gian** của segment (ví dụ segment text 3–4s → element chỉ hiện trong 3–4s).

### Trả lời: **Đúng, thiết kế hợp lý**

- **Hiện tại:** Canvas Elements có X, Y, Width, Height, ZIndex — **không có** StartTime/EndTime hay SegmentId. Chúng hiển thị “luôn” trên preview (hoặc theo IsVisible).
- **Thiết kế mong muốn:**  
  - Kéo thả element **vào segment** (trên timeline) → element gắn với segment đó.  
  - **Thời gian hiển thị** = thời gian của segment (StartTime → EndTime).  
  - Ví dụ: segment text 3s–4s → element subtitle chỉ hiện trong khoảng 3–4s khi play/preview.

### Cần thay đổi (khi implement)

| Phần | Thay đổi |
|------|----------|
| **Model** | Element/CanvasElement (hoặc bảng trung gian) cần **SegmentId** (nullable) hoặc **StartTime, EndTime** riêng. Nếu gắn với segment thì dùng SegmentId và lấy Start/End từ Segment. |
| **Drag & drop** | Nguồn: toolbar/canvas element. **Đích:** không chỉ Canvas (preview) mà còn **segment trên Timeline** (khi thả lên segment → gán SegmentId cho element, set Start/End từ segment). |
| **Preview / Render** | Tại thời điểm t: chỉ vẽ elements có segmentId = null (luôn hiện) HOẶC segment.StartTime ≤ t &lt; segment.EndTime. |

**Kết luận:** Ý tưởng “element kéo thả xuống segment để có thuộc tính thời gian” **đúng hướng** và cần bổ sung model + drag-drop target + logic hiển thị theo thời gian.

---

## 2. Gộp Segment properties và Element properties vào một khung

### Câu hỏi
Segment properties và Element properties có thể nhập chung vào **một khung** để Timeline nằm **full bottom** (full width) được không? Có gây hỏng code không?

### Trả lời: **Thiết kế ổn, không gây hỏng code nếu làm đúng**

- **Hiện tại:**
  - **Segment properties:** `SegmentEditorPanel` (DataContext = TimelineViewModel) — binding `SelectedSegment`, Start/End, Text, Transition, Chọn ảnh, v.v.
  - **Element properties:** `PropertyEditorView` (DataContext = CanvasViewModel.PropertyEditor) — binding `SelectedElement`, các field động qua PropertyEditorViewModel.
  - Layout: Row 3 = Timeline (3*) | Splitter | Segment editor (2*). Timeline không full width vì cột phải dành cho Segment editor.

- **Thiết kế gộp:**
  - **Một panel “Properties”** (một khung duy nhất) hiển thị:
    - **Khi chọn Segment:** nội dung giống SegmentEditorPanel (Start/End, Text, ảnh nền, transition…).
    - **Khi chọn Element:** nội dung giống PropertyEditorView (Title/Logo/… fields).
    - **Khi không chọn gì (hoặc chọn cả hai):** ưu tiên rõ ràng (ví dụ segment ưu tiên hơn, hoặc tab “Segment | Element”).
  - **Timeline:** chiếm **full width** phía dưới (một row full). Panel Properties đặt bên **phải** (cột phải), giống layout hiện tại của PropertyEditorView — tức Preview (giữa) + Properties (phải), Timeline full bottom.

### Tại sao không hỏng code

| Thành phần | Giữ nguyên | Chỉ thay đổi |
|------------|------------|--------------|
| **TimelineViewModel** | SelectedSegment, commands, logic | Không đổi. |
| **CanvasViewModel / PropertyEditorViewModel** | SelectedElement, SetSelectedElement, fields | Không đổi. |
| **SegmentEditorPanel.xaml** | Nội dung UI (binding SelectedSegment) | Có thể đưa vào trong một UserControl mới dạng “UnifiedPropertiesPanel” (hoặc ContentControl + DataTemplate). |
| **PropertyEditorView.xaml** | Nội dung UI (binding PropertyEditor) | Tương tự, đưa vào cùng panel, hiển thị theo điều kiện. |
| **MainWindow layout** | — | Row 3: chỉ còn Timeline (Grid.ColumnSpan=2, full width). Cột phải Row 2: không chỉ PropertyEditorView mà **một view mới** chứa cả Segment + Element nội dung. |

**Cách làm an toàn:** Tạo **một UserControl mới** (ví dụ `UnifiedPropertiesPanel`) chứa:
- Logic: nếu `TimelineViewModel.SelectedSegment != null` → hiển thị nội dung Segment (copy/xem như SegmentEditorPanel).
- Ngược lại nếu `CanvasViewModel.SelectedElement != null` → hiển thị nội dung Element (PropertyEditorView).
- Binding vẫn trỏ tới TimelineViewModel và CanvasViewModel (hoặc MainViewModel). **Không xóa** SegmentEditorPanel và PropertyEditorView — có thể dùng chúng như nội dung bên trong (ContentControl + DataTemplate, hoặc đặt hai StackPanel/Grid với Visibility theo điều kiện). Như vậy **không phá** code hiện có, chỉ đổi bố cục và nơi đặt panel.

### Layout đề xuất (sau khi gộp)

```
Row 0: Audio
Row 1: Script (Expander)
Row 2: [ Preview (Canvas) ] [ Unified Properties (Segment hoặc Element) ]  ← cột phải một khung
Row 3: Timeline (full width)   ← full bottom
Row 4: Render
```

Timeline full bottom → dễ nhìn, chỉnh sửa timeline thoải mái hơn.

---

## 3. Tóm tắt

| Ý tưởng | Đánh giá | Ghi chú |
|---------|----------|--------|
| Elements kéo thả **vào segment** để có thuộc tính thời gian (ví dụ 3–4s) | **Đúng**, nên làm | Cần: SegmentId/Start-End trên element, drop target trên timeline, preview/render chỉ vẽ element trong khoảng thời gian segment. |
| Gộp Segment properties + Element properties vào **một khung** | **Ổn** | Một panel hiển thị theo SelectedSegment / SelectedElement. |
| Timeline **full bottom** | **Ổn** | Chỉ cần sửa layout: Row 3 chỉ còn Timeline, cột phải Row 2 là Unified Properties. |
| Có gây hỏng code không? | **Không**, nếu gộp chỉ bằng UI (một view mới dùng lại SegmentEditorPanel + PropertyEditorView hoặc nội dung tương đương). | ViewModels và binding giữ nguyên. |

---

*Tài liệu tham khảo khi implement drag-drop element → segment và refactor layout Editor (Phase 6 / issue #12).*
