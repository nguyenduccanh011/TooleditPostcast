import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "PodcastVideoEditor", "capcut_api_standalone"))
os.chdir(os.path.join(os.path.dirname(__file__), "PodcastVideoEditor", "capcut_api_standalone"))

from create_draft import create_draft
from add_audio_track import add_audio_track
from save_draft_impl import save_draft_impl

CAPCUT_DRAFT_FOLDER = r"C:\Users\DUC CANH PC\AppData\Local\CapCut\User Data\Projects\com.lveditor.draft"

# 1. Create draft
script, draft_id = create_draft(1080, 1920)
print(f"✅ Draft created: {draft_id}")

# 2. Add audio
result = add_audio_track(
    audio_url="https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3",
    draft_id=draft_id,
    duration=10.0,
    volume=0.8,
    width=1080,
    height=1920
)
print(f"✅ Audio added: {result}")

# 3. Save draft to CapCut folder
print(f"Saving draft to: {CAPCUT_DRAFT_FOLDER}")
save_result = save_draft_impl(draft_id, CAPCUT_DRAFT_FOLDER)
print(f"✅ Save result: {save_result}")
print(f"\n🎉 Done! Open CapCut and you should see the new draft.")
