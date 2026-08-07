# -*- coding: utf-8 -*-
"""
桌面悬浮窗口 - 无边框置顶半透明弹幕窗口 + 系统托盘 + 设置对话框
"""
from PyQt5.QtCore import Qt, pyqtSignal, QPoint
from PyQt5.QtGui import QColor, QPainter, QIcon, QPixmap
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QSystemTrayIcon,
    QMenu, QAction, QDialog, QApplication,
)

from config import AppConfig
from danmaku_renderer import DanmakuRenderer
from danmaku_client import DanmakuEvent
from settings_dialog import SettingsDialog


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


class OverlayWindow(QWidget):
    """桌面弹幕悬浮窗口"""

    restart_requested = pyqtSignal()  # 请求重启连接
    open_main_window_requested = pyqtSignal()  # 请求打开主窗口

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

        action_main = QAction("🏠 打开主窗口", self)
        action_main.triggered.connect(self.open_main_window_requested.emit)
        menu.addAction(action_main)

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
