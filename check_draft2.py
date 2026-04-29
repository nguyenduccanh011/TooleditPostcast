import json, os

draft_dir = r"C:\Users\DUC CANH PC\AppData\Local\CapCut\User Data\Projects\com.lveditor.draft"
draft_id = "dfd_cat_1776506421_a970e01e"

path = os.path.join(draft_dir, draft_id, "draft_info.json")
d = json.load(open(path, "r", encoding="utf-8"))

print("=== AUDIO MATERIALS ===")
for a in d.get("materials", {}).get("audios", []):
    for k in ["path", "name", "material_name", "duration", "remote_url", "source_from"]:
        print(f"  {k}: {a.get(k)}")
    p = a.get("path", "")
    if p:
        print(f"  FILE EXISTS at path: {os.path.isfile(p)}")
    # Check assets folder
    audio_dir = os.path.join(draft_dir, draft_id, "assets", "audio")
    if os.path.isdir(audio_dir):
        print(f"  Files in assets/audio: {os.listdir(audio_dir)}")
    print()

# Also dump all keys of first audio material
print("=== ALL KEYS OF FIRST AUDIO ===")
audios = d.get("materials", {}).get("audios", [])
if audios:
    for k, v in audios[0].items():
        if isinstance(v, str) and len(v) > 200:
            v = v[:200] + "..."
        print(f"  {k}: {v}")
