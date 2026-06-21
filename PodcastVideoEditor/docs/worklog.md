# WORKLOG (nén — 1–3 dòng/mốc)

Lịch sử chi tiết theo phiên (Feb–Apr): `docs/archive/worklog-2026H1.md`.
Git history đầy đủ bắt đầu **2026-04-07**; giai đoạn 02→03 chỉ còn trong worklog archive.

## 2026-02 (pre-git, theo worklog cũ)
[2026-02-06] Khởi tạo PVE; Phase 1 Core Engine & Audio.
[2026-02-07] Phase 1 ✅; bắt đầu Phase 2 Canvas Editor & Visualizer.
[2026-02-08] Phase 2 ✅ (ST-7..12); Phase 3 Script & Timeline bắt đầu (audio track + waveform).
[2026-02-10..11] Phase 3 ✅ (timeline sync precision, script import).
[2026-02-12] TP-004 Multi-track core ✅ (ST-1..5); TP-005 MVP Visual & Preview: ST-1 (ảnh→segment) ✅, ST-2 (aspect) dở. **Docs dừng tại đây.**

## 2026-04 (git history, v1.3.4 → v1.3.18)
[2026-04-07] AI pipeline + template export/import robust; preview z-order; **v1.3.4**.
[2026-04-07..08] Render Phase 2 tối ưu mạnh + benchmark; FFmpeg concat normalize; chunk render; **v1.3.6/1.3.8/1.3.9**.
[2026-04-08..09] Motion/zoompan shimmer ổn định + tune chất lượng; in-app update (download + silent installer); **v1.3.10**.
[2026-04-10..11] Chunk render ổn định; Unicode/Vietnamese output path; filename sanitize; thống nhất style system + property panel UX.
[2026-04-12] Slider precision/opacity drag; button styling compact.
[2026-04-14] GPU QSV filter validation + GPU scale + probe logging; fix MAX_PATH template; **v1.3.15**.
[2026-04-15] Đơn giản hóa track span modes (project_duration stretch); fix cache invalidation preview.
[2026-04-16] Tăng độ song song chunk render cho máy 8+ core.
[2026-04-29..30] CapCut export integration + motion keyframe; **v1.3.18**.

---
Format mốc mới: `[YYYY-MM-DD] <action> — <result>` (1–3 dòng). Chi tiết dài → đẩy sang `docs/archive/`.
