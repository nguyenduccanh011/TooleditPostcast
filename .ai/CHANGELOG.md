# Changelog - Cải tiến quy trình

## Cấu trúc mới (2026-01-28)

### .ai/ (Quy trình chung - copy vào mỗi dự án)
- `00_RULES.md` - 10 quy tắc non-negotiables (R0-R10)
- `01_WORKFLOW.md` - Gate-based workflow với gates G1-G5
- `02_ROLES.md` - 7 roles: ANALYST, ARCHITECT, BUILDER, QA_LIGHT, REVIEWER, LIBRARIAN, RELEASE
- `03_DOC_STANDARDS.md` - Token-optimized document standards
- `04_CODE_STANDARDS.md` - Agnostic code standards
- `05_QA_LIGHT.md` - QA light process (không test hàng loạt)
- `06_LIBRARIAN.md` - Library/domain knowledge management
- `07_TEMPLATES/` - Templates cho các file docs
- `SESSION_ENTRY.md` - Protocol đọc file mỗi phiên
- `QUICK_START.md` - Hướng dẫn nhanh cho agent mới
- `README.md` - Index và quick reference

### docs/ (Tài liệu dự án - đổi tên từ số sang tên)
- `state.md` (HOT, <=60 lines) - Phase/objective/scope
- `active.md` (HOT, <=90 lines) - TP/ST hiện tại, resume instructions
- `code_rules.md` (WARM) - Project-specific code rules
- `decisions.md` (WARM) - ADR-lite decisions
- `issues.md` (WARM) - Tracked problems
- `worklog.md` (WARM) - Compressed log (1-3 lines/session)
- `arch.md` (COLD) - Architecture details
- `db.md` (COLD) - Database schema
- `api.md` (COLD) - API contracts
- `archive.md` (COLD) - Long discussions/details
- `knowledge/` - Library/domain knowledge

### scripts/
- `smoke.md` - Smoke tests template

## Cải tiến chính

### 1. Tối ưu token (Two-hot-doc principle)
- Chỉ đọc `docs/state.md` + `docs/active.md` mỗi phiên
- Giới hạn: state.md <=60 lines, active.md <=90 lines
- Các file khác chỉ đọc khi cần

### 2. Quy trình rõ ràng (Gate-based)
- 5 gates: G1 Scope/Goal, G2 Design Decision, G3 Build, G4 QA-light, G5 Review & Commit
- Session start/end checklist rõ ràng
- TP/ST lifecycle management

### 3. Role-based workflow
- 7 roles với quy trình cụ thể cho từng role
- Multi-skill agent (không tách agent riêng trừ khi cần)
- Role switching protocol rõ ràng

### 4. Issue management
- Báo cáo bắt buộc khi phát hiện vấn đề (R5)
- Issue types: scope|arch|dup|perf|deadcode|unclear|risk
- Không tự quyết định, phải báo cáo user

### 5. Library/domain lạ
- LIBRARIAN role với RFK protocol
- Ghi `docs/knowledge/<topic>.md` ngắn gọn, usable
- Không đoán API/behavior

### 6. QA Light
- Không test hàng loạt
- AI: terminal checks + smoke tests
- User: manual tests với script <=10 steps
- Mỗi lần test chỉ 1 vấn đề

### 7. Review & Commit
- REVIEWER role: soát deadcode/dup/structure drift/risk
- Chỉ commit khi user nói "GO COMMIT"
- Commit message rõ ràng

### 8. PAUSE/RESUME
- Resume instructions rõ ràng trong `docs/active.md`
- Đảm bảo context đủ để tiếp tục trơn tru

## Cách sử dụng

### Cho dự án mới
1. Copy `.ai/` vào root dự án
2. Copy templates từ `07_TEMPLATES/` sang `docs/` và điền nội dung
3. Đọc `QUICK_START.md` để bắt đầu

### Cho agent mới
1. Đọc `QUICK_START.md` trước
2. Đọc `00_RULES.md` - hiểu quy tắc
3. Đọc `01_WORKFLOW.md` - hiểu quy trình
4. Đọc `02_ROLES.md` - hiểu các role

### Mỗi phiên làm việc
- Bắt đầu: Đọc `docs/state.md` -> `docs/active.md`
- Kết thúc: Cập nhật `docs/active.md`, `docs/state.md`, `docs/worklog.md`
