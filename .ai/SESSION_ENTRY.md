# SESSION ENTRY PROTOCOL (Bắt buộc mỗi phiên)

## File đọc bắt buộc (theo thứ tự)

### Bước 1: Đọc quy trình chung (chỉ lần đầu hoặc khi cần refresh)
Nếu agent mới hoặc cần refresh kiến thức:
- `.ai/00_RULES.md` - Hiểu 10 quy tắc non-negotiables
- `.ai/01_WORKFLOW.md` - Hiểu gate-based workflow
- `.ai/02_ROLES.md` - Hiểu các role và quy trình

### Bước 2: Đọc tài liệu dự án (BẮT BUỘC mỗi phiên)
**Luôn luôn đọc 2 file HOT này:**
1. `docs/state.md` (<=60 lines)
   - Hiểu: Phase, Objective, Scope, Active Task Pack
   - Xác định: dự án đang ở giai đoạn nào, mục tiêu là gì

2. `docs/active.md` (<=90 lines)
   - Hiểu: TP/ST hiện tại, Current Subtask, Resume Instructions
   - Xác định: cần làm gì tiếp theo, có nhiệm vụ dở dang không

### Bước 3: Đọc file bổ sung (chỉ khi cần)
**Chỉ đọc khi có liên quan đến nhiệm vụ:**

- `docs/code_rules.md` 
  - Khi nào đọc: Nếu role là BUILDER (code)
  - Mục đích: Hiểu code standards của dự án

- `docs/decisions.md`
  - Khi nào đọc: Nếu role là ARCHITECT hoặc cần quyết định design
  - Mục đích: Hiểu các quyết định đã có, tránh lặp lại

- `docs/issues.md`
  - Khi nào đọc: Nếu có issue liên quan đến nhiệm vụ hiện tại
  - Mục đích: Hiểu vấn đề đang track, tránh conflict

- `docs/worklog.md`
  - Khi nào đọc: Nếu cần hiểu context lịch sử gần đây
  - Mục đích: Xem đã làm gì trước đó

- `docs/arch.md`, `docs/api.md`, `docs/db.md` (COLD docs)
  - Khi nào đọc: Chỉ khi cần chi tiết kiến trúc/API/DB
  - Mục đích: Hiểu cấu trúc kỹ thuật chi tiết

- `docs/archive.md` + feature/design docs (COLD)
  - Khi nào đọc: Khi cần implementation details; hoặc khi `state.md`/`active.md` tham chiếu rõ file (vd Multi-track → `docs/MULTI-TRACK-TIMELINE-DESIGN.md`)
  - Mục đích: Danh sách tài liệu chi tiết trong `docs/archive.md`; đọc file được tham chiếu trong TP/ST

### Bước 4: Đọc quy trình role (khi chọn role)
Sau khi xác định role cần dùng:
- Đọc phần role tương ứng trong `.ai/02_ROLES.md`
- Hiểu: nhiệm vụ, quy trình, output của role đó

## Format bắt buộc mỗi phản hồi

Mỗi phản hồi phải mở đầu bằng:
```
ROLE: <ANALYST|ARCHITECT|BUILDER|QA_LIGHT|REVIEWER|LIBRARIAN|RELEASE>
READING:
- docs/state.md
- docs/active.md
- (optional) docs/code_rules.md / docs/decisions.md / docs/issues.md
OBJECTIVE:
- <mục tiêu của phiên này>
NEXT:
- <bước tiếp theo sau khi hoàn thành objective>
```

## Quy trình xác định role

1. Đọc `docs/state.md` -> xác định Phase
2. Đọc `docs/active.md` -> xác định Current Subtask
3. Chọn role theo priority:
   - Unclear requirement → ANALYST
   - Architecture/design decision → ARCHITECT
   - Implementation/bug fix → BUILDER
   - Verification/QA → QA_LIGHT
   - Review before commit → REVIEWER
   - Library/domain lạ → LIBRARIAN
   - Release/sprint end → RELEASE

## Checklist nhanh

### ✅ Session Start (bắt buộc)
- [ ] Đọc `docs/state.md`
- [ ] Đọc `docs/active.md`
- [ ] Xác định role phù hợp
- [ ] Đọc quy trình role trong `.ai/02_ROLES.md` (nếu cần)
- [ ] Đọc file bổ sung (nếu cần: code_rules, decisions, issues)
- [ ] Viết response header với ROLE/READING/OBJECTIVE/NEXT

### ✅ Session End (bắt buộc)
- [ ] Cập nhật `docs/active.md` (resume instructions nếu pause)
- [ ] Cập nhật `docs/state.md` (phase/objective nếu thay đổi)
- [ ] Ghi 1-3 dòng vào `docs/worklog.md`

## Lưu ý quan trọng

1. **Two-hot-doc principle**: Chỉ bắt buộc đọc 2 file HOT (`state.md` + `active.md`)
2. **Không đọc thừa**: Chỉ đọc file bổ sung khi thực sự cần
3. **Token optimization**: Nếu file dài, chỉ đọc phần liên quan
4. **Resume instructions**: Luôn kiểm tra `docs/active.md` -> Resume Instructions để tiếp tục nhiệm vụ dở dang

## Ví dụ

### Ví dụ 1: Agent mới, dự án đang BUILD
```
READING:
- .ai/00_RULES.md (lần đầu)
- .ai/01_WORKFLOW.md (lần đầu)
- .ai/02_ROLES.md (lần đầu)
- docs/state.md (bắt buộc)
- docs/active.md (bắt buộc)
- docs/code_rules.md (vì role=BUILDER)
```

### Ví dụ 2: Agent tiếp tục nhiệm vụ, role=BUILDER
```
READING:
- docs/state.md (bắt buộc)
- docs/active.md (bắt buộc - đọc Resume Instructions)
- docs/code_rules.md (vì role=BUILDER)
```

### Ví dụ 3: Agent mới, dự án đang IDLE
```
READING:
- docs/state.md (bắt buộc)
- docs/active.md (bắt buộc)
- .ai/02_ROLES.md (đọc phần ANALYST vì cần tạo TP mới)
```
