import os
import sys

from utils import extract_room_id, resource_path


def test_extract_full_url():
    assert extract_room_id("https://live.bilibili.com/12722549") == 12722549


def test_extract_url_with_params():
    assert extract_room_id("https://live.bilibili.com/12722549?spm_id_from=333.1007") == 12722549


def test_extract_plain_number():
    assert extract_room_id("12722549") == 12722549


def test_extract_invalid():
    assert extract_room_id("") is None
    assert extract_room_id("hello world") is None
    assert extract_room_id("live.bilibili.com/abc") is None


def test_resource_path_dev_mode():
    """开发模式：资源路径基于 utils.py 所在目录"""
    expected = os.path.normpath(os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "assets", "icons", "app.png",
    ))
    assert resource_path("assets/icons/app.png") == expected


def test_resource_path_frozen(monkeypatch):
    """打包模式：资源路径基于 PyInstaller 解压目录 _MEIPASS"""
    monkeypatch.setattr(sys, "_MEIPASS", "C:/fake/bundle", raising=False)
    expected = os.path.normpath(os.path.join(
        "C:/fake/bundle", "assets", "icons", "app.png"
    ))
    assert resource_path("assets/icons/app.png") == expected
