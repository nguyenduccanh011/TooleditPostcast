# CapCut API Standalone

Gói API REST độc lập để điều khiển CapCut tạo video tự động.
Chỉ hỗ trợ CapCut, ngôn ngữ tiếng Việt (fallback tiếng Anh).

## Cài đặt

```bash
pip install -r requirements.txt
```

## Cấu hình

1. Copy `config.json.example` thành `config.json` và chỉnh sửa:

```json
{
  "is_capcut_env": true,
  "draft_domain": "https://your-domain.com",
  "port": 9001,
  "preview_router": "/draft/downloader",
  "is_upload_draft": false
}
```

Các tham số:
- `is_capcut_env`: `true` = CapCut (quốc tế), `false` = JianYing (Trung Quốc)
- `draft_domain`: Domain base cho draft URL
- `port`: Port chạy server (mặc định 9001)
- `preview_router`: Router path cho preview
- `is_upload_draft`: `true` nếu muốn upload draft lên OSS (cần cấu hình `oss_config`)

## Chạy Server

```bash
python capcut_server.py
```

Server chạy tại `http://localhost:9001` (hoặc port đã cấu hình)

## API Endpoints

### Tạo & quản lý draft
| Method | Endpoint | Mô tả |
|--------|----------|--------|
| POST | `/create_draft` | Tạo draft mới |
| POST | `/save_draft` | Lưu draft (download assets + export) |
| POST | `/query_draft_status` | Kiểm tra trạng thái task |
| POST | `/query_script` | Xem nội dung script của draft |
| POST | `/generate_draft_url` | Tạo URL preview cho draft |

### Thêm media tracks
| Method | Endpoint | Mô tả |
|--------|----------|--------|
| POST | `/add_video` | Thêm video track |
| POST | `/add_audio` | Thêm audio track |
| POST | `/add_image` | Thêm image track |
| POST | `/add_text` | Thêm text overlay |
| POST | `/add_subtitle` | Thêm phụ đề từ SRT |
| POST | `/add_sticker` | Thêm sticker |
| POST | `/add_effect` | Thêm hiệu ứng video |
| POST | `/add_video_keyframe` | Thêm keyframe animation |

### Truy vấn metadata (GET)
| Endpoint | Mô tả |
|----------|--------|
| `/get_transition_types` | Danh sách transition |
| `/get_intro_animation_types` | Animation mở đầu (video/image) |
| `/get_outro_animation_types` | Animation kết thúc |
| `/get_combo_animation_types` | Animation kết hợp |
| `/get_text_intro_types` | Animation mở đầu cho text |
| `/get_text_outro_types` | Animation kết thúc cho text |
| `/get_text_loop_anim_types` | Animation lặp cho text |
| `/get_mask_types` | Danh sách mask |
| `/get_font_types` | Danh sách font |
| `/get_audio_effect_types` | Hiệu ứng âm thanh |
| `/get_video_scene_effect_types` | Hiệu ứng scene |
| `/get_video_character_effect_types` | Hiệu ứng nhân vật |

## Ví dụ sử dụng

### Tạo draft + thêm video + text + lưu

```python
import requests

BASE = "http://localhost:9001"

# 1. Tạo draft
resp = requests.post(f"{BASE}/create_draft", json={"width": 1080, "height": 1920})
draft_id = resp.json()["output"]["draft_id"]

# 2. Thêm video
requests.post(f"{BASE}/add_video", json={
    "draft_id": draft_id,
    "video_url": "https://example.com/video.mp4",
    "duration": 10.0,          # duration (giây), bắt buộc nếu không dùng ffprobe
    "track_name": "video_main"
})

# 3. Thêm text
requests.post(f"{BASE}/add_text", json={
    "draft_id": draft_id,
    "text": "Hello World",
    "start": 0,
    "end": 5,
    "font_size": 10.0,
    "color": "#FFFFFF",
    "transform_y": -0.8
})

# 4. Thêm audio
requests.post(f"{BASE}/add_audio", json={
    "draft_id": draft_id,
    "audio_url": "https://example.com/music.mp3",
    "duration": 30.0
})

# 5. Lưu draft vào thư mục CapCut
requests.post(f"{BASE}/save_draft", json={
    "draft_id": draft_id,
    "draft_folder": "C:/Users/<user>/AppData/Local/CapCut/User Data/Projects/com.lveditor.draft"
})
```

### Thêm image với animation + transition

```python
requests.post(f"{BASE}/add_image", json={
    "draft_id": draft_id,
    "image_url": "https://example.com/photo.jpg",
    "start": 0,
    "end": 3,
    "intro_animation": "Zoom_In",
    "intro_animation_duration": 0.5,
    "transition": "Dissolve",
    "transition_duration": 0.5,
    "track_name": "image_main"
})
```

### Thêm keyframe animation

```python
requests.post(f"{BASE}/add_video_keyframe", json={
    "draft_id": draft_id,
    "track_name": "image_main",
    "property_types": ["scale_x", "scale_y", "scale_x", "scale_y"],
    "times": [0.0, 0.0, 3.0, 3.0],
    "values": ["1.0", "1.0", "1.25", "1.25"]
})
```

## Cấu trúc thư mục

```
capcut_api_standalone/
├── capcut_server.py          # Flask REST API server (entry point)
├── config.json.example       # Config mẫu
├── requirements.txt          # Dependencies
│
├── create_draft.py           # Tạo draft mới
├── save_draft_impl.py        # Logic lưu/export draft + download assets
├── add_video_track.py        # Thêm video track
├── add_audio_track.py        # Thêm audio track
├── add_image_impl.py         # Thêm image
├── add_text_impl.py          # Thêm text
├── add_subtitle_impl.py      # Thêm subtitle từ SRT
├── add_effect_impl.py        # Thêm effect
├── add_sticker_impl.py       # Thêm sticker
├── add_video_keyframe_impl.py# Thêm keyframe
├── get_duration_impl.py      # Lấy duration media (ffprobe)
│
├── draft_cache.py            # Cache draft in-memory (LRU)
├── save_task_cache.py        # Cache task status
├── downloader.py             # Download media files
├── util.py                   # Utilities
├── oss.py                    # Alibaba OSS upload (optional)
├── i18n.py                   # Internationalization (vi/en)
│
├── settings/                 # App settings (đọc từ config.json)
├── locales/                  # Translation files (vi.json, en.json)
├── template/                 # CapCut draft template files
└── pyJianYingDraft/          # Core draft manipulation library
```

## Lưu ý quan trọng

1. **ffprobe**: Cần cài FFmpeg để lấy duration/metadata media. Nếu không có, truyền `duration` khi gọi API.
2. **track_name**: Mỗi track cần tên duy nhất. CapCut không hỗ trợ đặt tên track trên giao diện, track_name chỉ dùng nội bộ để API quản lý.
3. **draft_folder**: Khi `save_draft`, truyền đường dẫn thư mục draft CapCut để tự động copy draft vào. Mở CapCut sẽ thấy draft mới.
4. **OSS**: Chỉ cần cấu hình `oss_config` nếu `is_upload_draft = true`.

## Tích hợp vào dự án khác

1. Copy toàn bộ thư mục `capcut_api_standalone/` vào dự án
2. Cài dependencies: `pip install -r capcut_api_standalone/requirements.txt`
3. Tạo `config.json` từ `config.json.example`
4. Chạy server: `python capcut_api_standalone/capcut_server.py`
5. Gọi API qua HTTP requests
