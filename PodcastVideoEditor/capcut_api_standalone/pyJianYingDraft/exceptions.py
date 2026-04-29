"""Custom exception classes"""

class TrackNotFound(NameError):
    """Track matching conditions not found"""
class AmbiguousTrack(ValueError):
    """Multiple tracks matching conditions found"""
class SegmentOverlap(ValueError):
    """New segment overlaps with existing track segment"""

class MaterialNotFound(NameError):
    """Material matching conditions not found"""
class AmbiguousMaterial(ValueError):
    """Multiple materials matching conditions found"""

class ExtensionFailed(ValueError):
    """Failed to extend segment when replacing material"""

class DraftNotFound(NameError):
    """Draft not found"""
class AutomationError(Exception):
    """Automation operation failed"""
class ExportTimeout(Exception):
    """Export timeout"""
