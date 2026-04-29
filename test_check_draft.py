import json

with open('f:/PROJECTS/TooleditPostcast/PodcastVideoEditor/capcut_api_standalone/dfd_cat_1776490655_71a6c5de/draft_info.json','r') as f:
    d = json.load(f)

tracks = d.get('tracks',[])
for t in tracks:
    segs = t.get('segments',[])
    ttype = t.get('type')
    tname = t.get('name','?')
    print(f'Track type={ttype}, name={tname}, segments={len(segs)}')
    for s in segs:
        tr = s.get('target_timerange',{})
        print(f'  segment: start={tr.get("start")}, duration={tr.get("duration")}')

materials = d.get('materials',{}).get('videos',[])
print(f'Video materials count: {len(materials)}')
for m in materials:
    mtype = m.get('type')
    mname = m.get('material_name')
    murl = m.get('remote_url','')[:60]
    print(f'  type={mtype}, name={mname}, url={murl}')
