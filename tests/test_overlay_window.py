from overlay_window import OverlayWindow
from config import AppConfig


def test_open_main_window_signal_exists(qapp):
    win = OverlayWindow(AppConfig())
    assert hasattr(win, "open_main_window_requested")
