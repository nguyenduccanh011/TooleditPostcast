# DOC STANDARDS (Token-optimized)

## Hot docs budgets (bắt buộc)
- `docs/state.md`: <= 60 lines
- `docs/active.md`: <= 90 lines

Nếu vượt -> nén, đẩy chi tiết sang archive/arch/api/db.

## Warm docs (đọc khi cần)
- `docs/code_rules.md`: project-specific code rules
- `docs/decisions.md`: ADR-lite decisions
- `docs/issues.md`: tracked problems
- `docs/worklog.md`: compressed log (1-3 lines per session)

## Cold docs (chỉ đọc khi cần thiết)
- `docs/arch.md`: architecture overview + references to detailed docs
- `docs/db.md`: database schema
- `docs/api.md`: API contracts
- `docs/archive.md`: index của tất cả tài liệu chi tiết (design.md, user-flow.md, feature docs)

## Quy tắc viết (token-optimized)
- Ưu tiên bullet points, câu ngắn
- Mọi file dài phải có TL;DR 5 dòng ở đầu (nếu cần)
- Không copy-paste chat vào docs
- Chỉ ghi cái có ích để resume và quyết định
- Loại bỏ thông tin trùng lặp

## Nén context
- Quyết định: chỉ lưu ở `decisions.md` (ADR-lite format)
- Nhật ký: 1–3 dòng/phiên ở `worklog.md`
- Chi tiết dài: `archive.md` (index + references)
- Kiến trúc tổng quan: `arch.md` (có references đến chi tiết)
- Design chi tiết: `docs/design.md` (xem khi implement)
- User flows chi tiết: `docs/user-flow.md` (xem khi implement UI)
- Feature specs: các file feature-*.md (xem khi implement feature cụ thể)
- API chi tiết: `api.md`
- DB chi tiết: `db.md`

**Lưu ý:** Các tài liệu chi tiết được tổ chức trong `docs/archive.md`. Chỉ đọc khi cần implementation details để tránh token waste.

## Format chuẩn

### state.md format
```markdown
# STATE
Phase: IDLE | DESIGN | BUILD | REVIEW | FIX | PAUSE

Objective (1-2 lines):
- ...

LOCKED (do not change without user):
- Scope: ...
- Direction/Architecture: ...
- Dependencies policy: ...

OPEN (discussable):
- ...

Active Task Pack:
- TP-XXX: <mô tả ngắn>

DO / DON'T:
- DO: ...
- DON'T: ...

Next Read:
- docs/active.md

Refs (only if needed):
- decisions: docs/decisions.md
- issues: docs/issues.md
- arch: docs/arch.md
```

### active.md format
```markdown
# ACTIVE
Pack: TP-XXX
Role: <current role>

Goal:
- ...

Subtasks:
- ST-1: ...
- ST-2: ...
- ST-3: ...

Current Subtask:
- ST-X: <status> | DONE | IN_PROGRESS | BLOCKED

Context (max 8 lines):
- ...

Definition of Done (max 6 lines):
- ...

Resume Instructions (max 5 lines):
- ...

Blockers:
- none | <mô tả>

If PAUSE:
- Where we left off: ...
- Next action: ...
```

### worklog.md format
```markdown
# WORKLOG (1-3 lines per session)

[YYYY-MM-DD] ROLE: <action> - <result>
[YYYY-MM-DD] ROLE: <action> - <result>
```

### decisions.md format (ADR-lite)
```markdown
# DECISIONS (ADR-lite list)

## D-XXX: <title>
Why:
- ...

Tradeoff:
- ...

Locked: yes/no
Date: YYYY-MM-DD
```

### issues.md format
```markdown
# ISSUES

## Trigger types
scope | arch | dup | perf | deadcode | unclear | risk

## I-XXX [OPEN|IN_PROGRESS|DONE] <title>
Type: <trigger type>
Impact: <mô tả ngắn>
Suggestion: <options A/B/C>
Needs user decision: yes/no
Status: OPEN|IN_PROGRESS|DONE
```

## Session end cleanup
- Cập nhật `active.md` với resume instructions rõ ràng
- Cập nhật `state.md` với phase/objective mới
- Ghi 1-3 dòng vào `worklog.md`
- Nếu có nội dung dài => đẩy vào `archive.md`
- Xóa thông tin không cần thiết khỏi hot docs
