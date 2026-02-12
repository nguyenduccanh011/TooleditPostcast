# Bổ sung kế hoạch: Editor UI, Preview tỉ lệ, Image API cho Segment

**Ngày:** 2026-02-11  
**Trạng thái:** 📋 KẾ HOẠCH (phần mở rộng — phát triển sau)  
**Mục đích:** Ghi nhận các khoảng trống để phát triển sau; không làm phức tạp đường có video đầu tiên.

---

## Ưu tiên hiện tại: Video đầu tiên + khung cơ bản

- **Đã làm (minimal):** Render dùng ảnh từ project (segment có BackgroundAssetId → Asset.FilePath). Nếu không có ảnh → placeholder tự tạo. Output lưu AppData/.../Renders. **→ Có thể xuất video MP4 ngay.**
- **Khung cơ bản:** Giữ nguyên Editor hiện tại (Canvas + Timeline + Properties + Render). Đủ để chỉnh segment, ảnh nền, render; mở rộng UI/Preview/API sau.

Phần dưới đây là **ý tưởng mở rộng**, triển khai khi cần (Phase 5/6 hoặc v1.1).

---

## Tổng quan (mở rộng sau)

Ba nhóm yêu cầu bổ sung so với kế hoạch hiện tại:

1. **Khung giao diện Editor** — Tùy chỉnh lại cho giống CapCut, đẹp chuẩn, giống phần mềm edit chuyên nghiệp.
2. **Khung Preview với tỉ lệ 16:9 / 9:16** — Có khung theo tỉ lệ; edit và preview trực tiếp trên giao diện.
3. **Lấy ảnh từ API + gán segment + preview** — Nút lấy ảnh từ API, chọn ảnh (tự động hoặc thủ công), áp vào segment media, xem preview.

Liên quan: **Issue #12** (UI Editor CapCut — Phase 6), **Req #6** (Auto image search — v1.1 trong state.md).

---

## 1. Khung giao diện Editor (CapCut-style, chuyên nghiệp)

### Mô tả
- Tùy chỉnh lại **các khung (panels)** trong màn hình Editor cho **giống CapCut** và các phần mềm edit chuyên nghiệp (Premiere, DaVinci, v.v.).
- Mục tiêu: bố cục gọn, rõ ràng, dễ thao tác; có thể collapse/expand panel; spacing, kích thước chuẩn.

### Gợi ý nội dung (để sau này chia ST)
- **Layout:** Vùng trái (media/library hoặc toolbar), giữa (preview lớn), phải (property panel), dưới (timeline + audio).
- **Panel:** Có thể thu gọn/mở rộng từng vùng (dock style); nhớ trạng thái (preference).
- **Visual:** Màu nền, viền, font size nhất quán; giống “dark theme” hoặc theme chuẩn editor.
- **Reference:** CapCut desktop, Premiere Pro panel layout (không copy UI, chỉ tham khảo bố cục).

### Phase / Issue
- Gắn với **Phase 6** và **Issue #12** (UI Editor tab — tối ưu gọn đẹp CapCut). Có thể tách 1–2 ST: (1) Layout & panels, (2) Theme/spacing chuẩn.

### Câu hỏi mở (trao đổi thêm)
- Có cần **nhiều preset layout** (ví dụ: “Focus timeline”, “Focus preview”) hay một layout cố định?
- Có cần **lưu vị trí/kích thước panel** (persist) khi đóng/mở app không?

---

## 2. Khung Preview với tỉ lệ 16:9 và 9:16

### Mô tả
- **Preview** (canvas xem video/editor) có **khung theo tỉ lệ màn hình**:
  - **16:9** (ngang, YouTube/desktop).
  - **9:16** (dọc, TikTok/Reels/Stories).
- **Edit và preview trực tiếp** trên chính giao diện này: user chỉnh element (title, logo, ảnh, script…) và xem ngay trên khung preview đúng tỉ lệ.

### Gợi ý nội dung (để sau này chia ST)
- **Chọn tỉ lệ:** Dropdown hoặc nút chuyển 16:9 / 9:16; canvas/preview đổi kích thước khung (letterbox hoặc fit) theo tỉ lệ, không vỡ layout.
- **Một canvas, nhiều tỉ lệ:** Canvas nội dung (elements) có thể dùng chung; khi đổi tỉ lệ chỉ đổi **khung hiển thị** (crop/frame) hoặc scale để xem đúng tỉ lệ output.
- **Preview real-time:** Phát audio + playhead → preview (canvas) cập nhật theo thời gian (segment, ảnh nền, element) — có thể nằm trong ST “script/preview sync” đã nêu trước đó.
- **Render settings:** Resolution/aspect ratio trong Render panel nên đồng bộ với tỉ lệ đang chọn (16:9 → 1920x1080; 9:16 → 1080x1920 hoặc tương đương).

### Phase / Issue
- Có thể **Phase 5 (Render)** hoặc **Phase 6 (Polish)**. Có thể tách ST: (1) Preview frame 16:9/9:16 + chuyển tỉ lệ, (2) Đồng bộ tỉ lệ với Render settings.

### Câu hỏi mở
- Chỉ 2 tỉ lệ 16:9 và 9:16 hay sau này thêm (4:5, 1:1, v.v.)?
- Khi đổi tỉ lệ giữa 16:9 ↔ 9:16, **vị trí element** (title, logo, script) có cần “safe zone” tự điều chỉnh hay user tự chỉnh?

---

## 3. Lấy ảnh từ API + chọn ảnh (tự động) + áp vào segment + preview

### Mô tả
- **Nút “Lấy ảnh từ API”** (hoặc tương tự): gọi API ảnh (Unsplash/Pexels/Pixabay — đã nhắc trong state.md, Req #6 v1.1) để tìm ảnh.
- **Chọn ảnh:** Tự động (theo keyword/segment) và/hoặc thủ công (user chọn từ kết quả).
- **Áp vào segment media:** Gán ảnh đã chọn làm ảnh nền (background) cho một hoặc nhiều segment trên timeline.
- **Preview:** Sau khi áp, xem ngay trên khung preview (và timeline) — đúng với nội dung “preview trực tiếp” ở mục 2.

### Gợi ý nội dung (để sau này chia ST)
- **UI:** Nút trong Editor (gần timeline hoặc segment panel): “Lấy ảnh từ API” / “Tìm ảnh”. Mở panel/dialog: nhập keyword (hoặc lấy từ segment text?), gọi API, hiển thị grid ảnh; user chọn ảnh (hoặc “Gán tự động” nếu có).
- **Auto gán segment:** “Gán tự động” = gán ảnh cho segment đang chọn, hoặc gán lần lượt cho nhiều segment (ví dụ mỗi segment một ảnh theo keyword từ script/segment text). Logic cần định nghĩa rõ (theo ST sau).
- **Persist:** Ảnh tải về (hoặc URL) lưu thành Asset; segment.BackgroundAssetId (hoặc tương đương) trỏ tới asset đó — đã có sẵn model segment + asset.
- **Preview:** Đã có segment + ảnh nền → preview trên canvas theo playhead (có thể nằm trong Render từ Canvas / preview sync).

### Phase / Issue
- **Req #6** (Auto image search) trong state.md đang **OUT OF SCOPE v1.0** (defer v1.1). Có thể:
  - **Option A:** Đưa vào **v1.1** như đã ghi; hoặc
  - **Option B:** Làm **sớm hơn** (Phase 5/6) dưới dạng “Image from API + gán segment” như một ST/TP riêng.
- API keys: Unsplash/Pexels/Pixabay — cần Settings (API keys) và docs (rate limit, license). State.md đã có Req #14 Settings (API keys).

### Câu hỏi mở
- “Chọn ảnh tự động”: nghĩa là (1) **auto gán ảnh cho từng segment** theo keyword/script, hay (2) **auto chọn một ảnh từ kết quả API** (ví dụ ảnh đầu tiên)? Cần rõ để thiết kế flow.
- API ưu tiên: Unsplash trước hay hỗ trợ nhiều nguồn (Pexels, Pixabay) ngay từ đầu?

---

## 4. Thứ tự đề xuất (sau khi chốt trao đổi)

| Ưu tiên | Nội dung | Phase gợi ý | Ghi chú |
|--------|----------|------------|---------|
| 1 | Editor UI — khung panels CapCut-style | Phase 6 (#12) | Layout, collapse, spacing |
| 2 | Preview khung 16:9 / 9:16 + edit/preview trực tiếp | Phase 5 hoặc 6 | Có thể 1 ST với Render sync |
| 3 | Script/text hiển thị lên preview theo playhead | Bổ sung Phase 3/5/6 | Đã nêu trong trao đổi trước |
| 4 | Nút lấy ảnh từ API + gán segment + preview | v1.1 hoặc Phase 5/6 | Req #6; cần chốt auto vs manual |

---

## 5. Cập nhật tài liệu (đề xuất)

- **state.md — Phase Commitments:** Giữ #12 Phase 6; có thể thêm dòng cho “Preview 16:9/9:16” và “Image API cho segment” (issue mới hoặc mở rộng #12).
- **issues.md:** Thêm **Issue #14** (Preview frame 16:9/9:16 + edit/preview), **Issue #15** (Image from API + gán segment + preview) nếu muốn track riêng; hoặc mở rộng mô tả #12.
- **active.md:** Khi bắt đầu Phase 6 (hoặc TP tương ứng), đưa các mục trên vào TP/ST cụ thể.

---

## 6. Tóm tắt

- **Editor UI:** Tùy chỉnh khung cho giống CapCut/chuyên nghiệp → Phase 6, #12.
- **Preview 16:9/9:16:** Khung tỉ lệ + edit/preview trực tiếp → bổ sung ST (Phase 5/6).
- **Ảnh từ API + gán segment + preview:** Nút lấy ảnh, chọn (tự động/thủ công), áp segment, xem preview → v1.1 hoặc Phase 5/6; Req #6.

**Chưa thực hiện code** — tài liệu này dùng để trao đổi, chốt câu hỏi mở, sau đó mới tách ST và implement.

---

Last updated: 2026-02-11
