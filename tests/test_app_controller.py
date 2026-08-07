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
