"""Internationalization - Vietnamese with English fallback."""

import json
import os

_translations_cache = {}

def _load_translations(lang):
    if lang in _translations_cache:
        return _translations_cache[lang]
    locale_file = os.path.join(os.path.dirname(__file__), "locales", f"{lang}.json")
    if os.path.exists(locale_file):
        with open(locale_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        _translations_cache[lang] = data
        return data
    return {}

def get_language():
    """Trả về ngôn ngữ hiện tại (mặc định 'vi')."""
    return os.environ.get("LANG", "vi").split("_")[0].split(".")[0] or "vi"


def t(key, default=None, **kwargs):
    """Dịch key sang tiếng Việt, fallback sang tiếng Anh."""
    # Ưu tiên tiếng Việt
    translations = _load_translations("vi")
    text = translations.get(key)
    # Fallback tiếng Anh
    if text is None:
        translations = _load_translations("en")
        text = translations.get(key)
    if text is None:
        text = default if default is not None else key
    if kwargs:
        try:
            text = text.format(**kwargs)
        except (KeyError, IndexError):
            pass
    return text
