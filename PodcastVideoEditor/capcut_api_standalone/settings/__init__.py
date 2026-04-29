"""Cấu hình CapCut API."""

from .local import *

__all__ = ["IS_CAPCUT_ENV"]


def get_platform_info():
    """Trả về thông tin platform CapCut."""
    return {
        "app_id": 359289,
        "app_source": "cc",
        "app_version": "6.5.0",
        "device_id": "c4ca4238a0b923820dcc509a6f75849b",
        "hard_disk_id": "307563e0192a94465c0e927fbc482942",
        "mac_address": "c3371f2d4fb02791c067ce44d8fb4ed5",
        "os": "mac",
        "os_version": "15.5",
    }
