# Manual Test Guide - Segment (ST-9)

**Mục đích:** Hướng dẫn kiểm thử thủ công cho Timeline & Segment Manager.  
**Phạm vi:** ST-9 (Timeline Editor, SegmentEditorPanel, Segment CRUD).  
**Cập nhật:** 2026-02-08

---

## Chuẩn bị

- [ ] Build solution thành công (0 errors)
- [ ] Có file audio test (ví dụ: `test.mp3` ít nhất 60 giây)
- [ ] Đã tạo project và load audio vào app

---

## 1. Test Add Segment

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 1.1 | Mở Timeline | Vào tab/panel Timeline | Hiện ruler thời gian, track trống, playhead (line dọc) |
| 1.2 | Đặt playhead | Click lên timeline (hoặc play audio rồi pause) | Playhead di chuyển tới vị trí click |
| 1.3 | Thêm segment | Click nút **Add** | Segment mới xuất hiện tại vị trí playhead, duration ~5s |
| 1.4 | Kiểm tra UI | Xem segment block | Có màu, có text "New Segment", có border |
| 1.5 | Thêm nhiều segment | Add thêm 2 segment (đổi playhead rồi Add) | Mỗi segment không chồng lấn với nhau |

**Pass nếu:** Segment thêm đúng vị trí, không overlap.

---

## 2. Test Select Segment

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 2.1 | Chọn segment | Click vào 1 segment block | Segment được highlight (border đậm/màu khác) |
| 2.2 | Kiểm tra panel | Xem SegmentEditorPanel (bên phải/dưới) | Panel hiện "Segment Properties", có field Description, Transition, Duration |
| 2.3 | Chọn segment khác | Click segment khác | Panel chuyển sang segment mới chọn |

**Pass nếu:** Chọn segment → panel cập nhật đúng.

---

## 3. Test Edit Segment Properties

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 3.1 | Sửa Description | Gõ text vào TextBox Description (vd: "Intro music") | Text lưu, segment block hiển thị text mới |
| 3.2 | Đổi Transition | Chọn trong ComboBox: cut, fade, slide-left, slide-right, zoom-in | Giá trị chọn được lưu |
| 3.3 | Đổi Duration | Kéo Slider Transition Duration (0–2s) | Giá trị hiển thị cập nhật theo slider |

**Pass nếu:** Sửa trong panel → dữ liệu segment cập nhật, không crash.

---

## 4. Test Drag Segment (Move)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 4.1 | Bắt đầu kéo | Click và giữ trên segment, kéo sang trái/phải | Segment di chuyển theo chuột |
| 4.2 | Thả chuột | Thả chuột ở vị trí mới | StartTime và EndTime đổi, duration giữ nguyên |
| 4.3 | Chồng lấn | Kéo segment chồng lên segment khác | App chặn hoặc hiện thông báo "overlaps", không cho chồng |

**Pass nếu:** Kéo segment thay đổi vị trí, không overlap.

---

## 5. Test Resize Segment

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 5.1 | Tìm handle | Hover chuột lên cạnh phải segment | Handle resize xuất hiện (màu khác, cursor đổi) |
| 5.2 | Kéo mở rộng | Kéo handle sang phải | EndTime tăng, duration tăng |
| 5.3 | Kéo thu hẹp | Kéo handle sang trái | EndTime giảm, duration giảm |
| 5.4 | Giới hạn | Kéo duration < 0 hoặc vượt tổng thời gian audio | App chặn, giữ giá trị hợp lệ |

**Pass nếu:** Resize thay đổi duration đúng, không vượt giới hạn.

---

## 6. Test Duplicate Segment

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 6.1 | Chọn segment | Click chọn 1 segment | Segment được select |
| 6.2 | Duplicate | Click nút **Duplicate** (hoặc Duplicate Min) | Segment mới xuất hiện ngay sau segment gốc |
| 6.3 | Kiểm tra | So sánh segment gốc và copy | Copy có text + " (Copy)", cùng transition/duration |
| 6.4 | Duplicate khi gần hết | Duplicate segment cuối timeline | Nếu không đủ chỗ → thông báo overlap hoặc chặn |

**Pass nếu:** Duplicate tạo bản sao đúng, không overlap.

---

## 7. Test Delete Segment

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 7.1 | Chọn segment | Click chọn 1 segment | Segment được select |
| 7.2 | Delete | Click nút **Delete** | Segment biến mất khỏi timeline |
| 7.3 | Panel | Kiểm tra SegmentEditorPanel | Panel clear hoặc hiện "Select a segment..." |
| 7.4 | Delete khi không chọn | Click Delete khi không có segment nào chọn | Không crash, có thể hiện thông báo "No segment selected" |

**Pass nếu:** Delete xóa segment, panel cập nhật, không crash.

---

## 8. Test Playhead Sync

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 8.1 | Play audio | Nhấn Play | Playhead (line dọc) di chuyển theo thời gian |
| 8.2 | Pause | Nhấn Pause | Playhead dừng tại vị trí hiện tại |
| 8.3 | Seek (nếu có) | Click lên timeline trong lúc play/pause | Playhead nhảy tới vị trí click |
| 8.4 | Độ chính xác | Quan sát playhead vs thời gian audio | Sai lệch ±50ms là chấp nhận được |

**Pass nếu:** Playhead theo sát vị trí phát audio.

---

## 9. Test Scroll Timeline

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 9.1 | Timeline dài | Dùng audio 5+ phút, thêm nhiều segment | Timeline dài, cần scroll |
| 9.2 | Scroll ngang | Dùng scrollbar hoặc scroll wheel (Shift+wheel) | Timeline scroll mượt |
| 9.3 | Playhead visible | Play audio, scroll timeline | Playhead vẫn hiện đúng vị trí (có thể auto-scroll) |

**Pass nếu:** Scroll hoạt động, không lag.

---

## 10. Test Edge Cases

| # | Kịch bản | Hành động | Kết quả mong đợi |
|---|----------|-----------|------------------|
| 10.1 | Không có project | Mở app chưa load project, thử Add segment | Thông báo "No project loaded" hoặc nút disabled |
| 10.2 | Description rỗng | Xóa hết text Description | Không crash, có thể lưu rỗng |
| 10.3 | Nhiều segment | Thêm 10+ segment liên tiếp | Không lag nặng, scroll được |
| 10.4 | Chọn segment rồi đổi tab | Chọn segment, chuyển sang tab khác rồi quay lại | Selection có thể giữ hoặc clear tùy thiết kế |

---

## Checklist tổng hợp

Đánh dấu khi hoàn thành:

```
[ ] 1. Add Segment
[ ] 2. Select Segment  
[ ] 3. Edit Segment Properties
[ ] 4. Drag Segment (Move)
[ ] 5. Resize Segment
[ ] 6. Duplicate Segment
[ ] 7. Delete Segment
[ ] 8. Playhead Sync
[ ] 9. Scroll Timeline
[ ] 10. Edge Cases
```

---

## Báo cáo lỗi

Khi gặp lỗi, ghi:

- **Test case:** (vd: 4.1 - Drag Segment)
- **Bước thao tác:** (mô tả ngắn)
- **Kết quả thực tế:** (app làm gì)
- **Kết quả mong đợi:** (theo bảng trên)
- **Screenshot/Log:** (nếu có)
