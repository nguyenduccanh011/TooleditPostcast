# ACTIVE

Pack: (none) — chưa có Task Pack đang theo dõi
Updated: 2026-06-21

## Trạng thái
- Việc shipped gần nhất: **GPU Skia compositor** — preview `Ctrl+G` + render mặc định (NVENC, song song, video frame-accurate) + audit/vá hiệu suất render. Commit `baf4c1c` (2026-06-21, chưa bump version). Pipeline FFmpeg cũ giữ làm fallback.
- Follow-up còn lại (chưa làm): compositor export chưa trộn multi-audio (tự fallback FFmpeg); seek video `-ss` keyframe (lệch 1–3 frame đầu clip); chưa retire pipeline cũ; (tùy chọn) GPU offscreen compositing.
- Chi tiết kiến trúc/điểm tiếp nối: memory `gpu-compositor-work`.
- Hiện chưa có TP mới đang chạy. → **Cập nhật mục này khi bắt đầu task mới.**

## Khi bắt đầu Task Pack mới
- Theo `.ai/01_WORKFLOW.md` (gate G1–G5) và format `active.md` trong `.ai/03_DOC_STANDARDS.md`:
  Goal / Subtasks (ST) / Current Subtask / Definition of Done / Resume Instructions.
- Ghi 1–3 dòng vào `docs/worklog.md` khi kết thúc phiên.

## Backlog tham khảo
> ⚠️ Backlog dưới đây lập 2026-03-14, **chưa rà** sau đợt dev tháng 4. Đối chiếu code trước khi pick
> (một số mục có thể đã làm: ví dụ output path, in-app update). Chi tiết đầy đủ:
> `docs/archive/active-history-2026Q1.md` (mục TP-UX / TP-FEAT).

- UX/CapCut-style: timeline zoom, multi-select segments, magnetic snap, track lock/mute UI,
  playhead timecode (MM:SS.mmm), window height fix.
- Feature: BGM track UI, transition gallery (xfade), volume envelope, auto-caption.
- Issues mở: `docs/issues.md` (#10 output path, #11 render từ Canvas, #12 UI polish CapCut-style).

## Next Read
- `docs/state.md` · `docs/issues.md` · `docs/archive.md`
