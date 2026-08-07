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
