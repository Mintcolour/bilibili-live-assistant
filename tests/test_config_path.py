import os
import sys

import config as config_module


def test_dev_mode_path_in_project_dir():
    """开发模式：config.json 位于 config.py 所在目录"""
    expected = os.path.join(
        os.path.dirname(os.path.abspath(config_module.__file__)), "config.json"
    )
    assert config_module.get_config_path() == expected


def test_frozen_path_next_to_exe(monkeypatch):
    """打包模式：config.json 位于 exe 所在目录"""
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", "C:/fake/dir/app.exe")
    monkeypatch.setattr(os, "access", lambda path, mode: True)
    expected = os.path.join(
        os.path.dirname(os.path.abspath(sys.executable)), "config.json"
    )
    assert config_module.get_config_path() == expected


def test_frozen_not_writable_falls_back_to_appdata(monkeypatch):
    """exe 目录不可写（如 Program Files）→ 回退 %APPDATA%"""
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", "C:/Program Files/app.exe")
    monkeypatch.setattr(os, "access", lambda path, mode: False)
    monkeypatch.setenv("APPDATA", "C:/fake/appdata")
    assert config_module.get_config_path() == os.path.join(
        "C:/fake/appdata", "BilibiliLiveAssistant", "config.json"
    )
