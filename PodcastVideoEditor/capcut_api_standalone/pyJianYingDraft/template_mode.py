"""Classes and functions related to template mode"""

from enum import Enum
from copy import deepcopy

from . import util
from . import exceptions
from .time_util import Timerange
from .segment import Base_segment
from .track import Base_track, Track_type, Track
from .local_materials import Video_material, Audio_material
from .video_segment import Video_segment, Clip_settings
from .audio_segment import Audio_segment
from .keyframe import Keyframe_list, Keyframe_property, Keyframe
from .metadata import Audio_scene_effect_type, Tone_effect_type, Speech_to_song_type, Effect_param_instance

from typing import List, Dict, Any

class Shrink_mode(Enum):
    """Methods for handling shorter replacement material"""

    cut_head = "cut_head"
    """Trim head, move segment start point forward"""
    cut_tail = "cut_tail"
    """Trim tail, move segment end point backward"""

    cut_tail_align = "cut_tail_align"
    """Trim tail and eliminate gaps, move segment end point backward, subsequent segments shift forward accordingly"""

    shrink = "shrink"
    """Keep midpoint unchanged, both endpoints move toward center"""

class Extend_mode(Enum):
    """Methods for handling longer replacement material"""

    cut_material_tail = "cut_material_tail"
    """Trim material tail (overrides `source_timerange`), keeping segment at original length, this method always succeeds"""

    extend_head = "extend_head"
    """Extend head, i.e. try to shift the segment start point forward; fails if it overlaps with the preceding segment"""
    extend_tail = "extend_tail"
    """Extend tail, i.e. try to shift the segment end point backward; fails if it overlaps with the subsequent segment"""

    push_tail = "push_tail"
    """Extend tail, shifting subsequent segments backward if necessary; this method always succeeds"""

class ImportedSegment(Base_segment):
    """Imported segment"""

    raw_data: Dict[str, Any]
    """Original JSON data"""

    __DATA_ATTRS = ["material_id", "target_timerange"]
    def __init__(self, json_data: Dict[str, Any]):
        self.raw_data = deepcopy(json_data)

        util.assign_attr_with_json(self, self.__DATA_ATTRS, json_data)

    def export_json(self) -> Dict[str, Any]:
        json_data = deepcopy(self.raw_data)
        json_data.update(util.export_attr_to_json(self, self.__DATA_ATTRS))
        return json_data

class ImportedMediaSegment(ImportedSegment):
    """Imported video/audio segment"""

    source_timerange: Timerange
    """Material time range used by the segment"""

    __DATA_ATTRS = ["source_timerange"]
    def __init__(self, json_data: Dict[str, Any]):
        super().__init__(json_data)

        util.assign_attr_with_json(self, self.__DATA_ATTRS, json_data)

    def export_json(self) -> Dict[str, Any]:
        json_data = super().export_json()
        json_data.update(util.export_attr_to_json(self, self.__DATA_ATTRS))
        return json_data


class ImportedTrack(Base_track):
    """Imported track in template mode"""

    raw_data: Dict[str, Any]
    """Original track data"""

    def __init__(self, json_data: Dict[str, Any]):
        self.track_type = Track_type.from_name(json_data["type"])
        self.name = json_data["name"]
        self.track_id = json_data["id"]
        self.render_index = max([int(seg["render_index"]) for seg in json_data["segments"]], default=0)

        self.raw_data = deepcopy(json_data)

    def export_json(self) -> Dict[str, Any]:
        ret = deepcopy(self.raw_data)
        ret.update({
            "name": self.name,
            "id": self.track_id
        })
        return ret

class EditableTrack(ImportedTrack):
    """Imported and modifiable track in template mode (audio/video and text tracks)"""

    segments: List[ImportedSegment]
    """List of segments in this track"""

    def __len__(self):
        return len(self.segments)

    @property
    def start_time(self) -> int:
        """Track start time, in microseconds"""
        if len(self.segments) == 0:
            return 0
        return self.segments[0].target_timerange.start

    @property
    def end_time(self) -> int:
        """Track end time, in microseconds"""
        if len(self.segments) == 0:
            return 0
        return self.segments[-1].target_timerange.end

    def export_json(self) -> Dict[str, Any]:
        ret = super().export_json()
        # Write render_index for each segment
        segment_exports = [seg.export_json() for seg in self.segments]
        for seg in segment_exports:
            seg["render_index"] = self.render_index
        ret["segments"] = segment_exports
        return ret

class ImportedTextTrack(EditableTrack):
    """Imported text track in template mode"""

    def __init__(self, json_data: Dict[str, Any]):
        super().__init__(json_data)
        self.segments = [ImportedSegment(seg) for seg in json_data["segments"]]

class ImportedMediaTrack(EditableTrack):
    """Imported audio/video track in template mode"""

    segments: List[ImportedMediaSegment]
    """List of segments in this track"""

    def __init__(self, json_data: Dict[str, Any]):
        super().__init__(json_data)
        self.segments = [ImportedMediaSegment(seg) for seg in json_data["segments"]]

    def check_material_type(self, material: object) -> bool:
        """Check if material type matches the track type"""
        if self.track_type == Track_type.video and isinstance(material, Video_material):
            return True
        if self.track_type == Track_type.audio and isinstance(material, Audio_material):
            return True
        return False

    def process_timerange(self, seg_index: int, src_timerange: Timerange,
                          shrink: Shrink_mode, extend: List[Extend_mode]) -> None:
        """Process time range changes from material replacement"""
        seg = self.segments[seg_index]
        new_duration = src_timerange.duration

        # Duration becomes shorter
        delta_duration = abs(new_duration - seg.duration)
        if new_duration < seg.duration:
            if shrink == Shrink_mode.cut_head:
                seg.start += delta_duration
            elif shrink == Shrink_mode.cut_tail:
                seg.duration -= delta_duration
            elif shrink == Shrink_mode.cut_tail_align:
                seg.duration -= delta_duration
                for i in range(seg_index+1, len(self.segments)):  # Subsequent segments also shift forward accordingly (maintain gap)
                    self.segments[i].start -= delta_duration
            elif shrink == Shrink_mode.shrink:
                seg.duration -= delta_duration
                seg.start += delta_duration // 2
            else:
                raise ValueError(f"Unsupported shrink mode: {shrink}")
        # Duration becomes longer
        elif new_duration > seg.duration:
            success_flag = False
            prev_seg_end = int(0) if seg_index == 0 else self.segments[seg_index-1].target_timerange.end
            next_seg_start = int(1e15) if seg_index == len(self.segments)-1 else self.segments[seg_index+1].start
            for mode in extend:
                if mode == Extend_mode.extend_head:
                    if seg.start - delta_duration >= prev_seg_end:
                        seg.start -= delta_duration
                        success_flag = True
                elif mode == Extend_mode.extend_tail:
                    if seg.target_timerange.end + delta_duration <= next_seg_start:
                        seg.duration += delta_duration
                        success_flag = True
                elif mode == Extend_mode.push_tail:
                    shift_duration = max(0, seg.target_timerange.end + delta_duration - next_seg_start)
                    seg.duration += delta_duration
                    if shift_duration > 0:  # Shift subsequent segments backward when necessary
                        for i in range(seg_index+1, len(self.segments)):
                            self.segments[i].start += shift_duration
                    success_flag = True
                elif mode == Extend_mode.cut_material_tail:
                    src_timerange.duration = seg.duration
                    success_flag = True
                else:
                    raise ValueError(f"Unsupported extend mode: {mode}")

                if success_flag:
                    break
            if not success_flag:
                raise exceptions.ExtensionFailed(f"Failed to extend segment to {new_duration}μs, tried the following methods: {extend}")

        # Write material time range
        seg.source_timerange = src_timerange

def import_track(json_data: Dict[str, Any], imported_materials: Dict[str, Any] = None) -> Track:
    """Import track
    :param json_data: Track data
    :param imported_materials: Imported material data, used for creating segment material instances
    """
    track_type = Track_type.from_name(json_data["type"])
    # Create new track instance, preserving all original attributes
    track = Track(
        track_type=track_type,
        name=json_data["name"],
        render_index=max([int(seg.get("render_index", 0)) for seg in json_data.get("segments", [])], default=0),
        mute=bool(json_data.get("attribute", 0))
    )
    
    # Set track_id, use original ID
    track.track_id = json_data.get("id")
    
    # If track type allows modification, import all segments
    if track_type.value.allow_modify and imported_materials:
        for segment_data in json_data.get("segments", []):
            material_id = segment_data.get("material_id")
            material = None
            
            # Process keyframe info
            common_keyframes = []
            for kf_list_data in segment_data.get("common_keyframes", []):
                # Create keyframe list
                kf_list = Keyframe_list(Keyframe_property(kf_list_data["property_type"]))
                kf_list.list_id = kf_list_data["id"]
                
                # Add keyframe
                for kf_data in kf_list_data["keyframe_list"]:
                    keyframe = Keyframe(kf_data["time_offset"], kf_data["values"][0])
                    keyframe.kf_id = kf_data["id"]
                    keyframe.values = kf_data["values"]
                    kf_list.keyframes.append(keyframe)
                
                common_keyframes.append(kf_list)
            
            # Find the corresponding material based on track type
            if track_type == Track_type.video:
                # Find video material from imported_materials
                for video_material in imported_materials.get("videos", []):
                    if video_material["id"] == material_id:
                        material = Video_material.from_dict(video_material)
                        break
                
                if material:
                    # Create video segment
                    segment = Video_segment(
                        material=material,
                        target_timerange=Timerange(
                            start=segment_data["target_timerange"]["start"],
                            duration=segment_data["target_timerange"]["duration"]
                        ),
                        source_timerange=Timerange(
                            start=segment_data["source_timerange"]["start"],
                            duration=segment_data["source_timerange"]["duration"]
                        ),
                        speed=segment_data.get("speed", 1.0),
                        clip_settings=Clip_settings(
                            transform_x=segment_data["clip"]["transform"]["x"],
                            transform_y=segment_data["clip"]["transform"]["y"],
                            scale_x=segment_data["clip"]["scale"]["x"],
                            scale_y=segment_data["clip"]["scale"]["y"]
                        )
                    )
                    segment.volume = segment_data.get("volume", 1.0)
                    segment.visible = segment_data.get("visible", True)
                    segment.common_keyframes = common_keyframes
                    track.segments.append(segment)
                
            elif track_type == Track_type.audio:
                # Find audio material from imported_materials
                for audio_material in imported_materials.get("audios", []):
                    if audio_material["id"] == material_id:
                        material = Audio_material.from_dict(audio_material)
                        break
                
                if material:
                    # Create audio segment
                    segment = Audio_segment(
                        material=material,
                        target_timerange=Timerange(
                            start=segment_data["target_timerange"]["start"],
                            duration=segment_data["target_timerange"]["duration"]
                        ),
                        volume=segment_data.get("volume", 1.0)
                    )
                    # Add audio effect
                    if "audio_effects" in imported_materials and imported_materials["audio_effects"]:
                        effect_data = imported_materials["audio_effects"][0]
                        # Find the corresponding effect type based on resource ID
                        for effect_type in Audio_scene_effect_type:
                            if effect_type.value.resource_id == effect_data["resource_id"]:
                                # Convert parameter values from 0-1 range to 0-100
                                params = []
                                for param in effect_data["audio_adjust_params"]:
                                    params.append(param["value"] * 100)
                                segment.add_effect(effect_type, params,effect_id=effect_data["id"])
                                break
                    segment.common_keyframes = common_keyframes
                    track.segments.append(segment)
            else:
                # Other type segments keep as is
                segment = ImportedSegment(segment_data)
                segment.common_keyframes = common_keyframes
                track.segments.append(segment)
    
    return track
