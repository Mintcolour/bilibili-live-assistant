# -*- coding: utf-8 -*-
"""
配置管理模块 - 读写 config.json
"""
import json
import os
from dataclasses import dataclass, field, asdict
from typing import Optional

CONFIG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json")

DEFAULT_CONFIG = {
    "room_id": 0,
    "cookie": {
        "buvid3": "",
        "SESSDATA": "",
        "bili_jct": "",
    },
    "display": {
        "font_size": 28,
        "scroll_speed": 120,          # 像素/秒
        "opacity": 0.85,               # 窗口不透明度 0.0-1.0
        "max_tracks": 8,               # 弹幕轨道数
        "track_height": 36,            # 每轨道高度(像素)
        "window_height_ratio": 0.35,   # 窗口高度占屏幕比例
        "show_enter": True,            # 显示进入通知
        "show_gift": True,             # 显示礼物通知
        "show_sc": True,               # 显示醒目留言
        "show_online": True,           # 显示人气值变化
        "danmaku_color": "#FFFFFF",    # 普通弹幕颜色
        "enter_color": "#4CAF50",      # 进入通知颜色
        "gift_color": "#FF69B4",       # 礼物通知颜色
        "sc_color": "#FFD700",         # 醒目留言颜色
        "online_color": "#2196F3",     # 在线人数颜色
    },
    "poll_interval": 30,               # 在线信息轮询间隔(秒)
}


@dataclass
class CookieConfig:
    buvid3: str = ""
    SESSDATA: str = ""
    bili_jct: str = ""


@dataclass
class DisplayConfig:
    font_size: int = 28
    scroll_speed: int = 120
    opacity: float = 0.85
    max_tracks: int = 8
    track_height: int = 36
    window_height_ratio: float = 0.35
    show_enter: bool = True
    show_gift: bool = True
    show_sc: bool = True
    show_online: bool = True
    danmaku_color: str = "#FFFFFF"
    enter_color: str = "#4CAF50"
    gift_color: str = "#FF69B4"
    sc_color: str = "#FFD700"
    online_color: str = "#2196F3"


@dataclass
class AppConfig:
    room_id: int = 0
    cookie: CookieConfig = field(default_factory=CookieConfig)
    display: DisplayConfig = field(default_factory=DisplayConfig)
    poll_interval: int = 30


def _dict_to_dataclass(cls, data: dict):
    """递归将 dict 转为 dataclass"""
    if not isinstance(data, dict):
        return data
    field_names = {f.name for f in cls.__dataclass_fields__.values()}
    kwargs = {}
    for k, v in data.items():
        if k in field_names:
            field_type = cls.__dataclass_fields__[k].type
            if hasattr(field_type, "__dataclass_fields__"):
                kwargs[k] = _dict_to_dataclass(field_type, v)
            else:
                kwargs[k] = v
    return cls(**kwargs)


def load_config() -> AppConfig:
    """加载配置，不存在则返回默认"""
    if not os.path.exists(CONFIG_FILE):
        return AppConfig()
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        # Merge with defaults so missing keys get default values
        merged = {**DEFAULT_CONFIG, **data}
        merged["cookie"] = {**DEFAULT_CONFIG["cookie"], **data.get("cookie", {})}
        merged["display"] = {**DEFAULT_CONFIG["display"], **data.get("display", {})}

        config = AppConfig()
        config.room_id = merged.get("room_id", 0)
        config.poll_interval = merged.get("poll_interval", 30)

        cookie_data = merged.get("cookie", {})
        config.cookie = CookieConfig(
            buvid3=cookie_data.get("buvid3", ""),
            SESSDATA=cookie_data.get("SESSDATA", ""),
            bili_jct=cookie_data.get("bili_jct", ""),
        )

        display_data = merged.get("display", {})
        config.display = DisplayConfig(**{
            k: v for k, v in display_data.items()
            if k in DisplayConfig.__dataclass_fields__
        })

        return config
    except (json.JSONDecodeError, KeyError, TypeError) as e:
        print(f"[Config] 加载配置失败，使用默认值: {e}")
        return AppConfig()


def save_config(config: AppConfig):
    """保存配置到 config.json"""
    data = {
        "room_id": config.room_id,
        "cookie": asdict(config.cookie),
        "display": asdict(config.display),
        "poll_interval": config.poll_interval,
    }
    with open(CONFIG_FILE, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print(f"[Config] 配置已保存到 {CONFIG_FILE}")
