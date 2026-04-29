import uuid
from typing import Dict

from create_draft import get_or_create_draft
from util import generate_draft_url
from pyJianYingDraft.template_mode import ImportedTrack


def _build_shape_material(shape_id: str, half_width: float, half_height: float, color_hex: str) -> Dict:
    return {
        "border_alpha": 1.0,
        "border_color": "#CCCCCC",
        "border_line_style": 0,
        "border_width": 4.0,
        "check_flag": 17,
        "color": "",
        "combo_info": {"text_templates": []},
        "constant_material_id": "",
        "custom_points": [
            -half_width, half_height,
            half_width, half_height,
            half_width, -half_height,
            -half_width, -half_height
        ],
        "custom_points_in": [0.0] * 8,
        "custom_points_out": [0.0] * 8,
        "endpoint_left_style": 0,
        "endpoint_right_style": 0,
        "fill_render_style": {
            "alpha": 1.0,
            "color": {
                "gradient": {
                    "alpha": [1.0, 1.0, 1.0],
                    "angle": 0.0,
                    "color": ["#CCCCCC", "#CCCCCC", "#CCCCCC"],
                    "mode": "all",
                    "percent": [0.0, 0.5, 1.0],
                    "style": "linear"
                },
                "render_type": "solid",
                "solid": {"alpha": 1.0, "color": color_hex},
                "texture": {
                    "alpha": 1.0,
                    "angle": 0.0,
                    "blend": "no",
                    "effect_id": "",
                    "fill": "tile",
                    "flip": [],
                    "path": "",
                    "play_speed": 1.0,
                    "range": 0,
                    "resource_id": "",
                    "scale": 1.0
                }
            }
        },
        "global_alpha": 1.0,
        "id": shape_id,
        "line_style": 0,
        "name": "rect_item",
        "roundness": 0.0,
        "shadow_alpha": 0.5,
        "shadow_angle": 270.0,
        "shadow_color": "#000000",
        "shadow_distance": 0.0,
        "shadow_expand": 0.0,
        "shadow_visible": False,
        "shape_type": 4,
        "source_platform": 0,
        "team_id": "",
        "type": "shape"
    }


def _build_sticker_segment(
    segment_id: str,
    shape_id: str,
    start_us: int,
    duration_us: int,
    transform_x: float,
    transform_y: float,
    render_index: int
) -> Dict:
    return {
        "clip": {
            "alpha": 1.0,
            "flip": {"horizontal": False, "vertical": False},
            "rotation": 0.0,
            "scale": {"x": 1.0, "y": 1.0},
            "transform": {"x": transform_x, "y": transform_y}
        },
        "common_keyframes": [],
        "enable_adjust": True,
        "enable_color_correct_adjust": False,
        "enable_color_curves": True,
        "enable_color_match_adjust": False,
        "enable_color_wheels": True,
        "enable_lut": True,
        "enable_smart_color_adjust": False,
        "extra_material_refs": [],
        "id": segment_id,
        "keyframe_refs": [],
        "last_nonzero_volume": 1.0,
        "material_id": shape_id,
        "reverse": False,
        "speed": 1.0,
        "source_timerange": None,
        "target_timerange": {"duration": duration_us, "start": start_us},
        "track_attribute": 0,
        "track_render_index": 0,
        "uniform_scale": {"on": True, "value": 1.0},
        "visible": True,
        "volume": 1.0,
        "render_index": render_index
    }


def add_shape_bar_impl(
    *,
    draft_id: str = None,
    start: float = 0.0,
    end: float = 1.0,
    width: int = 1080,
    height: int = 1920,
    bar_height_px: int = 100,
    transform_x: float = 0.0,
    transform_y: float = 0.0,
    track_name: str = "shape_bar",
    relative_index: int = 0,
    color: str = "#000000"
) -> Dict[str, str]:
    draft_id, script = get_or_create_draft(draft_id=draft_id, width=width, height=height)

    safe_width = max(1, int(width))
    safe_height = max(1, int(height))
    safe_bar_height = max(1, min(int(bar_height_px), safe_height))
    start_us = max(0, int(round(start * 1_000_000)))
    end_us = max(start_us + 1, int(round(end * 1_000_000)))
    duration_us = end_us - start_us

    shape_id = str(uuid.uuid4()).upper()
    segment_id = str(uuid.uuid4()).upper()
    track_id = str(uuid.uuid4()).upper()

    half_width = safe_width / 3.0
    half_height = safe_bar_height / 2.0
    render_index = int(relative_index)

    shape_material = _build_shape_material(shape_id, half_width, half_height, color)
    script.imported_materials.setdefault("shapes", []).append(shape_material)

    segment_data = _build_sticker_segment(
        segment_id=segment_id,
        shape_id=shape_id,
        start_us=start_us,
        duration_us=duration_us,
        transform_x=transform_x,
        transform_y=transform_y,
        render_index=render_index
    )
    track_data = {
        "attribute": 0,
        "flag": 0,
        "id": track_id,
        "is_default_name": False,
        "name": track_name,
        "segments": [segment_data],
        "type": "sticker"
    }
    script.imported_tracks.append(ImportedTrack(track_data))

    return {
        "draft_id": draft_id,
        "draft_url": generate_draft_url(draft_id)
    }

