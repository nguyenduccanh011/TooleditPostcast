# Index tài liệu chi tiết (Cold docs)

**Mục đích:** Danh sách tài liệu chi tiết để đọc **khi cần implementation** (theo quy trình: Session Start đọc `state.md` + `active.md`; khi TP/ST tham chiếu file bên dưới thì đọc file đó).

**Tham chiếu từ:** `state.md` (Immediate Next Steps, Phase Commitments), `active.md` (TP/ST, Resume Instructions), `decisions.md` (Forward References).

---

## Khi nào đọc

- **Tạo TP mới / bắt đầu phase:** Xem `state.md` Phase Commitments và mục "Immediate Next Steps" — nếu phase có ghi file dưới đây thì đọc file đó trước khi viết ST.
- **Đang làm ST:** Nếu `active.md` (TP/ST hoặc Resume Instructions) ghi "đọc `docs/XXX.md`" thì đọc `docs/XXX.md` khi implement.

---

## Danh sách tài liệu

| File | Nội dung | Đọc khi |
|------|----------|--------|
| **MULTI-TRACK-TIMELINE-DESIGN.md** | Thiết kế timeline đa track, Track/Segment, UI, z-order, chiều cao, Add segment. | Bắt đầu phase Multi-track timeline; implement ST thuộc TP Multi-track. |
| **phase2-plan.md** | Chi tiết Phase 2 (Canvas, Visualizer, Timeline ST-7–ST-12). | Tham khảo Phase 2 đã làm; test/gap so với plan. |
| **DETAILED-IMPLEMENTATION-PLAN.md** | Kế hoạch triển khai chi tiết Phase 2 (ST-7, ST-8, …). | Tham khảo kiến trúc/implementation Phase 2. |
| **QUICK-START-PHASE2.md** | Quick start Phase 2, workflow, dependency. | Onboard / nhắc quy trình Phase 2. |
| **issues.md** | Issues & blockers (#1–#13). | Tạo TP hoặc làm ST có gắn issue; Phase Commitments. |
| **reference-sources.md** | Nguồn tham khảo, study plan. | Cần tra cứu API/thư viện. |
| **code_rules.md** | Quy tắc code dự án. | Role BUILDER (implement). |
| **decisions.md** | ADR, forward references (Phase 3/5/6, Multi-track). | Quyết định design; đưa issue vào TP. |
| **arch.md** | Tổng quan kiến trúc. | Cần hiểu layer, component. |
| **MANUAL-TEST-ST12-UNIFIED-EDITOR.md** | Test thủ công unified editor. | QA / verify ST-12. |
| **TEST-AND-GAP-ST9-ST10.md** | Test & gap ST-9, ST-10 vs phase2-plan. | QA Phase 2 timeline. |
| **PROJECT-STATUS-FEB7.md**, **SESSION-REPORT-FEB7.md** | Báo cáo trạng thái / session. | Tham khảo lịch sử. |

---

## Quy trình ngắn

1. **Session Start:** Luôn đọc `state.md`, `active.md`.
2. **Tạo TP / bắt đầu phase:** Đọc `state.md` Phase Commitments + Immediate Next Steps; nếu có tên file (vd `MULTI-TRACK-TIMELINE-DESIGN.md`) → đọc file đó; đọc `issues.md` cho issue tương ứng.
3. **Làm ST:** Đọc `active.md` Current Subtask; nếu ST hoặc Resume ghi "đọc `docs/XXX.md`" → đọc `docs/XXX.md`; nếu code → đọc `code_rules.md`.

Cập nhật index này khi thêm tài liệu chi tiết mới.
