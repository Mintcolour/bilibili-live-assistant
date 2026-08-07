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
