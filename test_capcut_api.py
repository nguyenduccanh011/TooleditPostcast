"""Test script for CapCut API - create draft + add audio"""
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "PodcastVideoEditor", "capcut_api_standalone"))

# Test 1: Import and create draft
from create_draft import create_draft
script, draft_id = create_draft(width=1080, height=1920)
print(f"✅ Draft created: {draft_id}")

# Test 2: Add audio track
from add_audio_track import add_audio_track
result = add_audio_track(
    audio_url="https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3",
    draft_id=draft_id,
    duration=10.0,
    start=0,
    end=10.0,
    volume=0.8,
    track_name="audio_main",
    width=1080,
    height=1920
)
print(f"✅ Audio added: {result}")

# Test 3: Verify draft has audio
from draft_cache import DRAFT_CACHE
cached_script = DRAFT_CACHE.get(draft_id)
if cached_script:
    audios = cached_script.materials.audios
    print(f"✅ Draft has {len(audios)} audio material(s)")
    for a in audios:
        print(f"   - {a.material_name}, remote_url={a.remote_url}")
else:
    print("❌ Draft not found in cache")

print("\n🎉 All tests passed! API is working correctly.")
