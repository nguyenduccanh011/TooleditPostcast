"""Define audio segment class and related classes

Includes fade effect, audio effects, and related classes
"""

import uuid
from copy import deepcopy

from typing import Optional, Literal, Union
from typing import Dict, List, Any

from pyJianYingDraft.metadata.capcut_audio_effect_meta import CapCut_Speech_to_song_effect_type, CapCut_Voice_characters_effect_type, CapCut_Voice_filters_effect_type

from .time_util import tim, Timerange
from .segment import Media_segment
from .local_materials import Audio_material
from .keyframe import Keyframe_property, Keyframe_list

from .metadata import Effect_param_instance
from .metadata import Audio_scene_effect_type, Tone_effect_type, Speech_to_song_type

class Audio_fade:
    """Audio fade in/out effect"""

    fade_id: str
    """Global ID for fade effect, auto-generated"""

    in_duration: int
    """Fade-in duration, in microseconds"""
    out_duration: int
    """Fade-out duration, in microseconds"""

    def __init__(self, in_duration: int, out_duration: int):
        """Construct fade effect with given fade-in/out durations"""

        self.fade_id = uuid.uuid4().hex
        self.in_duration = in_duration
        self.out_duration = out_duration

    def export_json(self) -> Dict[str, Any]:
        return {
            "id": self.fade_id,
            "fade_in_duration": self.in_duration,
            "fade_out_duration": self.out_duration,
            "fade_type": 0,
            "type": "audio_fade"
        }

class Audio_effect:
    """Audio effect object"""

    name: str
    """Effect name"""
    effect_id: str
    """Effect global ID, auto-generated"""
    resource_id: str
    """Resource ID, provided by CapCut"""

    category_id: Literal["sound_effect", "tone", "speech_to_song"]
    category_name: Literal["Scene sound", "Tone", "Speech to song"]

    audio_adjust_params: List[Effect_param_instance]

    def __init__(self, effect_meta: Union[Audio_scene_effect_type, Tone_effect_type, Speech_to_song_type, CapCut_Voice_filters_effect_type, CapCut_Voice_characters_effect_type, CapCut_Speech_to_song_effect_type],
                 params: Optional[List[Optional[float]]] = None):
        """Construct audio effect with given metadata and param list, param range 0~100"""

        self.name = effect_meta.value.name
        self.effect_id = uuid.uuid4().hex
        self.resource_id = effect_meta.value.resource_id
        self.audio_adjust_params = []

        if isinstance(effect_meta, Audio_scene_effect_type):
            self.category_id = "sound_effect"
            self.category_name = "Scene sound"
        elif isinstance(effect_meta, Tone_effect_type):
            self.category_id = "tone"
            self.category_name = "Tone"
        elif isinstance(effect_meta, Speech_to_song_type):
            self.category_id = "speech_to_song"
            self.category_name = "Speech to song"
        elif isinstance(effect_meta, CapCut_Voice_filters_effect_type):
            self.category_id = "sound_effect"
            self.category_name = "Voice filters"
        elif isinstance(effect_meta, CapCut_Voice_characters_effect_type):
            self.category_id = "tone"
            self.category_name = "Voice characters"
        elif isinstance(effect_meta, CapCut_Speech_to_song_effect_type):
            self.category_id = "speech_to_song"
            self.category_name = "Speech to song"
        else:
            raise TypeError("Unsupported metadata type %s" % type(effect_meta))

        self.audio_adjust_params = effect_meta.value.parse_params(params)

    def export_json(self) -> Dict[str, Any]:
        return {
            "audio_adjust_params": [param.export_json() for param in self.audio_adjust_params],
            "category_id": self.category_id,
            "category_name": self.category_name,
            "id": self.effect_id,
            "is_ugc": False,
            "name": self.name,
            "production_path": "",
            "resource_id": self.resource_id,
            "speaker_id": "",
            "sub_type": 1,
            "time_range": {"duration": 0, "start": 0},  # Seems unused
            "type": "audio_effect"
            # Do not export path and constant_material_id
        }

class Audio_segment(Media_segment):
    """An audio segment placed on a track"""

    material_instance: Audio_material
    """Audio material instance"""

    fade: Optional[Audio_fade]
    """Audio fade in/out effect, may be None

    Automatically added to the material list when placed on a track
    """

    effects: List[Audio_effect]
    """Audio effects list

    Automatically added to the material list when placed on a track
    """

    def __init__(self, material: Audio_material, target_timerange: Timerange, *,
                 source_timerange: Optional[Timerange] = None, speed: Optional[float] = None, volume: float = 1.0):
        """Construct a track segment using the given audio material, specifying time info and playback speed/volume

        Args:
            material (`Audio_material`): Material instance
            target_timerange (`Timerange`): Target time range of the segment on the track
            source_timerange (`Timerange`, optional): Time range of the source material clip, defaults to clipping from the start based on `speed` to match `target_timerange` duration
            speed (`float`, optional): Playback speed, defaults to 1.0. When specified together with `source_timerange`, overrides the duration in `target_timerange`
            volume (`float`, optional): Volume, defaults to 1.0

        Raises:
            `ValueError`: The specified or calculated `source_timerange` exceeds the material duration
        """
        if source_timerange is not None and speed is not None:
            target_timerange = Timerange(target_timerange.start, round(source_timerange.duration / speed))
        elif source_timerange is not None and speed is None:
            speed = source_timerange.duration / target_timerange.duration
        else:  # source_timerange is None
            speed = speed if speed is not None else 1.0
            source_timerange = Timerange(0, round(target_timerange.duration * speed))

        # if source_timerange.end > material.duration:
        #     raise ValueError(f"Source timerange {source_timerange} exceeds material duration ({material.duration})")

        super().__init__(material.material_id, source_timerange, target_timerange, speed, volume)

        self.material_instance = deepcopy(material)
        self.fade = None
        self.effects = []

    def add_effect(self, effect_type: Union[Audio_scene_effect_type, Tone_effect_type, Speech_to_song_type, CapCut_Voice_filters_effect_type, CapCut_Voice_characters_effect_type, CapCut_Speech_to_song_effect_type],
                   params: Optional[List[Optional[float]]] = None,
                   effect_id: Optional[str] = None) -> "Audio_segment":
        """Add an audio effect that applies to the entire segment. Note: "Speech to song" effect cannot be auto-recognized by JianYing

        Args:
            effect_type (`Audio_scene_effect_type` | `Tone_effect_type` | `Speech_to_song_type`): Effect type, only one effect per category can be added.
            params (`List[Optional[float]]`, optional): Effect parameter list, items not provided or None use default values.
                Parameter range (0~100) is consistent with JianYing/CapCut. Refer to enum member annotations for available parameters and their order.
            effect_id (`str`, optional): Effect ID, auto-generated if not provided.

        Raises:
            `ValueError`: Attempting to add a duplicate effect category, too many parameters, or parameter values out of range.
        """
        if params is not None and len(params) > len(effect_type.value.params):
            raise ValueError("Too many parameters provided for audio effect %s" % effect_type.value.name)
        self.material_instance.has_audio_effect = True  # Add this line
        effect_inst = Audio_effect(effect_type, params)
        if effect_id is not None:
            effect_inst.effect_id = effect_id
        if effect_inst.category_id in [eff.category_id for eff in self.effects]:
            raise ValueError("This audio segment already has an effect of type (%s)" % effect_inst.category_name)
        self.effects.append(effect_inst)
        self.extra_material_refs.append(effect_inst.effect_id)

        return self

    def add_fade(self, in_duration: Union[str, int], out_duration: Union[str, int]) -> "Audio_segment":
        """Add fade in/out effect to the audio segment

        Args:
            in_duration (`int` or `str`): Fade-in duration in microseconds, if string will be parsed by `tim()`
            out_duration (`int` or `str`): Fade-out duration in microseconds, if string will be parsed by `tim()`

        Raises:
            `ValueError`: Fade effect already exists on this segment
        """
        if self.fade is not None:
            raise ValueError("Fade effect already exists on this segment")

        if isinstance(in_duration, str): in_duration = tim(in_duration)
        if isinstance(out_duration, str): out_duration = tim(out_duration)

        self.fade = Audio_fade(in_duration, out_duration)
        self.extra_material_refs.append(self.fade.fade_id)

        return self

    def add_keyframe(self, time_offset: int, volume: float) -> "Audio_segment":
        """Create a *volume control* keyframe for the audio segment, automatically added to the keyframe list

        Args:
            time_offset (`int`): Keyframe time offset in microseconds
            volume (`float`): Volume value at `time_offset`
        """
        _property = Keyframe_property.volume
        for kf_list in self.common_keyframes:
            if kf_list.keyframe_property == _property:
                kf_list.add_keyframe(time_offset, volume)
                return self
        kf_list = Keyframe_list(_property)
        kf_list.add_keyframe(time_offset, volume)
        self.common_keyframes.append(kf_list)
        return self

    def export_json(self) -> Dict[str, Any]:
        json_dict = super().export_json()
        json_dict.update({
            "clip": None,
            "hdr_settings": None
        })
        return json_dict
