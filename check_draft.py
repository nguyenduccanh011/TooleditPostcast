import json, sys, os

draft_dir = r"C:\Users\DUC CANH PC\AppData\Local\CapCut\User Data\Projects\com.lveditor.draft"
# Get the latest app-created draft (not our test one)
draft_id = "dfd_cat_1776502906_cc13dc27"

path = os.path.join(draft_dir, draft_id, "draft_info.json")
d = json.load(open(path, "r", encoding="utf-8"))

print("=== AUDIO MATERIALS ===")
for a in d.get("materials", {}).get("audios", []):
    print(f"  path: {a.get('path')}")
    print(f"  name: {a.get('name')}")
    print(f"  material_name: {a.get('material_name')}")
    print(f"  duration: {a.get('duration')}")
    # Check if file exists
    p = a.get("path", "")
    if p:
        exists = os.path.isfile(p)
        print(f"  FILE EXISTS: {exists}")
        if not exists:
            # Check what's in audio dir
            audio_dir = os.path.join(draft_dir, draft_id, "assets", "audio")
            if os.path.isdir(audio_dir):
                print(f"  Files in audio dir: {os.listdir(audio_dir)}")
    print()

# Also check the working test draft
test_id = "dfd_cat_1776505080_0f470c50"
path2 = os.path.join(draft_dir, test_id, "draft_info.json")
if os.path.exists(path2):
    d2 = json.load(open(path2, "r", encoding="utf-8"))
    print("=== TEST DRAFT AUDIO (working) ===")
    for a in d2.get("materials", {}).get("audios", []):
        print(f"  path: {a.get('path')}")
        print(f"  name: {a.get('name')}")
        p = a.get("path", "")
        if p:
            print(f"  FILE EXISTS: {os.path.isfile(p)}")
        print()
