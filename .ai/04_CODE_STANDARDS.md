# CODE STANDARDS (Agnostic)

## Minimal structure rule
- Ít file nhất có thể
- Chỉ tách file khi:
  1) > ~200-300 dòng và có ranh giới rõ
  2) có interface ổn định
  3) tái sử dụng thật (không phải "có thể tái sử dụng")

## Naming & boundaries
- Tên phản ánh domain, không chung chung (tránh: `utils.js`, `helpers.js`)
- Một file = một trách nhiệm chính rõ ràng
- Folder structure phản ánh domain, không phản ánh technical layer (tránh: `controllers/`, `services/` nếu không cần)

## Comments
- Chỉ comment "WHY", không comment "WHAT" nếu code tự nói được
- Comment phức tạp: giải thích lý do, không mô tả code
- TODO comments: chỉ khi có plan rõ ràng, không để TODO mơ hồ

## Dependency rule
- Không thêm dependency mới nếu chưa được user duyệt
- Nếu cần: ghi `docs/issues.md` + đề xuất A/B với trade-off
- Ưu tiên dependency nhẹ, stable, well-maintained

## Extension points
- Luôn xác định 1-2 điểm mở rộng: interface/adapter/config hook
- Extension points phải rõ ràng trong code (không ẩn)
- Document extension points trong `docs/arch.md` nếu cần

## Code quality
- Không deadcode
- Không unused configs/imports
- Không duplication (DRY principle)
- Error handling: có xử lý lỗi rõ ràng, không silent fail
- Type safety: dùng type nếu có (TypeScript, type hints, etc.)

## Project-specific override
- `docs/code_rules.md` override file này cho project-specific rules
- Nếu có conflict: `docs/code_rules.md` có priority cao hơn

## Forbidden patterns
- Deadcode
- Unused configs/imports
- New dependency without user approval
- Premature optimization
- Over-engineering (abstraction quá sớm)

## Testing (minimal)
- Chỉ test critical paths
- Terminal checks: build/lint/typecheck
- Smoke tests: minimal scenario
- Manual tests: viết script cho user (1-issue, <=10 steps)
