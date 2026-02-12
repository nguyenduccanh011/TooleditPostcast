# Kiểm tra quy trình & Manual Test – TP-004 Multi-track Timeline

**Ngày:** 2026-02-12  
**Mục đích:** Kiểm tra quy trình thực hiện, trạng thái TP hiện tại, và hướng dẫn manual test các chức năng chính đã implement (ST-1 → ST-5).

---

## 1. Kiểm tra quy trình thực hiện (Workflow)

### 1.1 Tài liệu bắt buộc mỗi phiên (theo `.ai/01_WORKFLOW.md`)

| Loại | File | Nội dung |
|------|------|----------|
| **Hot** | `docs/state.md` | Phase hiện tại, objective, scope, Current Phase |
| **Hot** | `docs/active.md` | TP/ST đang làm, Resume Instructions |
| Warm | `docs/code_rules.md` | Quy tắc code |
| Warm | `docs/decisions.md` | ADR |
| Warm | `docs/issues.md` | Issues đang track |
| Warm | `docs/worklog.md` | Lịch sử session |

**Session start:** Đọc `state.md` → `active.md` → (nếu code) `code_rules.md`.  
**Session end:** Cập nhật `active.md`, `state.md`, ghi 1–3 dòng vào `worklog.md`.

### 1.2 Các Gate (checkpoint)

| Gate | Mô tả |
|------|--------|
| **G1** | Scope/Goal: state.md + active.md phản ánh mục tiêu đúng |
| **G2** | Design: Thay đổi kiến trúc → ghi decisions.md, user chốt |
| **G3** | Build: Code theo ST trong active.md, không mở rộng scope |
| **G4** | QA-light: Smoke/terminal (nếu có), manual test script (≤10 bước/issue) |
| **G5** | Review & Commit: Review deadcode/dup/structure; chỉ commit khi user "GO COMMIT" |

### 1.3 Vòng đời Task Pack (TP)

1. **Tạo TP:** Phase=IDLE → tạo TP-XXX trong state.md + active.md (Goal, Subtasks ST-1, ST-2, …).
2. **Thực hiện ST:** Đọc active.md → Current Subtask → thực hiện theo role (BUILDER/QA_LIGHT/…) → cập nhật active.md (ST done, chuyển ST tiếp).
3. **Kết thúc TP:** Tất cả ST done → REVIEWER review → fix nếu cần → Commit (nếu GO COMMIT) → state.md Phase=IDLE, active.md Pack: (none).

**Kết luận quy trình:** Đúng chuẩn gate-based; session start/end và TP lifecycle đã định nghĩa rõ trong `01_WORKFLOW.md`. Cần tuân thủ đọc state/active mỗi phiên và cập nhật khi kết thúc.

---

## 2. Kiểm tra TP hiện tại (TP-004 Multi-track Timeline)

### 2.1 Tổng quan

| Mục | Nội dung |
|-----|----------|
| **Task Pack** | TP-004-MULTI-TRACK-TIMELINE |
| **Mục tiêu** | Chuyển timeline từ flat segments → multi-track (text / visual / audio), collision theo track, Add segment vào track được chọn. |
| **Tham chiếu** | `docs/MULTI-TRACK-TIMELINE-DESIGN.md` (mục 1–11) |

### 2.2 Trạng thái Subtask

| ST | Nội dung | Trạng thái |
|----|----------|------------|
| ST-1 | Core Data Models (Track, Segment.TrackId, Project.Tracks) | ✅ COMPLETE |
| ST-2 | EF Migration (bảng Tracks, 3 track default, gán segment cũ → Visual 1) | ✅ COMPLETE |
| ST-3 | ProjectService CRUD Track, CreateProject 3 tracks, ReplaceSegmentsOfTrackAsync | ✅ COMPLETE |
| ST-4 | TimelineViewModel: Tracks, SelectedTrack, collision per-track, Add/ApplyScript/Clear/Delete/Duplicate | ✅ COMPLETE |
| ST-5 | TimelineView UI: N tracks (ItemsControl), header + segment canvas, TrackHeightConverter | ✅ COMPLETE |
| **ST-6** | **Track Header UI & Selection (icon, lock, visibility, click → SelectedTrack)** | ⏳ **CURRENT** |
| ST-7 | Segment Property Panel tương thích multi-track | ⏳ NOT STARTED |

### 2.3 Build hiện tại

- **Trạng thái:** Build succeeded (0 errors).
- **Ghi chú:** ST-1–ST-5 đã hoàn tất; app chạy được, timeline hiển thị đa track.

---

## 3. Hướng dẫn Manual Test – Các chức năng chính (ST-1 → ST-5)

**Chuẩn bị:**

- Mở solution trong Visual Studio (hoặc `dotnet build` tại thư mục solution).
- Đảm bảo FFmpeg đã cài (path trong Settings nếu cần).
- Chạy app: `PodcastVideoEditor.Ui` làm startup project.

---

### Test 1: Tạo project mới – 3 track mặc định (ST-2, ST-3)

**Mục đích:** Xác nhận project mới có đủ 3 track (Text 1, Visual 1, Audio).

**Các bước:**

1. Khởi động app.
2. Trên **Home**, bấm **New Project** (hoặc tương đương).
3. Nhập tên project, chọn (hoặc bỏ qua) đường dẫn audio, tạo project.
4. Mở project (hoặc tự chuyển sang tab Editor sau khi tạo).
5. Ở tab **Editor**, kéo xuống vùng **Timeline** (dưới status bar "Timeline:").
6. **Kiểm tra:** Có **3 hàng track** hiển thị:
   - Hàng 1: **Text 1** (cao 48px).
   - Hàng 2: **Visual 1** (cao 100px).
   - Hàng 3: **Audio** (cao 48px), bên dưới có waveform (nếu đã load audio).

**Kết quả mong đợi:** Đủ 3 track, tên và chiều cao đúng (text/audio 48px, visual 100px).  
**Pass:** ☐ Có 3 track đúng tên và thứ tự.

---

### Test 2: Load project có sẵn (sau migration) – segment thuộc Visual 1 (ST-2, ST-3, ST-4)

**Mục đích:** Project cũ (đã migrate) mở lên, segment nằm trên track Visual 1.

**Các bước:**

1. Nếu có project đã tạo trước khi có multi-track: mở project đó từ **Home** (Open / Recent).
2. Vào tab **Editor**, xem Timeline.
3. **Kiểm tra:** Mọi segment cũ nằm trên **một hàng** tương ứng **Visual 1** (hàng thứ 2 từ trên).

**Kết quả mong đợi:** Không lỗi load; segment hiển thị trên Visual 1.  
**Pass:** ☐ Load OK, segment đúng track.

---

### Test 3: Add segment tại playhead – track Visual 1 (ST-4, ST-5)

**Mục đích:** Nút "Add" thêm segment visual vào track đang chọn (mặc định Visual 1).

**Các bước:**

1. Mở một project (mới hoặc có sẵn), đảm bảo đã load audio (để có duration).
2. Ở Timeline, di chuyển playhead (click hoặc kéo trên ruler) tới vị trí ví dụ **0:05**.
3. Bấm nút **Add** trên thanh trạng thái Timeline.
4. **Kiểm tra:**
   - Xuất hiện **một segment mới** trên hàng **Visual 1**.
   - Segment có độ dài khoảng 5 giây, bắt đầu tại vị trí playhead.
   - Không xuất hiện segment trên hàng Text 1 hay Audio.

**Kết quả mong đợi:** Chỉ track Visual 1 có segment mới; thời gian đúng.  
**Pass:** ☐ Add segment đúng track và thời gian.

---

### Test 4: Collision trong cùng track (ST-4)

**Mục đích:** Hai segment trên cùng track Visual 1 không overlap.

**Các bước:**

1. Trên track **Visual 1**, đã có 1 segment (ví dụ 0:00–0:05).
2. Đưa playhead vào **trong** khoảng segment đó (ví dụ 0:02).
3. Bấm **Add**.
4. **Kiểm tra:** App không tạo segment overlap (có thể báo lỗi/status hoặc không thêm segment).
5. Đưa playhead ra **ngoài** segment (ví dụ 0:06), bấm **Add**.
6. **Kiểm tra:** Segment mới xuất hiện tại 0:06, không chồng lên segment 0:00–0:05.

**Kết quả mong đợi:** Cùng track không overlap; khác track (nếu có segment text) vẫn có thể cùng thời điểm.  
**Pass:** ☐ Collision cùng track hoạt động.

---

### Test 5: Áp dụng script – segment vào track Text 1 (ST-3, ST-4)

**Mục đích:** Script apply tạo segment trên track **Text 1**, không ghi đè track Visual 1.

**Các bước:**

1. Mở project, có track Text 1 & Visual 1.
2. (Tùy chọn) Thêm 1 segment visual trên Visual 1 (Add) để sau này so sánh.
3. Mở expander **"Script (dán định dạng [start → end] text)"**.
4. Dán script ví dụ:
   ```
   [0.00 → 3.00] Phần mở đầu
   [3.00 → 6.00] Nội dung giữa
   [6.00 → 10.00] Kết thúc
   ```
5. Bấm **Áp dụng script**.
6. **Kiểm tra:**
   - Trên hàng **Text 1** xuất hiện **3 segment** tương ứng 0–3s, 3–6s, 6–10s.
   - Track **Visual 1** không bị xóa (segment visual vẫn còn nếu đã thêm ở bước 2).

**Kết quả mong đợi:** Script chỉ thay segments của track Text 1; Visual 1 giữ nguyên.  
**Pass:** ☐ Apply script đúng track Text 1.

---

### Test 6: Clear All – chỉ track đang chọn (ST-4)

**Mục đích:** "Clear All" chỉ xóa segment của **track được chọn** (SelectedTrack).

**Các bước:**

1. Có segment trên **Text 1** (sau Apply script) và segment trên **Visual 1**.
2. Chọn track **Visual 1** (click vào segment trên Visual 1 hoặc vào vùng track Visual 1 – tùy ST-6 đã làm hay chưa).  
   *(Hiện tại có thể mặc định SelectedTrack = Visual 1 khi mở project.)*
3. Bấm **Clear All**.
4. **Kiểm tra:** Chỉ segment trên **Visual 1** bị xóa; segment trên **Text 1** vẫn còn.
5. (Nếu có cách chọn track) Chọn **Text 1**, bấm **Clear All**.
6. **Kiểm tra:** Segment trên Text 1 bị xóa; Visual 1 không thay đổi (đã trống từ bước 3).

**Kết quả mong đợi:** Clear All chỉ ảnh hưởng track được chọn.  
**Pass:** ☐ Clear All đúng theo track.

---

### Test 7: Delete / Duplicate segment (ST-4)

**Mục đích:** Delete xóa segment đang chọn; Duplicate nhân bản trong **cùng track**.

**Các bước:**

1. Chọn một segment trên Visual 1 (click vào segment).
2. Bấm **Delete** (hoặc phím Delete).
3. **Kiểm tra:** Segment đó biến mất khỏi timeline.
4. Thêm lại 1 segment, chọn nó, bấm **Duplicate**.
5. **Kiểm tra:** Xuất hiện segment thứ 2 **trên cùng track Visual 1**, không overlap (logic có thể tự điều chỉnh start/end hoặc báo collision tùy implementation).

**Kết quả mong đợi:** Delete/Duplicate hoạt động; duplicate nằm cùng track.  
**Pass:** ☐ Delete và Duplicate OK.

---

### Test 8: Layout multi-track – ruler, playhead, kéo segment (ST-5)

**Mục đích:** UI timeline đa track: ruler, playhead, kéo segment hoạt động đúng.

**Các bước:**

1. Mở project có ít nhất 2 track có segment (Text 1 + Visual 1).
2. **Ruler:** Click/kéo trên ruler → playhead nhảy đúng vị trí; time display cập nhật.
3. **Playhead:** Phát audio → playhead di chuyển theo; dừng tại cuối.
4. **Segment:** Kéo một segment (trên Visual 1 hoặc Text 1) sang trái/phải.
5. **Kiểm tra:** Segment di chuyển theo chuột; không vượt quá 0 hoặc duration; không chồng segment khác **cùng track**.
6. Resize segment (kéo cạnh trái/phải nếu có thumb) → duration/start-end cập nhật.

**Kết quả mong đợi:** Ruler seek, playhead sync, drag & resize segment ổn định.  
**Pass:** ☐ Ruler, playhead, drag/resize OK.

---

### Test 9: Lưu và mở lại project – persistence (ST-2, ST-3)

**Mục đích:** Sau khi thêm/sửa/xóa segment (và track), đóng project rồi mở lại, dữ liệu đúng.

**Các bước:**

1. Tạo project mới, Add 1 segment visual, Apply script (vài segment text).
2. (Tùy chọn) Sửa text hoặc timing một segment, đảm bảo có nút Save/auto-save.
3. Đóng project hoặc mở project khác rồi quay lại project vừa chỉnh.
4. Mở lại project đó.
5. **Kiểm tra:** Vẫn 3 track; số segment trên Text 1 và Visual 1 giống lúc lưu; thời gian segment đúng.

**Kết quả mong đợi:** Persistence đúng; không mất track/segment.  
**Pass:** ☐ Save/Load OK.

---

## 4. Bảng tóm tắt – Manual Test

| # | Test | Chức năng | Pass |
|---|------|-----------|------|
| 1 | 3 track mặc định | ST-2, ST-3 | ☐ |
| 2 | Load project cũ (migration) | ST-2, ST-3, ST-4 | ☐ |
| 3 | Add segment tại playhead (Visual 1) | ST-4, ST-5 | ☐ |
| 4 | Collision cùng track | ST-4 | ☐ |
| 5 | Áp dụng script → Text 1 | ST-3, ST-4 | ☐ |
| 6 | Clear All theo track | ST-4 | ☐ |
| 7 | Delete / Duplicate segment | ST-4 | ☐ |
| 8 | Ruler, playhead, drag/resize | ST-5 | ☐ |
| 9 | Save/Load persistence | ST-2, ST-3 | ☐ |

---

## 5. Ghi chú khi test

- **ST-6 chưa xong:** Track header chưa có icon/lock/visibility đầy đủ; selection track có thể mặc định (ví dụ Visual 1). Test 6 phụ thuộc cách chọn track hiện tại (click segment → SelectedTrack = track của segment).
- **Lỗi:** Ghi lại bước tái hiện + thông báo lỗi (và log trong `%APPDATA%\PodcastVideoEditor\Logs\` nếu cần) vào `docs/issues.md` hoặc báo developer.
- **QA-light (G4):** Mỗi lần test nên tập trung 1 nhóm chức năng (ví dụ 1 test hoặc 1 nhóm 2–3 test) để dễ xác định lỗi.

Sau khi manual test xong, cập nhật `active.md` / `worklog.md` nếu có kết quả hoặc issue cần track.
