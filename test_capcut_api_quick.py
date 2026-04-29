"""
Test nhanh CapCut API: tạo draft → thêm audio → save vào thư mục CapCut.
Chạy: python test_capcut_api_quick.py
"""
import requests
import subprocess
import time
import sys
import os

BASE = "http://localhost:9001"
CAPCUT_DRAFT_FOLDER = r"C:\Users\DUC CANH PC\AppData\Local\CapCut\User Data\Projects\com.lveditor.draft"

# File audio rất nhỏ (~5KB) để test nhanh
AUDIO_URL = "https://actions.google.com/sounds/v1/alarms/beep_short.ogg"

def start_server():
    """Start capcut_server.py in background"""
    server_dir = os.path.join(os.path.dirname(__file__), "PodcastVideoEditor", "capcut_api_standalone")
    proc = subprocess.Popen(
        [sys.executable, "capcut_server.py"],
        cwd=server_dir,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    print("Waiting for server to start...")
    time.sleep(3)
    return proc

def main():
    proc = start_server()
    try:
        # 1. Tạo draft
        print("\n=== 1. Creating draft ===")
        resp = requests.post(f"{BASE}/create_draft", json={"width": 1080, "height": 1920}, timeout=10)
        data = resp.json()
        print(f"Response: {data}")
        
        if not data.get("success"):
            print("❌ Failed to create draft")
            return
        
        draft_id = data["output"]["draft_id"]
        print(f"Draft ID: {draft_id}")

        # 2. Thêm audio (truyền duration để skip ffprobe khi add)
        print("\n=== 2. Adding audio ===")
        resp = requests.post(f"{BASE}/add_audio", json={
            "draft_id": draft_id,
            "audio_url": AUDIO_URL,
            "duration": 1.0,
            "track_name": "audio_main",
            "draft_folder": CAPCUT_DRAFT_FOLDER,
        }, timeout=30)
        print(f"Response: {resp.json()}")

        # 3. Save draft vào thư mục CapCut (timeout 300s)
        print("\n=== 3. Saving draft to CapCut folder ===")
        print(f"   Target: {CAPCUT_DRAFT_FOLDER}")
        resp = requests.post(f"{BASE}/save_draft", json={
            "draft_id": draft_id,
            "draft_folder": CAPCUT_DRAFT_FOLDER,
        }, timeout=300)
        result = resp.json()
        print(f"Response: {result}")
        
        if result.get("success"):
            draft_path = os.path.join(CAPCUT_DRAFT_FOLDER, draft_id)
            if os.path.exists(draft_path):
                print(f"\n✅ Draft đã được lưu thành công vào CapCut!")
                print(f"   Path: {draft_path}")
                # List files recursively
                for root, dirs, files in os.walk(draft_path):
                    for f in files:
                        fp = os.path.join(root, f)
                        print(f"   - {os.path.relpath(fp, draft_path)}")
                print(f"\n👉 Mở CapCut sẽ thấy draft mới trong danh sách Projects.")
            else:
                print(f"\n⚠️ API trả success nhưng không tìm thấy folder: {draft_path}")
        else:
            print(f"\n❌ Save failed: {result.get('error')}")

    except requests.exceptions.ReadTimeout:
        print("❌ Error: Request timed out")
    except Exception as e:
        print(f"❌ Error: {e}")
    finally:
        print("\nServer stopped.")
        proc.terminate()

if __name__ == "__main__":
    main()
