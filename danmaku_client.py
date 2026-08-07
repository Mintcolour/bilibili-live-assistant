# -*- coding: utf-8 -*-
"""
弹幕客户端模块 - 封装 blivedm WebSocket 客户端
通过 Qt 信号将弹幕事件传递给 UI 层
"""
import asyncio
import http.cookies
import logging
from typing import Optional, Callable

import aiohttp
from PyQt5.QtCore import QObject, pyqtSignal

import sys
import os
# 将 blivedm_lib 加入搜索路径
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "blivedm_lib"))

import blivedm
import blivedm.models.web as web_models

logger = logging.getLogger("danmaku_client")


class DanmakuEvent:
    """统一弹幕事件数据结构"""
    # 类型常量
    TYPE_DANMAKU = "danmaku"
    TYPE_ENTER = "enter"
    TYPE_GIFT = "gift"
    TYPE_SC = "sc"
    TYPE_ONLINE = "online"
    TYPE_GUARD = "guard"

    def __init__(self, event_type: str, username: str, content: str,
                 color: str = "#FFFFFF", extra: dict = None):
        self.event_type = event_type
        self.username = username
        self.content = content
        self.color = color
        self.extra = extra or {}

    def __repr__(self):
        return f"DanmakuEvent({self.event_type}, {self.username}: {self.content})"


class DanmakuSignals(QObject):
    """Qt 信号桥接 - 将 async 回调转为 Qt 信号"""
    danmaku_received = pyqtSignal(object)  # DanmakuEvent
    connection_status = pyqtSignal(str)    # 连接状态信息


class BiliDanmakuHandler(blivedm.BaseHandler):
    """blivedm 事件处理器，转发到 Qt 信号"""

    def __init__(self, signals: DanmakuSignals, display_config):
        super().__init__()
        self._signals = signals
        self._cfg = display_config

    def _on_danmaku(self, client: blivedm.BLiveClient, message: web_models.DanmakuMessage):
        """普通弹幕"""
        evt = DanmakuEvent(
            event_type=DanmakuEvent.TYPE_DANMAKU,
            username=message.uname,
            content=message.msg,
            color=self._cfg.danmaku_color,
            extra={"uid": message.uid, "medal": message.medal_name, "medal_level": message.medal_level},
        )
        self._signals.danmaku_received.emit(evt)

    def _on_gift(self, client: blivedm.BLiveClient, message: web_models.GiftMessage):
        """礼物"""
        if not self._cfg.show_gift:
            return
        price_str = ""
        if message.coin_type == "gold":
            price_str = f" (¥{message.total_coin / 1000:.1f})"
        evt = DanmakuEvent(
            event_type=DanmakuEvent.TYPE_GIFT,
            username=message.uname,
            content=f"赠送 {message.gift_name}x{message.num}{price_str}",
            color=self._cfg.gift_color,
        )
        self._signals.danmaku_received.emit(evt)

    def _on_super_chat(self, client: blivedm.BLiveClient, message: web_models.SuperChatMessage):
        """醒目留言 SC"""
        if not self._cfg.show_sc:
            return
        evt = DanmakuEvent(
            event_type=DanmakuEvent.TYPE_SC,
            username=message.uname,
            content=f"¥{message.price} {message.message}",
            color=self._cfg.sc_color,
        )
        self._signals.danmaku_received.emit(evt)

    def _on_interact_word_v2(self, client: blivedm.BLiveClient, message: web_models.InteractWordV2Message):
        """用户进入/关注/分享"""
        if not self._cfg.show_enter:
            return
        action_map = {
            1: "进入了直播间",
            2: "关注了主播",
            3: "分享了直播间",
            4: "特别关注了主播",
            5: "互粉了",
            6: "为主播点赞了",
        }
        action = action_map.get(message.msg_type, "互动了")
        evt = DanmakuEvent(
            event_type=DanmakuEvent.TYPE_ENTER,
            username=message.username,
            content=action,
            color=self._cfg.enter_color,
        )
        self._signals.danmaku_received.emit(evt)

    def _on_user_toast_v2(self, client: blivedm.BLiveClient, message: web_models.UserToastV2Message):
        """上舰通知"""
        if message.source == 2:
            return  # 跳过赠送的上舰
        guard_names = {1: "总督", 2: "提督", 3: "舰长"}
        guard_name = guard_names.get(message.guard_level, "大航海")
        evt = DanmakuEvent(
            event_type=DanmakuEvent.TYPE_GUARD,
            username=message.username,
            content=f"开通了{guard_name}！",
            color=self._cfg.gift_color,
        )
        self._signals.danmaku_received.emit(evt)


class DanmakuClient:
    """弹幕客户端管理器"""

    def __init__(self, signals: DanmakuSignals):
        self._signals = signals
        self._client: Optional[blivedm.BLiveClient] = None
        self._session: Optional[aiohttp.ClientSession] = None
        self._handler: Optional[BiliDanmakuHandler] = None
        self._running = False

    async def start(self, room_id: int, cookie_config, display_config):
        """启动弹幕监听"""
        if self._running:
            logger.warning("DanmakuClient already running")
            return

        # 创建带 Cookie 的 session
        cookies = http.cookies.SimpleCookie()
        if cookie_config.SESSDATA:
            cookies["SESSDATA"] = cookie_config.SESSDATA
            cookies["SESSDATA"]["domain"] = "bilibili.com"
        if cookie_config.buvid3:
            cookies["buvid3"] = cookie_config.buvid3
            cookies["buvid3"]["domain"] = "bilibili.com"
        if cookie_config.bili_jct:
            cookies["bili_jct"] = cookie_config.bili_jct
            cookies["bili_jct"]["domain"] = "bilibili.com"

        self._session = aiohttp.ClientSession()
        self._session.cookie_jar.update_cookies(cookies)

        self._handler = BiliDanmakuHandler(self._signals, display_config)
        self._client = blivedm.BLiveClient(room_id, session=self._session)
        self._client.set_handler(self._handler)

        self._running = True
        self._signals.connection_status.emit(f"正在连接直播间 {room_id} ...")
        self._client.start()
        logger.info(f"DanmakuClient started for room {room_id}")

    async def stop(self):
        """停止弹幕监听"""
        if not self._running:
            return
        self._running = False
        if self._client:
            try:
                await self._client.stop_and_close()
            except Exception as e:
                logger.error(f"Error stopping client: {e}")
        if self._session:
            await self._session.close()
        self._signals.connection_status.emit("已断开连接")
        logger.info("DanmakuClient stopped")

    @property
    def is_running(self) -> bool:
        return self._running
