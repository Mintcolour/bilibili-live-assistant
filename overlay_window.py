# -*- coding: utf-8 -*-
"""
桌面悬浮窗口 - 无边框置顶半透明弹幕窗口 + 系统托盘 + 设置对话框
"""
from PyQt5.QtCore import Qt, pyqtSignal, QPoint
from PyQt5.QtGui import QColor, QPainter, QIcon, QPixmap
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QSystemTrayIcon,
    QMenu, QAction, QDialog, QFormLayout, QLineEdit, QSpinBox,
    QDoubleSpinBox, QCheckBox, QDialogButtonBox, QColorDialog,
    QPushButton, QApplication, QMessageBox,
)

from config import AppConfig, save_config
from danmaku_renderer import DanmakuRenderer
from danmaku_client import DanmakuEvent


def _make_tray_icon() -> QIcon:
    """生成系统托盘图标"""
    pixmap = QPixmap(32, 32)
    pixmap.fill(Qt.transparent)
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.Antialiasing)
    painter.setBrush(QColor("#00A1D6"))  # B站蓝
    painter.setPen(Qt.NoPen)
    painter.drawRoundedRect(2, 2, 28, 28, 6, 6)
    painter.setPen(QColor("white"))
    painter.drawText(pixmap.rect(), Qt.AlignCenter, "弹")
    painter.end()
    return QIcon(pixmap)


class SettingsDialog(QDialog):
    """设置对话框"""

    def __init__(self, config: AppConfig, parent=None):
        super().__init__(parent)
        self.setWindowTitle("弹幕设置")
        self.setMinimumWidth(400)
        self._config = config

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

        # 按钮
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addRow(buttons)

    def accept(self):
        """保存配置并关闭"""
        try:
            self._config.room_id = int(self.room_id_edit.text().strip())
        except ValueError:
            QMessageBox.warning(self, "错误", "直播间ID必须是数字")
            return

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


class OverlayWindow(QWidget):
    """桌面弹幕悬浮窗口"""

    restart_requested = pyqtSignal()  # 请求重启连接

    def __init__(self, config: AppConfig):
        super().__init__()
        self._config = config
        self._locked = False  # 是否锁定(鼠标穿透)
        self._drag_pos = QPoint()

        self._setup_window()
        self._setup_ui()
        self._setup_tray()

    def _setup_window(self):
        """设置窗口属性"""
        screen = QApplication.primaryScreen().geometry()
        win_h = int(screen.height() * self._config.display.window_height_ratio)
        win_w = screen.width()

        self.setGeometry(0, 0, win_w, win_h)
        self.setWindowFlags(
            Qt.FramelessWindowHint |
            Qt.WindowStaysOnTopHint |
            Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setWindowOpacity(self._config.display.opacity)

    def _setup_ui(self):
        """设置UI布局"""
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        self.renderer = DanmakuRenderer(self)
        self.renderer.configure(self._config.display)
        layout.addWidget(self.renderer)

    def _setup_tray(self):
        """设置系统托盘"""
        self.tray_icon = QSystemTrayIcon(self)
        self.tray_icon.setIcon(_make_tray_icon())
        self.tray_icon.setToolTip("B站直播弹幕助手")

        menu = QMenu()

        self.action_lock = QAction("🔒 锁定并穿透", self)
        self.action_lock.triggered.connect(self._toggle_lock)
        menu.addAction(self.action_lock)

        self.action_settings = QAction("⚙️ 弹幕配置", self)
        self.action_settings.triggered.connect(self._show_settings)
        menu.addAction(self.action_settings)

        menu.addSeparator()

        action_quit = QAction("❌ 退出", self)
        action_quit.triggered.connect(QApplication.quit)
        menu.addAction(action_quit)

        self.tray_icon.setContextMenu(menu)
        self.tray_icon.show()

    def _toggle_lock(self):
        """切换锁定/穿透模式"""
        self._locked = not self._locked
        if self._locked:
            self.setWindowFlags(
                Qt.FramelessWindowHint |
                Qt.WindowStaysOnTopHint |
                Qt.Tool |
                Qt.WindowTransparentForInput
            )
            self.action_lock.setText("🔓 解锁编辑")
        else:
            self.setWindowFlags(
                Qt.FramelessWindowHint |
                Qt.WindowStaysOnTopHint |
                Qt.Tool
            )
            self.action_lock.setText("🔒 锁定并穿透")
        self.show()

    def _show_settings(self):
        """显示设置对话框"""
        dialog = SettingsDialog(self._config, self)
        if dialog.exec_() == QDialog.Accepted:
            # 应用新配置
            self.renderer.configure(self._config.display)
            self.setWindowOpacity(self._config.display.opacity)
            # 请求重启连接
            self.restart_requested.emit()

    def add_danmaku(self, event: DanmakuEvent):
        """添加弹幕"""
        self.renderer.add_danmaku(event)

    def mousePressEvent(self, event):
        """鼠标拖动窗口"""
        if not self._locked and event.button() == Qt.LeftButton:
            self._drag_pos = event.globalPos() - self.frameGeometry().topLeft()
            event.accept()

    def mouseMoveEvent(self, event):
        """鼠标拖动移动"""
        if not self._locked and event.buttons() & Qt.LeftButton:
            self.move(event.globalPos() - self._drag_pos)
            event.accept()

    def closeEvent(self, event):
        """关闭时隐藏到托盘"""
        event.ignore()
        self.hide()
