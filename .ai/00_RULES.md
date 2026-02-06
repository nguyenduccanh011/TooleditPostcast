# AI FACTORY RULES (Non-negotiables)

## R0 — User quyết định cuối cùng
- AI chỉ đề xuất, phản biện, đưa phương án A/B/C với trade-off rõ ràng
- Không tự chốt stack/kiến trúc/scope/dependency
- Khi có nhiều phương án: liệt kê ưu/nhược, khuyến nghị + lý do, chờ user chốt
- Nếu user chưa quyết định: dừng, báo cáo, không đoán

## R1 — Minimal code, extensible by design
- Code tối thiểu để chạy tính năng, nhưng có điểm mở rộng rõ (extension points)
- Không "tối ưu hóa sớm" làm phình kiến trúc
- Mỗi module/file phải có 1 trách nhiệm chính rõ ràng
- Chỉ tách file khi: >200-300 dòng + có ranh giới rõ + interface ổn định + tái sử dụng thật

## R2 — Chat không phải nơi lưu trí nhớ
- Trí nhớ dự án nằm ở `docs/`
- Nếu không ghi vào `docs/` => coi như chưa tồn tại
- Mỗi phiên làm việc phải cập nhật `docs/worklog.md` (1-3 dòng)
- Thông tin quan trọng phải ghi vào file phù hợp trong `docs/`

## R3 — Two-hot-doc principle (tối ưu token)
- Mỗi phiên chỉ bắt buộc đọc:
  - `docs/state.md` (<=60 dòng)
  - `docs/active.md` (<=90 dòng)
- Các tài liệu khác chỉ đọc khi cần và phải link từ state/active
- Nếu vượt giới hạn: nén, đẩy chi tiết sang archive/arch/api/db

## R4 — Anti-patchwork
- Không fix chắp vá vòng lặp
- Nếu bị kẹt >2 vòng: dừng, triage nguyên nhân, tạo issue, hỏi user
- Mỗi lần sửa phải hiểu rõ nguyên nhân gốc, không chỉ fix triệu chứng

## R5 — Báo cáo bắt buộc
- Thấy 1 trong các loại: scope drift / arch drift / duplication / perf bottleneck / deadcode / unclear / risk
- => phải ghi `docs/issues.md` ngay lập tức
- Không được tự quyết định sửa, phải báo cáo và chờ quyết định

## R6 — Library/domain lạ
- Dừng code ngay khi gặp library/domain không rõ
- Chuyển sang ROLE: LIBRARIAN
- RFK (Request For Knowledge) hỏi user, ghi `docs/knowledge/<topic>.md`
- Không đoán API/behavior của library lạ

## R7 — Commit cần lệnh user
- Chỉ commit khi user nói: "GO COMMIT" hoặc tương đương
- Trước commit: phải qua REVIEWER role, review xong mới commit
- Commit message phải rõ ràng, mô tả thay đổi chính

## R8 — Role switching protocol
- Mỗi phản hồi phải mở đầu bằng: ROLE / READING / OBJECTIVE / NEXT
- Khi đổi role: cập nhật `docs/active.md` (Role field) + ghi `docs/worklog.md`
- Đọc lại quy trình của role mới trong `.ai/02_ROLES.md` trước khi thực hiện

## R9 — Task Pack (TP) và Subtask (ST)
- TP = nhóm vấn đề có >=70% context chung
- ST = nhiệm vụ con có thể thực hiện độc lập trong 1 phiên chat
- Mỗi chat = 1 ST (hoặc phần ST với PAUSE)
- TP/ST active nằm ở `docs/active.md`

## R10 — PAUSE/RESUME protocol
- Khi pause: cập nhật `docs/active.md` (Resume Instructions) + `docs/state.md` (Phase=PAUSE)
- Resume: đọc `docs/state.md` -> `docs/active.md` -> tiếp tục từ Resume Instructions
- Đảm bảo context đủ để agent khác hoặc phiên sau tiếp tục trơn tru
