# LIBRARIAN MODE (Unknown library/domain)

## Khi kích hoạt
- Library/domain không có trên mạng hoặc thiếu docs rõ ràng
- API mơ hồ, dễ bịa -> phải dừng code ngay
- Domain knowledge cần chuyên sâu, không có trên mạng

## RFK (Request For Knowledge)
Khi gặp library/domain lạ, dừng code và hỏi user:

### Câu hỏi chuẩn
1. **Tên + version?**
   - Library name: ...
   - Version: ...

2. **Mục đích?**
   - Dùng để làm gì?
   - Tại sao chọn library này?

3. **Entry points chính (1-3 API)?**
   - Main API/function/class: ...
   - Cách import/require: ...

4. **Example input/output?**
   - Input example: ...
   - Output example: ...

5. **Constraints?**
   - License: ...
   - Platform: ...
   - Performance: ...
   - Limitations: ...

6. **Có snippet/spec/log mẫu không?**
   - Code snippet: ...
   - Documentation link: ...
   - Example project: ...

## Output format
Sau khi nhận thông tin từ user, ghi `docs/knowledge/<topic>.md` theo template:

```markdown
# <topic>

What it is (2-3 lines):
- ...

Why we use it (2-3 lines):
- ...

Minimal API / Concepts:
- Entry point 1: ...
- Entry point 2: ...
- Entry point 3: ...

Golden path example:
```code
...
```

Pitfalls:
- ...

Provided by user:
- date: YYYY-MM-DD
- source: <user/url>
```

## Tổ chức knowledge
- Mỗi topic = 1 file trong `docs/knowledge/`
- Tên file: `<topic>.md` (lowercase, hyphen-separated)
- Nếu có nhiều version: `<topic>-v<version>.md`
- Index: `docs/knowledge/README.md` (optional, chỉ khi có nhiều files)

## Workflow
1. BUILDER gặp library lạ
2. Dừng code ngay
3. Chuyển ROLE: LIBRARIAN
4. RFK hỏi user (theo câu hỏi chuẩn)
5. Nhận thông tin từ user
6. Ghi `docs/knowledge/<topic>.md` (theo template)
7. Quay lại BUILDER role
8. Tiếp tục code với knowledge mới
9. Ghi worklog

## Quan trọng
- Không đoán API/behavior
- Không dùng library lạ mà không có knowledge
- Phải dừng code khi gặp library lạ
- Knowledge file phải ngắn gọn, usable (<= 100 lines)

## Template file
Có sẵn `docs/knowledge/_template.md` để copy khi tạo knowledge mới.
