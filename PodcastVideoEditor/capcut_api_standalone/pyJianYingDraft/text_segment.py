"""Define text segment class and related classes"""

import json
import uuid
from copy import deepcopy

from typing import Dict, Tuple, Any, List
from typing import Union, Optional, Literal

from pyJianYingDraft.metadata.capcut_text_animation_meta import CapCut_Text_intro, CapCut_Text_outro, CapCut_Text_loop_anim

from .time_util import Timerange, tim
from .segment import Clip_settings, Visual_segment
from .animation import Segment_animations, Text_animation

from .metadata import Font_type, Effect_meta
from .metadata import Text_intro, Text_outro, Text_loop_anim


# ── System font → CapCut font mapping ──────────────────────────────────
# Maps common system/OS font names to the closest CapCut Font_type key.
_SYSTEM_FONT_MAP: Dict[str, str] = {
    "arial": "Poppins_Regular",
    "helvetica": "Poppins_Regular",
    "verdana": "Poppins_Regular",
    "tahoma": "Poppins_Regular",
    "segoe ui": "Poppins_Regular",
    "calibri": "Poppins_Regular",
    "roboto": "Roboto_BlkCn",
    "times new roman": "OldStandardTT_Regular",
    "georgia": "HeptaSlab_Light",
    "courier new": "Anson",
    "consolas": "Anson",
    "comic sans ms": "Grandstander_Regular",
    "impact": "Inter_Black",
    "microsoft yahei": "SourceHanSansCN_Regular",
    "simhei": "SourceHanSansCN_Regular",
    "simsun": "SourceHanSerifCN_Regular",
    "noto sans": "Poppins_Regular",
    "noto serif": "SourceHanSerifCN_Regular",
    "open sans": "Poppins_Regular",
    "lato": "Poppins_Regular",
    "montserrat": "Poppins_Bold",
    "source han sans": "SourceHanSansCN_Regular",
}

_FALLBACK_FONT_KEYS = ["Poppins_Regular", "Inter_Black", "Nunito", "SourceHanSansCN_Regular"]


def _normalize_font_key(value: str) -> str:
    return value.strip().lower().replace(" ", "").replace("_", "").replace("-", "")


def _resolve_font_safe(font_str: str) -> "Font_type | None":
    """Resolve a font string to a Font_type member, with system font mapping and fallback."""
    if not font_str:
        return None

    # 1. Direct match by attribute name
    if font_str in Font_type.__members__:
        return getattr(Font_type, font_str)

    # 2. Normalized match
    normalized = _normalize_font_key(font_str)
    for key in Font_type.__members__:
        if _normalize_font_key(key) == normalized:
            return getattr(Font_type, key)

    # 3. System font mapping
    lower = font_str.strip().lower()
    mapped_key = _SYSTEM_FONT_MAP.get(lower)
    if mapped_key and mapped_key in Font_type.__members__:
        print(f"[font_resolve] Mapped system font '{font_str}' -> '{mapped_key}'")
        return getattr(Font_type, mapped_key)

    # 4. Fallback
    for fb in _FALLBACK_FONT_KEYS:
        if fb in Font_type.__members__:
            print(f"[font_resolve] Unknown font '{font_str}', fallback to '{fb}'")
            return getattr(Font_type, fb)

    # 5. Last resort
    first_key = next(iter(Font_type.__members__), None)
    if first_key:
        print(f"[font_resolve] Unknown font '{font_str}', fallback to '{first_key}'")
        return getattr(Font_type, first_key)
    return None

class Text_style:
    """Text style class"""

    size: float
    """Font size"""

    bold: bool
    """Bold or not"""
    italic: bool
    """Italic or not"""
    underline: bool
    """Underline or not"""

    color: Tuple[float, float, float]
    """Font color, RGB tuple, value range [0, 1]"""
    alpha: float
    """Font opacity"""

    align: Literal[0, 1, 2]
    """Alignment: 0=left, 1=center, 2=right"""
    vertical: bool
    """Whether vertical text"""

    letter_spacing: int
    """Letter spacing"""
    line_spacing: int
    """Line spacing"""

    def __init__(self, *, size: float = 8.0, bold: bool = False, italic: bool = False, underline: bool = False,
                 color: Tuple[float, float, float] = (1.0, 1.0, 1.0), alpha: float = 1.0,
                 align: Literal[0, 1, 2] = 0, vertical: bool = False,
                 letter_spacing: int = 0, line_spacing: int = 0):
        """
        Args:
            size (`float`, optional): Font size, default 8.0
            bold (`bool`, optional): Bold, default False
            italic (`bool`, optional): Italic, default False
            underline (`bool`, optional): Underline, default False
            color (`Tuple[float, float, float]`, optional): Font color RGB tuple, value range [0, 1], default white
            alpha (`float`, optional): Font opacity, value range [0, 1], default 1.0
            align (`int`, optional): Alignment, 0=left, 1=center, 2=right, default left
            vertical (`bool`, optional): Vertical text, default False
            letter_spacing (`int`, optional): Letter spacing, default 0
            line_spacing (`int`, optional): Line spacing, default 0
        """
        self.size = size
        self.bold = bold
        self.italic = italic
        self.underline = underline

        self.color = color
        self.alpha = alpha

        self.align = align
        self.vertical = vertical

        self.letter_spacing = letter_spacing
        self.line_spacing = line_spacing

class Text_border:
    """Text border parameters"""

    alpha: float
    """Border opacity"""
    color: Tuple[float, float, float]
    """Border color, RGB tuple, value range [0, 1]"""
    width: float
    """Border width"""

    def __init__(self, *, alpha: float = 1.0, color: Tuple[float, float, float] = (0.0, 0.0, 0.0), width: float = 40.0):
        """
        Args:
            alpha (`float`, optional): Border opacity, value range [0, 1], default 1.0
            color (`Tuple[float, float, float]`, optional): Border color RGB tuple, value range [0, 1], default black
            width (`float`, optional): Border width, range [0, 100], default 40.0
        """
        self.alpha = alpha
        self.color = color
        self.width = width / 100.0 * 0.2  # This mapping may not be accurate

    def export_json(self) -> Dict[str, Any]:
        """Export JSON data, place in material content styles"""
        return {
            "content": {
                "solid": {
                    "alpha": self.alpha,
                    "color": list(self.color),
                }
            },
            "width": self.width
        }

class Text_background:
    """Text background parameters"""

    style: Literal[0, 2]
    """Background style"""

    alpha: float
    """Background opacity"""
    color: str
    """Background color, format is '#RRGGBB'"""
    round_radius: float
    """Background round corner radius"""
    height: float
    """Background height"""
    width: float
    """Background width"""
    horizontal_offset: float
    """Background horizontal offset"""
    vertical_offset: float
    """Background vertical offset"""

    def __init__(self, *, color: str, style: Literal[1, 2] = 1, alpha: float = 1.0, round_radius: float = 0.0,
                 height: float = 0.14, width: float = 0.14,
                 horizontal_offset: float = 0.5, vertical_offset: float = 0.5):
        """
        Args:
            color (`str`): Background color, format is '#RRGGBB'
            style (`int`, optional): Background style, 1 and 2 correspond to two styles in CapCut, default is 1
            alpha (`float`, optional): Background opacity, same as in CapCut, value range [0, 1], default is 1.0
            round_radius (`float`, optional): Background round corner radius, same as in CapCut, value range [0, 1], default is 0.0
            height (`float`, optional): Background height, same as in CapCut, value range [0, 1], default is 0.14
            width (`float`, optional): Background width, same as in CapCut, value range [0, 1], default is 0.14
            horizontal_offset (`float`, optional): Background horizontal offset, same as in CapCut, value range [0, 1], default is 0.5
            vertical_offset (`float`, optional): Background vertical offset, same as in CapCut, value range [0, 1], default is 0.5
        """
        self.style = (0, 2)[style - 1]

        self.alpha = alpha
        self.color = color
        self.round_radius = round_radius
        self.height = height
        self.width = width
        self.horizontal_offset = horizontal_offset * 2 - 1
        self.vertical_offset = vertical_offset * 2 - 1

    def export_json(self) -> Dict[str, Any]:
        """Generate sub-JSON data, merged into Text_segment during export"""
        return {
            "background_style": self.style,
            "background_color": self.color,
            "background_alpha": self.alpha,
            "background_round_radius": self.round_radius,
            "background_height": self.height,
            "background_width": self.width,
            "background_horizontal_offset": self.horizontal_offset,
            "background_vertical_offset": self.vertical_offset,
        }

class Text_shadow:
    """Text shadow parameters"""

    has_shadow: bool
    """Whether to enable shadow"""
    alpha: float
    """Shadow opacity"""
    angle: float
    """Shadow angle"""
    color: str
    """Shadow color, format is '#RRGGBB'"""
    distance: float
    """Shadow distance"""
    smoothing: float
    """Shadow smoothing"""

    def __init__(self, *, has_shadow: bool = False, alpha: float = 0.9, angle: float = -45.0,
                 color: str = "#000000", distance: float = 5.0, smoothing: float = 0.45):
        """
        Args:
            has_shadow (`bool`, optional): Whether to enable shadow, default is False
            alpha (`float`, optional): Shadow opacity, value range [0, 1], default is 0.9
            angle (`float`, optional): Shadow angle, value range [-180, 180], default is -45.0
            color (`str`, optional): Shadow color, format is '#RRGGBB', default is black
            distance (`float`, optional): Shadow distance, default is 5.0
            smoothing (`float`, optional): Shadow smoothing, value range [0, 1], default is 0.15
        """
        self.has_shadow = has_shadow
        self.alpha = alpha
        self.angle = angle
        self.color = color
        self.distance = distance
        self.smoothing = smoothing

    def export_json(self) -> Dict[str, Any]:
        """Generate sub-JSON data, merged into Text_segment during export"""
        return {
            "has_shadow": self.has_shadow,
            "shadow_alpha": self.alpha,
            "shadow_angle": self.angle,
            "shadow_color": self.color,
            "shadow_distance": self.distance,
            "shadow_smoothing": self.smoothing * 3
        }

class TextBubble:
    """Text bubble material, essentially the same as filter material"""

    global_id: str
    """Bubble global id, automatically generated by the program"""

    effect_id: str
    resource_id: str

    def __init__(self, effect_id: str, resource_id: str):
        self.global_id = uuid.uuid4().hex
        self.effect_id = effect_id
        self.resource_id = resource_id

    def export_json(self) -> Dict[str, Any]:
        return {
            "apply_target_type": 0,
            "effect_id": self.effect_id,
            "id": self.global_id,
            "resource_id": self.resource_id,
            "type": "text_shape",
            "value": 1.0,
            # Do not export path and request_id
        }

class TextEffect(TextBubble):
    """Text fancy text material, essentially the same as filter material"""

    def export_json(self) -> Dict[str, Any]:
        ret = super().export_json()
        ret["type"] = "text_effect"
        ret["source_platform"] = 1
        return ret

class TextStyleRange:
    """Text style range class, used to define styles for specific text ranges"""
    
    start: int
    """Start position (inclusive)"""
    end: int
    """End position (exclusive)"""
    style: Text_style
    """Font style"""
    border: Optional[Text_border]
    """Text border parameters, None means no border"""
    font: Optional[Effect_meta]
    """Font setting, None means use global font"""
    
    def __init__(self, start: int, end: int, style: Text_style, border: Optional[Text_border] = None, font_str:str = None):
        """Create text style range
        
        Args:
            start (`int`): Start position (inclusive)
            end (`int`): End position (exclusive)
            style (`Text_style`): Font style
            border (`Text_border`, optional): Text border parameters, default is None (no border)
            font (optional): Font setting, default is None (use global font)
        """
        self.start = start
        self.end = end
        self.style = style
        self.border = border
        if font_str:
            resolved = _resolve_font_safe(font_str)
            if resolved is not None:
                self.font = resolved.value
            else:
                self.font = None
    
    def get_range(self) -> List[int]:
        """Get range list
        
        Returns:
            `List[int]`: Range list in [start, end] format
        """
        return [self.start, self.end]

class Text_segment(Visual_segment):
    """Text segment class, currently only supports setting basic font styles"""

    text: str
    """Text content"""
    font: Optional[Effect_meta]
    """Font type"""
    style: Text_style
    """Font style"""

    border: Optional[Text_border]
    """Text border parameters, None means no border"""
    background: Optional[Text_background]
    """Text background parameters, None means no background"""

    shadow: Optional[Text_shadow]
    """Text shadow parameters, None means no shadow"""

    bubble: Optional[TextBubble]
    """Text bubble effect, added to material list when placed on track"""
    effect: Optional[TextEffect]
    """Text fancy text effect, added to material list when placed on track, currently only supports some effects"""
    
    fixed_width: float
    """Fixed width, -1 means not fixed"""
    fixed_height: float
    """Fixed height, -1 means not fixed"""
    
    text_styles: List[TextStyleRange]
    """List of multiple text styles"""

    def __init__(self, text: str, timerange: Timerange, *,
                 font: Optional[Font_type] = None,
                 style: Optional[Text_style] = None, clip_settings: Optional[Clip_settings] = None,
                 border: Optional[Text_border] = None, background: Optional[Text_background] = None,
                 shadow: Optional[Text_shadow] = None,
                 fixed_width: int = -1, fixed_height: int = -1,
                 is_subtitle: bool = False, subtitle_group_id: str = ""):
        """Create a text segment with specified time info, font style and image adjustment settings

        After creation, use `Script_file.add_segment` to add it to a track

        Args:
            text (`str`): Text content
            timerange (`Timerange`): Time range of the segment on the track
            font (`Font_type`, optional): Font type, default is system font
            style (`Text_style`, optional): Font style, including size/color/alignment/opacity etc.
            clip_settings (`Clip_settings`, optional): Image adjustment settings, default is no transformation
            border (`Text_border`, optional): Text border parameters, default is no border
            background (`Text_background`, optional): Text background parameters, default is no background
            fixed_width (`int`, optional): Text fixed width (pixels), default is -1 (not fixed)
            fixed_height (`int`, optional): Text fixed height (pixels), default is -1 (not fixed)
        """
        super().__init__(uuid.uuid4().hex, None, timerange, 1.0, 1.0, clip_settings=clip_settings)

        self.text = text
        self.font = font.value if font else None
        self.style = style or Text_style()
        self.border = border
        self.background = background
        self.shadow = shadow
    
        self.bubble = None
        self.effect = None
        
        self.fixed_width = fixed_width
        self.fixed_height = fixed_height
        self.text_styles = []

        self.is_subtitle = is_subtitle
        self.subtitle_group_id = subtitle_group_id

        if self.is_subtitle:
            # CapCut subtitle segments only reference a (possibly empty) material_animations
            # entry, not the speed instance. Reset extra refs to match that layout.
            self.extra_material_refs = []
            self.animations_instance = Segment_animations()
            self.extra_material_refs.append(self.animations_instance.animation_id)

    # Method to set text style for a specific range
    def add_text_style(self, textStyleRange: TextStyleRange) -> "Text_segment":
        # Add new style range
        self.text_styles.append(textStyleRange)
        return self
        

    @classmethod
    def create_from_template(cls, text: str, timerange: Timerange, template: "Text_segment") -> "Text_segment":
        """Create a new text segment from template with specified text content"""
        new_segment = cls(text, timerange, style=deepcopy(template.style), clip_settings=deepcopy(template.clip_settings),
                          border=deepcopy(template.border), background=deepcopy(template.background))
        new_segment.font = deepcopy(template.font)

        # Handle animations etc.
        if template.animations_instance:
            new_segment.animations_instance = deepcopy(template.animations_instance)
            new_segment.animations_instance.animation_id = uuid.uuid4().hex
            new_segment.extra_material_refs.append(new_segment.animations_instance.animation_id)
        if template.bubble:
            new_segment.add_bubble(template.bubble.effect_id, template.bubble.resource_id)
        if template.effect:
            new_segment.add_effect(template.effect.effect_id)

        return new_segment

    def add_animation(self, animation_type: Union[Text_intro, Text_outro, Text_loop_anim,
                                                  CapCut_Text_intro, CapCut_Text_outro, CapCut_Text_loop_anim],
                      duration: Union[str, float] = 500000) -> "Text_segment":
        """Add the given intro/outro/loop animation to this segment's animation list. Duration can be set for intro/outro animations, loop animations automatically fill the remaining non-animated parts

        Note: If you want to use both loop and intro/outro animations, please **add intro/outro animations before loop animations**

        Args:
            animation_type (`Text_intro`, `Text_outro` or `Text_loop_anim`): Text animation type.
            duration (`str` or `float`, optional): Animation duration in microseconds, only effective for intro/outro animations.
                If a string is passed, the `tim()` function will be called to parse it. Default is 0.5 seconds
        """
        duration = min(tim(duration), self.target_timerange.duration)

        if (isinstance(animation_type, Text_intro) or isinstance(animation_type, CapCut_Text_intro)):
            start = 0
        elif (isinstance(animation_type, Text_outro) or isinstance(animation_type, CapCut_Text_outro)):
            start = self.target_timerange.duration - duration
        elif (isinstance(animation_type, Text_loop_anim) or isinstance(animation_type, CapCut_Text_loop_anim)):
            intro_trange = self.animations_instance and self.animations_instance.get_animation_trange("in")
            outro_trange = self.animations_instance and self.animations_instance.get_animation_trange("out")
            start = intro_trange.start if intro_trange else 0
            duration = self.target_timerange.duration - start - (outro_trange.duration if outro_trange else 0)
        else:
            raise TypeError("Invalid animation type %s" % type(animation_type))

        if self.animations_instance is None:
            self.animations_instance = Segment_animations()
            self.extra_material_refs.append(self.animations_instance.animation_id)

        self.animations_instance.add_animation(Text_animation(animation_type, start, duration))

        return self

    def add_bubble(self, effect_id: str, resource_id: str) -> "Text_segment":
        """Add bubble effect based on material info, which can be obtained from templates via `Script_file.inspect_material`

        Args:
            effect_id (`str`): The effect_id of the bubble effect
            resource_id (`str`): The resource_id of the bubble effect
        """
        self.bubble = TextBubble(effect_id, resource_id)
        self.extra_material_refs.append(self.bubble.global_id)
        return self

    def add_effect(self, effect_id: str) -> "Text_segment":
        """Add fancy text effect based on material info, which can be obtained from templates via `Script_file.inspect_material`

        Args:
            effect_id (`str`): The effect_id of the fancy text effect, which is also its resource_id
        """
        self.effect = TextEffect(effect_id, effect_id)
        self.extra_material_refs.append(self.effect.global_id)
        return self

    def export_material(self) -> Dict[str, Any]:
        """Material associated with this text segment, so Text_material class is not defined separately"""
        # Flag for combining various effects
        check_flag: int = 7
        if self.border:
            check_flag |= 8
        if self.background:
            check_flag |= 16
        if self.shadow and self.shadow.has_shadow:  # If shadow exists and is enabled
            check_flag |= 32  # Add shadow flag
    
        # Build styles array
        styles = []
        
        if self.text_styles:
            # Create a sorted list of style ranges
            sorted_styles = sorted(self.text_styles, key=lambda x: x.start)
            
            # Check if default style needs to be added at the beginning
            if sorted_styles[0].start > 0:
                # Add default style from 0 to the start of the first style
                default_style = {
                    "fill": {
                        "alpha": 1.0,
                        "content": {
                            "render_type": "solid",
                            "solid": {
                                "alpha": self.style.alpha,
                                "color": list(self.style.color)
                            }
                        }
                    },
                    "range": [0, sorted_styles[0].start],
                    "size": self.style.size,
                    "bold": self.style.bold,
                    "italic": self.style.italic,
                    "underline": self.style.underline,
                    "strokes": [self.border.export_json()] if self.border else []
                }
                
                # If shadow settings exist, add to style
                if self.shadow and self.shadow.has_shadow:
                    style_item["shadows"] = [
                        {
                            "diffuse": self.shadow.smoothing / 6,  # diffuse = smoothing/6
                            "angle": self.shadow.angle,
                            "content": {
                                "solid": {
                                    "color": [int(self.shadow.color[1:3], 16)/255, 
                                             int(self.shadow.color[3:5], 16)/255, 
                                             int(self.shadow.color[5:7], 16)/255]
                                }
                            },
                            "distance": self.shadow.distance,
                            "alpha": self.shadow.alpha
                        }
                    ]
                
                # If global font settings exist, add to style
                if self.font:
                    default_style["font"] = {
                        "id": self.font.resource_id,
                        "path": "C:/%s.ttf" % self.font.name
                    }
                
                # If effect settings exist, add to style
                if self.effect:
                    default_style["effectStyle"] = {
                        "id": self.effect.effect_id,
                        "path": "C:"  # Material files are not actually placed here
                    }
                    
                styles.append(default_style)
            
            # Process each style range
            for i, style_range in enumerate(sorted_styles):
                # Add style for current style range
                style_item = {
                    "fill": {
                        "alpha": 1.0,
                        "content": {
                            "render_type": "solid",
                            "solid": {
                                "alpha": style_range.style.alpha,
                                "color": list(style_range.style.color)
                            }
                        }
                    },
                    "range": style_range.get_range(),
                    "size": style_range.style.size,
                    "bold": style_range.style.bold,
                    "italic": style_range.style.italic,
                    "underline": style_range.style.underline,
                    "strokes": [style_range.border.export_json()] if style_range.border else []
                }
                
                # If TextStyleRange has font settings, use it first
                if hasattr(style_range, 'font') and style_range.font:
                    style_item["font"] = {
                        "id": style_range.font.resource_id,
                        "path": "C:/%s.ttf" % style_range.font.name
                    }
                # Otherwise, if global font settings exist, use global font
                elif self.font:
                    style_item["font"] = {
                        "id": self.font.resource_id,
                        "path": "C:/%s.ttf" % self.font.name
                    }
                
                # If effect settings exist, add to style
                if self.effect:
                    style_item["effectStyle"] = {
                        "id": self.effect.effect_id,
                        "path": "C:"  # Material files are not actually placed here
                    }
                    
                styles.append(style_item)
                
                # Check if default style needs to be added between current and next style
                if i < len(sorted_styles) - 1 and style_range.end < sorted_styles[i+1].start:
                    # Add default style from end of current style to start of next style
                    gap_style = {
                        "fill": {
                            "alpha": 1.0,
                            "content": {
                                "render_type": "solid",
                                "solid": {
                                    "alpha": self.style.alpha,
                                    "color": list(self.style.color)
                                }
                            }
                        },
                        "range": [style_range.end, sorted_styles[i+1].start],
                        "size": self.style.size,
                        "bold": self.style.bold,
                        "italic": self.style.italic,
                        "underline": self.style.underline,
                        "strokes": [self.border.export_json()] if self.border else []
                    }
                    
                    # If global font settings exist, add to style
                    if self.font:
                        gap_style["font"] = {
                            "id": self.font.resource_id,
                            "path": "C:/%s.ttf" % self.font.name
                        }
                    
                    # If effect settings exist, add to style
                    if self.effect:
                        gap_style["effectStyle"] = {
                            "id": self.effect.effect_id,
                            "path": "C:"  # Material files are not actually placed here
                        }
                        
                    styles.append(gap_style)
            
            # Check if default style needs to be added after the last style
            if sorted_styles[-1].end < len(self.text):
                # Add default style from end of last style to end of text
                end_style = {
                    "fill": {
                        "alpha": 1.0,
                        "content": {
                            "render_type": "solid",
                            "solid": {
                                "alpha": self.style.alpha,
                                "color": list(self.style.color)
                            }
                        }
                    },
                    "range": [sorted_styles[-1].end, len(self.text)],
                    "size": self.style.size,
                    "bold": self.style.bold,
                    "italic": self.style.italic,
                    "underline": self.style.underline,
                    "strokes": [self.border.export_json()] if self.border else []
                }
                
                # If global font settings exist, add to style
                if self.font:
                    end_style["font"] = {
                        "id": self.font.resource_id,
                        "path": "C:/%s.ttf" % self.font.name
                    }
                
                # If effect settings exist, add to style
                if self.effect:
                    end_style["effectStyle"] = {
                        "id": self.effect.effect_id,
                        "path": "C:"  # Material files are not actually placed here
                    }
                    
                styles.append(end_style)
        else:
            # If text_styles is empty, create a default style using global style
            style_item = {
                "fill": {
                    "alpha": 1.0,
                    "content": {
                        "render_type": "solid",
                        "solid": {
                            "alpha": self.style.alpha,
                            "color": list(self.style.color)
                        }
                    }
                },
                "range": [0, len(self.text)],
                "size": self.style.size,
                "bold": self.style.bold,
                "italic": self.style.italic,
                "underline": self.style.underline,
                "strokes": [self.border.export_json()] if self.border else []
            }
            
            # If shadow settings exist, add to style
            if self.shadow and self.shadow.has_shadow:
                style_item["shadows"] = [
                    {
                        "diffuse": self.shadow.smoothing / 6,  # diffuse = smoothing/6
                        "angle": self.shadow.angle,
                        "content": {
                            "solid": {
                                "color": [int(self.shadow.color[1:3], 16)/255, 
                                            int(self.shadow.color[3:5], 16)/255, 
                                            int(self.shadow.color[5:7], 16)/255]
                            }
                        },
                        "distance": self.shadow.distance,
                        "alpha": self.shadow.alpha
                    }
                ]
                
            # If global font settings exist, add to style
            if self.font:
                style_item["font"] = {
                    "id": self.font.resource_id,
                    "path": "C:/%s.ttf" % self.font.name  # Font files are not actually placed here
                }
            
            # If effect settings exist, add to style
            if self.effect:
                style_item["effectStyle"] = {
                    "id": self.effect.effect_id,
                    "path": "C:"  # Material files are not actually placed here
                }
                
            styles.append(style_item)

        content_json = {
            "styles": styles,
            "text": self.text
        }

        ret = {
            "id": self.material_id,
            "content": json.dumps(content_json, ensure_ascii=False),

            "typesetting": int(self.style.vertical),
            "alignment": self.style.align,
            "letter_spacing": self.style.letter_spacing * 0.05,
            "line_spacing": 0.02 + self.style.line_spacing * 0.05,

            "line_feed": 1,
            "line_max_width": 0.82,
            "force_apply_line_max_width": False,

            "check_flag": check_flag,

            "type": "subtitle" if self.is_subtitle else "text",

            "fixed_width": self.fixed_width,
            "fixed_height": self.fixed_height,

            # Blend (+4)
            # "global_alpha": 1.0,

            # Glow (+64), properties recorded by extra_material_refs

            # Shadow (+32)
            # "has_shadow": False,
            # "shadow_alpha": 0.9,
            # "shadow_angle": -45.0,
            # "shadow_color": "",
            # "shadow_distance": 5.0,
            # "shadow_point": {
            #     "x": 0.6363961030678928,
            #     "y": -0.6363961030678928
            # },
            # "shadow_smoothing": 0.45,

                        # Overall font settings
            "font_category_id": "",
            "font_category_name": "",
            "font_id": "",
            "font_name": "",
            "font_path": "",
            "font_resource_id": "",
            "font_size": float(self.style.size),
            "font_source_platform": 0,
            "font_team_id": "",
            "font_title": "none",
            "font_url": "",
            "fonts": [] if not self.text_styles else [
                # Generate fonts array from text_styles
                *[{
                    "category_id": "preset",
                    "category_name": "CapCut Preset",
                    "effect_id": style_range.font.resource_id if hasattr(style_range, 'font') and style_range.font else (self.font.resource_id if self.font else ""),
                    "file_uri": "",
                    "id": "BFBA9655-1FE5-41A0-A85D-577EFFF17BDD",
                    "path": "C:/%s.ttf" % (style_range.font.name if hasattr(style_range, 'font') and style_range.font else (self.font.name if self.font else "")),
                    "request_id": "20250713102314DA3D8F267527925ADC9A",
                    "resource_id": style_range.font.resource_id if hasattr(style_range, 'font') and style_range.font else (self.font.resource_id if self.font else ""),
                    "source_platform": 0,
                    "team_id": "",
                    "title": style_range.font.name if hasattr(style_range, 'font') and style_range.font else (self.font.name if self.font else "")
                } for style_range in self.text_styles if (hasattr(style_range, 'font') and style_range.font) or self.font]
            ],

            # Seems to be overridden by content
            # "text_alpha": 1.0,
            # "text_color": "#FFFFFF",
            # "text_curve": None,
            # "text_preset_resource_id": "",
            # "text_size": 30,
            # "underline": False,
        }

        if self.background:
            ret.update(self.background.export_json())

        # Add shadow parameters
        if self.shadow and self.shadow.has_shadow:
            shadow_json = self.shadow.export_json()
            ret.update(shadow_json)  # Merge shadow parameters into the returned dictionary

        if self.is_subtitle:
            ret.update({
                "add_type": 1,
                "group_id": self.subtitle_group_id or "",
                "initial_scale": 1.0,
                "text_size": 30,
                "text_color": "#FFFFFF",
                "text_alpha": 1.0,
                "global_alpha": 1.0,
                "use_effect_default_color": True,
                "subtitle_template_original_fontsize": 0.0,
                "preset_has_set_alignment": False,
                "is_rich_text": False,
                "is_words_linear": False,
                "words": {"end_time": [], "start_time": [], "text": []},
                "current_words": {"end_time": [], "start_time": [], "text": []},
                "caption_template_info": {
                    "category_id": "",
                    "category_name": "",
                    "effect_id": "",
                    "is_new": False,
                    "path": "",
                    "request_id": "",
                    "resource_id": "",
                    "resource_name": "",
                    "source_platform": 0,
                    "third_resource_id": ""
                },
                "recognize_type": 0,
                "recognize_task_id": "",
                "recognize_text": "",
                "language": "",
            })

        return ret
