# -*- coding: utf-8 -*-
"""设置对话框（主窗口与托盘共用）"""
from dataclasses import asdict

from PyQt5.QtWidgets import (
    QDialog, QFormLayout, QLineEdit, QSpinBox, QDoubleSpinBox, QCheckBox,
    QDialogButtonBox, QMessageBox, QPushButton,
)

from config import AppConfig, DisplayConfig, DEFAULT_CONFIG, save_config
from utils import extract_room_id


class SettingsDialog(QDialog):
    """设置对话框"""

    def __init__(self, config: AppConfig, parent=None):
        super().__init__(parent)
        self.setWindowTitle("弹幕设置")
        self.setMinimumWidth(400)
        self._config = config

        # 打开时快照，取消时恢复，避免重置后点取消仍改动共享配置
        self._display_backup = asdict(config.display)

        layout = QFormLayout(self)

        # 房间号
        self.room_id_edit = QLineEdit(str(config.room_id))
        self.room_id_edit.setPlaceholderText("输入直播间ID")
        layout.addRow("直播间ID:", self.room_id_edit)

        # Cookie
        self.sessdata_edit = QLineEdit(config.cookie.SESSDATA)
        self.sessdata_edit.setEchoMode(QLineEdit.Password)
        self.sessdata_edit.setPlaceholderText("B站登录Cookie中的SESSDATA")
        layout.addRow("SESSDATA:", self.sessdata_edit)

        self.buvid3_edit = QLineEdit(config.cookie.buvid3)
        self.buvid3_edit.setPlaceholderText("B站Cookie中的buvid3")
        layout.addRow("buvid3:", self.buvid3_edit)

        self.bili_jct_edit = QLineEdit(config.cookie.bili_jct)
        self.bili_jct_edit.setEchoMode(QLineEdit.Password)
        self.bili_jct_edit.setPlaceholderText("B站Cookie中的bili_jct")
        layout.addRow("bili_jct:", self.bili_jct_edit)

        # 显示参数
        self.font_size_spin = QSpinBox()
        self.font_size_spin.setRange(12, 72)
        self.font_size_spin.setValue(config.display.font_size)
        layout.addRow("字体大小:", self.font_size_spin)

        self.speed_spin = QSpinBox()
        self.speed_spin.setRange(30, 500)
        self.speed_spin.setValue(config.display.scroll_speed)
        self.speed_spin.setSuffix(" px/s")
        layout.addRow("滚动速度:", self.speed_spin)

        self.opacity_spin = QDoubleSpinBox()
        self.opacity_spin.setRange(0.1, 1.0)
        self.opacity_spin.setSingleStep(0.05)
        self.opacity_spin.setValue(config.display.opacity)
        layout.addRow("不透明度:", self.opacity_spin)

        self.tracks_spin = QSpinBox()
        self.tracks_spin.setRange(1, 20)
        self.tracks_spin.setValue(config.display.max_tracks)
        layout.addRow("弹幕轨道数:", self.tracks_spin)

        # 在线信息轮询间隔
        self.poll_interval_spin = QSpinBox()
        self.poll_interval_spin.setRange(5, 600)
        self.poll_interval_spin.setValue(config.poll_interval)
        self.poll_interval_spin.setSuffix(" 秒")
        layout.addRow("在线信息间隔:", self.poll_interval_spin)

        # 显示开关
        self.show_enter_cb = QCheckBox("显示用户进入通知")
        self.show_enter_cb.setChecked(config.display.show_enter)
        layout.addRow(self.show_enter_cb)

        self.show_gift_cb = QCheckBox("显示礼物通知")
        self.show_gift_cb.setChecked(config.display.show_gift)
        layout.addRow(self.show_gift_cb)

        self.show_sc_cb = QCheckBox("显示醒目留言(SC)")
        self.show_sc_cb.setChecked(config.display.show_sc)
        layout.addRow(self.show_sc_cb)

        self.show_online_cb = QCheckBox("定时显示人气值和高能榜")
        self.show_online_cb.setChecked(config.display.show_online)
        layout.addRow(self.show_online_cb)

        # 重置按钮
        btn_reset = QPushButton("重置为默认")
        btn_reset.setObjectName("plain")
        btn_reset.clicked.connect(self.reset_defaults)
        layout.addRow(btn_reset)

        # 按钮
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addRow(buttons)

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

    def reject(self):
        """取消：恢复打开时的显示配置快照"""
        self._config.display = DisplayConfig(**self._display_backup)
        super().reject()

    def accept(self):
        """保存配置并关闭"""
        rid = extract_room_id(self.room_id_edit.text())
        if rid is None:
            QMessageBox.warning(self, "错误", "请输入数字直播间ID或完整直播链接")
            return
        self._config.room_id = rid

        self._config.cookie.SESSDATA = self.sessdata_edit.text().strip()
        self._config.cookie.buvid3 = self.buvid3_edit.text().strip()
        self._config.cookie.bili_jct = self.bili_jct_edit.text().strip()

        self._config.display.font_size = self.font_size_spin.value()
        self._config.display.scroll_speed = self.speed_spin.value()
        self._config.display.opacity = self.opacity_spin.value()
        self._config.display.max_tracks = self.tracks_spin.value()
        self._config.display.show_enter = self.show_enter_cb.isChecked()
        self._config.display.show_gift = self.show_gift_cb.isChecked()
        self._config.display.show_sc = self.show_sc_cb.isChecked()
        self._config.display.show_online = self.show_online_cb.isChecked()
        self._config.poll_interval = self.poll_interval_spin.value()

        save_config(self._config)
        super().accept()
