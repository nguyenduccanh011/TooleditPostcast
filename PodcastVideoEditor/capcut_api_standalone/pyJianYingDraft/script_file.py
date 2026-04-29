import os
import json
import math
from copy import deepcopy

from typing import Optional, Literal, Union, overload
from typing import Type, Dict, List, Any


from . import util
from . import exceptions
from .template_mode import ImportedTrack, EditableTrack, ImportedMediaTrack, ImportedTextTrack, Shrink_mode, Extend_mode, import_track
from .time_util import Timerange, tim, srt_tstamp
from .local_materials import Video_material, Audio_material
from .segment import Base_segment, Speed, Clip_settings
from .audio_segment import Audio_segment, Audio_fade, Audio_effect
from .video_segment import Video_segment, Sticker_segment, Segment_animations, Video_effect, Transition, Filter, BackgroundFilling
from .effect_segment import Effect_segment, Filter_segment
from .text_segment import Text_segment, Text_style, TextBubble, Text_border, Text_background, TextEffect
from .track import Track_type, Base_track, Track

from settings.local import IS_CAPCUT_ENV
from .metadata import Video_scene_effect_type, Video_character_effect_type, Filter_type, Font_type

class Script_material:
    """Material information section of the draft file"""

    audios: List[Audio_material]
    """Audio material list"""
    videos: List[Video_material]
    """Video material list"""
    stickers: List[Dict[str, Any]]
    """Sticker material list"""
    texts: List[Dict[str, Any]]
    """Text material list"""

    audio_effects: List[Audio_effect]
    """Audio effect list"""
    audio_fades: List[Audio_fade]
    """Audio fade in/out effect list"""
    animations: List[Segment_animations]
    """Animation material list"""
    video_effects: List[Video_effect]
    """Video effect list"""

    speeds: List[Speed]
    """Speed change list"""
    masks: List[Dict[str, Any]]
    """Mask list"""
    transitions: List[Transition]
    """Transition effect list"""
    filters: List[Union[Filter, TextBubble]]
    """Filter/text decoration/text bubble list, exported to `effects`"""
    canvases: List[BackgroundFilling]
    """Background filling list"""

    def __init__(self):
        self.audios = []
        self.videos = []
        self.stickers = []
        self.texts = []

        self.audio_effects = []
        self.audio_fades = []
        self.animations = []
        self.video_effects = []

        self.speeds = []
        self.masks = []
        self.transitions = []
        self.filters = []
        self.canvases = []

    @overload
    def __contains__(self, item: Union[Video_material, Audio_material]) -> bool: ...
    @overload
    def __contains__(self, item: Union[Audio_fade, Audio_effect]) -> bool: ...
    @overload
    def __contains__(self, item: Union[Segment_animations, Video_effect, Transition, Filter]) -> bool: ...

    def __contains__(self, item) -> bool:
        if isinstance(item, Video_material):
            return item.material_id in [video.material_id for video in self.videos]
        elif isinstance(item, Audio_material):
            return item.material_id in [audio.material_id for audio in self.audios]
        elif isinstance(item, Audio_fade):
            return item.fade_id in [fade.fade_id for fade in self.audio_fades]
        elif isinstance(item, Audio_effect):
            return item.effect_id in [effect.effect_id for effect in self.audio_effects]
        elif isinstance(item, Segment_animations):
            return item.animation_id in [ani.animation_id for ani in self.animations]
        elif isinstance(item, Video_effect):
            return item.global_id in [effect.global_id for effect in self.video_effects]
        elif isinstance(item, Transition):
            return item.global_id in [transition.global_id for transition in self.transitions]
        elif isinstance(item, Filter):
            return item.global_id in [filter_.global_id for filter_ in self.filters]
        else:
            raise TypeError("Invalid argument type '%s'" % type(item))

    def export_json(self) -> Dict[str, List[Any]]:
        result = {
            "ai_translates": [],
            "audio_balances": [],
            "audio_effects": [effect.export_json() for effect in self.audio_effects],
            "audio_fades": [fade.export_json() for fade in self.audio_fades],
            "audio_track_indexes": [],
            "audios": [audio.export_json() for audio in self.audios],
            "beats": [],
            "canvases": [canvas.export_json() for canvas in self.canvases],
            "chromas": [],
            "color_curves": [],
            "digital_humans": [],
            "drafts": [],
            "effects": [_filter.export_json() for _filter in self.filters],
            "flowers": [],
            "green_screens": [],
            "handwrites": [],
            "hsl": [],
            "images": [],
            "log_color_wheels": [],
            "loudnesses": [],
            "manual_deformations": [],
            "material_animations": [ani.export_json() for ani in self.animations],
            "material_colors": [],
            "multi_language_refs": [],
            "placeholders": [],
            "plugin_effects": [],
            "primary_color_wheels": [],
            "realtime_denoises": [],
            "shapes": [],
            "smart_crops": [],
            "smart_relights": [],
            "sound_channel_mappings": [],
            "speeds": [spd.export_json() for spd in self.speeds],
            "stickers": self.stickers,
            "tail_leaders": [],
            "text_templates": [],
            "texts": self.texts,
            "time_marks": [],
            "transitions": [transition.export_json() for transition in self.transitions],
            "video_effects": [effect.export_json() for effect in self.video_effects],
            "video_trackings": [],
            "videos": [video.export_json() for video in self.videos],
            "vocal_beautifys": [],
            "vocal_separations": []
        }

        # Decide whether to use common_mask or masks based on IS_CAPCUT_ENV
        if IS_CAPCUT_ENV:
            result["common_mask"] = self.masks
        else:
            result["masks"] = self.masks
            
        return result

class Script_file:
    """CapCut Draft file, most interfaces are defined here"""

    save_path: Optional[str]
    """Draft file save path, only valid in template mode"""
    content: Dict[str, Any]
    """Draft file content"""

    width: int
    """Video width in pixels"""
    height: int
    """Video height in pixels"""
    fps: int
    """Video frame rate"""
    duration: int
    """Total video duration in microseconds"""

    materials: Script_material
    """Material information section of the draft file"""
    tracks: Dict[str, Track]
    """TrackInfo"""

    imported_materials: Dict[str, List[Dict[str, Any]]]
    """Imported material info"""
    imported_tracks: List[Track]
    """ImportInfo"""

    TEMPLATE_FILE = "draft_content_template.json"

    def __init__(self, width: int, height: int, fps: int = 30):
        """Create a CapCut draft

        Args:
            width (int): Video width in pixels
            height (int): Video height in pixels
            fps (int, optional): Video frame rate. Defaults to 30.
        """
        self.save_path = None

        self.width = width
        self.height = height
        self.fps = fps
        self.duration = 0

        self.materials = Script_material()
        self.tracks = {}

        self.imported_materials = {}
        self.imported_tracks = []

        with open(os.path.join(os.path.dirname(__file__), self.TEMPLATE_FILE), "r", encoding="utf-8") as f:
            self.content = json.load(f)

    @staticmethod
    def load_template(json_path: str) -> "Script_file":
        """Load draft template from JSON file

        Args:
            json_path (str): JSON file path

        Raises:
            `FileNotFoundError`: JSON file does not exist
        """
        obj = Script_file(**util.provide_ctor_defaults(Script_file))
        obj.save_path = json_path
        if not os.path.exists(json_path):
            raise FileNotFoundError("JSONFile '%s' does not exist" % json_path)
        with open(json_path, "r", encoding="utf-8") as f:
            obj.content = json.load(f)

        util.assign_attr_with_json(obj, ["fps", "duration"], obj.content)
        util.assign_attr_with_json(obj, ["width", "height"], obj.content["canvas_config"])

        obj.imported_materials = deepcopy(obj.content["materials"])
        obj.imported_tracks = [import_track(track_data, obj.imported_materials) for track_data in obj.content["tracks"]]

        return obj

    def add_material(self, material: Union[Video_material, Audio_material]) -> "Script_file":
        """Add a material to the draft file"""
        if material in self.materials:  # Material already exists
            return self
        if isinstance(material, Video_material):
            self.materials.videos.append(material)
        elif isinstance(material, Audio_material):
            self.materials.audios.append(material)
        else:
            raise TypeError("Invalid material type: '%s'" % type(material))
        return self

    def add_track(self, track_type: Track_type, track_name: Optional[str] = None, *,
                  mute: bool = False,
                  relative_index: int = 0, absolute_index: Optional[int] = None) -> "Script_file":
        """Add a track with specified type and name to the draft file, with customizable track layer order

        Note: Video segments on the main video track (the bottommost video track) must start from 0s, otherwise CapCut will force-align them to 0s.

        To avoid confusion, omitting the name is only allowed when creating the first track of the same type

        Args:
            track_type (Track_type): Track type
            track_name (str, optional): Track name. Can only be omitted when creating the first track of the same type.
            mute (bool, optional): Whether the track is muted. Defaults to not muted.
            relative_index (int, optional): Relative layer position (among tracks of the same type), higher is closer to foreground. Defaults to 0.
            absolute_index (int, optional): Absolute layer position, higher is closer to foreground. This parameter directly overrides the `render_index` property of the corresponding segment, for experienced users.
                This parameter cannot be used together with `relative_index`.

        Raises:
            `NameError`: A track of the same type already exists and no name was specified, or track with the same name already exists
        """

        if track_name is None:
            if track_type in [track.track_type for track in self.tracks.values()]:
                raise NameError("'%s' already exists, please specify a name for the new track" % track_type)
            track_name = track_type.name
        if track_name in [track.name for track in self.tracks.values()]:
            return self

        if absolute_index is not None:
            render_index = absolute_index
        else:
            render_index = track_type.value.render_index + relative_index

        self.tracks[track_name] = Track(track_type, track_name, render_index, mute)
        return self

    def get_track(self, segment_type: Type[Base_segment], track_name: Optional[str]) -> Track:
        # Specified track name
        if track_name is not None:
            if track_name not in self.tracks:
                raise NameError("No track named  '%s' " % track_name)
            return self.tracks[track_name]
        # Find the unique track of the same type
        count = sum([1 for track in self.tracks.values() if track.track_type == segment_type])
        if count == 0: raise exceptions.TrackNotFound(f"No track accepting  '{segment_type}' ")
        if count > 1: raise NameError(f"Multiple tracks accepting  '{segment_type}' , please specify a track name")

        return next(track for track in self.tracks.values() if track.accept_segment_type == segment_type)

    def _get_track_and_imported_track(self, segment_type: Type[Base_segment], track_name: Optional[str]) -> List[Track]:
        """Get all tracks of the specified type (including regular tracks and imported tracks)
        
        Args:
            segment_type (Type[Base_segment]): SegmentType
            track_name (Optional[str]): Track name, if specified only returns the track with that name
            
        Returns:
            List[Track]: List of matching tracks
            
        Raises:
            NameError: Specified track name was not found
        """
        result_tracks = []
        
        # If a track name is specified
        if track_name is not None:
            # Search in regular tracks
            if track_name in self.tracks:
                result_tracks.append(self.tracks[track_name])
            # Search in imported tracks
            for track in self.imported_tracks:
                if track.name == track_name:
                    result_tracks.append(track)
            if not result_tracks:
                raise NameError("No track named  '%s' " % track_name)
        else:
            # Search regular tracks for those accepting this segment type
            for track in self.tracks.values():
                if track.accept_segment_type == segment_type:
                    result_tracks.append(track)
            # Search imported tracks for those accepting this segment type
            for track in self.imported_tracks:
                if track.accept_segment_type == segment_type:
                    result_tracks.append(track)
            if not result_tracks:
                raise NameError("No track accepting  '%s' " % segment_type)
            if len(result_tracks) > 1:
                raise NameError("Multiple tracks accepting  '%s' , please specify a track name" % segment_type)
        
        return result_tracks

    def add_segment(self, segment: Union[Video_segment, Sticker_segment, Audio_segment, Text_segment],
                    track_name: Optional[str] = None) -> "Script_file":
        """Add a segment to the specified track

        Args:
            segment (`Video_segment`, `Sticker_segment`, `Audio_segment`, or `Text_segment`): The segment to add
            track_name (`str`, optional): Target track name. Can be omitted when there is only one track of this type.

        Raises:
            `NameError`: Specified track name not found, or `track_name` parameter not provided when required
            `TypeError`: Segment type does not match track type
            `SegmentOverlap`: New segment overlaps with existing segments
        """
        tracks = self._get_track_and_imported_track(type(segment), track_name)
        target = tracks[0] 

        # Add to track and update duration
        target.add_segment(segment)
        self.duration = max(self.duration, segment.end)

        # Automatically add related materials
        if isinstance(segment, Video_segment):
            # Entry/exit animations
            if (segment.animations_instance is not None) and (segment.animations_instance not in self.materials):
                self.materials.animations.append(segment.animations_instance)
            # Effect
            for effect in segment.effects:
                if effect not in self.materials:
                    self.materials.video_effects.append(effect)
            # Filter
            for filter_ in segment.filters:
                if filter_ not in self.materials:
                    self.materials.filters.append(filter_)
            # Mask
            if segment.mask is not None:
                self.materials.masks.append(segment.mask.export_json())
            # Transition
            if (segment.transition is not None) and (segment.transition not in self.materials):
                self.materials.transitions.append(segment.transition)
            # Background filling
            if segment.background_filling is not None:
                self.materials.canvases.append(segment.background_filling)

            self.materials.speeds.append(segment.speed)
        elif isinstance(segment, Sticker_segment):
            self.materials.stickers.append(segment.export_material())
        elif isinstance(segment, Audio_segment):
            # Fade in/out
            if (segment.fade is not None) and (segment.fade not in self.materials):
                self.materials.audio_fades.append(segment.fade)
            # Effect
            for effect in segment.effects:
                if effect not in self.materials:
                    self.materials.audio_effects.append(effect)
            self.materials.speeds.append(segment.speed)
        elif isinstance(segment, Text_segment):
            # Entry/exit animations
            if (segment.animations_instance is not None) and (segment.animations_instance not in self.materials):
                self.materials.animations.append(segment.animations_instance)
            # Bubble effect
            if segment.bubble is not None:
                self.materials.filters.append(segment.bubble)
            # Text effect
            if segment.effect is not None:
                self.materials.filters.append(segment.effect)
            # Font style
            self.materials.texts.append(segment.export_material())

        # AddSegmentMaterial
        if isinstance(segment, (Video_segment, Audio_segment)):
            self.add_material(segment.material_instance)

        return self

    def add_effect(self, effect: Union[Video_scene_effect_type, Video_character_effect_type],
                   t_range: Timerange, track_name: Optional[str] = None, *,
                   params: Optional[List[Optional[float]]] = None) -> "Script_file":
        """Add an effect segment to the specified effect track

        Args:
            effect (`Video_scene_effect_type` or `Video_character_effect_type`): Effect type
            t_range (`Timerange`): Time range of the effect segment
            track_name (`str`, optional): Target track name. Can be omitted when there is only one effect track.
            params (`List[Optional[float]]`, optional): Effect parameter list. Items not provided or set to None use default values.
                Parameter value range (0~100) is consistent with CapCut. Which parameters an effect type has and their order are defined by the enum member annotations.

        Raises:
            `NameError`: Specified track name not found, or `track_name` parameter not provided when required
            `TypeError`: Specified track is not an effect track
            `ValueError`: New segment overlaps with existing segments、Number of parameters providedexceeds the parameter count for this effect type, orParametervalue out of range.
        """
        target = self.get_track(Effect_segment, track_name)

        # Add to track and update duration
        segment = Effect_segment(effect, t_range, params)
        target.add_segment(segment)
        self.duration = max(self.duration, t_range.start + t_range.duration)

        # Automatically add related materials
        if segment.effect_inst not in self.materials:
            self.materials.video_effects.append(segment.effect_inst)
        return self

    def add_filter(self, filter_meta: Filter_type, t_range: Timerange,
                   track_name: Optional[str] = None, intensity: float = 100.0) -> "Script_file":
        """Add a filter segment to the specified filter track

        Args:
            filter_meta (`Filter_type`): Filter type
            t_range (`Timerange`): Time range of the filter segment
            track_name (`str`, optional): Target track name. Can be omitted when there is only one filter track.
            intensity (`float`, optional): Filter intensity (0-100). Only effective when the selected filter supports intensity adjustment. Defaults to 100.

        Raises:
            `NameError`: Specified track name not found, or `track_name` parameter not provided when required
            `TypeError`: Specified track is not a filter track
            `ValueError`: New segment overlaps with existing segments
        """
        target = self.get_track(Filter_segment, track_name)

        # Add to track and update duration
        segment = Filter_segment(filter_meta, t_range, intensity / 100.0)  # Convert to 0-1 range
        target.add_segment(segment)
        self.duration = max(self.duration, t_range.end)

        # Automatically add related materials
        self.materials.filters.append(segment.material)
        return self

    def import_srt(self, srt_content: str, track_name: str, *,
                   time_offset: Union[str, float] = 0.0,
                   style_reference: Optional[Text_segment] = None,
                   font: Optional[str] = None,
                   text_style: Text_style = Text_style(size=5, align=1),
                   clip_settings: Optional[Clip_settings] = Clip_settings(transform_y=-0.8),
                   border: Optional[Text_border] = None,
                   background: Optional[Text_background] = None,
                   bubble: Optional[TextBubble] = None,
                   effect: Optional[TextEffect] = None) -> "Script_file":
        """Import subtitles from an SRT file, supports passing a `Text_segment` as a style reference

        Note: By default, the `clip_settings` property of the reference segment will not be used. If needed, explicitly pass `clip_settings=None` to this function.

        Args:
            srt_content (`str`): SRT subtitle content or local file path
            track_name (`str`): Target text track name, will be auto-created if it does not exist
            style_reference (`Text_segment`, optional): Text segment used as style reference. If provided, its style will be used.
            font (`Optional[str]`, optional): Font. Defaults to None.
            time_offset (`Union[str, float]`, optional): Overall subtitle time offset in microseconds. Defaults to 0.
            text_style (`Text_style`, optional): Subtitle style. Defaults to mimicking CapCut subtitle import style. Overridden by `style_reference`.
            clip_settings (`Clip_settings`, optional): Image adjustment settings. Defaults to mimicking CapCut subtitle import settings. Overrides `style_reference` settings unless set to `None`.
            border (`Text_border`, optional): Border settings. By default does not modify the border settings from the style reference.
            background (`Text_background`, optional): Background settings. By default does not modify the background settings from the style reference.
            bubble (`TextBubble`, optional): Bubble effect. By default does not add a bubble effect.
            effect (`TextEffect`, optional): Text effect. By default does not add a text effect.

        Raises:
            `NameError`: Track with the same name already exists
            `TypeError`: Track type mismatch
        """
        if style_reference is None and clip_settings is None:
            raise ValueError("Please provide `clip_settings` parameter when no style reference is provided")

        font_type = None
        if font:
            from .text_segment import _resolve_font_safe
            font_type = _resolve_font_safe(font)

        time_offset = tim(time_offset)
        # Check if track_name exists in self.tracks or self.imported_tracks
        track_exists = (track_name in self.tracks) or any(track.name == track_name for track in self.imported_tracks)
        if not track_exists:
            self.add_track(Track_type.text, track_name, relative_index=999)  # On top of all text tracks

        # CapCut groups subtitles imported in one batch by a shared group_id (e.g. "en-US_<timestamp_ms>").
        import time as _time
        subtitle_group_id = "en-US_%d" % int(_time.time() * 1000)

        # Check if it is a local file path
        if os.path.exists(srt_content):
            with open(srt_content, "r", encoding="utf-8-sig") as srt_file:
                lines = srt_file.readlines()
        else:
            # Split content by lines directly
            lines = srt_content.splitlines()

        def __add_text_segment(text: str, t_range: Timerange) -> None:
            fixed_width = -1
            if self.width < self.height:  # Portrait
                fixed_width = int(1080 * 0.6)
            else:  # Landscape
                fixed_width = int(1920 * 0.7)
            
            if style_reference:
                seg = Text_segment.create_from_template(text, t_range, style_reference)
                if clip_settings is not None:
                    seg.clip_settings = deepcopy(clip_settings)
                # Copy other optional properties
                if border:
                    seg.border = deepcopy(border)
                if background:
                    seg.background = deepcopy(background)
                if bubble:
                    seg.bubble = deepcopy(bubble)
                if effect:
                    seg.effect = deepcopy(effect)
                # Set fixed width/height
                seg.fixed_width = fixed_width
                # SetFont
                if font_type:
                    seg.font = font_type.value
                # Mark as subtitle so export_material emits CapCut subtitle structure
                seg.is_subtitle = True
                seg.subtitle_group_id = subtitle_group_id
                seg.extra_material_refs = []
                if seg.animations_instance is None:
                    seg.animations_instance = Segment_animations()
                seg.extra_material_refs.append(seg.animations_instance.animation_id)
            else:
                seg = Text_segment(text, t_range, style=text_style, clip_settings=clip_settings,
                                  border=border, background=background,
                                  font = font_type,
                                  fixed_width=fixed_width,
                                  is_subtitle=True,
                                  subtitle_group_id=subtitle_group_id)
                # Add bubble and text effects
                if bubble:
                    seg.bubble = deepcopy(bubble)
                if effect:
                    seg.effect = deepcopy(effect)
            # If there are bubble or text effects, add them to the material list
            if bubble:
                self.materials.filters.append(bubble)
            if effect:
                self.materials.filters.append(effect)
            self.add_segment(seg, track_name)

        index = 0
        text: str = ""
        text_trange: Timerange
        read_state: Literal["index", "timestamp", "content"] = "index"
        while index < len(lines):
            line = lines[index].strip()
            if read_state == "index":
                if len(line) == 0:
                    index += 1
                    continue
                if not line.isdigit():
                    raise ValueError("Expected a number at line %d, got '%s'" % (index+1, line))
                index += 1
                read_state = "timestamp"
            elif read_state == "timestamp":
                # Read timestamp
                start_str, end_str = line.split(" --> ")
                start, end = srt_tstamp(start_str), srt_tstamp(end_str)
                text_trange = Timerange(start + time_offset, end - start)

                index += 1
                read_state = "content"
            elif read_state == "content":
                # Content ended, generate segment
                if len(line) == 0:
                    __add_text_segment(text.strip(), text_trange)

                    text = ""
                    read_state = "index"
                else:
                    text += line + "\n"
                index += 1

        # Add the last segment
        if len(text) > 0:
            __add_text_segment(text.strip(), text_trange)

        return self

    def get_imported_track(self, track_type: Literal[Track_type.video, Track_type.audio, Track_type.text],
                           name: Optional[str] = None, index: Optional[int] = None) -> Track:
        """Get the imported track of the specified type for replacement operations

        It is recommended to filter by track name (if known)

        Args:
            track_type (`Track_type.video`, `Track_type.audio` or `Track_type.text`): Track type, currently only audio/video and text tracks are supported
            name (`str`, optional): Track name, If not specified, does not filter by name.
            index (`int`, optional): Track index among **imported tracks of the same type**, with 0 being the bottommost track. If not specified, does not filter by index.

        Raises:
            `TrackNotFound`: No matching track found
            `AmbiguousTrack`: Multiple matching tracks found
        """
        tracks_of_same_type: List[Track] = []
        for track in self.imported_tracks:
            if track.track_type == track_type:
                assert isinstance(track, Track)
                tracks_of_same_type.append(track)

        ret: List[Track] = []
        for ind, track in enumerate(tracks_of_same_type):
            if (name is not None) and (track.name != name): continue
            if (index is not None) and (ind != index): continue
            ret.append(track)

        if len(ret) == 0:
            raise exceptions.TrackNotFound(
                "No matching track found: track_type=%s, name=%s, index=%s" % (track_type, name, index))
        if len(ret) > 1:
            raise exceptions.AmbiguousTrack(
                "Multiple matching tracks found: track_type=%s, name=%s, index=%s" % (track_type, name, index))

        return ret[0]

    def import_track(self, source_file: "Script_file", track: EditableTrack, *,
                     offset: Union[str, int] = 0,
                     new_name: Optional[str] = None, relative_index: Optional[int] = None) -> "Script_file":
        """add one`Editable_track`Importto current`Script_file`in, such as from templateDraftinImportspecificTextor videoTrackto the currently editingDraft filein

        note: this method will preserve eachSegmentand itsMaterialof/theid, therefore does not support adding to the sameDraftmultiple timesImportsameTrack

        Args:
            source_file (`Script_file`): sourceFile，contains toImport
            track (`EditableTrack`): to/needImport, can be accessed via`get_imported_track`methodGet.
            offset (`str | int`, optional): Trackof/theTime offset (microseconds), can be an integer microsecond value or time string(such as"1s"). DefaultnotAddOffset.
            new_name (`str`, optional): newTrack name, Defaultuse sourceTrack name.
            relative_index (`int`, optional): relative index，used to adjustImportTrackrendering layer order of. Defaultmaintain original layer order.
        """
        # directly copy originalTrackstructure, modify rendering layer order as needed
        imported_track = deepcopy(track)
        if relative_index is not None:
            imported_track.render_index = track.track_type.value.render_index + relative_index
        if new_name is not None:
            imported_track.name = new_name

        # applyOffsetamount
        offset_us = tim(offset)
        if offset_us != 0:
            for seg in imported_track.segments:
                seg.target_timerange.start = max(0, seg.target_timerange.start + offset_us)
        self.imported_tracks.append(imported_track)

        # collect all that need to be copiedMaterialID
        material_ids = set()
        segments: List[Dict[str, Any]] = track.raw_data.get("segments", [])
        for segment in segments:
            # mainMaterialID
            material_id = segment.get("material_id")
            if material_id:
                material_ids.add(material_id)

            # extra_material_refsin theMaterialID
            extra_refs: List[str] = segment.get("extra_material_refs", [])
            material_ids.update(extra_refs)

        # copyMaterial
        for material_type, material_list in source_file.imported_materials.items():
            for material in material_list:
                if material.get("id") in material_ids:
                    self.imported_materials[material_type].append(deepcopy(material))
                    material_ids.remove(material.get("id"))

        assert len(material_ids) == 0, "following not foundMaterial: %s" % material_ids

        # UpdatetotalDuration
        self.duration = max(self.duration, track.end_time)

        return self

    def replace_material_by_name(self, material_name: str, material: Union[Video_material, Audio_material],
                                 replace_crop: bool = False) -> "Script_file":
        """replace specifiedNameof/theMaterial, and affects all that reference itSegment

        this method will not change the correspondingSegmentof/theDurationand reference range(`source_timerange`), especially suitable for imagesMaterial

        Args:
            material_name (`str`): to be replacedMaterialName
            material (`Video_material` or `Audio_material`): newMaterial, currently only supports video and audio
            replace_crop (`bool`, optional): whether to replace originalMaterialof/theCropSet, Defaultis false. only for videoMaterialvalid/effective.

        Raises:
            `MaterialNotFound`: based on specifiedNamenot found matching newMaterialof the same typeMaterial
            `AmbiguousMaterial`: based on specifiedNamefound multiple matching newMaterialof the same typeMaterial
        """
        video_mode = isinstance(material, Video_material)
        # find/searchMaterial
        target_json_obj: Optional[Dict[str, Any]] = None
        target_material_list = self.imported_materials["videos" if video_mode else "audios"]
        name_key = "material_name" if video_mode else "name"
        for mat in target_material_list:
            if mat[name_key] == material_name:
                if target_json_obj is not None:
                    raise exceptions.AmbiguousMaterial(
                        "found multiple with name '%s', Typeis/as '%s' of/theMaterial" % (material_name, type(material)))
                target_json_obj = mat
        if target_json_obj is None:
            raise exceptions.MaterialNotFound("not found with name '%s', Typeis/as '%s' of/theMaterial" % (material_name, type(material)))

        # UpdateMaterialInfo
        target_json_obj.update({name_key: material.material_name, "path": material.path, "duration": material.duration})
        if video_mode:
            target_json_obj.update({"width": material.width, "height": material.height, "material_type": material.material_type})
            if replace_crop:
                target_json_obj.update({"crop": material.crop_settings.export_json()})

        return self

    def replace_material_by_seg(self, track: EditableTrack, segment_index: int, material: Union[Video_material, Audio_material],
                                source_timerange: Optional[Timerange] = None, *,
                                handle_shrink: Shrink_mode = Shrink_mode.cut_tail,
                                handle_extend: Union[Extend_mode, List[Extend_mode]] = Extend_mode.cut_material_tail) -> "Script_file":
        """replace specified audio/videoTrackspecify onSegmentof/theMaterial, speed change not yet supportedSegmentof/theMaterialreplace

        Args:
            track (`Editable_track`): to replaceMaterial, by`get_imported_track`Get
            segment_index (`int`): to replaceMaterialof/theSegmentindex, from0Start
            material (`Video_material` or `Audio_material`): newMaterial, must match originalMaterialTypeconsistent
            source_timerange (`Timerange`, optional): from originalMaterialinextract/cropof/theTime range, Defaultfor the full duration, if it's an imageMaterialthenDefaultwith originalSegmentequal length.
            handle_shrink (`Shrink_mode`, optional): newMaterialthan originalMaterialshorterProcessmethod/way, Defaultis/asCroptail/end, makeSegmentlength andMaterialconsistent.
            handle_extend (`Extend_mode` or `List[Extend_mode]`, optional): newMaterialthan originalMateriallongerProcessmethod/way, will try one by one in order untilSuccessor throw exception.
                Defaultis truncatedMaterialtail/end, makeSegmentmaintain original length

        Raises:
            `IndexError`: `segment_index`out of bounds
            `TypeError`: TrackorMaterialTypeincorrect
            `ExtensionFailed`: newMaterialthan originalMateriallongerProcessFailed
        """
        if not isinstance(track, ImportedMediaTrack):
            raise TypeError("specify(Typeis/as %s)not supportedMaterialreplace" % track.track_type)
        if not 0 <= segment_index < len(track):
            raise IndexError("Segmentindex %d exceeds [0, %d) range of" % (segment_index, len(track)))
        if not track.check_material_type(material):
            raise TypeError("specifiedMaterialType %s mismatchTrack type %s", (type(material), track.track_type))
        seg = track.segments[segment_index]

        if isinstance(handle_extend, Extend_mode):
            handle_extend = [handle_extend]
        if source_timerange is None:
            if isinstance(material, Video_material) and (material.material_type == "photo"):
                source_timerange = Timerange(0, seg.duration)
            else:
                source_timerange = Timerange(0, material.duration)

        # Processtime change
        track.process_timerange(segment_index, source_timerange, handle_shrink, handle_extend)

        # finally replaceMateriallink
        track.segments[segment_index].material_id = material.material_id
        self.add_material(material)

        # TODO: Updatetotal length
        return self

    def replace_text(self, track: EditableTrack, segment_index: int, text: Union[str, List[str]],
                     recalc_style: bool = True) -> "Script_file":
        """replace specifiedTextTrackspecify onSegmentof/theTextcontent, supports normalTextSegmentorTexttemplateSegment

        Args:
            track (`Editable_track`): to replaceTexttext track, by`get_imported_track`Get
            segment_index (`int`): to replaceTextof/theSegmentindex, from0Start
            text (`str` or `List[str]`): newTextcontent, forTexttemplate, a string list should be passed in.
            recalc_style (`bool`): whether to recalculateFontstyle distribution, i.e. adjust eachFontstyle application range to maintain original proportions as much as possible, Defaultenable.

        Raises:
            `IndexError`: `segment_index`out of bounds
            `TypeError`: Track typeincorrect
            `ValueError`: TexttemplateSegmentof/theTextquantity mismatch
        """
        if not isinstance(track, ImportedTextTrack):
            raise TypeError("specify(Typeis/as %s)not supportedTextcontent replacement" % track.track_type)
        if not 0 <= segment_index < len(track):
            raise IndexError("Segmentindex %d exceeds [0, %d) range of" % (segment_index, len(track)))

        def __recalc_style_range(old_len: int, new_len: int, styles: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
            """adjustFontstyle distribution"""
            new_styles: List[Dict[str, Any]] = []
            for style in styles:
                start = math.ceil(style["range"][0] / old_len * new_len)
                end = math.ceil(style["range"][1] / old_len * new_len)
                style["range"] = [start, end]
                if start != end:
                    new_styles.append(style)
            return new_styles

        replaced: bool = False
        material_id: str = track.segments[segment_index].material_id
        # try to inTextMaterialreplace in
        for mat in self.imported_materials["texts"]:
            if mat["id"] != material_id:
                continue

            if isinstance(text, list):
                if len(text) != 1:
                    raise ValueError(f"normalTextSegmentcan only have oneTextcontent, but replacement content is {text}")
                text = text[0]

            content = json.loads(mat["content"])
            if recalc_style:
                content["styles"] = __recalc_style_range(len(content["text"]), len(text), content["styles"])
            content["text"] = text
            mat["content"] = json.dumps(content, ensure_ascii=False)
            replaced = True
            break
        if replaced:
            return self

        # try to inTextreplace in template
        for template in self.imported_materials["text_templates"]:
            if template["id"] != material_id:
                continue

            resources = template["text_info_resources"]
            if isinstance(text, str):
                text = [text]
            if len(text) > len(resources):
                raise ValueError(f"Texttemplate'{template['name']}'only{len(resources)}segmentText, but provided{len(text)}segment replacement content")

            for sub_material_id, new_text in zip(map(lambda x: x["text_material_id"], resources), text):
                for mat in self.imported_materials["texts"]:
                    if mat["id"] != sub_material_id:
                        continue

                    if isinstance(mat["content"], str):
                        mat["content"] = new_text
                    else:
                        content = json.loads(mat["content"])
                        if recalc_style:
                            content["styles"] = __recalc_style_range(len(content["text"]), len(new_text), content["styles"])
                        content["text"] = new_text
                        mat["content"] = json.dumps(content, ensure_ascii=False)
                    break
            replaced = True
            break

        assert replaced, f"specified not foundSegmentof/theMaterial {material_id}"

        return self

    def inspect_material(self) -> None:
        """outputDraftinImportof/theSticker、Textbubble and text decorationMaterialmetadata of"""
        print("StickerMaterial:")
        for sticker in self.imported_materials["stickers"]:
            print("\tResource id: %s '%s'" % (sticker["resource_id"], sticker.get("name", "")))

        print("TextBubble effect:")
        for effect in self.imported_materials["effects"]:
            if effect["type"] == "text_shape":
                print("\tEffect id: %s ,Resource id: %s '%s'" %
                      (effect["effect_id"], effect["resource_id"], effect.get("name", "")))

        print("text decoration effect:")
        for effect in self.imported_materials["effects"]:
            if effect["type"] == "text_effect":
                print("\tResource id: %s '%s'" % (effect["resource_id"], effect.get("name", "")))

    def dumps(self) -> str:
        """will/convertDraft filecontentExportis/asJSONstring"""
        self.content["fps"] = self.fps
        self.content["duration"] = self.duration
        self.content["canvas_config"] = {"width": self.width, "height": self.height, "ratio": "original"}
        self.content["materials"] = self.materials.export_json()

        self.content["last_modified_platform"] = {
            "app_id": 359289,
            "app_source": "cc",
            "app_version": "6.5.0",
            "device_id": "c4ca4238a0b923820dcc509a6f75849b",
            "hard_disk_id": "307563e0192a94465c0e927fbc482942",
            "mac_address": "c3371f2d4fb02791c067ce44d8fb4ed5",
            "os": "mac",
            "os_version": "15.5"
        }

        self.content["platform"] = {
            "app_id": 359289,
            "app_source": "cc",
            "app_version": "6.5.0",
            "device_id": "c4ca4238a0b923820dcc509a6f75849b",
            "hard_disk_id": "307563e0192a94465c0e927fbc482942",
            "mac_address": "c3371f2d4fb02791c067ce44d8fb4ed5",
            "os": "mac",
            "os_version": "15.5"
        }

        # mergeImportof/theMaterial
        for material_type, material_list in self.imported_materials.items():
            if material_type not in self.content["materials"]:
                self.content["materials"][material_type] = material_list
            else:
                self.content["materials"][material_type].extend(material_list)

        # for/toTracksort andExport
        track_list: List[Base_track] = list(self.tracks.values())
        track_list.extend(self.imported_tracks)
        track_list.sort(key=lambda track: track.render_index)
        self.content["tracks"] = [track.export_json() for track in track_list]

        return json.dumps(self.content, ensure_ascii=False, indent=4)

    def dump(self, file_path: str) -> None:
        """will/convertDraft filecontent write toFile"""
        with open(file_path, "w", encoding="utf-8") as f:
            f.write(self.dumps())

    def save(self) -> None:
        """SaveDraft fileto the time of openingPath, only available in template mode

        Raises:
            `ValueError`: not in template mode
        """
        if self.save_path is None:
            raise ValueError("does not haveSetSavePath, may not be in template mode")
        self.dump(self.save_path)
