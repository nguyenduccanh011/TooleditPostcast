# Hướng dẫn Manual Test – Các chức năng chính

**Mục đích:** Kiểm thử thủ công toàn bộ luồng chính của Podcast Video Editor (sau Phase 1–3 + TP-004 ST-1–ST-5).  
**Tham chiếu:** `state.md`, `active.md`, `MANUAL-TEST-ST12-UNIFIED-EDITOR.md`  
**Cập nhật:** 2026-02-12

---

## 1. Quy trình thực hiện (Workflow) – Kiểm tra nhanh

| Bước | Nội dung | Ghi chú |
|------|----------|--------|
| **G1** | Scope/Goal | `state.md` + `active.md` phản ánh mục tiêu và TP/ST hiện tại |
| **G2** | Design | Thay đổi kiến trúc → ghi `decisions.md`, user chốt |
| **G3** | Build | Code theo subtask trong `active.md`, không mở rộng scope |
| **G4** | QA-light | Smoke/manual test (script ngắn, 1 vấn đề/lần) |
| **G5** | Review & Commit | Review deadcode/dup/structure; commit khi user "GO COMMIT" |

**Session:** Mỗi phiên bắt đầu đọc `state.md` + `active.md`; kết thúc cập nhật active, state, 1–3 dòng `worklog.md`.

---

## 2. TP hiện tại (Current Task Pack)

| Mục | Nội dung |
|-----|----------|
| **TP** | TP-004-MULTI-TRACK-TIMELINE |
| **Mục tiêu** | Timeline nhiều track (Text / Visual / Audio), mỗi track một hàng, collision per-track. |
| **ST đã xong** | ST-1 (Models), ST-2 (Migration), ST-3 (ProjectService), ST-4 (TimelineViewModel), ST-5 (TimelineView layout) |
| **ST hiện tại** | **ST-6** — Track Header UI & Selection (icon, tên, lock/visibility, chọn track) |
| **ST tiếp theo** | ST-7 — Segment Editor Panel tương thích multi-track |

**Kiểm tra nhanh:** Mở `docs/active.md` → phần "Current Work Status" và "Next Action" phải khớp với công việc đang làm.

---

## 3. Chuẩn bị trước khi test

- [ ] Build solution thành công: `dotnet build` (0 errors)
- [ ] Có file audio test (ví dụ `test.mp3`, 30–60 giây)
- [ ] Chạy app: sẽ thấy 3 tab **Home**, **Editor**, **Settings**
- [ ] (Tùy chọn) Đã apply migration: `dotnet ef database update` (project cũ có 3 track mặc định)

---

## 4. Manual test – Các chức năng chính

### 4.1. Home & Project (Phase 1)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|------------|------------------|
| 1.1 | Mở app | Khởi động ứng dụng | Tab **Home** hiển thị; có New Project, Open Selected, Recent Projects |
| 1.2 | Tạo project | **New Project** → nhập tên, chọn file audio → OK | Project tạo xong, **tự chuyển sang tab Editor** |
| 1.3 | Mở project | Về Home → chọn project → **Open Selected** | Mở project, chuyển sang Editor; audio + timeline load đúng |

**Pass:** New/Open project hoạt động, chuyển tab đúng.

---

### 4.2. Editor – Audio (Phase 1)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|------------|------------------|
| 2.1 | Phát audio | Trên thanh Audio, click **Play** | Audio phát, thanh tiến trình chạy |
| 2.2 | Tạm dừng | Click **Pause** | Audio dừng, vị trí giữ nguyên |
| 2.3 | Seek | Kéo slider hoặc click trên ruler timeline | Vị trí phát nhảy đúng; playhead timeline trùng với audio |

**Pass:** Play/Pause/Seek hoạt động; playhead sync với audio.

---

### 4.3. Editor – Canvas & Elements (Phase 2)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|------------|------------------|
| 3.1 | Thêm Title | Toolbar Canvas → **Title** | Một khối Title xuất hiện trên canvas, có thể kéo thả |
| 3.2 | Thêm Visualizer | **Visualizer** → Play audio | Visualizer hiển thị; khi Play, spectrum cập nhật theo nhạc |
| 3.3 | Thêm Image/Logo/Text | Lần lượt **Image**, **Logo**, **Text** | Mỗi loại thêm một element lên canvas |
| 3.4 | Chọn element | Click vào element | Border highlight; **Property panel bên phải** hiện đúng thuộc tính |
| 3.5 | Sửa property | Đổi Text/FontSize/Color trong panel | Canvas cập nhật ngay |
| 3.6 | Delete/Duplicate | Chọn → **Delete** hoặc **Duplicate** | Delete xóa; Duplicate tạo bản sao |
| 3.7 | Z-order | **Front** / **Back** | Thứ tự lớp thay đổi đúng |

**Pass:** Thêm/sửa/xóa/duplicate/z-order element; Property panel đồng bộ với canvas.

---

### 4.4. Editor – Multi-track Timeline (TP-004, ST-1–ST-5)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|------------|------------------|
| 4.1 | Xem timeline | Nhìn vùng Timeline dưới Canvas | Có **ruler** (0:00, 0:05...), **nhiều hàng track** (Text 1, Visual 1, Audio), **waveform** ở dưới; mỗi track có cột tên (header) + vùng segment |
| 4.2 | Playhead sync | Play audio | **Playhead** (line dọc) di chuyển theo thời gian; đồng bộ với ruler và audio |
| 4.3 | Seek trên timeline | Click hoặc kéo trên ruler / vùng segment | Playhead nhảy đúng; audio seek theo |
| 4.4 | Thêm segment | Đảm bảo track **Visual 1** đang được chọn (mặc định) → click **Add** | Segment mới xuất hiện tại vị trí playhead trên track Visual 1, duration ~5s |
| 4.5 | Segment theo track | Mở project có sẵn segment (sau migration) | Segment cũ nằm trên track **Visual 1**; các track Text 1, Audio có thể trống |
| 4.6 | Chọn segment | Click vào một block segment | **Segment Editor Panel** (bên cạnh timeline) hiện Description, Transition, Duration của segment đó |
| 4.7 | Sửa segment | Trong panel: đổi Description hoặc Duration | Timeline cập nhật (độ dài block thay đổi nếu đổi duration) |
| 4.8 | Kéo/resize segment | Kéo cạnh trái/phải block (nếu UI hỗ trợ) | Start/End thay đổi; không overlap segment khác **cùng track** |
| 4.9 | Xóa / Nhân bản segment | **Delete** hoặc **Duplicate** trong panel | Segment bị xóa hoặc nhân bản trên **cùng track** |

**Pass:** Timeline hiển thị N track; playhead sync; Add segment vào Visual 1; chọn/sửa/xóa/duplicate segment đúng; collision chỉ trong cùng track.

---

### 4.5. Script – Áp dụng lên track Text (Phase 3 + TP-004)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|------------|------------------|
| 5.1 | Mở panel Script | Trên Timeline, mở expender **Script (dán định dạng [start → end] text)** | Có TextBox đa dòng + nút **Áp dụng script** |
| 5.2 | Dán script | Dán nội dung dạng `[0 → 5] Intro` và `[5 → 10] Phần 1` (mỗi dòng một segment) | Nút "Áp dụng script" enable khi có text |
| 5.3 | Áp dụng script | Click **Áp dụng script** | Segment **text** được tạo và gán vào track **Text 1**; segment cũ của track Text 1 bị thay thế; timeline refresh |
| 5.4 | Kiểm tra track | Nhìn track "Text 1" | Các block segment hiển thị đúng theo start/end đã paste |

**Pass:** Script paste + Áp dụng script tạo segment text trên track Text 1; không ảnh hưởng track Visual/Audio.

---

### 4.6. Render (Phase 1)

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|------------|------------------|
| 6.1 | Cuộn xuống Render | Kéo xuống vùng Render (dưới Timeline) | Có Resolution, Aspect Ratio, Quality, Progress, Status, **Start Render**, **Cancel** |
| 6.2 | Start Render | Chọn resolution/quality → **Start Render** | Progress chạy, status cập nhật; khi xong có thông báo/file output (tùy implementation) |

**Pass:** Render panel hiển thị; Start Render không lỗi (project context đúng).

---

### 4.7. Settings & Menu

| # | Bước | Hành động | Kết quả mong đợi |
|---|------|------------|------------------|
| 7.1 | Mở Settings | **Edit → Settings** | Chuyển sang tab **Settings** (FFmpeg path, App Data path) |
| 7.2 | Tab | Click **Home** / **Editor** | Chuyển tab đúng |

**Pass:** Chỉ 3 tab; Edit → Settings mở đúng tab.

---

## 5. Checklist tổng hợp – Chức năng chính

- [ ] **Workflow:** state.md + active.md phản ánh đúng TP-004, ST-6 hiện tại
- [ ] **Home:** New/Open project → chuyển sang Editor
- [ ] **Audio:** Play/Pause/Seek; playhead sync với audio
- [ ] **Canvas:** Title/Visualizer/Image/Logo/Text; Property panel; Delete/Duplicate/Front/Back
- [ ] **Timeline multi-track:** N track (Text 1, Visual 1, Audio); ruler + waveform; playhead sync
- [ ] **Segment:** Add (vào Visual 1); chọn segment → panel cập nhật; sửa/drag/resize/delete/duplicate
- [ ] **Script:** Dán `[start → end] text` → Áp dụng script → segment trên track Text 1
- [ ] **Render:** Start Render không lỗi
- [ ] **Settings:** Edit → Settings mở tab Settings

---

## 6. Test nhanh sau khi hoàn thành ST-6 (Track Header)

Khi ST-6 xong, bổ sung test:

- [ ] Track header hiển thị icon (T / V / 🔊) và tên track
- [ ] Click vào vùng header (hoặc vùng trống của track) → **SelectedTrack** = track đó (highlight)
- [ ] Click vào segment → **SelectedSegment** + **SelectedTrack** = track chứa segment
- [ ] Lock/Visibility (nếu đã implement): toggle hoạt động, segment/row ẩn hoặc khóa đúng

---

## 7. Tài liệu liên quan

| File | Nội dung |
|------|----------|
| `docs/state.md` | Phase, scope, TP hiện tại |
| `docs/active.md` | TP-004 subtasks, ST-6/ST-7, Resume Instructions |
| `docs/MANUAL-TEST-ST12-UNIFIED-EDITOR.md` | Chi tiết test Editor thống nhất (layout, từng panel) |
| `.ai/01_WORKFLOW.md` | Gate, session start/end, TP lifecycle |

**Kết luận:** Nếu tất cả mục trên pass thì các chức năng chính (Phase 1–3 + Multi-track ST-1–ST-5) đạt yêu cầu. Test ST-6 riêng khi triển khai xong Track Header.
