# Active Task Pack - Phase 3

## Current Phase: Phase 3 - Script & Timeline

**Duration Target:** Week 7-8 (per state.md)  
**Task Pack:** TP-003-SCRIPT-TIMELINE

---

## Task Pack: TP-003-SCRIPT-TIMELINE

### Overview
Củng cố Timeline với track audio (waveform/track), đồng bộ playhead chính xác, và hỗ trợ script (import/ghi theo segment). Tham chiếu Phase Commitments: issue #13 (Audio track), #5 (Timeline sync), #12 optional Phase 6.

### Subtasks (ST)

#### ST-1: Audio Track in Timeline (ref. issue #13)
**Objective:** Tích hợp track audio vào timeline — hiển thị waveform hoặc biểu diễn track, đồng bộ với playhead (CapCut/Premiere style).
**Status:** ✅ **COMPLETED** (2026-02-08)

**Acceptance Criteria:**
- [x] Audio track hiển thị trong timeline (hàng riêng dưới segments, 48px)
- [x] Waveform bar representation (peak samples từ AudioService.GetPeakSamples, vẽ trên WaveformCanvas)
- [x] Playhead đồng bộ với vị trí phát audio (đã có từ ST-9)
- [x] Scroll timeline theo chiều ngang đồng bộ (cùng ScrollViewer)
- [x] Build succeeds (0 errors; đóng app trước khi build để tránh lock DLL)

**Notes:** Có thể dùng NAudio để lấy sample/peak data; vẽ bằng WPF hoặc SkiaSharp. Chi tiết implementation xem khi bắt tay (BUILDER role).

---

#### ST-2: Timeline Sync Precision (ref. issue #5)
**Objective:** Đảm bảo playhead/segment sync ±50ms; xử lý seek (nhảy vị trí) ổn định.
**Status:** ✅ **COMPLETED** (2026-02-10)

**Acceptance Criteria:**
- [x] Playhead position sync với AudioService.CurrentPosition trong ±50ms (30fps sync loop, Background priority)
- [x] Seek (click ruler hoặc kéo playhead) cập nhật audio position đúng (TimelineViewModel.SeekTo + AudioService.Seek)
- [x] Không giật/lag khi seek trong lúc phát (async/await pattern, smooth)
- [x] Enhanced: Click/drag trên ruler để seek (tương tác tương tự segment area)
- [x] Build succeeds (0 errors)

**Implementation:**
- TimelineViewModel: 30fps sync loop với accurate positioning
- AudioService: Accurate seek với ±20ms tolerance (sample-level precision)
- TimelineView: Click/drag support trên ruler Border (MouseDown/Move/Up events)
- Smooth UX: Background dispatcher priority, no blocking

**Notes:** Không cần auto-highlight segment (user decision). Manual testing đã verify hoạt động tốt.

---

#### ST-3: Script Import / Display (Paste-only, định dạng có cấu trúc)
**Objective:** Ô nhập để **dán (paste)** script vào project; parse định dạng `[start → end] text` và tạo/cập nhật segment. Không dùng file .txt.
**Status:** ✅ **COMPLETED** (2026-02-11)

**Định dạng script (bắt buộc):**
```
[start_sec → end_sec]  Nội dung text
```
Ví dụ: `[0.00 → 6.04]  Chào mừng đến với podcast...` — mỗi dòng = một segment (Start, End, Text).

**Acceptance Criteria (tổng):**
- [x] Có ô nhập (paste) script và nút Áp dụng
- [x] Parser: chuỗi → danh sách (Start, End, Text) theo regex `[X.XX → Y.YY] content`
- [x] Áp dụng script: thay thế toàn bộ segment bằng danh sách từ script (persist qua ReplaceSegmentsAndSaveAsync)
- [x] SegmentEditorPanel binding Text đã có — hoạt động sau khi áp dụng
- [x] Build succeeds (0 errors)

**Chia nhỏ (đã thực hiện):**
- **ST-3a** — UI: Expander "Script (dán định dạng [start → end] text)" + TextBox + nút "Áp dụng script" ✅
- **ST-3b** — ScriptParser.Parse() trong Core/Utilities/ScriptParser.cs ✅
- **ST-3c** — ApplyScriptCommand → ReplaceSegmentsAsync (ProjectService) + refresh Segments ✅
- **ST-3d** — SegmentEditorPanel đã bind SelectedSegment.Text ✅
- **ST-3e** — Build succeeded ✅

**Notes:** Không dùng AI segmentation (v1.1). Multi-track (media/text/sticker kiểu CapCut) để sau Phase 3 — thiết kế chi tiết: `docs/MULTI-TRACK-TIMELINE-DESIGN.md`.

---

### Dependencies Between Subtasks

```
ST-1 (Audio track) — có thể làm trước hoặc song song với ST-2
ST-2 (Sync precision) — cải thiện hiện có, không block ST-1
ST-3 (Script) — độc lập, có thể làm sau ST-1/ST-2
```

---

## Current Work Status

### Phase 3 Progress (TP-003)
- [x] ST-1: 100% (Audio track in timeline) ✅
- [x] ST-2: 100% (Timeline sync precision) ✅
- [x] ST-3: 100% (Script import/display — paste-only) ✅

**Phase 2 (TP-002):** ✅ Đã đóng (ST-7–ST-12 done). Chi tiết lưu trong worklog/state.

---

## Next Action

**Current Subtask:** TP-003 ST-3 đã xong. Phase 3 (Script & Timeline) hoàn tất.

**Resume Instructions:**
- ST-3 (Script paste + áp dụng) đã implement: ScriptParser, UI Expander + TextBox + nút "Áp dụng script", ReplaceSegmentsAsync persist. Test thủ công: mở project → mở Expander Script → dán script mẫu → Áp dụng script → kiểm tra timeline + Segment Properties.
- Tiếp theo: Phase 4 (AI & Automation) hoặc Phase 5/6; nhớ đưa #10, #11, #12 vào TP (xem `docs/state.md` Phase Commitments).
- **Nếu chọn phase Multi-track timeline:** Tạo TP trong `active.md`; **đọc `docs/MULTI-TRACK-TIMELINE-DESIGN.md`** trước khi viết ST và ghi rõ trong TP (vd "Prerequisite: đọc MULTI-TRACK-TIMELINE-DESIGN.md"). Danh sách tài liệu chi tiết: `docs/archive.md`.

---

Last updated: 2026-02-10
