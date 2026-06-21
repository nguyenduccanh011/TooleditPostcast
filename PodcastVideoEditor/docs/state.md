# STATE — Podcast Video Editor (PVE)

Phase: IDLE (giữa các task — không có Task Pack đang theo dõi)
Updated: 2026-06-21

## Metadata
- Stack: C# **.NET 9** + WPF (MVVM Toolkit) + FFmpeg + SkiaSharp + NAudio + EF Core (SQLite) + Serilog
- Target: Desktop Windows — công cụ sản xuất video podcast hàng loạt
- Version: **v1.3.18** (commit gần nhất 2026-04-30)

## Objective (1-2 dòng)
- Tạo video từ podcast (audio + script) tự động: multi-track timeline, segment ảnh/video,
  visualizer, motion (Ken Burns/zoompan), render MP4 và export sang CapCut.

## Capabilities đã có (theo git history tháng 4/2026)
- **Render pipeline:** chunk song song (tối ưu cho 8+ core), GPU/QSV scale + filter, motion/zoompan ổn định, bitrate control.
- **Timeline:** multi-track (text/visual/audio), segment ảnh, preview aspect ratio + composite theo playhead.
- **Template:** import/export robust (xử lý MAX_PATH, Unicode/Vietnamese path, missing asset).
- **AI:** pipeline xử lý script (segment hóa).
- **Phân phối:** in-app update (download + silent installer); export CapCut (motion keyframe).

## LOCKED (không đổi nếu chưa hỏi user)
- Scope: desktop Windows, render local (không backend).
- Architecture: MVVM + Service layer + EF Core SQLite; render qua FFmpeg.

## Next Read
- `docs/active.md` (task hiện tại) · `docs/archive.md` (index tài liệu chi tiết + lịch sử)

## Refs (khi cần)
- decisions: `docs/decisions.md` · issues: `docs/issues.md` · code rules: `docs/code_rules.md`
- backlog & lịch sử chi tiết: `docs/archive.md`

---
Lưu ý: Lịch sử phase/data-model/metrics chi tiết (bản kế hoạch cũ) nằm trong `docs/archive/`
(xem `archive.md`). Doc này chỉ giữ trạng thái hiện tại để resume nhanh.
