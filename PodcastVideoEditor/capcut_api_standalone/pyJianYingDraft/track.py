"""Track classes and their metadata"""

import uuid

from enum import Enum
from typing import TypeVar, Generic, Type
from typing import Dict, List, Any, Union
from dataclasses import dataclass
from abc import ABC, abstractmethod
import pyJianYingDraft as draft

from .exceptions import SegmentOverlap
from .segment import Base_segment
from .video_segment import Video_segment, Sticker_segment
from .audio_segment import Audio_segment
from .text_segment import Text_segment
from .effect_segment import Effect_segment, Filter_segment

@dataclass
class Track_meta:
    """Track metadata associated with a track type"""

    segment_type: Union[Type[Video_segment], Type[Audio_segment],
                        Type[Effect_segment], Type[Filter_segment],
                        Type[Text_segment], Type[Sticker_segment], None]
    """Segment type associated with the track"""
    render_index: int
    """Default render order, higher values are closer to foreground"""
    allow_modify: bool
    """Whether modification is allowed when imported"""

class Track_type(Enum):
    """Track type enumeration

    Variable names correspond to the type attribute, values represent the associated track metadata
    """

    video = Track_meta(Video_segment, 0, True)
    audio = Track_meta(Audio_segment, 0, True)
    effect = Track_meta(Effect_segment, 10000, False)
    filter = Track_meta(Filter_segment, 11000, False)
    sticker = Track_meta(Sticker_segment, 14000, False)
    text = Track_meta(Text_segment, 15000, True)  # Originally 14000, changed to 15000 to avoid conflict with sticker

    adjust = Track_meta(None, 0, False)
    """Only used when importing, do not attempt to create a track of this type"""

    @staticmethod
    def from_name(name: str) -> "Track_type":
        """Get track type enum by name"""
        for t in Track_type:
            if t.name == name:
                return t
        raise ValueError("Invalid track type: %s" % name)


class Base_track(ABC):
    """Track base class"""

    track_type: Track_type
    """Track type"""
    name: str
    """Track name"""
    track_id: str
    """Track global ID"""
    render_index: int
    """Render order, higher values are closer to foreground"""

    @abstractmethod
    def export_json(self) -> Dict[str, Any]: ...

Seg_type = TypeVar("Seg_type", bound=Base_segment)
class Track(Base_track, Generic[Seg_type]):
    """Track for non-template mode"""

    mute: bool
    """Whether muted"""

    segments: List[Seg_type]
    """List of segments in this track"""
    
    pending_keyframes: List[Dict[str, Any]]
    """List of pending keyframes"""

    def __init__(self, track_type: Track_type, name: str, render_index: int, mute: bool):
        self.track_type = track_type
        self.name = name
        self.track_id = uuid.uuid4().hex
        self.render_index = render_index

        self.mute = mute
        self.segments = []
        self.pending_keyframes = []
        
    def add_pending_keyframe(self, property_type: str, time: float, value: str) -> None:
        """Add a pending keyframe
        
        Args:
            property_type: Keyframe property type
            time: Keyframe time point (seconds)
            value: Keyframe value
        """
        self.pending_keyframes.append({
            "property_type": property_type,
            "time": time,
            "value": value
        })
        
    def process_pending_keyframes(self) -> None:
        """Process all pending keyframes"""
        if not self.pending_keyframes:
            return
            
        for kf_info in self.pending_keyframes:
            property_type = kf_info["property_type"]
            time = kf_info["time"]
            value = kf_info["value"]
            
            try:
                # Find the segment corresponding to the time point (time unit: microseconds)
                target_time = int(time * 1000000)  # Convert seconds to microseconds
                target_segment = next(
                    (segment for segment in self.segments 
                     if segment.target_timerange.start <= target_time <= segment.target_timerange.end),
                    None
                )
                        
                if target_segment is None:
                    print(f"Warning: No segment found at time {time}s in track {self.name}, skipping this keyframe")
                    continue
                    
                # Convert property type string to enum value
                property_enum = getattr(draft.Keyframe_property, property_type)
                    
                # Parse value
                if property_type == 'alpha' and value.endswith('%'):
                    float_value = float(value[:-1]) / 100
                elif property_type == 'volume' and value.endswith('%'):
                    float_value = float(value[:-1]) / 100
                elif property_type == 'rotation' and value.endswith('deg'):
                    float_value = float(value[:-3])
                elif property_type in ['saturation', 'contrast', 'brightness']:
                    if value.startswith('+'):
                        float_value = float(value[1:])
                    elif value.startswith('-'):
                        float_value = -float(value[1:])
                    else:
                        float_value = float(value)
                else:
                    float_value = float(value)
                    
                # Calculate time offset
                offset_time = target_time - target_segment.target_timerange.start
                    
                # Add keyframe
                target_segment.add_keyframe(property_enum, offset_time, float_value)
                print(f"Successfully added keyframe: {property_type} at {time}s")
            except Exception as e:
                print(f"Failed to add keyframe: {str(e)}")
        
        # Clear pending keyframes
        self.pending_keyframes = []

    @property
    def end_time(self) -> int:
        """Track end time, in microseconds"""
        if len(self.segments) == 0:
            return 0
        return self.segments[-1].target_timerange.end

    @property
    def accept_segment_type(self) -> Type[Seg_type]:
        """Return the segment type allowed by this track"""
        return self.track_type.value.segment_type  # type: ignore

    def add_segment(self, segment: Seg_type) -> "Track[Seg_type]":
        """Add a segment to the track. The segment must match the track type and not overlap with existing segments.

        Args:
            segment (Seg_type): The segment to add

        Raises:
            `TypeError`: New segment type does not match the track type
            `SegmentOverlap`: New segment overlaps with an existing segment
        """
        if not isinstance(segment, self.accept_segment_type):
            raise TypeError("New segment (%s) is not of the same type as the track (%s)" % (type(segment), self.accept_segment_type))

        # Check if segments overlap
        for seg in self.segments:
            if seg.overlaps(segment):
                raise SegmentOverlap("New segment overlaps with existing segment [start: {}, end: {}]"
                                     .format(segment.target_timerange.start, segment.target_timerange.end))

        self.segments.append(segment)
        return self

    def export_json(self) -> Dict[str, Any]:
        # Write render_index for each segment
        segment_exports = [seg.export_json() for seg in self.segments]
        for seg in segment_exports:
            seg["render_index"] = self.render_index

        return {
            "attribute": int(self.mute),
            "flag": 0,
            "id": self.track_id,
            "is_default_name": len(self.name) == 0,
            "name": self.name,
            "segments": segment_exports,
            "type": self.track_type.name
        }
