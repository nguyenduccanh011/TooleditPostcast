"""Define video/text animation related classes"""

import uuid

from typing import Union, Optional
from typing import Literal, Dict, List, Any

from .time_util import Timerange

from .metadata.animation_meta import Animation_meta
from .metadata import Intro_type, Outro_type, Group_animation_type
from .metadata import CapCut_Intro_type, CapCut_Outro_type, CapCut_Group_animation_type
from .metadata import Text_intro, Text_outro, Text_loop_anim
from .metadata import CapCut_Text_intro, CapCut_Text_loop_anim, CapCut_Text_outro

class Animation:
    """A video/text animation effect"""

    name: str
    """Animation name, defaults to the animation effect name"""
    effect_id: str
    """Another animation ID, provided by CapCut itself"""
    animation_type: str
    """Animation type, defined in subclasses"""
    resource_id: str
    """Resource ID, provided by CapCut itself"""

    start: int
    """Animation offset from segment start, in microseconds"""
    duration: int
    """Animation duration, in microseconds"""

    is_video_animation: bool
    """Whether this is a video animation, defined in subclasses"""

    def __init__(self, animation_meta: Animation_meta, start: int, duration: int):
        self.name = animation_meta.title
        self.effect_id = animation_meta.effect_id
        self.resource_id = animation_meta.resource_id

        self.start = start
        self.duration = duration

    def export_json(self) -> Dict[str, Any]:
        return {
            "anim_adjust_params": None,
            "platform": "all",
            "panel": "video" if self.is_video_animation else "",
            "material_type": "video" if self.is_video_animation else "sticker",

            "name": self.name,
            "id": self.effect_id,
            "type": self.animation_type,
            "resource_id": self.resource_id,

            "start": self.start,
            "duration": self.duration,
            # Do not export path and request_id
        }

class Video_animation(Animation):
    """A video animation effect"""

    animation_type: Literal["in", "out", "group"]

    def __init__(self, animation_type: Union[Intro_type, Outro_type, Group_animation_type, CapCut_Intro_type, CapCut_Outro_type, CapCut_Group_animation_type],
                 start: int, duration: int):
        super().__init__(animation_type.value, start, duration)

        if ((isinstance(animation_type, Intro_type) or isinstance(animation_type, CapCut_Intro_type))):
            self.animation_type = "in"
        elif isinstance(animation_type, Outro_type) or isinstance(animation_type, CapCut_Outro_type):
            self.animation_type = "out"
        elif isinstance(animation_type, Group_animation_type) or isinstance(animation_type, CapCut_Group_animation_type):
            self.animation_type = "group"

        self.is_video_animation = True

class Text_animation(Animation):
    """A text animation effect"""

    animation_type: Literal["in", "out", "loop"]

    def __init__(self, animation_type: Union[Text_intro, Text_outro, Text_loop_anim, CapCut_Text_intro, CapCut_Text_outro, CapCut_Text_loop_anim],
                 start: int, duration: int):
        super().__init__(animation_type.value, start, duration)

        if (isinstance(animation_type, Text_intro) or isinstance(animation_type, CapCut_Text_intro)):
            self.animation_type = "in"
        elif (isinstance(animation_type, Text_outro) or isinstance(animation_type, CapCut_Text_outro)):
            self.animation_type = "out"
        elif (isinstance(animation_type, Text_loop_anim) or isinstance(animation_type, CapCut_Text_loop_anim)):
            self.animation_type = "loop"

        self.is_video_animation = False

class Segment_animations:
    """A series of animations attached to a material

    For video segments: entrance, exit, or group animation; for text segments: entrance, exit, or loop animation"""

    animation_id: str
    """Global ID for the animation series, auto-generated"""

    animations: List[Animation]
    """List of animations"""

    def __init__(self):
        self.animation_id = uuid.uuid4().hex
        self.animations = []

    def get_animation_trange(self, animation_type: Literal["in", "out", "group", "loop"]) -> Optional[Timerange]:
        """Get the time range of the specified animation type"""
        for animation in self.animations:
            if animation.animation_type == animation_type:
                return Timerange(animation.start, animation.duration)
        return None

    def add_animation(self, animation: Union[Video_animation, Text_animation]) -> None:
        # Do not allow adding more than one animation of the same type (e.g. two intro animations)
        if animation.animation_type in [ani.animation_type for ani in self.animations]:
            raise ValueError(f"This segment already has an animation of type '{animation.animation_type}'")

        if isinstance(animation, Video_animation):
            # Do not allow group animation and intro/outro animations at the same time
            if any(ani.animation_type == "group" for ani in self.animations):
                raise ValueError("This segment already has a group animation, cannot add other animations")
            if animation.animation_type == "group" and len(self.animations) > 0:
                raise ValueError("Cannot add group animation when other animations already exist")
        elif isinstance(animation, Text_animation):
            if any(ani.animation_type == "loop" for ani in self.animations):
                raise ValueError("This segment already has a loop animation. To use both loop and intro/outro animations, add intro/outro first then loop")

        self.animations.append(animation)

    def export_json(self) -> Dict[str, Any]:
        return {
            "id": self.animation_id,
            "type": "sticker_animation",
            "multi_language_current": "none",
            "animations": [animation.export_json() for animation in self.animations]
        }
