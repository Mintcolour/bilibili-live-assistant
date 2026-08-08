# -*- coding: utf-8 -*-
"""主控制窗口 - 状态、连接控制、功能开关、显示设置"""
import asyncio

from PyQt5.QtCore import QSignalBlocker, pyqtSignal
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

    rerun_wizard_requested = pyqtSignal()  # 请求重新运行向导

    def __init__(self, controller):
        super().__init__()
        self._controller = controller
        self.setWindowTitle("B站直播弹幕助手")
        self.setMinimumSize(520, 480)
        self._build_ui()
        self._wire_signals()

    # ---------- UI 构建 ----------
    def _build_ui(self):
        central = QWidget(self)
        root = QVBoxLayout(central)
        root.setContentsMargins(16, 16, 16, 16)
        root.setSpacing(12)

        # 标题栏
        header = QHBoxLayout()
        title = QLabel("B站直播弹幕助手")
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

        # 菜单栏：帮助 → 重新运行向导
        menu_help = self.menuBar().addMenu("帮助")
        action_rerun = menu_help.addAction("重新运行向导")
        action_rerun.triggered.connect(self.rerun_wizard_requested.emit)

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
        """设置变更后刷新主窗口控件（抑制信号，避免重复保存/应用）"""
        d = self._controller.config.display
        with QSignalBlocker(self.font_spin):
            self.font_spin.setValue(d.font_size)
        with QSignalBlocker(self.speed_spin):
            self.speed_spin.setValue(d.scroll_speed)
        with QSignalBlocker(self.opacity_spin):
            self.opacity_spin.setValue(d.opacity)
        with QSignalBlocker(self.switches["enter"]):
            self.switches["enter"].setChecked(d.show_enter)
        with QSignalBlocker(self.switches["gift"]):
            self.switches["gift"].setChecked(d.show_gift)
        with QSignalBlocker(self.switches["sc"]):
            self.switches["sc"].setChecked(d.show_sc)
        with QSignalBlocker(self.switches["online"]):
            self.switches["online"].setChecked(d.show_online)
