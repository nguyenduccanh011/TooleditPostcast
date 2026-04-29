import pyJianYingDraft as draft
from util import generate_draft_url, hex_to_rgb
from pyJianYingDraft import trange, Font_type
from typing import Optional, List  # add List type hint
from pyJianYingDraft import exceptions
from create_draft import get_or_create_draft
from pyJianYingDraft.text_segment import TextBubble, TextEffect, TextStyleRange
from i18n import t


def _normalize_font_key(value: str) -> str:
    return value.strip().lower().replace(" ", "").replace("_", "").replace("-", "")


def _find_font_key(font: str) -> str | None:
    normalized = _normalize_font_key(font)
    for key in Font_type.__members__.keys():
        if _normalize_font_key(key) == normalized:
            return key
    return None


def _resolve_font_or_fallback(font: Optional[str]) -> Font_type | None:
    """Wrapper around the shared _resolve_font_safe for backward compatibility."""
    from pyJianYingDraft.text_segment import _resolve_font_safe
    return _resolve_font_safe(font)

def _resolve_font_or_fallback_OLD(font: Optional[str]) -> Font_type | None:
    """DEPRECATED - kept for reference only."""
    if font is None:
        return None

    key = _find_font_key(font)
    if key is not None:
        return getattr(Font_type, key)

    for fallback_key in ("Poppins_Regular", "Inter_Black", "Nunito", "SourceHanSansCN_Regular"):
        if fallback_key in Font_type.__members__:
            print(f"[add_text_impl] Unsupported font '{font}', fallback to '{fallback_key}'.")
            return getattr(Font_type, fallback_key)

    # Last-resort fallback
    first_key = next(iter(Font_type.__members__.keys()), None)
    if first_key is not None:
        print(f"[add_text_impl] Unsupported font '{font}', fallback to '{first_key}'.")
        return getattr(Font_type, first_key)
    return None


def add_text_impl(
    text: str,
    start: float,
    end: float,
    draft_id: str | None = None,  # Python 3.10+ new syntax
    transform_y: float = -0.8,
    transform_x: float = 0,
    font: Optional[str] = None,
    font_color: str = "#ffffff",
    font_size: float = 8.0,
    track_name: str = "text_main",
    vertical: bool = False,
    bold: bool = False,
    italic: bool = False,
    underline: bool = False,
    font_alpha: float = 1.0,
    # Border parameters
    border_alpha: float = 1.0,
    border_color: str = "#000000",
    border_width: float = 0.0,
    # Background parameters
    background_color: str = "#000000",
    background_style: int = 1,
    background_alpha: float = 0.0,
    background_round_radius: float = 0.0,
    background_height: float = 0.14,
    background_width: float = 0.14,
    background_horizontal_offset: float = 0.5,
    background_vertical_offset: float = 0.5,
    # Shadow parameters
    shadow_enabled: bool = False,
    shadow_alpha: float = 0.9,
    shadow_angle: float = -45.0,
    shadow_color: str = "#000000",
    shadow_distance: float = 5.0,
    shadow_smoothing: float = 0.15,
    # Bubble effect
    bubble_effect_id: str | None = None,
    bubble_resource_id: str | None = None,
    # Text effect
    effect_effect_id: str | None = None,
    intro_animation: str | None = None,
    intro_duration: float = 0.5,
    outro_animation: str | None = None,
    outro_duration: float = 0.5,
    width: int = 1080,
    height: int = 1920,
    fixed_width: float = -1,  # Text fixed width ratio, default -1 means not fixed
    fixed_height: float = -1,  # Text fixed height ratio, default -1 means not fixed
    is_subtitle: bool = False,
    subtitle_group_id: str = "",
    # Multi-style text parameters
    text_styles: Optional[List[TextStyleRange]] = None,  # Style list for different parts of text
):
    """
    Add text subtitle to the specified draft (configurable parameter version)
    :param text: Text content
    :param start: Start time (seconds)
    :param end: End time (seconds)
    :param draft_id: Draft ID (optional, default None creates a new draft)
    :param transform_y: Y-axis position (default -0.8, bottom of screen)
    :param transform_x: X-axis position (default 0, center of screen)
    :param font: Font name (supports all fonts in Font_type)
    :param font_color: Font color #FFF0FF
    :param font_size: Font size (float value, default 8.0)
    :param track_name: Track name
    :param vertical: Whether to display vertically (default False)
    :param bold: Bold style
    :param italic: Italic style
    :param underline: Underline style
    :param font_alpha: Text transparency, range 0.0-1.0 (default 1.0, completely opaque)
    :param border_alpha: Border transparency, range 0.0-1.0 (default 1.0)
    :param border_color: Border color (default black)
    :param border_width: Border width (default 0.0, no border display)
    :param background_color: Background color (default black)
    :param background_style: Background style (default 1)
    :param background_alpha: Background transparency (default 0.0, no background display)
    :param background_round_radius: Background corner radius, range 0.0-1.0 (default 0.0)
    :param background_height: Background height, range 0.0-1.0 (default 0.14)
    :param background_width: Background width, range 0.0-1.0 (default 0.14)
    :param background_horizontal_offset: Background horizontal offset, range 0.0-1.0 (default 0.5)
    :param background_vertical_offset: Background vertical offset, range 0.0-1.0 (default 0.5)
    :param shadow_enabled: Whether to enable shadow (default False)
    :param shadow_alpha: Shadow alpha, range 0.0-1.0 (default 0.9)
    :param shadow_angle: Shadow angle, range -180.0 to 180.0 (default -45.0)
    :param shadow_color: Shadow color (default black)
    :param shadow_distance: Shadow distance (default 5.0)
    :param shadow_smoothing: Shadow smoothing, range 0.0-1.0 (default 0.15)
    :param bubble_effect_id: Bubble effect ID
    :param bubble_resource_id: Bubble resource ID
    :param effect_effect_id: Text effect ID
    :param intro_animation: Intro animation type
    :param intro_duration: Intro animation duration (seconds), default 0.5 seconds
    :param outro_animation: Outro animation type
    :param outro_duration: Outro animation duration (seconds), default 0.5 seconds
    :param width: Video width (pixels)
    :param height: Video height (pixels)
    :param fixed_width: Text fixed width ratio, range 0.0-1.0, default -1 means not fixed
    :param fixed_height: Text fixed height ratio, range 0.0-1.0, default -1 means not fixed
    :param is_subtitle: Export text with subtitle material layout/group behavior
    :param subtitle_group_id: Shared subtitle group id for batch style editing
    :param text_styles: Style list for different parts of text，each item is a TextStyleRange
    :return: Updated draft information
    """
    # Resolve font key with safe fallback (prevents hard failure on unknown system fonts).
    font_type = _resolve_font_or_fallback(font)
    
    # Validate alpha value range
    if not 0.0 <= font_alpha <= 1.0:
        raise ValueError("alpha value must be between 0.0 and 1.0")
    if not 0.0 <= border_alpha <= 1.0:
        raise ValueError("border_alpha value must be between 0.0 and 1.0")
    if not 0.0 <= background_alpha <= 1.0:
        raise ValueError("background_alpha value must be between 0.0 and 1.0")
    
    # Get or create draft
    draft_id, script = get_or_create_draft(
        draft_id=draft_id,
        width=width,
        height=height
    )

    # Add text track
    if track_name is not None:
        try:
            imported_track = script.get_imported_track(draft.Track_type.text, name=track_name)
            # If no exception is thrown, the track already exists
        except exceptions.TrackNotFound:
            # Track doesn't exist, create a new track
            script.add_track(draft.Track_type.text, track_name=track_name)
    else:
        script.add_track(draft.Track_type.audio)

    # Convert hexadecimal color to RGB tuple
    try:
        rgb_color = hex_to_rgb(font_color)
        rgb_border_color = hex_to_rgb(border_color)
    except ValueError as e:
        raise ValueError(f"Color parameter error: {str(e)}")
    
    # Create text_border
    text_border = None
    if border_width > 0:
        text_border = draft.Text_border(
            alpha=border_alpha,
            color=rgb_border_color,
            width=border_width
        )
    
    # Create text_background
    text_background = None
    if background_alpha > 0:
        text_background = draft.Text_background(
            color=background_color,
            style=background_style,
            alpha=background_alpha,
            round_radius=background_round_radius,
            height=background_height,
            width=background_width,
            horizontal_offset=background_horizontal_offset,
            vertical_offset=background_vertical_offset
        )
    
    # Create text_shadow
    text_shadow = None
    if shadow_enabled:
        text_shadow = draft.Text_shadow(
            has_shadow=shadow_enabled,
            alpha=shadow_alpha,
            angle=shadow_angle,
            color=shadow_color,
            distance=shadow_distance,
            smoothing=shadow_smoothing
        )

    # Create bubble effect
    text_bubble = None
    if bubble_effect_id and bubble_resource_id:
        text_bubble = TextBubble(
            effect_id=bubble_effect_id,
            resource_id=bubble_resource_id
        )
    
    # Create text effect
    text_effect = None
    if effect_effect_id:
        text_effect = TextEffect(
            effect_id=effect_effect_id
        )
    
    # Convert ratio to pixel value
    pixel_fixed_width = -1
    pixel_fixed_height = -1
    if fixed_width > 0:
        pixel_fixed_width = int(fixed_width * script.width)
    if fixed_height > 0:
        pixel_fixed_height = int(fixed_height * script.height)
    
    # Create text segment (using configurable parameters)
    text_segment = draft.Text_segment(
        text,
        trange(f"{start}s", f"{end-start}s"),
        font=font_type,  # Use font from Font_type
        style=draft.Text_style(
            color=rgb_color,
            size=font_size,
            bold=bold,
            italic=italic,
            underline=underline,
            align=1,
            vertical=vertical,  # Set whether to display vertically
            alpha=font_alpha  # Set transparency
        ),
        clip_settings=draft.Clip_settings(transform_y=transform_y, transform_x=transform_x),
        border=text_border,
        background=text_background,
        shadow=text_shadow,
        fixed_width=pixel_fixed_width,
        fixed_height=pixel_fixed_height,
        is_subtitle=is_subtitle,
        subtitle_group_id=subtitle_group_id
    )

    # Apply multi-style text settings
    if text_styles:
        for style_range in text_styles:
            # Validate range
            if style_range.start < 0 or style_range.end > len(text) or style_range.start >= style_range.end:
                raise ValueError(
                    t(
                        "invalid_text_range",
                        start=style_range.start,
                        end=style_range.end,
                        length=len(text),
                    )
                )
            
            # Apply style to text range
            text_segment.add_text_style(style_range)

    if text_bubble:
        text_segment.add_bubble(text_bubble.effect_id, text_bubble.resource_id)
    if text_effect:
        text_segment.add_effect(text_effect.effect_id)

    # Add intro animation
    if intro_animation:
        try:
            animation_type = getattr(draft.CapCut_Text_intro, intro_animation)
            # Convert seconds to microseconds
            duration_microseconds = int(intro_duration * 1000000)
            text_segment.add_animation(animation_type, duration_microseconds)  # Add intro animation, set duration
        except:
            print(f"Warning: Unsupported intro animation type {intro_animation}, this parameter will be ignored")

    # Add outro animation
    if outro_animation:
        try:
            animation_type = getattr(draft.CapCut_Text_outro, outro_animation)
            # Convert seconds to microseconds
            duration_microseconds = int(outro_duration * 1000000)
            text_segment.add_animation(animation_type, duration_microseconds)  # Add outro animation, set duration
        except:
            print(f"Warning: Unsupported outro animation type {outro_animation}, this parameter will be ignored")

    # Add text segment to track
    script.add_segment(text_segment, track_name=track_name)

    return {
        "draft_id": draft_id,
        "draft_url": generate_draft_url(draft_id)
    }
