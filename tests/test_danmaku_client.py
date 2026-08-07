from danmaku_client import BiliDanmakuHandler, DanmakuSignals

from blivedm.clients.ws_base import AuthError, InitError


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
