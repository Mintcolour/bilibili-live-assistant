# -*- coding: utf-8 -*-
"""
B站直播弹幕助手 - 主入口
使用 qasync 桥接 asyncio 和 PyQt5 事件循环
"""
import asyncio
import logging
import sys

from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import QApplication, QMessageBox, QInputDialog
from qasync import QEventLoop

from config import load_config, save_config, AppConfig
from danmaku_client import DanmakuClient, DanmakuSignals, DanmakuEvent
from live_info import LiveInfoFetcher, LiveInfoSignals
from overlay_window import OverlayWindow

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s %(levelname)s %(name)s] %(message)s",
)
logger = logging.getLogger("main")


class LiveAssistantApp:
    """应用主控制器"""

    def __init__(self):
        self.config: AppConfig = load_config()
        self._setup_signals()
        self._setup_clients()
        self._setup_window()

    def _setup_signals(self):
        """初始化 Qt 信号"""
        self.danmaku_signals = DanmakuSignals()
        self.live_info_signals = LiveInfoSignals()

    def _setup_clients(self):
        """初始化客户端"""
        self.danmaku_client = DanmakuClient(self.danmaku_signals)
        self.live_info = LiveInfoFetcher(self.live_info_signals)

    def _setup_window(self):
        """初始化窗口"""
        self.window = OverlayWindow(self.config)
        # 连接信号
        self.danmaku_signals.danmaku_received.connect(self._on_danmaku)
        self.danmaku_signals.connection_status.connect(self._on_status)
        self.live_info_signals.online_updated.connect(self._on_danmaku)
        self.live_info_signals.rank_updated.connect(self._on_danmaku)
        self.window.restart_requested.connect(self._on_restart)

    def _on_danmaku(self, event: DanmakuEvent):
        """收到弹幕事件"""
        self.window.add_danmaku(event)

    def _on_status(self, status: str):
        """连接状态变化"""
        logger.info(f"Status: {status}")
        self.window.tray_icon.setToolTip(f"B站直播弹幕助手 - {status}")

    def _on_restart(self):
        """重启连接（同步入口，创建异步任务）"""
        asyncio.ensure_future(self._restart_async())

    async def _restart_async(self):
        """重启连接"""
        await self._stop_all()
        await self._start_all()

    async def _start_all(self):
        """启动所有服务"""
        room_id = self.config.room_id
        if room_id <= 0:
            room_id = await self._ask_room_id()
            if room_id <= 0:
                return

        await self.danmaku_client.start(
            room_id, self.config.cookie, self.config.display
        )
        if self.config.display.show_online:
            await self.live_info.start(
                room_id, self.config.cookie, self.config.poll_interval
            )
        self.window.show()
        logger.info(f"Started, room_id={room_id}")

    async def _stop_all(self):
        """停止所有服务"""
        await self.danmaku_client.stop()
        await self.live_info.stop()

    async def _ask_room_id(self) -> int:
        """询问直播间ID"""
        text, ok = QInputDialog.getText(
            None, "B站直播弹幕助手",
            "请输入直播间ID:\n(从直播间URL获取，如 live.bilibili.com/12345 中的 12345)"
        )
        if ok and text.strip().isdigit():
            room_id = int(text.strip())
            self.config.room_id = room_id
            save_config(self.config)
            return room_id
        return 0

    async def run(self):
        """运行应用"""
        await self._start_all()

    async def cleanup(self):
        """清理"""
        await self._stop_all()


def main():
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)

    loop = QEventLoop(app)
    asyncio.set_event_loop(loop)

    assistant = LiveAssistantApp()

    async def _run():
        await assistant.run()

    async def _shutdown():
        await assistant.cleanup()

    # 注册退出时的清理
    app.aboutToQuit.connect(lambda: asyncio.ensure_future(_shutdown()))

    with loop:
        loop.create_task(_run())
        loop.run_forever()


if __name__ == "__main__":
    main()
