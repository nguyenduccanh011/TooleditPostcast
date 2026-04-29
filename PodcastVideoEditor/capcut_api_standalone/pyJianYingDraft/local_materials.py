import os
import uuid
import subprocess
import json
from typing import Optional, Literal
from typing import Dict, Any
import imageio.v2 as imageio

class Crop_settings:
    """Material crop settings, all attributes range from 0 to 1, note the coordinate origin is at the upper-left corner"""

    upper_left_x: float
    upper_left_y: float
    upper_right_x: float
    upper_right_y: float
    lower_left_x: float
    lower_left_y: float
    lower_right_x: float
    lower_right_y: float

    def __init__(self, *, upper_left_x: float = 0.0, upper_left_y: float = 0.0,
                 upper_right_x: float = 1.0, upper_right_y: float = 0.0,
                 lower_left_x: float = 0.0, lower_left_y: float = 1.0,
                 lower_right_x: float = 1.0, lower_right_y: float = 1.0):
        """Initialize crop settings, default parameters mean no cropping"""
        self.upper_left_x = upper_left_x
        self.upper_left_y = upper_left_y
        self.upper_right_x = upper_right_x
        self.upper_right_y = upper_right_y
        self.lower_left_x = lower_left_x
        self.lower_left_y = lower_left_y
        self.lower_right_x = lower_right_x
        self.lower_right_y = lower_right_y

    def export_json(self) -> Dict[str, Any]:
        return {
            "upper_left_x": self.upper_left_x,
            "upper_left_y": self.upper_left_y,
            "upper_right_x": self.upper_right_x,
            "upper_right_y": self.upper_right_y,
            "lower_left_x": self.lower_left_x,
            "lower_left_y": self.lower_left_y,
            "lower_right_x": self.lower_right_x,
            "lower_right_y": self.lower_right_y
        }

class Video_material:
    """Local video material (video or image), a single material can be used in multiple segments"""

    material_id: str
    """Material global ID, auto-generated"""
    local_material_id: str
    """Material local ID, purpose unclear"""
    material_name: str
    """Material name"""
    path: str
    """Material file path"""
    remote_url: Optional[str] = None
    """Remote URL address"""
    duration: int
    """Material duration, in microseconds"""
    height: int
    """Material height"""
    width: int
    """Material width"""
    crop_settings: Crop_settings
    """Material crop settings"""
    material_type: Literal["video", "photo"]
    """Material type: video or image"""
    replace_path: Optional[str] = None
    """Replacement path, if set, this path will replace the original path when exporting JSON"""

    def __init__(self, material_type: Literal["video", "photo"], 
                 path: Optional[str] = None,
                 replace_path: Optional[str] = None, 
                 material_name: Optional[str] = None, 
                 crop_settings: Crop_settings = Crop_settings(), 
                 remote_url: Optional[str] = None,
                 duration: Optional[float] = None,
                 width: Optional[int] = None,
                 height: Optional[int] = None):
        """Load video (or image) material from specified location

        Args:
            path (`str`, optional): Material file path, supports mp4, mov, avi and other common video files, as well as jpg, jpeg, png image files.
            replace_path (`str`, optional): Replacement path, used to replace original path when exporting JSON.
            material_type (`Literal["video", "photo"]`, optional): Material type, if specified this value takes priority.
            material_name (`str`, optional): Material name, if not specified, defaults to the filename.
            crop_settings (`Crop_settings`, optional): Material crop settings, defaults to no cropping.
            remote_url (`str`, optional): Remote URL address.
            duration (`float`, optional): Duration in seconds, if provided skips ffprobe detection.
            width (`int`, optional): Material width, if not specified, obtained via ffprobe.
            height (`int`, optional): Material height, if not specified, obtained via ffprobe.

        Raises:
            `ValueError`: Unsupported material file type or missing required parameters.
            `FileNotFoundError`: Material file does not exist.
        """
        # Ensure at least path or remote_url is provided
        if not path and not remote_url:
            raise ValueError("Must provide at least one of path or remote_url")
            
        # Handle remote URL case
        if remote_url:
            if not material_name:
                raise ValueError("material_name must be specified when using remote_url")
            self.remote_url = remote_url
            self.path = ""  # Remote resource has no local path
        else:
            # Handle local file case
            path = os.path.abspath(path)
            if not os.path.exists(path):
                raise FileNotFoundError(f"Cannot find {path}")
            self.path = path
            self.remote_url = None

        # Set material name
        self.material_name = material_name if material_name else os.path.basename(path)
        self.material_id = uuid.uuid3(uuid.NAMESPACE_DNS, self.material_name).hex
        self.replace_path = replace_path
        self.crop_settings = crop_settings
        self.local_material_id = ""
        self.material_type = material_type

        # If photo type, skip ffprobe media info retrieval
        if material_type == "photo":
            self.material_type = "photo"
            self.duration = 10800000000  # Static image defaults to 3 hours
            # Get image dimensions using imageio
            try:
                # img = imageio.imread(self.remote_url)
                # self.height, self.width = img.shape[:2]
                # Use default dimensions, real dimensions will be obtained during download
                self.width = 0
                self.height = 0
            except Exception as e:
                # If retrieval fails, use default values
                self.width = 1920
                self.height = 1080
            return


        # If duration is provided externally, use it directly, skip ffprobe detection
        if duration is not None and width is not None and height is not None:
            self.duration = int(float(duration) * 1e6)  # Convert to microseconds
            self.width = width
            self.height = height
            return  # Return directly, skip subsequent ffprobe detection
        
        # If duration not provided, use ffprobe to obtain it
        try:
            # Use ffprobe to get media info
            media_path = self.path if self.path else self.remote_url
            command = [
                'ffprobe',
                '-v', 'error',
                '-select_streams', 'v:0',  # Select first video stream
                '-show_entries', 'stream=width,height,duration,codec_type',  # Add codec_type
                '-show_entries', 'format=duration,format_name',  # Add format_name
                '-of', 'json',
                media_path
            ]
            result = subprocess.check_output(command, stderr=subprocess.STDOUT)
            result_str = result.decode('utf-8')
            # Find JSON start position (first '{')
            json_start = result_str.find('{')
            if json_start != -1:
                json_str = result_str[json_start:]
                info = json.loads(json_str)
            else:
                raise ValueError(f"Cannot find JSON data in output: {result_str}")
            
            if 'streams' in info and len(info['streams']) > 0:
                stream = info['streams'][0]
                self.width = int(stream.get('width', 0))
                self.height = int(stream.get('height', 0))
                
                # If material_type is specified, use the specified type
                if material_type is not None:
                    self.material_type = material_type
                else:
                    # Determine if it's a dynamic video via format_name and codec_type
                    format_name = info.get('format', {}).get('format_name', '').lower()
                    codec_type = stream.get('codec_type', '').lower()
                    
                    # Check if it's a GIF or other dynamic video
                    if 'gif' in format_name or (codec_type == 'video' and stream.get('duration') is not None):
                        self.material_type = "video"
                    else:
                        self.material_type = "photo"
                
                # Set duration
                if self.material_type == "video":
                    # Prefer stream duration, fallback to format duration
                    duration = stream.get('duration') or info['format'].get('duration', '0')
                    self.duration = int(float(duration) * 1e6)  # Convert to microseconds
                else:
                    self.duration = 10800000000  # Static image defaults to 3 hours
            else:
                raise ValueError(f"Cannot get stream info for media file {media_path}")
                
        except subprocess.CalledProcessError as e:
            raise ValueError(f"Error processing file {media_path}: {e.output.decode('utf-8')}")
        except json.JSONDecodeError as e:
            raise ValueError(f"Error parsing media info: {e}")

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Video_material":
        """Create video material object from dictionary
        
        Args:
            data (Dict[str, Any]): Dictionary containing material information
            
        Returns:
            Video_material: Newly created video material object
        """
        # Create instance without calling __init__
        instance = cls.__new__(cls)
        
        # Set basic attributes
        instance.material_id = data["id"]
        instance.local_material_id = data.get("local_material_id", "")
        instance.material_name = data["material_name"]
        instance.path = data["path"]
        instance.duration = data["duration"]
        instance.height = data["height"]
        instance.width = data["width"]
        instance.material_type = data["type"]
        instance.replace_path = None  # Default: no replacement path
        
        # Set crop settings
        crop_data = data.get("crop", {})
        instance.crop_settings = Crop_settings(
            upper_left_x=crop_data.get("upper_left_x", 0.0),
            upper_left_y=crop_data.get("upper_left_y", 0.0),
            upper_right_x=crop_data.get("upper_right_x", 1.0),
            upper_right_y=crop_data.get("upper_right_y", 0.0),
            lower_left_x=crop_data.get("lower_left_x", 0.0),
            lower_left_y=crop_data.get("lower_left_y", 1.0),
            lower_right_x=crop_data.get("lower_right_x", 1.0),
            lower_right_y=crop_data.get("lower_right_y", 1.0)
        )
        
        return instance

    def export_json(self) -> Dict[str, Any]:
        video_material_json = {
            "audio_fade": None,
            "category_id": "",
            "category_name": "local",
            "check_flag": 63487,
            "crop": self.crop_settings.export_json(),
            "crop_ratio": "free",
            "crop_scale": 1.0,
            "duration": self.duration,
            "height": self.height,
            "id": self.material_id,
            "local_material_id": self.local_material_id,
            "material_id": self.material_id,
            "material_name": self.material_name,
            "media_path": "",
            "path": self.replace_path if self.replace_path is not None else self.path,
            "remote_url": self.remote_url,
            "type": self.material_type,
            "width": self.width
        }
        return video_material_json

class Audio_material:
    """Local audio material"""

    material_id: str
    """Material global ID, auto-generated"""
    material_name: str
    """Material name"""
    path: str
    """Material file path"""
    remote_url: Optional[str] = None
    """Remote URL address"""
    replace_path: Optional[str] = None
    """Replacement path, if set, this path will replace the original path when exporting JSON"""

    has_audio_effect: bool = False
    """Whether audio effects are applied"""

    duration: int
    """Material duration, in microseconds"""

    def __init__(self, path: Optional[str] = None, replace_path = None, material_name: Optional[str] = None, 
                 remote_url: Optional[str] = None, duration: Optional[float] = None):
        """Load audio material from specified location, note: video files should not be used as audio material
    
        Args:
            path (`str`, optional): Material file path, supports mp3, wav and other common audio files.
            material_name (`str`, optional): Material name, if not specified, defaults to the filename from the URL.
            remote_url (`str`, optional): Remote URL address.
            duration (`float`, optional): Audio duration in seconds, if provided skips ffprobe detection.
    
        Raises:
            `ValueError`: Unsupported material file type or missing required parameters.
        """
        if not path and not remote_url:
            raise ValueError("Must provide at least one of path or remote_url")
    
        if path:
            path = os.path.abspath(path)
            if not os.path.exists(path):
                raise FileNotFoundError(f"Cannot find {path}")
    
        # Get filename from URL as material_name
        if not material_name and remote_url:
            original_filename = os.path.basename(remote_url.split('?')[0])  # Fix: use remote_url instead of audio_url
            name_without_ext = os.path.splitext(original_filename)[0]  # Get filename without extension
            material_name = f"{name_without_ext}.mp3"  # Use original filename + fixed mp3 extension
        
        self.material_name = material_name if material_name else (os.path.basename(path) if path else "unknown")
        self.material_id = uuid.uuid3(uuid.NAMESPACE_DNS, self.material_name).hex
        self.path = path if path else ""
        self.replace_path = replace_path
        self.remote_url = remote_url
        
        # If duration is provided externally, use it directly, skip ffprobe detection
        if duration is not None:
            self.duration = int(float(duration) * 1e6)  # Convert to microseconds
            return  # Return directly, skip subsequent ffprobe detection
        
        # If duration not provided, use ffprobe to obtain it
        self.duration = 0  # Initialize to 0, will be updated later if path exists
    
        try:
            # Use ffprobe to get audio info
            command = [
                'ffprobe',
                '-v', 'error',
                '-select_streams', 'a:0',  # Select first audio stream
                '-show_entries', 'stream=duration',
                '-show_entries', 'format=duration',
                '-of', 'json',
                path if path else remote_url
            ]
            result = subprocess.check_output(command, stderr=subprocess.STDOUT)
            result_str = result.decode('utf-8')
            # Find JSON start position (first '{')
            json_start = result_str.find('{')
            if json_start != -1:
                json_str = result_str[json_start:]
                info = json.loads(json_str)
            else:
                raise ValueError(f"Cannot find JSON data in output: {result_str}")

            # Check for video streams
            video_command = [
                'ffprobe',
                '-v', 'error',
                '-select_streams', 'v:0',
                '-show_entries', 'stream=codec_type',
                '-of', 'json',
                path if path else remote_url
            ]
            video_result = subprocess.check_output(video_command, stderr=subprocess.STDOUT)
            video_result_str = video_result.decode('utf-8')
            # Find JSON start position (first '{')
            video_json_start = video_result_str.find('{')
            if video_json_start != -1:
                video_json_str = video_result_str[video_json_start:]
                video_info = json.loads(video_json_str)
            else:
                print(f"Cannot find JSON data in output: {video_result_str}")
            
            if 'streams' in video_info and len(video_info['streams']) > 0:
                raise ValueError("Audio material should not contain video tracks")

            # Check audio streams
            if 'streams' in info and len(info['streams']) > 0:
                stream = info['streams'][0]
                # Prefer stream duration, fallback to format duration
                duration_value = stream.get('duration') or info['format'].get('duration', '0')
                self.duration = int(float(duration_value) * 1e6)  # Convert to microseconds
            else:
                raise ValueError(f"The given material file {path} has no audio track")

        except subprocess.CalledProcessError as e:
            raise ValueError(f"Error processing file {path}: {e.output.decode('utf-8')}")
        except json.JSONDecodeError as e:
            raise ValueError(f"Error parsing media info: {e}")
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Audio_material":
        """Create audio material object from dictionary
        
        Args:
            data (Dict[str, Any]): Dictionary containing material information
            
        Returns:
            Audio_material: Newly created audio material object
        """
        # Create instance without calling __init__
        instance = cls.__new__(cls)
        
        # Set basic attributes
        instance.material_id = data["id"]
        instance.material_name = data["name"]  # Note: key is "name" not "material_name"
        instance.path = data["path"]
        instance.duration = data["duration"]
        instance.replace_path = None  # Default: no replacement path
        instance.remote_url = data.get("remote_url")
        
        return instance

    def export_json(self) -> Dict[str, Any]:
        return {
            "app_id": 0,
            "category_id": "",
            "category_name": "local",
            "check_flag": 3 if hasattr(self, 'has_audio_effect') and self.has_audio_effect else 1,
            "copyright_limit_type": "none",
            "duration": self.duration,
            "effect_id": "",
            "formula_id": "",
            "id": self.material_id,
            "intensifies_path": "",
            "is_ai_clone_tone": False,
            "is_text_edit_overdub": False,
            "is_ugc": False,
            "local_material_id": self.material_id,
            "music_id": self.material_id,
            "name": self.material_name,
            "path": self.replace_path if self.replace_path is not None else self.path,
            "remote_url": self.remote_url,
            "query": "",
            "request_id": "",
            "resource_id": "",
            "search_id": "",
            "source_from": "",
            "source_platform": 0,
            "team_id": "",
            "text_id": "",
            "tone_category_id": "",
            "tone_category_name": "",
            "tone_effect_id": "",
            "tone_effect_name": "",
            "tone_platform": "",
            "tone_second_category_id": "",
            "tone_second_category_name": "",
            "tone_speaker": "",
            "tone_type": "",
            "type": "extract_music",
            "video_id": "",
            "wave_points": []
        }
