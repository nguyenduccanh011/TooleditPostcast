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

#### ST-3: Script Import / Display
**Objective:** Import file script (txt) và gán nội dung vào segment text; hoặc hiển thị script theo segment (đã có field Text trên Segment).
**Status:** 🔲 TODO

**Acceptance Criteria:**
- [ ] Có cách import script (file .txt hoặc paste) vào project
- [ ] Nội dung script có thể gán vào segment (ví dụ: từng đoạn theo thời gian, hoặc split by paragraph/line)
- [ ] UI hiển thị/ chỉnh sửa script per segment (SegmentEditorPanel đã có Text — có thể mở rộng)
- [ ] Build succeeds (0 errors)

**Notes:** Không dùng AI segmentation (v1.1); v1.0 manual hoặc split đơn giản theo dòng/đoạn.

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
- [ ] ST-3: 0% (Script import/display) — **Current**

**Phase 2 (TP-002):** ✅ Đã đóng (ST-7–ST-12 done). Chi tiết lưu trong worklog/state.

---

## Next Action

**Current Subtask:** ST-3 — Script Import/Display.

**Resume Instructions:**
- Đọc `docs/active.md` → thực hiện ST-3 (BUILDER). Tạo UI import script .txt, gán vào segment Text field.
- ST-1 & ST-2 đã xong: Timeline có audio waveform, playhead sync chính xác, click/drag ruler để seek.
- Trước khi làm Phase 5/6: nhớ đưa #10, #11, #12 vào TP tương ứng (xem `docs/state.md` Phase Commitments).

---

Last updated: 2026-02-10
