"""Define video segment class and related classes

Includes image adjustment settings, animation effects, effects, transitions, etc.
"""

import uuid
from copy import deepcopy

from typing import Optional, Literal, Union, overload
from typing import Dict, List, Tuple, Any

from pyJianYingDraft.metadata.capcut_effect_meta import CapCut_Video_character_effect_type, CapCut_Video_scene_effect_type
from pyJianYingDraft.metadata.capcut_mask_meta import CapCut_Mask_type
from settings import IS_CAPCUT_ENV

from .time_util import tim, Timerange
from .segment import Visual_segment, Clip_settings
from .local_materials import Video_material
from .animation import Segment_animations, Video_animation

from .metadata import Effect_meta, Effect_param_instance
from .metadata import Mask_meta, Mask_type, Filter_type, Transition_type, CapCut_Transition_type
from .metadata import Intro_type, Outro_type, Group_animation_type
from .metadata import CapCut_Intro_type, CapCut_Outro_type, CapCut_Group_animation_type
from .metadata import Video_scene_effect_type, Video_character_effect_type


class Mask:
    """Mask object"""

    mask_meta: Mask_meta
    """Mask metadata"""
    global_id: str
    """Mask global ID, auto-generated"""

    center_x: float
    """Mask center X coordinate, in half material width units"""
    center_y: float
    """Mask center Y coordinate, in half material height units"""
    width: float
    height: float
    aspect_ratio: float

    rotation: float
    invert: bool
    feather: float
    """Feather level, 0-1"""
    round_corner: float
    """Rectangle mask corner radius, 0-1"""

    def __init__(self, mask_meta: Mask_meta,
                 cx: float, cy: float, w: float, h: float,
                 ratio: float, rot: float, inv: bool, feather: float, round_corner: float):
        self.mask_meta = mask_meta
        self.global_id = uuid.uuid4().hex

        self.center_x, self.center_y = cx, cy
        self.width, self.height = w, h
        self.aspect_ratio = ratio

        self.rotation = rot
        self.invert = inv
        self.feather = feather
        self.round_corner = round_corner

    def export_json(self) -> Dict[str, Any]:
        return {
            "config": {
                "aspectRatio": self.aspect_ratio,
                "centerX": self.center_x,
                "centerY": self.center_y,
                "feather": self.feather,
                "height": self.height,
                "invert": self.invert,
                "rotation": self.rotation,
                "roundCorner": self.round_corner,
                "width": self.width
            },
            "category": "video",
            "category_id": "",
            "category_name": "",
            "id": self.global_id,
            "name": self.mask_meta.name,
            "platform": "all",
            "position_info": "",
            "resource_type": self.mask_meta.resource_type,
            "resource_id": self.mask_meta.resource_id,
            "type": "mask"
            # Do not export the path field
        }

class Video_effect:
    """Video effect material"""

    name: str
    """Effect name"""
    global_id: str
    """Effect global ID, auto-generated"""
    effect_id: str
    """Effect type ID, provided by CapCut"""
    resource_id: str
    """Resource ID, provided by CapCut"""

    effect_type: Literal["video_effect", "face_effect"]
    apply_target_type: Literal[0, 2]
    """Apply target type, 0: segment, 2: global"""

    adjust_params: List[Effect_param_instance]

    def __init__(self, effect_meta: Union[Video_scene_effect_type, Video_character_effect_type],
                 params: Optional[List[Optional[float]]] = None, *,
                 apply_target_type: Literal[0, 2] = 0):
        """Construct a video effect object from the given effect metadata and parameter list, params range is 0~100"""

        self.name = effect_meta.value.name
        self.global_id = uuid.uuid4().hex
        self.effect_id = effect_meta.value.effect_id
        self.resource_id = effect_meta.value.resource_id
        self.adjust_params = []

        if IS_CAPCUT_ENV:
            if isinstance(effect_meta, CapCut_Video_scene_effect_type):
                self.effect_type = "video_effect"
            elif isinstance(effect_meta, CapCut_Video_character_effect_type):
                self.effect_type = "face_effect"
            else:
                raise TypeError("Invalid effect meta type %s" % type(effect_meta))
        else:
            if isinstance(effect_meta, Video_scene_effect_type):
                self.effect_type = "video_effect"
            elif isinstance(effect_meta, Video_character_effect_type):
                self.effect_type = "face_effect"
            else:
                raise TypeError("Invalid effect meta type %s" % type(effect_meta))

        self.apply_target_type = apply_target_type

        self.adjust_params = effect_meta.value.parse_params(params)

    def export_json(self) -> Dict[str, Any]:
        return {
            "adjust_params": [param.export_json() for param in self.adjust_params],
            "apply_target_type": self.apply_target_type,
            "apply_time_range": None,
            "category_id": "",  # Always set to empty
            "category_name": "",  # Always set to empty
            "common_keyframes": [],
            "disable_effect_faces": [],
            "effect_id": self.effect_id,
            "formula_id": "",
            "id": self.global_id,
            "name": self.name,
            "platform": "all",
            "render_index": 11000,
            "resource_id": self.resource_id,
            "source_platform": 0,
            "time_range": None,
            "track_render_index": 0,
            "type": self.effect_type,
            "value": 1.0,
            "version": ""
            # Do not export path, request_id, and algorithm_artifact_path fields
        }

class Filter:
    """Filter material"""

    global_id: str
    """Filter global ID, auto-generated"""

    effect_meta: Effect_meta
    """Filter metadata"""
    intensity: float
    """Filter intensity (the only parameter for filters)"""

    apply_target_type: Literal[0, 2]
    """Apply target type, 0: segment, 2: global"""

    def __init__(self, meta: Effect_meta, intensity: float, *,
                 apply_target_type: Literal[0, 2] = 0):
        """Construct a filter material object from the given filter metadata and intensity"""

        self.global_id = uuid.uuid4().hex
        self.effect_meta = meta
        self.intensity = intensity
        self.apply_target_type = apply_target_type

    def export_json(self) -> Dict[str, Any]:
        return {
            "adjust_params": [],
            "algorithm_artifact_path": "",
            "apply_target_type": self.apply_target_type,
            "bloom_params": None,
            "category_id": "",  # Always set to empty
            "category_name": "",  # Always set to empty
            "color_match_info": {
                "source_feature_path": "",
                "target_feature_path": "",
                "target_image_path": ""
            },
            "effect_id": self.effect_meta.effect_id,
            "enable_skin_tone_correction": False,
            "exclusion_group": [],
            "face_adjust_params": [],
            "formula_id": "",
            "id": self.global_id,
            "intensity_key": "",
            "multi_language_current": "",
            "name": self.effect_meta.name,
            "panel_id": "",
            "platform": "all",
            "resource_id": self.effect_meta.resource_id,
            "source_platform": 1,
            "sub_type": "none",
            "time_range": None,
            "type": "filter",
            "value": self.intensity,
            "version": ""
            # Do not export path and request_id
        }

class Transition:
    """Transition object"""

    name: str
    """Transition name"""
    global_id: str
    """Transition global ID, auto-generated"""
    effect_id: str
    """Transition effect ID, provided by CapCut"""
    resource_id: str
    """Resource ID, provided by CapCut"""

    duration: int
    """Transition duration, in microseconds"""
    is_overlap: bool
    """Whether it overlaps with the previous segment (?)"""

    def __init__(self, effect_meta: Union[Transition_type, CapCut_Transition_type], duration: Optional[int] = None):
        """Construct a transition object from the given transition metadata and duration"""
        self.name = effect_meta.value.name
        self.global_id = uuid.uuid4().hex
        self.effect_id = effect_meta.value.effect_id
        self.resource_id = effect_meta.value.resource_id

        self.duration = duration if duration is not None else effect_meta.value.default_duration
        self.is_overlap = effect_meta.value.is_overlap

    def export_json(self) -> Dict[str, Any]:
        return {
            "category_id": "",  # Always set to empty
            "category_name": "",  # Always set to empty
            "duration": self.duration,
            "effect_id": self.effect_id,
            "id": self.global_id,
            "is_overlap": self.is_overlap,
            "name": self.name,
            "platform": "all",
            "resource_id": self.resource_id,
            "type": "transition"
            # Do not export path and request_id fields
        }

class BackgroundFilling:
    """Background filling object"""

    global_id: str
    """Background filling global ID, auto-generated"""
    fill_type: Literal["canvas_blur", "canvas_color"]
    """Background filling type"""
    blur: float
    """Blur level, 0-1"""
    color: str
    """Background color, format '#RRGGBBAA'"""

    def __init__(self, fill_type: Literal["canvas_blur", "canvas_color"], blur: float, color: str):
        self.global_id = uuid.uuid4().hex
        self.fill_type = fill_type
        self.blur = blur
        self.color = color

    def export_json(self) -> Dict[str, Any]:
        return {
            "id": self.global_id,
            "type": self.fill_type,
            "blur": self.blur,
            "color": self.color,
            "source_platform": 0,
        }

class Video_segment(Visual_segment):
    """A video/image segment placed on a track"""

    material_instance: Video_material
    """Material instance"""
    material_size: Tuple[int, int]
    """Material dimensions"""

    effects: List[Video_effect]
    """Effect list

    Automatically added to the material list when placed on a track
    """
    filters: List[Filter]
    """Filter list

    Automatically added to the material list when placed on a track
    """
    mask: Optional[Mask]
    """Mask instance, may be None

    Automatically added to the material list when placed on a track
    """
    transition: Optional[Transition]
    """Transition instance, may be None

    Automatically added to the material list when placed on a track
    """
    background_filling: Optional[BackgroundFilling]
    """Background filling instance, may be None

    Automatically added to the material list when placed on a track
    """

    visible: Optional[bool]
    """Whether visible
    Defaults to True
    """

    # TODO: Accept path for material parameter for convenient construction
    def __init__(self, material: Video_material, target_timerange: Timerange, *,
                 source_timerange: Optional[Timerange] = None, speed: Optional[float] = None, volume: float = 1.0,
                 clip_settings: Optional[Clip_settings] = None):
        """Build a track segment using the given video/image material, specifying its time info and image adjustment settings

        Args:
            material (`Video_material`): Material instance
            target_timerange (`Timerange`): Target time range of the segment on the track
            source_timerange (`Timerange`, optional): Source clip time range, by default clips from the beginning based on `speed` to match `target_timerange` duration
            speed (`float`, optional): Playback speed, defaults to 1.0. When specified together with`source_timerange`it overrides the duration in`target_timerange`
            volume (`float`, optional): Volume, defaults to 1.0
            clip_settings (`Clip_settings`, optional): Image adjustment settings, defaults to no transformation

        Raises:
            `ValueError`: The specified or calculated`source_timerange`exceeds the material duration range
        """
        # if source_timerange is not None and speed is not None:
        #     target_timerange = Timerange(target_timerange.start, round(source_timerange.duration / speed))
        # elif source_timerange is not None and speed is None:
        #     speed = source_timerange.duration / target_timerange.duration
        # else:  # source_timerange is None
        #     speed = speed if speed is not None else 1.0
        #     source_timerange = Timerange(0, round(target_timerange.duration * speed))

        # if source_timerange.end > material.duration:
        #     source_timerange = Timerange(source_timerange.start, material.duration - source_timerange.start)
        #     # Recalculate target time range
        #     target_timerange = Timerange(target_timerange.start, round(source_timerange.duration / speed))

        super().__init__(material.material_id, source_timerange, target_timerange, speed, volume, clip_settings=clip_settings)

        self.material_instance = deepcopy(material)
        self.material_size = (material.width, material.height)
        self.effects = []
        self.filters = []
        self.transition = None
        self.mask = None
        self.background_filling = None

    def add_animation(self, animation_type: Union[Intro_type, Outro_type, Group_animation_type, CapCut_Intro_type, CapCut_Outro_type, CapCut_Group_animation_type],
                      duration: Optional[Union[int, str]] = None) -> "Video_segment":
        """Add the given intro/outro/combo animation to this segment's animation list.

        Args:
            animation_type (`Intro_type`, `Outro_type`, or `Group_animation_type`): Animation type
            duration (`int` or `str`, optional): Animation duration in microseconds. If a string is passed, it will be parsed via `tim()`.
                If not specified, the default value defined by the animation type will be used. Only applies to intro and outro animations.
        """
        if duration is not None:
            duration = tim(duration)
        if (isinstance(animation_type, Intro_type) or isinstance(animation_type, CapCut_Intro_type)):
            start = 0
            duration = duration or animation_type.value.duration
        elif isinstance(animation_type, Outro_type) or isinstance(animation_type, CapCut_Outro_type):
            duration = duration or animation_type.value.duration
            start = self.target_timerange.duration - duration
        elif isinstance(animation_type, Group_animation_type) or isinstance(animation_type, CapCut_Group_animation_type):
            start = 0
            duration = duration or self.target_timerange.duration
        else:
            raise TypeError("Invalid animation type %s" % type(animation_type))

        if self.animations_instance is None:
            self.animations_instance = Segment_animations()
            self.extra_material_refs.append(self.animations_instance.animation_id)

        self.animations_instance.add_animation(Video_animation(animation_type, start, duration))

        return self

    def add_effect(self, effect_type: Union[Video_scene_effect_type, Video_character_effect_type],
                   params: Optional[List[Optional[float]]] = None) -> "Video_segment":
        """Add an effect applied to the entire video segment.

        Args:
            effect_type (`Video_scene_effect_type` or `Video_character_effect_type`): Effect type
            params (`List[Optional[float]]`, optional): Effect parameter list. Parameters not provided or None will use default values.
                Parameter value range (0~100) is consistent with JianYing. See enum member annotations for specific parameters and their order.

        Raises:
            `ValueError`: Too many parameters provided, or parameter value out of range.
        """
        if params is not None and len(params) > len(effect_type.value.params):
            raise ValueError("Too many parameters for effect %s" % effect_type.value.name)

        effect_inst = Video_effect(effect_type, params)
        self.effects.append(effect_inst)
        self.extra_material_refs.append(effect_inst.global_id)

        return self

    def add_filter(self, filter_type: Filter_type, intensity: float = 100.0) -> "Video_segment":
        """Add a filter to the video segment.

        Args:
            filter_type (`Filter_type`): Filter type
            intensity (`float`, optional): Filter intensity (0-100), only effective when the selected filter supports intensity adjustment. Default is 100.
        """
        filter_inst = Filter(filter_type.value, intensity / 100.0)  # convert to 0~1 range
        self.filters.append(filter_inst)
        self.extra_material_refs.append(filter_inst.global_id)

        return self

    def add_mask(self, draft: "Script_file", mask_type: Union[Mask_type, CapCut_Mask_type], *, center_x: float = 0.0, center_y: float = 0.0, size: float = 0.5,
                 rotation: float = 0.0, feather: float = 0.0, invert: bool = False,
                 rect_width: Optional[float] = None, round_corner: Optional[float] = None) -> "Video_segment":
        """Add a mask to the video segment.

        Args:
            mask_type (`Mask_type`): Mask type
            center_x (`float`, optional): Mask center X coordinate (in material pixels). Default is at material center.
            center_y (`float`, optional): Mask center Y coordinate (in material pixels). Default is at material center.
            size (`float`, optional): Main mask size (mirror visible height / circle diameter / heart height, etc.), as proportion of material height. Default is 0.5.
            rotation (`float`, optional): Clockwise rotation angle. Default is no rotation.
            feather (`float`, optional): Mask feather parameter, range 0~100. Default is no feather.
            invert (`bool`, optional): Whether to invert mask, default not inverted
            rect_width (`float`, optional): Rectangle mask width, only allowed when mask type is rectangle, as proportion of material width. Default same as `size`.
            round_corner (`float`, optional): Rectangle mask round corner, only allowed when mask type is rectangle, range 0~100. Default is 0.

        Raises:
            `ValueError`: Attempting to add multiple masks or incorrectly setting `rect_width` and `round_corner`.
        """

        if self.mask is not None:
            raise ValueError("This segment already has a mask, cannot add another one")
        is_rectangle_mask = getattr(mask_type.value, "resource_type", "") == "rectangle"
        if (rect_width is not None or round_corner is not None) and not is_rectangle_mask:
            raise ValueError("`rect_width` and `round_corner` are only allowed when mask type is rectangle")
        if rect_width is None and is_rectangle_mask:
            rect_width = size
        if round_corner is None:
            round_corner = 0

        # Get draft width/height instead of using material width/height
        draft_width = draft.width
        draft_height = draft.height
        
        width = rect_width or size * draft_height * mask_type.value.default_aspect_ratio / draft_width
        self.mask = Mask(mask_type.value, center_x / (draft_width / 2), center_y / (draft_height / 2),
                         w=width, h=size, ratio=mask_type.value.default_aspect_ratio,
                         rot=rotation, inv=invert, feather=feather/100, round_corner=round_corner/100)
        self.extra_material_refs.append(self.mask.global_id)
        return self

    def add_transition(self, transition_type: Union[Transition_type, CapCut_Transition_type], *, duration: Optional[Union[int, str]] = None) -> "Video_segment":
        """Add a transition to the video segment. Note: the transition should be added to the **preceding** segment.

        Args:
            transition_type (`Transition_type` or `CapCut_Transition_type`): Transition type
            duration (`int` or `str`, optional): Transition duration in microseconds. If a string is passed, it will be parsed via `tim()`. If not specified, the default value defined by the transition type will be used.

        Raises:
            `ValueError`: Attempting to add multiple transitions.
        """
        if self.transition is not None:
            raise ValueError("This segment already has a transition, cannot add another one")
        if isinstance(duration, str): duration = tim(duration)

        self.transition = Transition(transition_type, duration)
        self.extra_material_refs.append(self.transition.global_id)
        return self

    def add_background_filling(self, fill_type: Literal["blur", "color"], blur: float = 0.0625, color: str = "#00000000") -> "Video_segment":
        """Add background filling to the video segment.

        Note: Background filling only takes effect on segments in the bottom layer video track.

        Args:
            fill_type (`blur` or `color`): Fill type. `blur` for blur, `color` for solid color.
            blur (`float`, optional): Blur degree, 0.0-1.0. Only effective when `fill_type` is `blur`. JianYing has four blur levels: 0.0625, 0.375, 0.75 and 1.0. Default is 0.0625.
            color (`str`, optional): Fill color in '#RRGGBBAA' format. Only effective when `fill_type` is `color`.

        Raises:
            `ValueError`: Segment already has background filling, or `fill_type` is invalid.
        """
        if self.background_filling is not None:
            raise ValueError("This segment already has a background filling effect")

        if fill_type == "blur":
            self.background_filling = BackgroundFilling("canvas_blur", blur, color)
        elif fill_type == "color":
            self.background_filling = BackgroundFilling("canvas_color", blur, color)
        else:
            raise ValueError(f"Invalid background filling type: {fill_type}")

        self.extra_material_refs.append(self.background_filling.global_id)
        return self

    def export_json(self) -> Dict[str, Any]:
        json_dict = super().export_json()
        json_dict.update({
            "hdr_settings": {"intensity": 1.0, "mode": 1, "nits": 1000},
        })
        return json_dict

class Sticker_segment(Visual_segment):
    """A sticker segment placed on a track."""

    resource_id: str
    """Sticker resource ID"""

    def __init__(self, resource_id: str, target_timerange: Timerange, *, clip_settings: Optional[Clip_settings] = None):
        """Construct a sticker segment based on a sticker resource_id, specifying its time info and clip settings.

        After creation, use `Script_file.add_segment` to add it to a track.

        Args:
            resource_id (`str`): Sticker resource_id, obtainable via `Script_file.inspect_material` from a template.
            target_timerange (`Timerange`): Target time range of the segment on the track
            clip_settings (`Clip_settings`, optional): Image adjustment settings, defaults to no transformation
        """
        super().__init__(uuid.uuid4().hex, None, target_timerange, 1.0, 1.0, clip_settings=clip_settings)
        self.resource_id = resource_id

    def export_material(self) -> Dict[str, Any]:
        """Create a minimal sticker material object. No separate sticker material class is needed."""
        return {
            "id": self.material_id,
            "resource_id": self.resource_id,
            "sticker_id": self.resource_id,
            "source_platform": 1,
            "type": "sticker",
        }
