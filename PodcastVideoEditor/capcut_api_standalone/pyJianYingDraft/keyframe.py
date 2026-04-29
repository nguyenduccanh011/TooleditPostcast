import uuid

from enum import Enum
from typing import Dict, List, Any

class Keyframe:
    """A single keyframe (control point), currently only supports linear interpolation"""

    kf_id: str
    """Global keyframe ID, auto-generated"""
    time_offset: int
    """Time offset relative to the material start point"""
    values: List[float]
    """Keyframe values, usually only one element"""

    def __init__(self, time_offset: int, value: float):
        """Initialize a keyframe with given time offset and value"""
        self.kf_id = uuid.uuid4().hex

        self.time_offset = time_offset
        self.values = [value]

    def export_json(self) -> Dict[str, Any]:
        return {
            # Default values
            "curveType": "Line",
            "graphID": "",
            "left_control": {"x": 0.0, "y": 0.0},
            "right_control": {"x": 0.0, "y": 0.0},
            # Custom properties
            "id": self.kf_id,
            "time_offset": self.time_offset,
            "values": self.values
        }

class Keyframe_property(Enum):
    """Property type controlled by keyframes"""

    position_x = "KFTypePositionX"
    """Positive = move right. Value = `displayed value in editor` / `draft width`, i.e. unit is half canvas width"""
    position_y = "KFTypePositionY"
    """Positive = move up. Value = `displayed value in editor` / `draft height`, i.e. unit is half canvas height"""
    rotation = "KFTypeRotation"
    """Clockwise rotation in **degrees**"""

    scale_x = "KFTypeScaleX"
    """X-axis scale ratio (1.0 = no scaling), mutually exclusive with `uniform_scale`"""
    scale_y = "KFTypeScaleY"
    """Y-axis scale ratio (1.0 = no scaling), mutually exclusive with `uniform_scale`"""
    uniform_scale = "UNIFORM_SCALE"
    """Uniform X+Y scale ratio (1.0 = no scaling), mutually exclusive with `scale_x` and `scale_y`"""

    alpha = "KFTypeAlpha"
    """Opacity, 1.0 = fully opaque, only for `Video_segment`"""
    saturation = "KFTypeSaturation"
    """Saturation, 0.0 = original, range -1.0 to 1.0, only for `Video_segment`"""
    contrast = "KFTypeContrast"
    """Contrast, 0.0 = original, range -1.0 to 1.0, only for `Video_segment`"""
    brightness = "KFTypeBrightness"
    """Brightness, 0.0 = original, range -1.0 to 1.0, only for `Video_segment`"""

    volume = "KFTypeVolume"
    """Volume, 1.0 = original volume, only for `Audio_segment` and `Video_segment`"""

class Keyframe_list:
    """Keyframe list, records a series of keyframes related to a specific property"""

    list_id: str
    """Global keyframe list ID, auto-generated"""
    keyframe_property: Keyframe_property
    """Property associated with this keyframe list"""
    keyframes: List[Keyframe]
    """List of keyframes"""

    def __init__(self, keyframe_property: Keyframe_property):
        """Initialize a keyframe list for the given property"""
        self.list_id = uuid.uuid4().hex

        self.keyframe_property = keyframe_property
        self.keyframes = []

    def add_keyframe(self, time_offset: int, value: float):
        """Add a keyframe with given time offset and value to this list"""
        keyframe = Keyframe(time_offset, value)
        self.keyframes.append(keyframe)
        self.keyframes.sort(key=lambda x: x.time_offset)

    def export_json(self) -> Dict[str, Any]:
        return {
            "id": self.list_id,
            "keyframe_list": [kf.export_json() for kf in self.keyframes],
            "material_id": "",
            "property_type": self.keyframe_property.value
        }
