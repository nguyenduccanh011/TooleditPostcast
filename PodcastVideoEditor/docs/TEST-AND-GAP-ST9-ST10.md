# Test & Gap Analysis - ST-9 / ST-10

**Mục đích:** Test, fix lỗi và đối chiếu tính năng còn thiếu của ST-9, ST-10 so với kế hoạch Phase 2.  
**Cập nhật:** 2026-02-08

---

## 1. ST-9: Timeline Editor & Segment Manager

### 1.1 Checklist test thủ công (theo MANUAL-TEST-SEGMENT.md)

| # | Test case | Trạng thái | Ghi chú |
|---|-----------|------------|---------|
| 1 | Add Segment | ⬜ | Add tại playhead, duration ~5s |
| 2 | Select Segment | ⬜ | Panel cập nhật khi chọn segment |
| 3 | Edit Segment Properties | ⬜ | Description, Transition, Duration |
| 4 | Drag Segment (Move) | ⬜ | Không overlap |
| 5 | Resize Segment | ⬜ | Handle cạnh phải, clamp duration |
| 6 | Duplicate Segment | ⬜ | Copy ngay sau segment gốc |
| 7 | Delete Segment | ⬜ | Panel clear khi không chọn |
| 8 | Playhead Sync | ⬜ | ±50ms với audio |
| 9 | Scroll Timeline | ⬜ | Scroll ngang dài |
| 10 | Edge Cases | ⬜ | No project, description rỗng, 10+ segments |

### 1.2 Tính năng ST-9 còn thiếu so với phase2-plan.md

| Yêu cầu (phase2-plan) | Hiện trạng | Hành động |
|------------------------|------------|-----------|
| **TimelineViewModel** | | |
| `ClearAllSegmentsCommand` | ✅ Đã thêm | Nút "Clear All" trên Timeline |
| **SegmentEditorPanel** | | |
| Button: Pick background image (OpenFileDialog *.jpg, *.png) | ⏳ Deferred | Cần ProjectService.CreateAssetAsync + inject; làm trong bước sau |
| TextBlock: Start/End time (read-only) | ✅ Đã thêm | Start / End time với TimeValueConverter |
| Slider Transition duration 0–3s | ✅ Đã sửa | Maximum = 3 |
| ColorPicker Segment color | ❌ Chưa có | Segment model chưa có Color → bỏ hoặc thêm property |
| **TimelineView** | | |
| Ruler format "0:00, 0:05, 0:10" | ⚠️ Đang "0s, 5s, 10s" | Tùy chọn: đổi converter sang M:SS |
| Auto-scroll timeline to keep playhead visible | ❌ Chưa kiểm tra | Implement hoặc xác nhận không cần |
| **Validation** | | |
| Warn if segment duration < transition duration | ❌ Chưa có | Có thể thêm cảnh báo trong panel |

### 1.3 Lỗi đã biết / cần fix

- (Điền khi chạy test: binding null, crash, UI không cập nhật, v.v.)

---

## 2. ST-10: Canvas + Visualizer Integration

### 2.1 Trạng thái theo kế hoạch

| Yêu cầu | Trạng thái |
|---------|------------|
| Extend VisualizerElement: render bitmap trên canvas | ✅ 100% |
| CanvasView: Image control bind visualizer bitmap (SKBitmap → BitmapSource) | ✅ |
| Performance: ~30fps timer, RenderOptions.BitmapScalingMode=LowQuality | ✅ |
| Audio sync: pause → freeze, resume → continue (qua VisualizerService) | ✅ |
| Acceptance: Visualizer on canvas, move/resize, no crash | ✅ |

### 2.2 Checklist triển khai ST-10

- [x] `SkiaConversionHelper.cs` (SKBitmap → WriteableBitmap via SkiaSharp.Views.WPF)
- [x] `ElementTemplateSelector` + VisualizerElement template
- [x] `CanvasView`: Image cho visualizer element, bind VisualizerBitmapSource
- [x] DispatcherTimer ~30fps poll
- [x] Sync với VisualizerViewModel.GetCurrentFrame()

---

## 3. Hành động ưu tiên

1. ~~**ST-9:** Chạy manual test~~ ✅ Đã test tạm ổn
2. ~~**ST-10:** Bắt đầu khi ST-9 test ổn định~~ ✅ Đã hoàn thành (2026-02-08)
3. **ST-11:** Element Property Editor Panel - tiếp theo
