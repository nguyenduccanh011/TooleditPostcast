# WORKFLOW (Gate-based)

## Required files (project)

### Hot Docs (bắt buộc mỗi phiên)
- `docs/state.md` (HOT, <=60 lines) - Phase, objective, scope
- `docs/active.md` (HOT, <=90 lines) - Current TP/ST, resume instructions

### Warm Docs (đọc khi cần)
- `docs/code_rules.md` (WARM) - Project-specific code rules
- `docs/decisions.md` (WARM) - ADR-lite decisions
- `docs/issues.md` (WARM) - Tracked problems
- `docs/worklog.md` (WARM) - Session log (1-3 lines per session)

### Cold Docs (chỉ đọc khi cần chi tiết)
- `docs/arch.md` (COLD) - Architecture overview + references to detailed docs
- `docs/api.md` (COLD) - API contracts
- `docs/db.md` (COLD) - Database schema
- `docs/archive.md` (COLD) - Index của tất cả tài liệu chi tiết

**Lưu ý:** Các tài liệu chi tiết (design, feature-specific docs) được liệt kê trong `docs/archive.md`. Chỉ đọc khi cần implementation details để tránh token waste. Để hiểu tổng quan, xem `docs/decisions.md` và `docs/arch.md`. **Khi `state.md` hoặc `active.md` tham chiếu rõ file (vd `docs/MULTI-TRACK-TIMELINE-DESIGN.md`) cho một phase/TP:** đọc file đó khi implement TP/ST tương ứng.

## Gates (checkpoints)
### G1: Scope/Goal Gate
- `state.md` + `active.md` phản ánh mục tiêu và phạm vi hiện tại
- Nếu không rõ: chuyển ANALYST role, làm rõ với user

### G2: Design Decision Gate
- Nếu có thay đổi hướng/kiến trúc => ghi `decisions.md` và user chốt
- Không được tự quyết định, phải đưa options + trade-off

### G3: Build Gate
- Code theo `active.md` subtask, không mở rộng scope
- Nếu cần mở rộng: dừng, báo cáo issue, hỏi user

### G4: QA-light Gate
- Chạy smoke/terminal checks (nếu có `scripts/smoke.md`)
- Manual test: viết 1-issue test script cho user (<=10 bước)
- Mỗi lần test chỉ 1 vấn đề

### G5: Review & Commit Gate
- REVIEWER role: soát deadcode/dup/structure drift/risk
- Không commit nếu chưa GO COMMIT từ user
- Sau review xong: commit (nếu có lệnh) + báo cáo user

## Session start checklist (bắt buộc)

**Xem chi tiết:** `.ai/SESSION_ENTRY.md`

**Tóm tắt:**
1. **Bắt buộc:** Đọc `docs/state.md` (hiểu phase/objective/scope)
2. **Bắt buộc:** Đọc `docs/active.md` (hiểu TP/ST hiện tại, resume instructions)
3. **Nếu code:** Đọc `docs/code_rules.md` (hoặc `.ai/04_CODE_STANDARDS.md`)
4. **Nếu liên quan quyết định:** Đọc `docs/decisions.md`
5. **Nếu có issue:** Đọc `docs/issues.md` (chỉ issues liên quan)
6. **Xác định role:** Đọc phần role tương ứng trong `.ai/02_ROLES.md`

**Tài liệu chi tiết (chỉ đọc khi cần implementation):**
- `docs/arch.md` - Architecture overview (có references đến chi tiết)
- `docs/design.md` - Chi tiết design modules (xem khi implement backend/frontend)
- `docs/user-flow.md` - Chi tiết user flows (xem khi implement UI)
- Feature / design docs — Xem `docs/archive.md` để biết danh sách; nếu TP/ST trong `active.md` ghi "đọc docs/XXX.md" thì đọc file đó khi implement

## Session end checklist (bắt buộc)
- Cập nhật `docs/active.md`:
  - Nếu hoàn thành ST: đánh dấu done, chuyển ST tiếp theo hoặc kết thúc TP
  - Nếu pause: ghi Resume Instructions rõ ràng
- Cập nhật `docs/state.md`:
  - Phase (IDLE/DESIGN/BUILD/REVIEW/FIX/PAUSE)
  - Objective nếu thay đổi
- Ghi 1-3 dòng vào `docs/worklog.md`:
  - Format: `[YYYY-MM-DD] ROLE: <action> - <result>`
- Nếu nội dung dài => đẩy vào `docs/archive.md`

## Task Pack (TP) lifecycle
### Tạo TP mới (khi Phase=IDLE)
1. Hỏi user: "Vấn đề/feature tiếp theo là gì?"
2. Tạo TP-XXX trong `docs/state.md` (Active Task Pack)
3. Tạo TP-XXX trong `docs/active.md` với:
   - Goal (1-2 dòng)
   - Subtasks (ST-1, ST-2, ST-3...)
   - Current Subtask: ST-1
4. Set Phase: DESIGN hoặc BUILD tùy

### Thực hiện ST
1. Đọc `docs/active.md` -> Current Subtask
2. Xác định role phù hợp (ANALYST/ARCHITECT/BUILDER/QA_LIGHT/REVIEWER)
3. Thực hiện theo quy trình role đó
4. Cập nhật `docs/active.md`:
   - Đánh dấu ST done
   - Chuyển Current Subtask sang ST tiếp theo
   - Cập nhật Context nếu cần

### Kết thúc TP
1. Tất cả ST done
2. REVIEWER role: review toàn bộ TP
3. Nếu có issue: fix hoặc báo cáo user
4. Commit (nếu có GO COMMIT)
5. Cập nhật `docs/state.md`: Phase=IDLE, Active Task Pack: (none)
6. Cập nhật `docs/active.md`: Pack: TP-000, Goal: No active task
7. Báo cáo user: TP hoàn thành

## Issue trigger workflow
1. Phát hiện vấn đề (scope/arch/dup/perf/deadcode/unclear/risk)
2. Ghi `docs/issues.md`:
   - Type: scope|arch|dup|perf|deadcode|unclear|risk
   - Impact: mô tả ngắn
   - Suggestion: options A/B/C
   - Needs user decision: yes/no
3. Báo cáo user ngay
4. Chờ quyết định
5. Thực hiện theo phương án user chốt

## Library/domain lạ workflow
1. Dừng code ngay
2. Chuyển ROLE: LIBRARIAN
3. RFK hỏi user (theo `.ai/06_LIBRARIAN.md`)
4. Ghi `docs/knowledge/<topic>.md` (theo template)
5. Quay lại code với knowledge mới

## Review & Commit workflow
1. REVIEWER role đọc code mới (chưa commit)
2. Kiểm tra:
   - Deadcode
   - Duplication
   - Structure drift (lệch kiến trúc)
   - Risk (security/performance)
   - Code quality (theo `docs/code_rules.md`)
3. Ghi findings vào `docs/issues.md` nếu có
4. Nếu có vấn đề: báo cáo user, chờ quyết định
5. Nếu OK: báo cáo user "Ready to commit"
6. Chờ user: "GO COMMIT"
7. Commit với message rõ ràng
8. Báo cáo: "Committed: <summary>"
