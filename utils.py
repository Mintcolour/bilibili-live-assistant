# -*- coding: utf-8 -*-
"""通用工具函数"""
import re
from typing import Optional

# 匹配纯数字房间号，或 live.bilibili.com/xxx 链接中的数字
_ROOM_ID_RE = re.compile(r"(?:live\.bilibili\.com/)?(\d{2,10})")


def extract_room_id(text: str) -> Optional[int]:
    """从用户输入中提取直播间ID，支持纯数字或完整直播链接"""
    text = (text or "").strip()
    if not text:
        return None
    m = _ROOM_ID_RE.search(text)
    if not m:
        return None
    return int(m.group(1))
