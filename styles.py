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
