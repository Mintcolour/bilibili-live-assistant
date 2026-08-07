# B站直播弹幕助手 — 新手友好界面升级 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 B 站直播弹幕助手升级为新手友好的桌面应用：主控制窗口 + 首次运行三步向导 + 统一美化 + 单文件 exe 打包。

**Architecture:** 保持单进程 + qasync 事件循环，弹幕数据链路（blivedm → Qt 信号 → 渲染引擎）不动，只增改 UI 层。新增 `app_controller.py`（控制器）、`main_window.py`（主窗口）、`wizard.py`（向导）、`settings_dialog.py`（设置对话框抽出）、`styles.py`（QSS 主题）、`utils.py`（房间号提取）；改 `config.py`（frozen 路径）、`danmaku_client.py`（错误透出）、`overlay_window.py`（托盘加"打开主窗口"）、`danmaku_renderer.py`（彩色填充修复）、`main.py`（入口整合）。最后 PyInstaller 单文件打包。

**Tech Stack:** Python 3.13 (venv)，PyQt5 5.15，qasync，aiohttp，blivedm（vendored 于 `blivedm_lib/`），pytest（仅开发用），PyInstaller（仅打包用）。

## Global Constraints

- 运行环境：`venv\Scripts\python.exe`（Windows）
- 所有测试用 `venv\Scripts\python -m pytest tests/ -v` 运行；测试文件放 `tests/`，先 `venv\Scripts\pip install pytest`
- **本目录不是 git 仓库**：所有任务的"提交"步骤替换为"运行验证通过"（如用户需要版本管理可先 `git init`）
- 弹幕数据链路不动：`danmaku_client.py` 的事件结构、`danmaku_renderer.py` 的轨道/队列逻辑不重构
- QSS 主题**不应用于透明悬浮窗**（会破坏透明背景），悬浮窗只改弹幕绘制
- blivedm 关键 API：`BLiveClient.start()` 是同步方法（内部 `asyncio.create_task`）；连接停止时回调 `handler.on_client_stopped(client, exception)`，`exception=None` 表示正常停止，否则为异常对象；`InitError`/`AuthError` 定义在 `blivedm.clients.ws_base`
- 注释与文案用中文，风格与现有代码一致
- `pytest`、`pyinstaller` 只装进 venv，**不写入 requirements.txt**

---

### Task 1: config.py 配置路径支持 frozen（打包场景）

**Files:**
- Modify: `config.py`（顶部路径常量区，load_config/save_config）
- Test: `tests/test_config_path.py`
- Create: `tests/conftest.py`、`tests/__init__.py`（若目录不存在）

**Interfaces:**
- Produces: `config.get_config_dir() -> str`、`config.get_config_path() -> str`
- 行为：开发模式（未 frozen）→ 项目目录；frozen → exe 所在目录；exe 目录不可写 → `%APPDATA%\BilibiliLiveAssistant\config.json`（自动建目录）
- `load_config()` / `save_config()` 内部改用 `get_config_path()`，模块级 `CONFIG_FILE` 常量删除

- [ ] **Step 1: 安装 pytest 并建测试骨架**

```bash
venv\Scripts\pip install pytest
mkdir tests
```

创建 `tests/__init__.py`（空文件）与 `tests/conftest.py`：

```python
import os

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

import pytest
from PyQt5.QtWidgets import QApplication


@pytest.fixture(scope="session")
def qapp():
    app = QApplication.instance() or QApplication([])
    return app
```

- [ ] **Step 2: 写失败测试 `tests/test_config_path.py`**

```python
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
    assert config_module.get_config_path() == "C:/fake/dir/config.json"


def test_frozen_not_writable_falls_back_to_appdata(monkeypatch):
    """exe 目录不可写（如 Program Files）→ 回退 %APPDATA%"""
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", "C:/Program Files/app.exe")
    monkeypatch.setattr(os, "access", lambda path, mode: False)
    monkeypatch.setenv("APPDATA", "C:/fake/appdata")
    assert config_module.get_config_path() == os.path.join(
        "C:/fake/appdata", "BilibiliLiveAssistant", "config.json"
    )
```

注意：测试里 `monkeypatch.setattr(os, "access", ...)` 会让 `os.makedirs` 的权限判断也受影响吗？不会——`os.makedirs` 内部不走 `os.access`。

- [ ] **Step 3: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_config_path.py -v`
Expected: FAIL（`AttributeError: module 'config' has no attribute 'get_config_path'`）

- [ ] **Step 4: 实现 `config.py` 路径改造**

在 `config.py` 顶部（`CONFIG_FILE` 常量处）替换为：

```python
import sys

def get_config_dir() -> str:
    """配置目录：打包后为 exe 所在目录（便携式），开发时为主文件目录"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def get_config_path() -> str:
    """配置文件路径；目标目录不可写时回退 %APPDATA%"""
    base = get_config_dir()
    if not os.access(base, os.W_OK):
        appdata = os.environ.get("APPDATA", ".")
        path = os.path.join(appdata, "BilibiliLiveAssistant", "config.json")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        return path
    return os.path.join(base, "config.json")
```

删除原 `CONFIG_FILE = ...` 常量。`load_config()` 中 `os.path.exists(CONFIG_FILE)` 改为 `os.path.exists(get_config_path())`，open 语句用 `get_config_path()`；`save_config()` 的 open 语句同样改为 `get_config_path()`。

- [ ] **Step 5: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_config_path.py -v`
Expected: PASS（3 个用例）

- [ ] **Step 6: 验证现有功能不回归**

Run: `venv\Scripts\python -c "from config import load_config, save_config; c = load_config(); print('room_id =', c.room_id)"`
Expected: 打印 `room_id = 12722549`（读现有 config.json 正常）

---

### Task 2: utils.py 房间号提取 + needs_onboarding 判定

**Files:**
- Create: `utils.py`
- Modify: `config.py`（加 `needs_onboarding`）
- Test: `tests/test_utils.py`、`tests/test_onboarding.py`

**Interfaces:**
- Produces: `utils.extract_room_id(text: str) -> Optional[int]`（无效返回 None）
- Produces: `config.needs_onboarding(config: AppConfig) -> bool`（room_id<=0 且 Cookie 全空）
- Consumes: `config.AppConfig`（Task 1 已有）

- [ ] **Step 1: 写失败测试 `tests/test_utils.py`**

```python
from utils import extract_room_id


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
```

- [ ] **Step 2: 写失败测试 `tests/test_onboarding.py`**

```python
from config import AppConfig, CookieConfig, needs_onboarding


def test_default_config_needs_onboarding():
    assert needs_onboarding(AppConfig()) is True


def test_configured_config_no_onboarding():
    cfg = AppConfig(room_id=12722549, cookie=CookieConfig(SESSDATA="x"))
    assert needs_onboarding(cfg) is False


def test_partial_config_no_onboarding():
    # 只有房间号也算已配置（跳过向导）
    assert needs_onboarding(AppConfig(room_id=12722549)) is False
```

- [ ] **Step 3: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_utils.py tests/test_onboarding.py -v`
Expected: FAIL（`ModuleNotFoundError: No module named 'utils'` 等）

- [ ] **Step 4: 创建 `utils.py` 并在 `config.py` 加判定**

`utils.py`：

```python
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
```

`config.py` 末尾追加：

```python
def needs_onboarding(config: AppConfig) -> bool:
    """首次运行判定：房间号未填 且 Cookie 全空"""
    return config.room_id <= 0 and not (
        config.cookie.SESSDATA or config.cookie.buvid3 or config.cookie.bili_jct
    )
```

- [ ] **Step 5: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_utils.py tests/test_onboarding.py -v`
Expected: PASS（7 个用例）

---

### Task 3: danmaku_client.py 连接失败原因透出

**Files:**
- Modify: `danmaku_client.py`（BiliDanmakuHandler 增加 on_client_stopped 重写）
- Test: `tests/test_danmaku_client.py`

**Interfaces:**
- Produces: `BiliDanmakuHandler.on_client_stopped(client, exception)` — 异常时经 `connection_status` 信号发 `"连接失败：..."` 文本；正常停止（exception=None）不发
- Consumes: 现有 `DanmakuSignals.connection_status`

- [ ] **Step 1: 写失败测试 `tests/test_danmaku_client.py`**

```python
from blivedm.clients.ws_base import AuthError, InitError

from danmaku_client import BiliDanmakuHandler, DanmakuSignals


def _make_handler():
    signals = DanmakuSignals()
    msgs = []
    signals.connection_status.connect(msgs.append)
    return BiliDanmakuHandler(signals, None), msgs


def test_init_error_emits_friendly_message():
    handler, msgs = _make_handler()
    handler.on_client_stopped(None, InitError("init_room() failed"))
    assert msgs and msgs[-1].startswith("连接失败")
    assert "直播间" in msgs[-1]


def test_auth_error_emits_cookie_message():
    handler, msgs = _make_handler()
    handler.on_client_stopped(None, AuthError("auth failed"))
    assert msgs and "Cookie" in msgs[-1]


def test_normal_stop_emits_nothing():
    handler, msgs = _make_handler()
    handler.on_client_stopped(None, None)
    assert not any("失败" in m for m in msgs)
```

- [ ] **Step 2: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_danmaku_client.py -v`
Expected: FAIL（`on_client_stopped` 未在 handler 上定义——BaseHandler 默认空实现）

- [ ] **Step 3: 在 `danmaku_client.py` 的 `BiliDanmakuHandler` 中实现**

文件顶部 import 区追加：

```python
from blivedm.clients.ws_base import InitError, AuthError
```

在 `BiliDanmakuHandler` 类内（`_on_user_toast_v2` 方法之后）追加：

```python
    def on_client_stopped(self, client, exception):
        """客户端停止：将连接失败原因透出到 UI"""
        if exception is None:
            return  # 正常停止，不提示
        if isinstance(exception, InitError):
            msg = "连接失败：直播间不存在或无法获取弹幕服务器"
        elif isinstance(exception, AuthError):
            msg = "连接失败：认证失败（Cookie 可能已过期）"
        else:
            msg = f"连接失败：{exception}"
        self._signals.connection_status.emit(msg)
```

- [ ] **Step 4: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_danmaku_client.py -v`
Expected: PASS（3 个用例）

- [ ] **Step 5: 编译检查**

Run: `venv\Scripts\python -m py_compile danmaku_client.py`
Expected: 无输出（成功）

---

### Task 4: styles.py 统一 QSS 主题

**Files:**
- Create: `styles.py`
- Test: `tests/test_styles.py`

**Interfaces:**
- Produces: `styles.STYLESHEET: str`（QSS 文本）、`styles.apply(app)`（对 QApplication 应用）

- [ ] **Step 1: 写失败测试 `tests/test_styles.py`**

```python
from styles import STYLESHEET


def test_stylesheet_has_core_selectors():
    for sel in ["QMainWindow", "QDialog", "QPushButton", "QLineEdit", "QCheckBox", "QFrame#card"]:
        assert sel in STYLESHEET
```

- [ ] **Step 2: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_styles.py -v`
Expected: FAIL（`ModuleNotFoundError: No module named 'styles'`）

- [ ] **Step 3: 创建 `styles.py`**

```python
# -*- coding: utf-8 -*-
"""统一 QSS 主题（浅色卡片式，B站蓝主色）。仅用于普通窗口，不应用于透明悬浮窗。"""

STYLESHEET = """
QMainWindow, QDialog, QWizard {
    background-color: #F5F6FA;
    color: #333333;
    font-family: "Microsoft YaHei";
    font-size: 13px;
}
QFrame#card {
    background-color: #FFFFFF;
    border-radius: 8px;
}
QPushButton {
    background-color: #00A1D6;
    color: #FFFFFF;
    border: none;
    border-radius: 4px;
    padding: 6px 16px;
    font-weight: bold;
}
QPushButton:hover { background-color: #33B5E0; }
QPushButton:pressed { background-color: #0089B8; }
QPushButton:disabled { background-color: #B3DCF0; }
QPushButton#danger { background-color: #F44336; }
QPushButton#danger:hover { background-color: #FF6652; }
QPushButton#plain { background-color: #EEF1F6; color: #333333; }
QPushButton#plain:hover { background-color: #E0E5EE; }
QLineEdit, QSpinBox, QDoubleSpinBox {
    background-color: #FFFFFF;
    border: 1px solid #D0D3DC;
    border-radius: 4px;
    padding: 4px 8px;
}
QLineEdit:focus, QSpinBox:focus, QDoubleSpinBox:focus { border-color: #00A1D6; }
QCheckBox { spacing: 6px; }
QCheckBox::indicator {
    width: 16px;
    height: 16px;
    border: 1px solid #D0D3DC;
    border-radius: 3px;
    background-color: #FFFFFF;
}
QCheckBox::indicator:checked { background-color: #00A1D6; border-color: #00A1D6; }
QLabel#status_ok { color: #4CAF50; font-weight: bold; }
QLabel#status_err { color: #F44336; font-weight: bold; }
QToolTip { background-color: #333333; color: #FFFFFF; border: none; padding: 4px; }
"""


def apply(app):
    """对 QApplication 应用主题"""
    app.setStyleSheet(STYLESHEET)
```

- [ ] **Step 4: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_styles.py -v`
Expected: PASS（1 个用例）

---

### Task 5: settings_dialog.py 从 overlay_window.py 抽出 + 重置默认按钮

**Files:**
- Create: `settings_dialog.py`
- Modify: `overlay_window.py`（删除 SettingsDialog 类，改为 import）
- Test: `tests/test_settings_dialog.py`

**Interfaces:**
- Produces: `settings_dialog.SettingsDialog(AppConfig, parent=None)` — 行为与原类一致（accept 时写回 config 并 save_config），新增 `reset_defaults()` 方法
- Consumes: `config.AppConfig`、`config.DEFAULT_CONFIG`、`config.DisplayConfig`

- [ ] **Step 1: 写失败测试 `tests/test_settings_dialog.py`**

```python
from config import AppConfig, DEFAULT_CONFIG
from settings_dialog import SettingsDialog


def test_reset_defaults_restores_display(qapp):
    cfg = AppConfig()
    cfg.display.font_size = 60
    dialog = SettingsDialog(cfg)
    dialog.reset_defaults()
    assert cfg.display.font_size == DEFAULT_CONFIG["display"]["font_size"]
    assert dialog.font_size_spin.value() == DEFAULT_CONFIG["display"]["font_size"]
```

- [ ] **Step 2: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_settings_dialog.py -v`
Expected: FAIL（`ModuleNotFoundError: No module named 'settings_dialog'`）

- [ ] **Step 3: 创建 `settings_dialog.py`**

把 `overlay_window.py` 中 `SettingsDialog` 类整体搬移到此文件（内容不变），顶部 import 改为：

```python
# -*- coding: utf-8 -*-
"""设置对话框（主窗口与托盘共用）"""
from PyQt5.QtWidgets import (
    QDialog, QFormLayout, QLineEdit, QSpinBox, QDoubleSpinBox, QCheckBox,
    QDialogButtonBox, QMessageBox,
)

from config import AppConfig, DisplayConfig, DEFAULT_CONFIG, save_config
```

在 `accept()` 方法之前新增方法（放在 __init__ 之后）：

```python
    def reset_defaults(self):
        """恢复默认显示设置并刷新控件"""
        defaults = DEFAULT_CONFIG["display"]
        self._config.display = DisplayConfig(**defaults)
        self.font_size_spin.setValue(self._config.display.font_size)
        self.speed_spin.setValue(self._config.display.scroll_speed)
        self.opacity_spin.setValue(self._config.display.opacity)
        self.tracks_spin.setValue(self._config.display.max_tracks)
        self.show_enter_cb.setChecked(self._config.display.show_enter)
        self.show_gift_cb.setChecked(self._config.display.show_gift)
        self.show_sc_cb.setChecked(self._config.display.show_sc)
        self.show_online_cb.setChecked(self._config.display.show_online)
```

在按钮行（`QDialogButtonBox` 之前）加一行按钮：

```python
        # 重置按钮
        btn_reset = QPushButton("重置为默认")
        btn_reset.setObjectName("plain")
        btn_reset.clicked.connect(self.reset_defaults)
        layout.addRow(btn_reset)
```

（`QPushButton` 需加入顶部 import。）

- [ ] **Step 4: 修改 `overlay_window.py`：删除类、改 import**

删除 `overlay_window.py` 中的整个 `class SettingsDialog(QDialog): ...` 定义（从 `class SettingsDialog` 到 `super().accept()` 结束）；删除不再使用的 import（`QDialog, QFormLayout, QLineEdit, QSpinBox, QDoubleSpinBox, QCheckBox, QDialogButtonBox, QColorDialog, QMessageBox` 按实际残留清理，保留 `QDialog`——`_show_settings` 里 `dialog.exec_() == QDialog.Accepted` 还要用）。顶部追加：

```python
from settings_dialog import SettingsDialog
```

- [ ] **Step 5: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_settings_dialog.py -v`
Expected: PASS（1 个用例）

- [ ] **Step 6: 编译检查两个文件**

Run: `venv\Scripts\python -m py_compile settings_dialog.py overlay_window.py`
Expected: 无输出（成功）

---

### Task 6: app_controller.py 控制器抽出

**Files:**
- Create: `app_controller.py`
- Test: `tests/test_app_controller.py`

**Interfaces:**
- Produces: `app_controller.AppController`：
  - 属性：`config: AppConfig`、`danmaku_signals`、`live_info_signals`、`danmaku_client`、`live_info`、`window`（OverlayWindow）
  - `async start() -> bool`（房间号<=0 时发状态"请先填写直播间ID"并返回 False）
  - `async stop()`、`async restart()`、`request_restart()`（同步，ensure_future）
  - `apply_display_config()`（renderer.configure + setWindowOpacity）
  - `set_overlay_visible(visible: bool)`
  - `async cleanup()`
- Consumes: `config.load_config`、`DanmakuClient`、`LiveInfoFetcher`、`OverlayWindow`
- 说明：`LiveAssistantApp` 从 main.py 迁出改名为 `AppController`；删除 `_ask_room_id`（房间号由主窗口输入）

- [ ] **Step 1: 写失败测试 `tests/test_app_controller.py`**

```python
from app_controller import AppController


def test_controller_start_without_room_emits_status(qapp):
    ctrl = AppController()
    ctrl.config.room_id = 0
    msgs = []
    ctrl.danmaku_signals.connection_status.connect(msgs.append)

    import asyncio
    result = asyncio.get_event_loop().run_until_complete(ctrl.start())

    assert result is False
    assert msgs and "请先填写直播间ID" in msgs[-1]


def test_set_overlay_visible(qapp):
    ctrl = AppController()
    ctrl.window.show()
    ctrl.set_overlay_visible(False)
    assert not ctrl.window.isVisible()
    ctrl.set_overlay_visible(True)
    assert ctrl.window.isVisible()
```

注意：`AppController.__init__` 会创建 `OverlayWindow`（需 QApplication，conftest 的 qapp fixture 已提供）。测试中 `ctrl.window.show()` 在 offscreen 平台可用。事件循环用 `qasync` 的环境下直接 `asyncio.get_event_loop().run_until_complete` 需要 loop 是 running 状态——此处 `start()` 在房间号无效时**不会 await 任何东西就返回**，普通 loop 即可，若报 loop 相关错误改为在测试内临时建 loop（`asyncio.new_event_loop()` + `set_event_loop` + `run_until_complete` + `close`）。

- [ ] **Step 2: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_app_controller.py -v`
Expected: FAIL（`ModuleNotFoundError: No module named 'app_controller'`）

- [ ] **Step 3: 创建 `app_controller.py`**

```python
# -*- coding: utf-8 -*-
"""应用控制器 - 管理客户端生命周期与跨窗口信号"""
import asyncio
import logging

from config import load_config
from danmaku_client import DanmakuClient, DanmakuSignals
from live_info import LiveInfoFetcher, LiveInfoSignals
from overlay_window import OverlayWindow

logger = logging.getLogger("app_controller")


class AppController:
    """应用主控制器"""

    def __init__(self):
        self.config = load_config()
        self.danmaku_signals = DanmakuSignals()
        self.live_info_signals = LiveInfoSignals()
        self.danmaku_client = DanmakuClient(self.danmaku_signals)
        self.live_info = LiveInfoFetcher(self.live_info_signals)
        self.window = OverlayWindow(self.config)
        # 信号接线：弹幕事件 → 悬浮窗
        self.danmaku_signals.danmaku_received.connect(self.window.add_danmaku)
        self.danmaku_signals.connection_status.connect(self._on_status)
        self.live_info_signals.online_updated.connect(self.window.add_danmaku)
        self.live_info_signals.rank_updated.connect(self.window.add_danmaku)
        self.window.restart_requested.connect(self.request_restart)

    def _on_status(self, status: str):
        logger.info(f"Status: {status}")
        self.window.tray_icon.setToolTip(f"B站直播弹幕助手 - {status}")

    async def start(self) -> bool:
        """启动弹幕监听与在线轮询；房间号无效时返回 False"""
        room_id = self.config.room_id
        if room_id <= 0:
            self.danmaku_signals.connection_status.emit("请先填写直播间ID")
            return False
        await self.danmaku_client.start(room_id, self.config.cookie, self.config.display)
        if self.config.display.show_online:
            await self.live_info.start(room_id, self.config.cookie, self.config.poll_interval)
        self.window.show()
        logger.info(f"Started, room_id={room_id}")
        return True

    async def stop(self):
        await self.danmaku_client.stop()
        await self.live_info.stop()

    async def restart(self):
        await self.stop()
        await self.start()

    def request_restart(self):
        """同步入口：设置保存等场景触发重启连接"""
        asyncio.ensure_future(self.restart())

    def apply_display_config(self):
        """把当前 config.display 应用到悬浮窗（热生效）"""
        self.window.renderer.configure(self.config.display)
        self.window.setWindowOpacity(self.config.display.opacity)

    def set_overlay_visible(self, visible: bool):
        if visible:
            self.window.show()
        else:
            self.window.hide()

    async def cleanup(self):
        await self.stop()
```

- [ ] **Step 4: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_app_controller.py -v`
Expected: PASS（2 个用例）

- [ ] **Step 5: 验证编译 + 现有 main.py 仍可运行（此时 main.py 未改，仍用自己的 LiveAssistantApp）**

Run: `venv\Scripts\python -m py_compile app_controller.py && venv\Scripts\python -m pytest tests/ -v`
Expected: 全部 PASS（test_app_controller 的 2 个 + 之前任务的用例）

---

### Task 7: main_window.py 主控制窗口

**Files:**
- Create: `main_window.py`
- Test: `tests/test_main_window.py`

**Interfaces:**
- Produces: `main_window.MainWindow(controller)`：
  - 属性：`status_label: QLabel`、`popularity_label: QLabel`、`room_edit: QLineEdit`、`btn_start: QPushButton`、`switches: dict[str, QCheckBox]`、`font_spin/speed_spin/opacity_spin`
  - 方法：`set_connected(connected: bool)`、`show_and_raise()`、`refresh_from_config()`
  - 信号订阅：`controller.danmaku_signals.connection_status`、`controller.live_info_signals.online_updated`
  - 交互：房间号输入（支持链接，即时校验）；开始/停止（`controller.start/stop`）；功能开关与显示参数改动 → `save_config` + `controller.apply_display_config()`；"打开详细设置" → SettingsDialog → `controller.request_restart()`
- Consumes: `utils.extract_room_id`、`settings_dialog.SettingsDialog`、`config.save_config`、Task 6 的 `AppController`

- [ ] **Step 1: 写失败测试 `tests/test_main_window.py`**

```python
from config import AppConfig
from danmaku_client import DanmakuSignals, DanmakuEvent
from live_info import LiveInfoSignals
from main_window import MainWindow


class FakeController:
    """最小控制器桩：只带测试需要的接口"""

    def __init__(self):
        self.config = AppConfig(room_id=12722549)
        self.danmaku_signals = DanmakuSignals()
        self.live_info_signals = LiveInfoSignals()
        self.danmaku_client = type("C", (), {"is_running": False})()
        self.window = type("W", (), {
            "renderer": type("R", (), {"configure": lambda self, cfg: None})(),
            "setWindowOpacity": lambda self, o: None,
            "show": lambda self: None,
            "hide": lambda self: None,
        })()

    def apply_display_config(self):
        pass

    def request_restart(self):
        pass


def test_status_signal_updates_label(qapp):
    ctrl = FakeController()
    win = MainWindow(ctrl)
    ctrl.danmaku_signals.connection_status.emit("连接失败：直播间不存在或无法获取弹幕服务器")
    assert "连接失败" in win.status_label.text()
    assert win.status_label.objectName() == "status_err"
    ctrl.danmaku_signals.connection_status.emit("正在连接直播间 12722549 ...")
    assert "正在连接" in win.status_label.text()
    assert win.status_label.objectName() == "status_ok"


def test_popularity_signal_updates_label(qapp):
    ctrl = FakeController()
    win = MainWindow(ctrl)
    evt = DanmakuEvent(DanmakuEvent.TYPE_ONLINE, "系统", "人气: 24")
    ctrl.live_info_signals.online_updated.emit(evt)
    assert win.popularity_label.text() == "人气: 24"
```

- [ ] **Step 2: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_main_window.py -v`
Expected: FAIL（`ModuleNotFoundError: No module named 'main_window'`）

- [ ] **Step 3: 创建 `main_window.py`**

```python
# -*- coding: utf-8 -*-
"""主控制窗口 - 状态、连接控制、功能开关、显示设置"""
import asyncio

from PyQt5.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QLabel, QLineEdit,
    QPushButton, QCheckBox, QFrame, QSpinBox, QDoubleSpinBox, QApplication,
    QDialog,
)

from config import save_config
from settings_dialog import SettingsDialog
from utils import extract_room_id


class MainWindow(QMainWindow):
    """主控制窗口"""

    def __init__(self, controller):
        super().__init__()
        self._controller = controller
        self.setWindowTitle("B站直播弹幕助手")
        self.setMinimumSize(520, 480)
        self._build_ui()
        self._wire_signals()

    # ---------- UI 构建 ----------
    def _build_ui(self):
        cfg = self._controller.config
        central = QWidget(self)
        root = QVBoxLayout(central)
        root.setContentsMargins(16, 16, 16, 16)
        root.setSpacing(12)

        # 标题栏
        header = QHBoxLayout()
        title = QLabel("🎬 B站直播弹幕助手")
        title.setStyleSheet("font-size: 18px; font-weight: bold;")
        header.addWidget(title)
        header.addStretch(1)
        self.status_label = QLabel("○ 未连接")
        self.status_label.setObjectName("status_ok")
        header.addWidget(self.status_label)
        self.room_label = QLabel("")
        header.addWidget(self.room_label)
        root.addLayout(header)

        root.addWidget(self._card_room())
        root.addWidget(self._card_switches())
        root.addWidget(self._card_display())

        # 底部
        bottom = QHBoxLayout()
        self.overlay_check = QCheckBox("显示悬浮窗")
        self.overlay_check.setChecked(True)
        self.overlay_check.toggled.connect(
            lambda checked: self._controller.set_overlay_visible(checked)
        )
        bottom.addWidget(self.overlay_check)
        bottom.addStretch(1)
        btn_min = QPushButton("最小化到托盘")
        btn_min.setObjectName("plain")
        btn_min.clicked.connect(self.hide)
        btn_quit = QPushButton("退出")
        btn_quit.setObjectName("danger")
        btn_quit.clicked.connect(QApplication.quit)
        bottom.addWidget(btn_min)
        bottom.addWidget(btn_quit)
        root.addLayout(bottom)

        self.setCentralWidget(central)

    def _card(self, title: str) -> QFrame:
        card = QFrame()
        card.setObjectName("card")
        lay = QVBoxLayout(card)
        lay.setContentsMargins(12, 8, 12, 12)
        label = QLabel(title)
        label.setStyleSheet("font-weight: bold; color: #00A1D6;")
        lay.addWidget(label)
        return card

    def _card_room(self) -> QFrame:
        card = self._card("直播间")
        lay = card.layout()
        row = QHBoxLayout()
        self.room_edit = QLineEdit()
        if self._controller.config.room_id > 0:
            self.room_edit.setText(str(self._controller.config.room_id))
        self.room_edit.setPlaceholderText("房间号，或粘贴 live.bilibili.com/xxx 链接")
        self.room_edit.textChanged.connect(self._on_room_text_changed)
        row.addWidget(self.room_edit, 1)
        self.btn_start = QPushButton("开始监听")
        self.btn_start.clicked.connect(self._toggle_connection)
        row.addWidget(self.btn_start)
        lay.addLayout(row)

        self.room_error = QLabel("")
        self.room_error.setStyleSheet("color: #F44336;")
        lay.addWidget(self.room_error)

        pop_row = QHBoxLayout()
        pop_row.addWidget(QLabel("人气:"))
        self.popularity_label = QLabel("--")
        pop_row.addWidget(self.popularity_label)
        pop_row.addStretch(1)
        lay.addLayout(pop_row)
        return card

    def _card_switches(self) -> QFrame:
        card = self._card("功能开关")
        lay = card.layout()
        row = QHBoxLayout()
        d = self._controller.config.display
        cb_danmaku = QCheckBox("弹幕")
        cb_danmaku.setChecked(True)
        cb_danmaku.setEnabled(False)  # 弹幕常开
        self.switches = {
            "danmaku": cb_danmaku,
            "enter": QCheckBox("进入"),
            "gift": QCheckBox("礼物"),
            "sc": QCheckBox("SC"),
            "online": QCheckBox("高能榜"),
        }
        self.switches["enter"].setChecked(d.show_enter)
        self.switches["gift"].setChecked(d.show_gift)
        self.switches["sc"].setChecked(d.show_sc)
        self.switches["online"].setChecked(d.show_online)
        for cb in self.switches.values():
            row.addWidget(cb)
        for key in ("enter", "gift", "sc", "online"):
            self.switches[key].toggled.connect(self._on_switch_changed)
        lay.addLayout(row)
        return card

    def _card_display(self) -> QFrame:
        card = self._card("显示设置")
        lay = card.layout()
        d = self._controller.config.display
        row = QHBoxLayout()
        row.addWidget(QLabel("字体"))
        self.font_spin = QSpinBox()
        self.font_spin.setRange(12, 72)
        self.font_spin.setValue(d.font_size)
        row.addWidget(self.font_spin)
        row.addWidget(QLabel("速度"))
        self.speed_spin = QSpinBox()
        self.speed_spin.setRange(30, 500)
        self.speed_spin.setSuffix(" px/s")
        self.speed_spin.setValue(d.scroll_speed)
        row.addWidget(self.speed_spin)
        row.addWidget(QLabel("透明度"))
        self.opacity_spin = QDoubleSpinBox()
        self.opacity_spin.setRange(0.1, 1.0)
        self.opacity_spin.setSingleStep(0.05)
        self.opacity_spin.setValue(d.opacity)
        row.addWidget(self.opacity_spin)
        lay.addLayout(row)

        row2 = QHBoxLayout()
        btn = QPushButton("打开详细设置…")
        btn.setObjectName("plain")
        btn.clicked.connect(self._open_settings)
        row2.addWidget(btn)
        row2.addStretch(1)
        lay.addLayout(row2)
        return card

    # ---------- 交互 ----------
    def _on_room_text_changed(self, text: str):
        if text.strip() and extract_room_id(text) is None:
            self.room_error.setText("请输入数字房间号或完整直播链接")
        else:
            self.room_error.setText("")

    def _toggle_connection(self):
        c = self._controller
        if c.danmaku_client.is_running:
            self.btn_start.setText("开始监听")
            asyncio.ensure_future(c.stop())
            self.set_connected(False)
            return
        rid = extract_room_id(self.room_edit.text())
        if rid is None:
            self.room_error.setText("请先填写有效的直播间ID")
            return
        c.config.room_id = rid
        save_config(c.config)
        self.room_label.setText(f"直播间 {rid}")
        self.btn_start.setText("停止")
        asyncio.ensure_future(c.start())

    def _on_switch_changed(self):
        d = self._controller.config.display
        d.show_enter = self.switches["enter"].isChecked()
        d.show_gift = self.switches["gift"].isChecked()
        d.show_sc = self.switches["sc"].isChecked()
        d.show_online = self.switches["online"].isChecked()
        save_config(self._controller.config)
        self._controller.apply_display_config()

    def _on_display_changed(self):
        d = self._controller.config.display
        d.font_size = self.font_spin.value()
        d.scroll_speed = self.speed_spin.value()
        d.opacity = self.opacity_spin.value()
        save_config(self._controller.config)
        self._controller.apply_display_config()

    def _open_settings(self):
        dialog = SettingsDialog(self._controller.config, self)
        if dialog.exec_() == QDialog.Accepted:
            self._controller.apply_display_config()
            self._controller.request_restart()
            self.refresh_from_config()

    # ---------- 信号 ----------
    def _wire_signals(self):
        c = self._controller
        c.danmaku_signals.connection_status.connect(self._on_status)
        c.live_info_signals.online_updated.connect(self._on_popularity)
        self.font_spin.valueChanged.connect(self._on_display_changed)
        self.speed_spin.valueChanged.connect(self._on_display_changed)
        self.opacity_spin.valueChanged.connect(self._on_display_changed)

    def _on_status(self, status: str):
        if any(k in status for k in ("失败", "断开", "请先填写")):
            self.status_label.setObjectName("status_err")
        else:
            self.status_label.setObjectName("status_ok")
        self.status_label.setText(f"● {status}")
        self.status_label.style().unpolish(self.status_label)
        self.status_label.style().polish(self.status_label)

    def _on_popularity(self, event):
        self.popularity_label.setText(event.content)

    # ---------- 外部接口 ----------
    def set_connected(self, connected: bool):
        self.btn_start.setText("停止" if connected else "开始监听")

    def show_and_raise(self):
        self.show()
        self.raise_()
        self.activateWindow()

    def refresh_from_config(self):
        """设置变更后刷新主窗口控件"""
        d = self._controller.config.display
        self.font_spin.setValue(d.font_size)
        self.speed_spin.setValue(d.scroll_speed)
        self.opacity_spin.setValue(d.opacity)
        self.switches["enter"].setChecked(d.show_enter)
        self.switches["gift"].setChecked(d.show_gift)
        self.switches["sc"].setChecked(d.show_sc)
        self.switches["online"].setChecked(d.show_online)
```

- [ ] **Step 4: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_main_window.py -v`
Expected: PASS（2 个用例）

- [ ] **Step 5: 编译检查**

Run: `venv\Scripts\python -m py_compile main_window.py`
Expected: 无输出（成功）

---

### Task 8: wizard.py 首次运行三步向导

**Files:**
- Create: `wizard.py`
- Test: `tests/test_wizard.py`

**Interfaces:**
- Produces: `wizard.build_config_from_fields(room_text, sessdata, buvid3, bili_jct, font_size, scroll_speed, opacity) -> AppConfig`（纯函数，无效房间号 → room_id=0）
- Produces: `wizard.FirstRunWizard(config: AppConfig, parent=None)`（QWizard，accept() 时把字段写回 config，不负责 save_config）
- Consumes: `utils.extract_room_id`、`config` 各 dataclass

- [ ] **Step 1: 写失败测试 `tests/test_wizard.py`**

```python
from wizard import build_config_from_fields


def test_build_config_from_link():
    cfg = build_config_from_fields(
        "https://live.bilibili.com/12722549?spm_id_from=333",
        "SESS", "BUV", "JCT", 30, 150, 0.9,
    )
    assert cfg.room_id == 12722549
    assert cfg.cookie.SESSDATA == "SESS"
    assert cfg.cookie.buvid3 == "BUV"
    assert cfg.cookie.bili_jct == "JCT"
    assert cfg.display.font_size == 30
    assert cfg.display.scroll_speed == 150
    assert cfg.display.opacity == 0.9


def test_build_config_invalid_room_zero():
    cfg = build_config_from_fields("abc", "", "", "", 28, 120, 0.85)
    assert cfg.room_id == 0


def test_build_config_strips_cookie_whitespace():
    cfg = build_config_from_fields("123456", "  SESS  ", "", "", 28, 120, 0.85)
    assert cfg.cookie.SESSDATA == "SESS"
```

- [ ] **Step 2: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_wizard.py -v`
Expected: FAIL（`ModuleNotFoundError: No module named 'wizard'`）

- [ ] **Step 3: 创建 `wizard.py`**

```python
# -*- coding: utf-8 -*-
"""首次运行三步向导：直播间 → Cookie → 显示设置"""
from PyQt5.QtWidgets import (
    QWizard, QWizardPage, QVBoxLayout, QHBoxLayout, QLabel, QLineEdit,
    QSpinBox, QDoubleSpinBox, QPushButton,
)

from config import AppConfig, CookieConfig, DisplayConfig
from utils import extract_room_id

COOKIE_GUIDE = (
    "1. 用浏览器打开并登录 bilibili.com\n"
    "2. 按 F12 打开开发者工具\n"
    "3. 切换到「应用」(Application) → Cookie → https://www.bilibili.com\n"
    "4. 找到 SESSDATA、buvid3、bili_jct 三个值并填入左侧\n\n"
    "不填 Cookie 也能接收弹幕，只是用户名会打码、UID 显示为 0。"
)


def build_config_from_fields(
    room_text: str, sessdata: str, buvid3: str, bili_jct: str,
    font_size: int, scroll_speed: int, opacity: float,
) -> AppConfig:
    """纯函数：向导字段 → AppConfig"""
    cfg = AppConfig()
    rid = extract_room_id(room_text)
    cfg.room_id = rid or 0
    cfg.cookie = CookieConfig(
        buvid3=buvid3.strip(), SESSDATA=sessdata.strip(), bili_jct=bili_jct.strip()
    )
    cfg.display = DisplayConfig(
        font_size=font_size, scroll_speed=scroll_speed, opacity=opacity
    )
    return cfg


class RoomPage(QWizardPage):
    """步骤1：直播间"""

    def __init__(self):
        super().__init__()
        self.setTitle("直播间")
        lay = QVBoxLayout(self)
        lay.addWidget(QLabel("填写你要观看的直播间："))
        self.room_edit = QLineEdit()
        self.room_edit.setPlaceholderText("直播间ID，或粘贴 live.bilibili.com/12345 链接")
        lay.addWidget(self.room_edit)
        tip = QLabel("直播间ID 是直播页地址栏 live.bilibili.com/ 后面的数字")
        tip.setStyleSheet("color: #888888;")
        lay.addWidget(tip)
        self.error_label = QLabel("")
        self.error_label.setStyleSheet("color: #F44336;")
        lay.addWidget(self.error_label)
        self.room_edit.textChanged.connect(self._validate)

    def _validate(self, text: str):
        valid = extract_room_id(text) is not None
        self.error_label.setText("" if valid else "请输入数字房间号或完整直播链接")
        self.completeChanged.emit()

    def isComplete(self) -> bool:
        return extract_room_id(self.room_edit.text()) is not None


class CookiePage(QWizardPage):
    """步骤2：登录 Cookie（可选）"""

    def __init__(self):
        super().__init__()
        self.setTitle("登录 Cookie（可选）")
        lay = QVBoxLayout(self)
        self.sessdata_edit = QLineEdit()
        self.sessdata_edit.setEchoMode(QLineEdit.Password)
        self.sessdata_edit.setPlaceholderText("SESSDATA")
        self.buvid3_edit = QLineEdit()
        self.buvid3_edit.setPlaceholderText("buvid3")
        self.bili_jct_edit = QLineEdit()
        self.bili_jct_edit.setEchoMode(QLineEdit.Password)
        self.bili_jct_edit.setPlaceholderText("bili_jct")
        lay.addWidget(QLabel("SESSDATA："))
        lay.addWidget(self.sessdata_edit)
        lay.addWidget(QLabel("buvid3："))
        lay.addWidget(self.buvid3_edit)
        lay.addWidget(QLabel("bili_jct："))
        lay.addWidget(self.bili_jct_edit)

        btn_guide = QPushButton("如何获取 Cookie？")
        btn_guide.setObjectName("plain")
        btn_guide.setCheckable(True)
        btn_guide.toggled.connect(lambda checked: self.guide_label.setVisible(checked))
        lay.addWidget(btn_guide)
        self.guide_label = QLabel(COOKIE_GUIDE)
        self.guide_label.setVisible(False)
        self.guide_label.setStyleSheet(
            "background: #F5F6FA; padding: 8px; color: #666666;"
        )
        lay.addWidget(self.guide_label)
        lay.addStretch(1)

    def isComplete(self) -> bool:
        return True  # Cookie 可选


class SettingsPage(QWizardPage):
    """步骤3：显示设置"""

    def __init__(self):
        super().__init__()
        self.setTitle("显示设置")
        lay = QVBoxLayout(self)
        row = QHBoxLayout()
        row.addWidget(QLabel("字体大小"))
        self.font_spin = QSpinBox()
        self.font_spin.setRange(12, 72)
        self.font_spin.setValue(28)
        row.addWidget(self.font_spin)
        row.addStretch(1)
        lay.addLayout(row)

        row = QHBoxLayout()
        row.addWidget(QLabel("滚动速度"))
        self.speed_spin = QSpinBox()
        self.speed_spin.setRange(30, 500)
        self.speed_spin.setSuffix(" px/s")
        self.speed_spin.setValue(120)
        row.addWidget(self.speed_spin)
        row.addStretch(1)
        lay.addLayout(row)

        row = QHBoxLayout()
        row.addWidget(QLabel("不透明度"))
        self.opacity_spin = QDoubleSpinBox()
        self.opacity_spin.setRange(0.1, 1.0)
        self.opacity_spin.setSingleStep(0.05)
        self.opacity_spin.setValue(0.85)
        row.addWidget(self.opacity_spin)
        row.addStretch(1)
        lay.addLayout(row)

        tip = QLabel("这些都可以在主窗口随时调整。")
        tip.setStyleSheet("color: #888888;")
        lay.addWidget(tip)
        lay.addStretch(1)

    def isComplete(self) -> bool:
        return True


class FirstRunWizard(QWizard):
    """首次使用引导向导"""

    def __init__(self, config: AppConfig, parent=None):
        super().__init__(parent)
        self.setWindowTitle("首次使用引导")
        self.setMinimumSize(480, 420)
        self._config = config
        # 用已有配置预填
        d = config.display
        self.room_page = RoomPage()
        if config.room_id > 0:
            self.room_page.room_edit.setText(str(config.room_id))
        self.cookie_page = CookiePage()
        self.cookie_page.sessdata_edit.setText(config.cookie.SESSDATA)
        self.cookie_page.buvid3_edit.setText(config.cookie.buvid3)
        self.cookie_page.bili_jct_edit.setText(config.cookie.bili_jct)
        self.settings_page = SettingsPage()
        self.settings_page.font_spin.setValue(d.font_size)
        self.settings_page.speed_spin.setValue(d.scroll_speed)
        self.settings_page.opacity_spin.setValue(d.opacity)
        self.addPage(self.room_page)
        self.addPage(self.cookie_page)
        self.addPage(self.settings_page)

    def accept(self):
        """收集字段写回 config（保存由调用方负责）"""
        cfg = build_config_from_fields(
            self.room_page.room_edit.text(),
            self.cookie_page.sessdata_edit.text(),
            self.cookie_page.buvid3_edit.text(),
            self.cookie_page.bili_jct_edit.text(),
            self.settings_page.font_spin.value(),
            self.settings_page.speed_spin.value(),
            self.settings_page.opacity_spin.value(),
        )
        self._config.room_id = cfg.room_id
        self._config.cookie = cfg.cookie
        self._config.display.font_size = cfg.display.font_size
        self._config.display.scroll_speed = cfg.display.scroll_speed
        self._config.display.opacity = cfg.display.opacity
        super().accept()
```

- [ ] **Step 4: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_wizard.py -v`
Expected: PASS（3 个用例）

- [ ] **Step 5: 编译检查**

Run: `venv\Scripts\python -m py_compile wizard.py`
Expected: 无输出（成功）

---

### Task 9: main.py 入口整合 + overlay 托盘"打开主窗口"

**Files:**
- Modify: `main.py`（重写）
- Modify: `overlay_window.py`（新增 `open_main_window_requested` 信号 + 托盘菜单项）

**Interfaces:**
- Produces: `overlay_window.OverlayWindow.open_main_window_requested: pyqtSignal()`（托盘"打开主窗口"动作发出）
- Consumes: `AppController`、`MainWindow`、`FirstRunWizard`、`needs_onboarding`、`save_config`、`styles`

- [ ] **Step 1: 写失败测试（信号存在性）`tests/test_overlay_window.py`**

```python
from overlay_window import OverlayWindow
from config import AppConfig


def test_open_main_window_signal_exists(qapp):
    win = OverlayWindow(AppConfig())
    assert hasattr(win, "open_main_window_requested")
```

- [ ] **Step 2: 运行测试确认失败**

Run: `venv\Scripts\python -m pytest tests/test_overlay_window.py -v`
Expected: FAIL（`OverlayWindow` 无 `open_main_window_requested` 属性）

- [ ] **Step 3: 修改 `overlay_window.py`**

类定义处（`class OverlayWindow(QWidget):` 下方）已有 `restart_requested = pyqtSignal()`，在其后追加：

```python
    open_main_window_requested = pyqtSignal()  # 请求打开主窗口
```

`_setup_tray` 中，在 `action_settings` 之后追加（menu.addAction(action_settings) 下一行）：

```python
        action_main = QAction("🏠 打开主窗口", self)
        action_main.triggered.connect(self.open_main_window_requested.emit)
        menu.addAction(action_main)
```

- [ ] **Step 4: 运行测试确认通过**

Run: `venv\Scripts\python -m pytest tests/test_overlay_window.py -v`
Expected: PASS（1 个用例）

- [ ] **Step 5: 重写 `main.py`**

```python
# -*- coding: utf-8 -*-
"""
B站直播弹幕助手 - 主入口
首次运行 → 三步引导向导；之后 → 主控制窗口。使用 qasync 桥接 asyncio 和 PyQt5 事件循环。
"""
import asyncio
import logging
import sys

from PyQt5.QtWidgets import QApplication, QDialog
from qasync import QEventLoop

from app_controller import AppController
from config import needs_onboarding, save_config
from main_window import MainWindow
from wizard import FirstRunWizard
import styles

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s %(levelname)s %(name)s] %(message)s",
)
logger = logging.getLogger("main")


def main():
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    styles.apply(app)

    loop = QEventLoop(app)
    asyncio.set_event_loop(loop)

    controller = AppController()
    main_window = MainWindow(controller)
    # 托盘"打开主窗口" → 显示主窗口
    controller.window.open_main_window_requested.connect(main_window.show_and_raise)

    async def _run():
        if needs_onboarding(controller.config):
            wizard = FirstRunWizard(controller.config)
            accepted = wizard.exec_() == QDialog.Accepted
            if accepted:
                save_config(controller.config)
            if not accepted and controller.config.room_id <= 0:
                # 向导被关闭且没有房间号：进主窗口但不自动连接
                main_window.show()
                return
        main_window.show()
        ok = await controller.start()
        if ok:
            main_window.set_connected(True)

    async def _shutdown():
        await controller.cleanup()

    app.aboutToQuit.connect(lambda: asyncio.ensure_future(_shutdown()))

    with loop:
        loop.create_task(_run())
        loop.run_forever()


if __name__ == "__main__":
    main()
```

注意：`main.py` 里 `asyncio.ensure_future` 在 `_shutdown` 的连接中调用（`aboutToQuit` 时 loop 可能已关闭，属既有模式的保留行为，不在此任务处理）。

- [ ] **Step 6: 全量测试 + 编译**

Run: `venv\Scripts\python -m pytest tests/ -v`
Expected: 全部 PASS（约 20 个用例）
Run: `venv\Scripts\python -m py_compile main.py overlay_window.py app_controller.py main_window.py wizard.py settings_dialog.py`
Expected: 无输出（成功）

- [ ] **Step 7: 手工冒烟（可见 GUI）**

Run: `venv\Scripts\python main.py`（或双击 `run.bat`）
Expected: 出现主控制窗口（当前 config 已配置 → 不显示向导），自动连接后悬浮窗出现；托盘菜单含"打开主窗口"。**此步骤需要你（用户）肉眼确认**：窗口布局、连接状态、托盘行为。

---

### Task 10: 弹幕渲染美化（修复彩色填充 + 柔和描边）

**Files:**
- Modify: `danmaku_renderer.py`（`_render_to_pixmap` 方法）

**背景（Task 7 之前发现的原生缺陷）**：`_render_to_pixmap` 两遍 `drawText` 都没有设置 brush，Qt 默认黑色填充——实际效果是**黑色填充的文字 + 彩色描边**，弹幕颜色看起来发暗。本任务改为标准"描边弹幕"画法：第一遍深色粗描边填充，第二遍彩色填充细描边。

- [ ] **Step 1: 修改 `_render_to_pixmap`**

原实现：

```python
        # 描边
        pen = QPen(QColor(0, 0, 0, 200))
        pen.setWidth(3)
        painter.setPen(pen)
        painter.drawText(pixmap.rect(), Qt.AlignCenter, text)

        # 填充
        color = QColor(item.event.color)
        painter.setPen(QPen(color))
        painter.drawText(pixmap.rect(), Qt.AlignCenter, text)
```

替换为：

```python
        # 第一遍：深色粗描边（笔与刷都用深色，形成轮廓）
        painter.setPen(QPen(QColor(0, 0, 0, 180), 4))
        painter.setBrush(QColor(0, 0, 0, 180))
        painter.drawText(pixmap.rect(), Qt.AlignCenter, text)

        # 第二遍：彩色填充，覆盖中心区域，仅留深色描边
        color = QColor(item.event.color)
        painter.setPen(Qt.NoPen)
        painter.setBrush(color)
        painter.drawText(pixmap.rect(), Qt.AlignCenter, text)
```

- [ ] **Step 2: 编译检查**

Run: `venv\Scripts\python -m py_compile danmaku_renderer.py`
Expected: 无输出（成功）

- [ ] **Step 3: 全量测试回归**

Run: `venv\Scripts\python -m pytest tests/ -v`
Expected: 全部 PASS

- [ ] **Step 4: 手工视觉确认（需你确认）**

Run: `venv\Scripts\python main.py`
Expected: 悬浮窗弹幕文字颜色真实（彩色填充 + 深色描边），不再是黑底彩边。

---

### Task 11: PyInstaller 单文件 exe 打包

**Files:**
- Create: `build.spec`、`build.bat`
- 产物：`dist/B站弹幕助手.exe`

**Interfaces:**
- Produces: `build.spec`（PyInstaller 配置）、`build.bat`（一键打包入口）

- [ ] **Step 1: 安装 PyInstaller**

```bash
venv\Scripts\pip install pyinstaller
```

- [ ] **Step 2: 创建 `build.spec`**

```python
# -*- mode: python ; coding: utf-8 -*-
# PyInstaller 配置：单文件、无控制台。blivedm_lib 通过 pathex 收集。
a = Analysis(
    ['main.py'],
    pathex=['.', 'blivedm_lib'],
    binaries=[],
    datas=[],
    hiddenimports=['qasync', 'aiohttp', 'pure_protobuf', 'blivedm'],
    hookspath=[],
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='B站弹幕助手',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,            # 无控制台窗口
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
```

- [ ] **Step 3: 创建 `build.bat`**

```bat
@echo off
chcp 65001 >nul
cd /d "%~dp0"
venv\Scripts\pyinstaller --clean --noconfirm build.spec
echo.
echo 打包完成: dist\B站弹幕助手.exe
pause
```

- [ ] **Step 4: 执行打包**

Run: `cmd //c build.bat`（或 `venv\Scripts\pyinstaller --clean --noconfirm build.spec`）
Expected: 无报错；`dist\B站弹幕助手.exe` 生成

- [ ] **Step 5: 验证 exe 可运行**

```bash
# 启动 exe，等 5 秒，检查进程存活与配置落盘
powershell.exe -NoProfile -Command "Start-Process -FilePath 'dist\B站弹幕助手.exe'"
sleep 5
powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='B站弹幕助手.exe'\" | Select-Object ProcessId, CreationDate | Format-Table -AutoSize"
ls dist/*.json 2>/dev/null || echo "等待向导完成后再检查 config"
```

Expected: 进程存活（无启动即崩溃）；exe 目录（dist/）出现 `config.json`（若弹向导，需人工完成一次）。**此步骤需要你（用户）配合确认 exe 界面正常**。

- [ ] **Step 6: 打包产物全流程复测（需你确认）**

用 `dist\B站弹幕助手.exe` 完成：向导（首次）→ 主窗口 → 连接 → 悬浮窗 → 托盘 → 重启后配置仍在。

---

## 自审记录

- **Spec 覆盖**：主窗口（T7）✓；向导三步（T8）✓；配置持久化 frozen（T1）✓；连接失败透出（T3）✓；美化（T4 主题 + T10 弹幕绘制）✓；设置对话框抽出+重置（T5）✓；托盘打开主窗口（T9）✓；打包（T11）✓；错误处理表各场景分别落在 T1（不可写回退）、T3（失败原因）、T8（向导关闭不保存）、T7（输入校验）✓
- **占位符扫描**：无 TBD/TODO；所有代码步骤含完整实现
- **类型一致性**：`AppController.start() -> bool` 在 T6 定义、T9 使用一致；`MainWindow(controller)` 构造签名 T7 定义、T9 使用一致；`extract_room_id` T2 定义、T7/T8 使用一致；`needs_onboarding` T2 定义、T9 使用一致
- **顺序依赖**：T9 依赖 T6/T7/T8（main.py 引用三者）；T7 依赖 T5（SettingsDialog）；各测试无跨任务依赖
