# Motion Shimmer (Zoompan) - Decision Summary (2026-04-09)

## 1) Mục tiêu và kết quả hiện tại
- Mục tiêu: giảm rung/shimmer khi motion still-image (Ken Burns) đến mức không ảnh hưởng trải nghiệm người xem.
- Kết quả hiện tại: đạt ~90% cải thiện theo đánh giá thực tế.
- Tình trạng còn lại: micro-shimmer nhẹ, chỉ thấy khi soi kỹ.
- Quyết định hiện tại: không triển khai subpixel transform path thay zoompan (tạm hoãn).

## 2) Hành vi quan sát được (symptoms)
- Ban đầu có rung thấy rõ theo chu kỳ thấp (cảm giác rung theo nhóm nhịp).
- Sau các bản fix, rung thô tạo giảm mạnh; còn shimmer tần số cao hơn, biên độ thấp hơn.
- Log chẩn đoán cho thấy nhiều segment rơi vào vùng step frequency có thể dễ thấy shimmer (8-20Hz) trước khi boost supersample.
- CFR 30fps ổn định, không phải lỗi pacing PTS/frame.

## 3) Nguyên nhân chính đã xác định
- Nguyên nhân 1: encoder budget thấp cho cảnh có motion (cq cao + bitrate floor thấp) gây mất chi tiết và rung nhìn thấy.
- Nguyên nhân 2: NVENC preset quá nhanh (p1) trong cảnh motion, làm giảm temporal coherence.
- Nguyên nhân 3: zoompan có tính chất lượng tử hóa tọa độ theo frame (đặc biệt khi độ dịch chuyển mỗi frame nhỏ), tạo micro-step/shimmer nội tại.
- Nguyên nhân 4: biểu thức pan gần biên canh/có clamp để tạo discontinuity nhẹ theo chu kỳ.

## 4) Những thứ đã làm và có hiệu quả
- Motion-aware encoder tuning:
  - Giảm CQ theo độ phức tạp motion,
  - Nâng preset NVENC theo độ phức tạp,
  - Đặt bitrate floor cao hơn theo tier.
- Chính lại đánh giá motion complexity (không để alpha overlays làm loạng metric).
- Refactor pan expressions theo normalized trajectory + edge safety margin (2%).
- Thêm diagnostics:
  - MotionDiagnostics (step Hz source/internal),
  - Cảnh báo shimmer-band (8-20Hz).
- Thêm adaptive supersampling có mục tiêu cao hơn khi rơi shimmer-band.

## 5) Những thứ đã thử nhưng KHÔNG phải tác nhân chính
- Giả thuyết lỗi frame pacing/CFR: không đúng (file output CFR ổn định).
- RGBA workspace trước zoompan:
  - Đã thử để giảm quantization artifacts.
  - Kết quả user test: không thấy cải thiện rõ rằng.
  - Tác động phụ: render chậm hơn đáng kể.
  - Kết luận: rollback (giữ pixFmt theo ngữ cảnh alpha/yuv420p).
- Root cause bổ sung đã xác nhận: nhánh `FFmpegFilterGraphBuilder` còn thiếu lớp ép single-frame trước `zoompan`, nên looped image input có thể làm fan-out và quay lại kiểu jitter/reset theo chu kỳ. Nhánh này đã được đồng bộ với composer bằng `select='eq(n,0)'`.

## 6) Vấn đề còn tồn tại
- Micro-shimmer nội tại của zoompan vẫn còn một phần nhỏ trong một số cảnh subtle motion.
- Đặc biệt dễ nhận ra khi:
  - Segment dài + travel nhỏ (px/frame rất thấp),
  - User zoom vào chi tiết để soi motion.

## 7) Đánh giá nợ kỹ thuật khi KHÔNG triển khai subpixel transform path
- Mức độ nợ kỹ thuật: THẤP → TRUNG BÌNH (có kiểm soát).
- Lý do:
  - Chất lượng đã đạt mức chấp nhận cho production,
  - Lỗi còn lại có tính "edge case khi soi kỹ",
  - Chi phí/rủi ro rewrite motion path lớn hơn giá trị thu được hiện tại.
- Rủi ro nếu để lâu:
  - Có thể phát sinh feedback từ nhóm user rất nhạy cảm với motion artifacts,
  - Khó đạt mức "pixel-perfect smooth" trong mọi tình huống chỉ với zoompan.

## 8) Điều kiện kích hoạt revisit (khi nào nên làm subpixel path)
Thực hiện lại đề tài subpixel transform khi có ít nhất 1 điều kiện:
- Tỷ lệ bug report liên quan shimmer tăng rõ rằng trên user thực.
- Có yêu cầu chất lượng "broadcast-level" cho still-image motion.
- Có benchmark/prototype cho thấy transform path đạt cải thiện rõ nét với overhead chấp nhận được.

## 9) Scope nếu revisit trong tương lai
- Chỉ áp dụng cho still-image motion (không mở rộng toàn bộ pipeline ngay).
- Có feature flag A/B để so sánh với zoompan.
- Định nghĩa metric trước khi code:
  - Duplicate-step ratio,
  - EstStepHz distribution,
  - Render time delta,
  - User-perceived smoothness.

## 10) Thông tin truy vết nhanh
- Commit quality/motion tuning tổng hợp: b1bd015
- Commit rollback RGBA workspace: 0579646
- Release đã build/tag sau cùng: v1.3.9
- Artifact installer: artifacts/release/packages/PodcastVideoEditor-Setup-v1.3.9.exe

## 11) Kết luận ngắn
- Không triển khai subpixel transform ở thời điểm này KHÔNG phải quyết định "để nó xấu"; đây là trade-off hợp lý theo ROI.
- Hệ thống đang ở trạng thái ổn định, đã có diagnostics, đã giảm rung lớn và chỉ còn micro-artifact khó nhận thấy trong điều kiện bình thường.
- Có đủ thông tin để quay lại nâng cấp kiến trúc motion khi có trigger kinh doanh/chất lượng rõ rằng.
