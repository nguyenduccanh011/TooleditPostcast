"""Pass 3: translate remaining Chinese phrases."""
import re, os

TRANSLATIONS = {
    # script_file.py remaining
    "一致": "consistent",
    "截取": "extract/crop",
    
    # video_segment.py remaining
    "函数进行解析": "function to parse",
    "再单独定义": "define separately again",
    "传入了过多": "passed in too many",
    "作用于整个": "applied to the entire",
    "圆形直径": "circle diameter",
    "只适用于": "only applies to",
    "可视部分": "visible part",
    "比例表示": "proportion to represent",
    "及具体": "and specific",
    "若传入": "if passed in",
    "会调用": "will call",
    "心点": "center point",
    "像素": "pixels",
    "给定": "given",
    "坐标": "coordinate",
    "分别": "respectively",
    "效果": "effect",
    "四档": "four levels",
    "表示": "represents",
    "单位": "unit",
    "爱心": "heart",
    "程度": "degree",
    "根据": "based on",
    "定义": "define",
    "无效": "invalid",
    "安放": "place",
    "试图": "attempt to",
    "允许": "allow",
    "生效": "take effect",
    "尺寸": "size",
    "矩形": "rectangle",
    "转化": "convert",
    "底层": "bottom layer",
    "极简": "minimal",
    "构建": "construct",
    "镜面": "mirror",
    "格式": "format",
    "能再": "can again",
    "理论": "theoretically",
    "前面": "preceding",
    "角度": "angle",
    "已有": "existing",
    
    # template_mode.py remaining
    "也依次前移相应": "also shift forward accordingly",
    "保留所有原始": "preserve all original",
    "依次后移后续": "shift subsequent ones backward in order",
    "即尝试前移": "i.e. try to shift forward",
    "即尝试后移": "i.e. try to shift backward",
    "后移后续": "shift subsequent backward",
    "且可修改": "and can modify",
    "保持原样": "keep as is",
    "延伸头部": "extend head",
    "允许修改": "allow modification",
    "保持间隙": "maintain gap",
    "尝试过": "tried",
    "用原始": "use original",
    "终止点": "end point",
    "若有必": "if necessary",
    "起始点": "start point",
    "延长": "extend",
    "变长": "becomes longer",
    "实例": "instance",
    "重合": "coincide",
    "取用": "use/take",
    "其他": "other",
    "原始": "original",
    "是否": "whether",
    "延伸": "extend",
    "有必": "necessary",
    "用于": "used for",
    "匹配": "match",
    "所有": "all",
    "每个": "each",
    "映射": "map",
    "写入": "write",
    "后续": "subsequent",
    "变短": "becomes shorter",
    "起始": "start",
    "变更": "change",
    "包含": "contains",
    "前续": "preceding",
    "数据": "data",
    "未能": "failed to",
}

def translate_file(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    sorted_items = sorted(TRANSLATIONS.items(), key=lambda x: len(x[0]), reverse=True)
    for cn, en in sorted_items:
        content = content.replace(cn, en)
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    remaining = re.findall(r'[\u4e00-\u9fff]+', content)
    return remaining

base = os.path.dirname(os.path.abspath(__file__))
for fname in ['script_file.py', 'video_segment.py', 'template_mode.py']:
    fpath = os.path.join(base, fname)
    if not os.path.exists(fpath):
        print(f"SKIP: {fname}")
        continue
    remaining = translate_file(fpath)
    if remaining:
        unique = sorted(set(remaining), key=lambda x: -len(x))
        print(f"\n{fname}: {len(remaining)} remaining, {len(unique)} unique:")
        for p in unique[:30]:
            print(f"  '{p}'")
    else:
        print(f"{fname}: ✓ CLEAN!")
