# Quick Start Guide

## Cho Agent mới
1. Đọc `00_RULES.md` - hiểu các quy tắc non-negotiables
2. Đọc `01_WORKFLOW.md` - hiểu quy trình gate-based
3. Đọc `02_ROLES.md` - hiểu các role và khi nào dùng

## Mỗi phiên làm việc (bắt buộc)

### Session Start - Đọc file theo thứ tự
**Bắt buộc (luôn luôn):**
1. `docs/state.md` - Hiểu phase/objective/scope
2. `docs/active.md` - Hiểu TP/ST hiện tại, resume instructions

**Bổ sung (chỉ khi cần):**
3. `docs/code_rules.md` - Nếu role=BUILDER (code)
4. `docs/decisions.md` - Nếu role=ARCHITECT hoặc cần quyết định
5. `docs/issues.md` - Nếu có issue liên quan
6. `.ai/02_ROLES.md` - Đọc phần role tương ứng

**Tài liệu chi tiết (chỉ đọc khi cần implementation details):**
- `docs/arch.md` - Tổng quan kiến trúc (có references đến chi tiết)
- `docs/design.md` - Chi tiết design modules (xem khi implement)
- `docs/user-flow.md` - Chi tiết user flows (xem khi implement UI)
- Các feature docs (xem `docs/archive.md` để biết danh sách đầy đủ)

**Format bắt buộc:**
```
ROLE: <chọn role phù hợp>
READING:
- docs/state.md
- docs/active.md
- (optional) docs/code_rules.md / docs/decisions.md / docs/issues.md
OBJECTIVE:
- <mục tiêu của phiên này>
NEXT:
- <bước tiếp theo>
```

**Xem chi tiết:** `.ai/SESSION_ENTRY.md`

### Session End
- Cập nhật `docs/active.md` (resume instructions nếu pause)
- Cập nhật `docs/state.md` (phase/objective)
- Ghi 1-3 dòng vào `docs/worklog.md`

## Quy trình cơ bản

### 1. Tạo TP mới (khi Phase=IDLE)
- Hỏi user: "Vấn đề/feature tiếp theo?"
- Tạo TP-XXX trong `docs/state.md` và `docs/active.md`
- Chia thành ST-1, ST-2, ST-3...

### 2. Thực hiện ST
- Đọc `docs/active.md` -> Current Subtask
- Chọn role phù hợp (ANALYST/ARCHITECT/BUILDER/QA_LIGHT/REVIEWER)
- Thực hiện theo quy trình role đó
- Cập nhật `docs/active.md`

### 3. Kết thúc TP
- Tất cả ST done
- REVIEWER role: review toàn bộ
- Commit (nếu có GO COMMIT)
- Cập nhật `docs/state.md`: Phase=IDLE

## Khi gặp vấn đề

### Issue detected
- Ghi `docs/issues.md` ngay
- Báo cáo user
- Chờ quyết định

### Library lạ
- Dừng code ngay
- Chuyển ROLE: LIBRARIAN
- RFK hỏi user
- Ghi `docs/knowledge/<topic>.md`

### Cần quyết định
- Đưa options A/B/C với trade-off
- Khuyến nghị + lý do
- Chờ user chốt
- Ghi `docs/decisions.md`

## Cấu trúc tài liệu dự án

### Hot Docs (bắt buộc mỗi phiên)
- `docs/state.md` - Phase, objective, scope
- `docs/active.md` - Current TP/ST, resume instructions

### Warm Docs (đọc khi cần)
- `docs/code_rules.md` - Code standards
- `docs/decisions.md` - ADR-lite decisions
- `docs/issues.md` - Tracked issues
- `docs/worklog.md` - Session log

### Cold Docs (chỉ đọc khi cần chi tiết)
- `docs/arch.md` - Architecture overview + references
- `docs/api.md` - API contracts
- `docs/db.md` - Database schema
- `docs/archive.md` - Index của tất cả tài liệu chi tiết

**Lưu ý:** Các tài liệu chi tiết (design.md, user-flow.md, feature docs) được tổ chức trong `docs/archive.md`. Chỉ đọc khi cần implementation details. Để hiểu tổng quan, xem `docs/decisions.md` và `docs/arch.md`.

## Quan trọng
- User quyết định cuối cùng (R0)
- Không commit nếu chưa GO COMMIT (R7)
- Báo cáo issue ngay khi phát hiện (R5)
- Tuân thủ two-hot-doc principle (R3)
- Chỉ đọc tài liệu chi tiết khi cần implementation (token optimization)