# QA LIGHT

## Nguyên tắc
- Không test hàng loạt như QA truyền thống (tốn tài nguyên, khó tự động)
- AI làm terminal checks + smoke tests
- User làm manual tests (UI/UX, hành vi khó tự động)
- Mỗi lần test chỉ 1 vấn đề (one-issue)

## AI làm (terminal-first)
### Build checks
- Build/lint/typecheck (nếu có)
- Dependency check
- Config validation

### Smoke tests
- Health check / API ping
- Minimal scenario (theo `scripts/smoke.md` nếu có)
- Critical path test (nếu có thể tự động)

### Terminal checks examples
```bash
# Build check
npm run build
# hoặc
python -m pytest --collect-only

# Lint check
npm run lint
# hoặc
flake8 .

# Type check
npm run typecheck
# hoặc
mypy .

# Health check
curl http://localhost:3000/health
# hoặc
npm run test:smoke
```

## User làm (manual)
### Khi nào cần manual test
- UI/UX test
- Hành vi phức tạp khó tự động
- Integration test phức tạp
- Performance test (nếu cần)

### Quy tắc manual test
- 1 lần test = 1 vấn đề (one-issue)
- AI cung cấp steps + expected result ngắn (<= 10 bước)
- User báo lại kết quả: PASS / FAIL / PARTIAL

### Format test script cho user
```markdown
## Test: <tên test>

**Mục đích:** <mô tả ngắn>

**Steps:**
1. ...
2. ...
3. ...

**Expected result:**
- ...

**Actual result:** (user điền)
- ...

**Status:** PASS / FAIL / PARTIAL
```

## QA workflow
1. BUILDER code xong
2. Chuyển QA_LIGHT role
3. Chạy terminal checks:
   - Build/lint/typecheck
   - Smoke tests
4. Nếu có manual test cần:
   - Viết test script (<=10 steps)
   - Hướng dẫn user test
   - Chờ kết quả từ user
5. Ghi kết quả vào `docs/active.md`
6. Nếu PASS: chuyển REVIEWER
7. Nếu FAIL/PARTIAL: báo cáo user, chuyển BUILDER fix

## Smoke test file (optional)
Nếu có `scripts/smoke.md`, format:
```markdown
# Smoke Tests

## Test 1: <name>
Command: `...`
Expected: ...

## Test 2: <name>
Command: `...`
Expected: ...
```

## Quan trọng
- Không test quá nhiều, chỉ test critical
- Mỗi lần test chỉ 1 vấn đề
- Nếu test fail: báo cáo user ngay, không tự fix
- Test script phải ngắn gọn, dễ hiểu
