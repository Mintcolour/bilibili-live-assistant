# -*- coding: utf-8 -*-
"""应用控制器 - 管理客户端生命周期与跨窗口信号"""
import asyncio
import logging

from config import load_config
from danmaku_client import DanmakuClient, DanmakuSignals
from live_info import LiveInfoFetcher, LiveInfoSignals
from overlay_window import OverlayWindow

logger = logging.getLogger("app_controller")


class AppController:
    """应用主控制器"""

    def __init__(self):
        self.config = load_config()
        self.danmaku_signals = DanmakuSignals()
        self.live_info_signals = LiveInfoSignals()
        self.danmaku_client = DanmakuClient(self.danmaku_signals)
        self.live_info = LiveInfoFetcher(self.live_info_signals)
        self.window = OverlayWindow(self.config)
        # 信号接线：弹幕事件 → 悬浮窗
        self.danmaku_signals.danmaku_received.connect(self.window.add_danmaku)
        self.danmaku_signals.connection_status.connect(self._on_status)
        self.live_info_signals.online_updated.connect(self.window.add_danmaku)
        self.live_info_signals.rank_updated.connect(self.window.add_danmaku)
        self.window.restart_requested.connect(self.request_restart)

    def _on_status(self, status: str):
        logger.info(f"Status: {status}")
        self.window.tray_icon.setToolTip(f"B站直播弹幕助手 - {status}")

    async def start(self) -> bool:
        """启动弹幕监听与在线轮询；房间号无效时返回 False"""
        room_id = self.config.room_id
        if room_id <= 0:
            self.danmaku_signals.connection_status.emit("请先填写直播间ID")
            return False
        await self.danmaku_client.start(room_id, self.config.cookie, self.config.display)
        if self.config.display.show_online:
            await self.live_info.start(room_id, self.config.cookie, self.config.poll_interval)
        self.window.show()
        logger.info(f"Started, room_id={room_id}")
        return True

    async def stop(self):
        await self.danmaku_client.stop()
        await self.live_info.stop()

    async def restart(self):
        await self.stop()
        await self.start()

    def request_restart(self):
        """同步入口：设置保存等场景触发重启连接"""
        asyncio.ensure_future(self.restart())

    def apply_display_config(self):
        """把当前 config.display 应用到悬浮窗（热生效）"""
        self.window.renderer.configure(self.config.display)
        self.window.setWindowOpacity(self.config.display.opacity)

    def set_overlay_visible(self, visible: bool):
        if visible:
            self.window.show()
        else:
            self.window.hide()

    async def cleanup(self):
        await self.stop()
