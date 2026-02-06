# AI Operating System - Index

## Entry Points (đọc theo thứ tự)
1. `QUICK_START.md` - **Bắt đầu ở đây** - Hướng dẫn nhanh cho agent mới
2. `SESSION_ENTRY.md` - **Mỗi phiên làm việc** - File cần đọc khi bắt đầu phiên
3. `00_RULES.md` - Non-negotiables rules (R0-R10)
4. `01_WORKFLOW.md` - Gate-based workflow (G1-G5)
5. `02_ROLES.md` - Role definitions và quy trình (7 roles)

## Standards
6. `03_DOC_STANDARDS.md` - Document standards (token-optimized)
7. `04_CODE_STANDARDS.md` - Code standards (agnostic)
8. `05_QA_LIGHT.md` - QA light process
9. `06_LIBRARIAN.md` - Library/domain knowledge management

## Templates
10. `07_TEMPLATES/` - Templates cho các file docs

## Project Docs (root)
- `docs/state.md` và `docs/active.md` là main memory (HOT)
- Tất cả project docs khác nằm trong `docs/`

## Quick Reference
- **Session start**: Đọc `docs/state.md` -> `docs/active.md`
- **Role switching**: Đọc `02_ROLES.md` -> chọn role phù hợp
- **Issue detected**: Ghi `docs/issues.md` ngay
- **Library lạ**: Chuyển LIBRARIAN role, RFK user
- **Before commit**: REVIEWER role, chờ GO COMMIT

## Cải tiến chính
- ✅ Tối ưu token (Two-hot-doc principle)
- ✅ Quy trình rõ ràng (Gate-based workflow)
- ✅ Role-based với quy trình cụ thể
- ✅ Issue management bắt buộc
- ✅ Library/domain knowledge management
- ✅ QA Light (không test hàng loạt)
- ✅ Review & Commit có kiểm soát
- ✅ PAUSE/RESUME protocol

Xem `CHANGELOG.md` để biết chi tiết các thay đổi.

Last updated: 2026-01-28
