# Manual Test Guide - ST-12 Unified Editor

**Mục đích:** Kiểm thử thủ công màn hình Editor thống nhất (CapCut-like) sau ST-12.  
**Phạm vi:** Tab Editor duy nhất – Audio, Canvas, Properties, Timeline, Segment, Render.  
**Cập nhật:** 2026-02-08

---

## ST-12 là gì? Mục tiêu & Đã thực hiện

### Mục tiêu (Objective)
- **Hợp nhất UI** thành **một màn hình Editor duy nhất**: không cần chuyển qua lại giữa tab "Editor" và "Canvas Editor".
- Trên một tab **Editor** có đủ: **Canvas (preview) + Toolbar + Property panel + Timeline + Audio + Render** – flow giống CapCut (chọn audio → thêm element → xem preview → render).

### Đã thực hiện (Implementation)
1. **Tab "Editor"** thay layout cũ bằng Grid một màn hình:
   - **Hàng 0:** Thanh Audio (play/pause, timeline audio).
   - **Hàng 1:** Bên trái = Canvas (có Toolbar thêm Title/Logo/Image/Visualizer/Text, Delete, Duplicate, Z-order, Clear, Grid). Bên phải = Property panel (chỉnh thuộc tính element khi chọn).
   - **Hàng 2:** Timeline (segment blocks) + Segment Editor Panel (mô tả, transition, duration).
   - **Hàng 3:** Render (resolution, aspect ratio, quality, Start Render).
2. **Xóa tab "Canvas Editor"** – toàn bộ chức năng gộp vào tab Editor.
3. **Còn 3 tab:** Home | Editor | Settings. Menu **Edit → Settings** mở đúng tab Settings; **New/Open project** chuyển sang tab Editor.

---

## Chuẩn bị

- [ ] Build solution thành công (0 errors)
- [ ] Có file audio test (ví dụ `test.mp3`, ít nhất 30–60 giây)
- [ ] Chạy app → sẽ thấy 3 tab: **Home**, **Editor**, **Settings**

---

## 1. Test luồng mở project & vào Editor

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 1.1 | Mở app | Khởi động Podcast Video Editor | Tab **Home** đang chọn, có nút New Project, Open Selected, danh sách Recent Projects |
| 1.2 | Tạo project | Click **New Project** → nhập tên, chọn file audio → OK | Project tạo xong, app **tự chuyển sang tab Editor** |
| 1.3 | Kiểm tra Editor | Nhìn tab Editor | Một màn hình có: **Audio** (trên), **Canvas + Property panel** (giữa), **Timeline + Segment** (dưới), **Render** (dưới cùng). Không cần chuyển tab khác để xem canvas |
| 1.4 | Mở project có sẵn | Về Home → chọn project → **Open Selected** | Mở project, **tự chuyển sang tab Editor** |

**Pass nếu:** Một màn hình Editor đủ công cụ; không còn tab "Canvas Editor"; New/Open đều vào Editor.

---

## 2. Test Menu & Tab

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 2.1 | Menu Settings | **Edit → Settings** | Chuyển sang tab **Settings** (FFmpeg path, App Data path) |
| 2.2 | Về Editor | Click tab **Editor** | Trở lại màn hình Editor đầy đủ |
| 2.3 | Tab Home | Click tab **Home** | Danh sách project, New/Open/Delete/Refresh |

**Pass nếu:** Chỉ có 3 tab; Settings mở đúng khi chọn Edit → Settings.

---

## 3. Test Audio + Canvas (Toolbar + Elements)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 3.1 | Phát audio | Trên thanh Audio, click **Play** | Audio phát, thanh tiến trình chạy |
| 3.2 | Thêm Title | Trên Canvas toolbar click **Title** | Một khối Title xuất hiện trên canvas (có thể kéo thả) |
| 3.3 | Thêm Visualizer | Click **Visualizer** | Một khối Visualizer xuất hiện; khi **Play** audio → hình spectrum cập nhật theo nhạc |
| 3.4 | Thêm Image/Logo/Text | Lần lượt click **Image**, **Logo**, **Text** | Mỗi loại thêm một element lên canvas |
| 3.5 | Chọn element | Click vào một element trên canvas | Border highlight; **Property panel bên phải** hiện đúng thuộc tính (Text, FontSize, Color, v.v.) |
| 3.6 | Sửa trong Property panel | Đổi Text/FontSize/Color trong panel | Element trên canvas **cập nhật ngay** |
| 3.7 | Kéo thả | Kéo element sang vị trí khác | Vị trí thay đổi, không lỗi |
| 3.8 | Delete/Duplicate | Chọn element → **Delete**; hoặc **Duplicate** | Delete xóa element; Duplicate tạo bản sao |
| 3.9 | Front/Back | Chọn element → **Front** hoặc **Back** | Thứ tự lớp (z-order) thay đổi đúng |

**Pass nếu:** Audio phát; thêm/sửa/xóa/duplicate/z-order element hoạt động; Property panel sync với canvas.

---

## 4. Test Timeline + Segment

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 4.1 | Xem Timeline | Nhìn vùng Timeline (dưới Canvas) | Có ruler thời gian (0:00, 0:05...), track segment, playhead (line dọc) |
| 4.2 | Playhead sync | Play audio | Playhead di chuyển theo thời gian phát |
| 4.3 | Thêm segment | Click **Add** (trên Timeline) | Segment mới tại vị trí playhead, duration ~5s |
| 4.4 | Chọn segment | Click vào block segment | **Segment Editor Panel** (cạnh Timeline) hiện Description, Transition, Duration |
| 4.5 | Sửa segment | Đổi Description hoặc Duration trong panel | Timeline cập nhật (độ dài block thay đổi nếu đổi duration) |
| 4.6 | Drag/resize segment | Kéo cạnh trái/phải block (nếu UI hỗ trợ) | Start/End time thay đổi, không overlap segment khác |
| 4.7 | Delete/Duplicate segment | Nút Delete/Duplicate trong Segment panel | Segment bị xóa hoặc nhân bản đúng |

**Pass nếu:** Timeline hiển thị đúng; playhead sync với audio; thêm/sửa/xóa/duplicate segment hoạt động; panel segment đồng bộ.

---

## 5. Test Render (dùng đúng project)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|-----------|------------------|
| 5.1 | Cuộn xuống Render | Kéo xuống vùng Render (dưới Timeline) | Có Resolution, Aspect Ratio, Quality, Progress, Status, **Start Render**, **Cancel** |
| 5.2 | Chọn project | Đảm bảo đã mở 1 project (từ Home hoặc New) | Render dùng project hiện tại (binding đúng) |
| 5.3 | Start Render | Chọn resolution/quality → **Start Render** | Progress chạy, status cập nhật; khi xong có thông báo/file output (tùy implementation) |

**Pass nếu:** Render panel hiển thị và Start Render không lỗi (project context đúng).

---

## 6. Checklist tổng hợp – Editor thống nhất

- [ ] Chỉ có 3 tab: Home, Editor, Settings
- [ ] New/Open project → tự chuyển sang tab Editor
- [ ] Edit → Settings → mở tab Settings
- [ ] Trên tab Editor: thấy đủ Audio, Canvas (toolbar + canvas), Property panel, Timeline, Segment panel, Render
- [ ] Không cần chuyển tab để vừa xem canvas vừa chỉnh timeline/render
- [ ] Audio play → playhead timeline di chuyển
- [ ] Thêm Title/Visualizer/Image/Logo/Text → hiện trên canvas; chọn → Property panel cập nhật
- [ ] Thêm/sửa/xóa segment trên Timeline → Segment panel đồng bộ
- [ ] Render dùng đúng project hiện tại

---

**Kết luận:** Nếu tất cả mục trên pass thì ST-12 (Unified Editor) và flow CapCut-like đạt yêu cầu. Có thể kết hợp với `MANUAL-TEST-SEGMENT.md` cho chi tiết segment/drag-resize.
