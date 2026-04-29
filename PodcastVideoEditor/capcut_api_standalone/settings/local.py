"""
Cấu hình cho CapCut API Server.
Đọc từ config.json, mặc định dùng CapCut.
Auto-detect draft_folder nếu không cấu hình.
"""

import os
import sys
import json5

CONFIG_FILE_PATH = os.path.join(os.path.dirname(os.path.dirname(__file__)), "config.json")

# Luôn là CapCut (không hỗ trợ JianYing)
IS_CAPCUT_ENV = True

DRAFT_DOMAIN = "https://www.install-ai-guider.top"
PREVIEW_ROUTER = "/draft/downloader"
IS_UPLOAD_DRAFT = False
PORT = 9000
DRAFT_FOLDER = None
OSS_CONFIG = []
MP4_OSS_CONFIG = []


def _auto_detect_draft_folder() -> str | None:
    """Auto-detect CapCut draft folder based on OS."""
    candidates = []
    if sys.platform == "win32":
        local = os.environ.get("LOCALAPPDATA", "")
        if local:
            candidates.append(os.path.join(local, "CapCut", "User Data", "Projects", "com.lveditor.draft"))
        # Fallback: try common path
        home = os.path.expanduser("~")
        candidates.append(os.path.join(home, "AppData", "Local", "CapCut", "User Data", "Projects", "com.lveditor.draft"))
    elif sys.platform == "darwin":
        home = os.path.expanduser("~")
        candidates.append(os.path.join(home, "Movies", "CapCut", "User Data", "Projects", "com.lveditor.draft"))
        candidates.append(os.path.join(home, "Movies", "JianyingPro", "User Data", "Projects", "com.lveditor.draft"))
    else:
        # Linux - unlikely but try
        home = os.path.expanduser("~")
        candidates.append(os.path.join(home, ".capcut", "Projects", "com.lveditor.draft"))

    for path in candidates:
        if os.path.isdir(path):
            return path
    return None

if os.path.exists(CONFIG_FILE_PATH):
    try:
        with open(CONFIG_FILE_PATH, "r", encoding="utf-8") as f:
            local_config = json5.load(f)

            if "draft_domain" in local_config:
                DRAFT_DOMAIN = local_config["draft_domain"]
            if "port" in local_config:
                PORT = local_config["port"]
            if "preview_router" in local_config:
                PREVIEW_ROUTER = local_config["preview_router"]
            if "is_upload_draft" in local_config:
                IS_UPLOAD_DRAFT = local_config["is_upload_draft"]
            if "oss_config" in local_config:
                OSS_CONFIG = local_config["oss_config"]
            if "mp4_oss_config" in local_config:
                MP4_OSS_CONFIG = local_config["mp4_oss_config"]
            if "draft_folder" in local_config:
                DRAFT_FOLDER = local_config["draft_folder"]
    except Exception:
        pass

# Auto-detect draft_folder nếu chưa được cấu hình
if not DRAFT_FOLDER:
    DRAFT_FOLDER = _auto_detect_draft_folder()
    if DRAFT_FOLDER:
        print(f"[settings] Auto-detected CapCut draft folder: {DRAFT_FOLDER}")
    else:
        print("[settings] WARNING: Could not auto-detect CapCut draft folder. Set 'draft_folder' in config.json.")
