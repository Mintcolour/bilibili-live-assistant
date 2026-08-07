# -*- coding: utf-8 -*-
"""
在线信息模块 - 定时轮询B站API获取人气值和高能榜
使用 WBI 签名访问 getInfoByRoom 接口获取与网页端一致的人气值
"""
import asyncio
import datetime
import hashlib
import http.cookies
import logging
import urllib.parse
from typing import Optional

import aiohttp
from PyQt5.QtCore import QObject, pyqtSignal

from danmaku_client import DanmakuEvent

logger = logging.getLogger("live_info")

ROOM_INFO_URL = "https://api.live.bilibili.com/room/v1/Room/get_info"
ROOM_INFO_V2_URL = "https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom"
GOLD_RANK_URL = "https://api.live.bilibili.com/xlive/general-interface/v1/rank/getOnlineGoldRank"
WBI_INIT_URL = "https://api.bilibili.com/x/web-interface/nav"
USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

# WBI 密码表（与 blivedm / B站前端一致）
_WBI_KEY_INDEX_TABLE = [
    46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
    27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
]


class WbiSigner:
    """WBI 签名器（参照 blivedm 实现）"""

    def __init__(self, session: aiohttp.ClientSession):
        self._session = session
        self._wbi_key = ""
        self._last_refresh: Optional[datetime.datetime] = None
        self._lock = asyncio.Lock()

    async def _refresh_key(self):
        async with self._lock:
            if self._wbi_key and self._last_refresh and (
                datetime.datetime.now() - self._last_refresh < datetime.timedelta(hours=11)
            ):
                return
            try:
                async with self._session.get(
                    WBI_INIT_URL, headers={"User-Agent": USER_AGENT},
                    timeout=aiohttp.ClientTimeout(total=10),
                ) as resp:
                    data = await resp.json()
                wbi_img = data["data"]["wbi_img"]
                img_key = wbi_img["img_url"].rpartition("/")[2].partition(".")[0]
                sub_key = wbi_img["sub_url"].rpartition("/")[2].partition(".")[0]
                shuffled = img_key + sub_key
                self._wbi_key = "".join(
                    shuffled[i] for i in _WBI_KEY_INDEX_TABLE if i < len(shuffled)
                )
                self._last_refresh = datetime.datetime.now()
            except Exception as e:
                logger.error(f"WBI key refresh failed: {e}")

    async def sign(self, params: dict) -> dict:
        """对参数进行 WBI 签名"""
        await self._refresh_key()
        if not self._wbi_key:
            return params
        wts = str(int(datetime.datetime.now().timestamp()))
        to_sign = {**params, "wts": wts}
        to_sign = {k: to_sign[k] for k in sorted(to_sign.keys())}
        for k, v in to_sign.items():
            to_sign[k] = "".join(ch for ch in str(v) if ch not in "!'()*")
        w_rid = hashlib.md5(
            (urllib.parse.urlencode(to_sign) + self._wbi_key).encode("utf-8")
        ).hexdigest()
        return {**params, "wts": wts, "w_rid": w_rid}


class LiveInfoSignals(QObject):
    """在线信息 Qt 信号"""
    online_updated = pyqtSignal(object)   # DanmakuEvent (TYPE_ONLINE)
    rank_updated = pyqtSignal(object)      # DanmakuEvent (TYPE_ONLINE)


class LiveInfoFetcher:
    """在线信息获取器"""

    def __init__(self, signals: LiveInfoSignals):
        self._signals = signals
        self._session: Optional[aiohttp.ClientSession] = None
        self._signer: Optional[WbiSigner] = None
        self._task: Optional[asyncio.Task] = None
        self._running = False
        self._last_online: int = -1
        self._poll_interval: int = 30

    async def start(self, room_id: int, cookie_config, poll_interval: int = 30):
        """启动定时轮询"""
        if self._running:
            return
        self._running = True
        self._poll_interval = poll_interval
        self._last_online = -1

        self._session = aiohttp.ClientSession(
            headers={
                "User-Agent": USER_AGENT,
                "Referer": "https://live.bilibili.com/",
            }
        )
        self._signer = WbiSigner(self._session)
        # 设置 Cookie
        if cookie_config.SESSDATA:
            cookies = http.cookies.SimpleCookie()
            cookies["SESSDATA"] = cookie_config.SESSDATA
            cookies["SESSDATA"]["domain"] = "bilibili.com"
            self._session.cookie_jar.update_cookies(cookies)

        self._task = asyncio.create_task(self._poll_loop(room_id))
        logger.info(f"LiveInfoFetcher started, interval={poll_interval}s")

    async def stop(self):
        """停止轮询"""
        self._running = False
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        if self._session:
            await self._session.close()
        logger.info("LiveInfoFetcher stopped")

    async def _poll_loop(self, room_id: int):
        """轮询循环"""
        while self._running:
            try:
                await self._fetch_online(room_id)
                await self._fetch_gold_rank(room_id)
            except Exception as e:
                logger.error(f"Poll error: {e}")
            await asyncio.sleep(self._poll_interval)

    async def _fetch_online(self, room_id: int):
        """获取在线人数（网页端真实显示值），每次轮询都发送弹幕"""
        try:
            # 优先使用 getInfoByRoom（WBI签名），与网页端显示一致
            signed = await self._signer.sign({"room_id": room_id})
            async with self._session.get(
                ROOM_INFO_V2_URL,
                params=signed,
                timeout=aiohttp.ClientTimeout(total=10),
            ) as resp:
                if resp.status != 200:
                    logger.warning(f"getInfoByRoom API status={resp.status}")
                    return
                data = await resp.json()
                if data.get("code") != 0:
                    logger.warning(f"getInfoByRoom API error: {data.get('message')}")
                    # 降级到旧接口
                    await self._fetch_online_fallback(room_id)
                    return

                room_info = data["data"].get("room_info", {})
                watched = data["data"].get("watched_show", {})
                online = room_info.get("online", 0)
                watched_text = watched.get("text_large", "")

                self._last_online = online
                # B站已不再提供实时在线人数，room_info.online 为网页端显示的人气值
                content = f"人气: {online}"
                if watched_text:
                    content += f" · {watched_text}"
                evt = DanmakuEvent(
                    event_type=DanmakuEvent.TYPE_ONLINE,
                    username="系统",
                    content=content,
                    color="#2196F3",
                )
                self._signals.online_updated.emit(evt)
        except asyncio.TimeoutError:
            logger.warning("getInfoByRoom API timeout")
        except Exception as e:
            logger.error(f"Fetch online error: {e}")

    async def _fetch_online_fallback(self, room_id: int):
        """降级方案：使用 get_info 接口（online 为热度模糊值，仅供参考）"""
        try:
            async with self._session.get(
                ROOM_INFO_URL,
                params={"room_id": room_id},
                timeout=aiohttp.ClientTimeout(total=10),
            ) as resp:
                data = await resp.json()
                if data.get("code") != 0:
                    return
                online = data["data"].get("online", 0)
                self._last_online = online
                evt = DanmakuEvent(
                    event_type=DanmakuEvent.TYPE_ONLINE,
                    username="系统",
                    content=f"人气: {online}",
                    color="#2196F3",
                )
                self._signals.online_updated.emit(evt)
        except Exception as e:
            logger.error(f"Fallback fetch error: {e}")

    async def _fetch_gold_rank(self, room_id: int):
        """获取高能榜"""
        try:
            # 先获取主播UID
            async with self._session.get(
                ROOM_INFO_URL,
                params={"room_id": room_id},
                timeout=aiohttp.ClientTimeout(total=10),
            ) as resp:
                if resp.status != 200:
                    return
                data = await resp.json()
                if data.get("code") != 0:
                    return
                ruid = data["data"].get("uid", 0)

            if not ruid:
                return

            async with self._session.get(
                GOLD_RANK_URL,
                params={"ruid": ruid, "roomId": room_id, "page": 1, "pageSize": 10},
                timeout=aiohttp.ClientTimeout(total=10),
            ) as resp:
                if resp.status != 200:
                    return
                data = await resp.json()
                if data.get("code") != 0:
                    return

                rank_data = data.get("data", {})
                online_num = rank_data.get("onlineNum", 0)
                items = rank_data.get("OnlineRankItem", [])

                if items:
                    top3 = items[:3]
                    names = [f"{item.get('userRank', '?')}.{item.get('name', '?')}" for item in top3]
                    # onlineNum 为含匿名/隐藏用户在榜总数，接口只返回可见用户
                    if online_num > len(items):
                        content = f"高能榜(共{online_num}人在榜): {' | '.join(names)}"
                    else:
                        content = f"高能榜: {' | '.join(names)}"
                    evt = DanmakuEvent(
                        event_type=DanmakuEvent.TYPE_ONLINE,
                        username="系统",
                        content=content,
                        color="#9C27B0",
                    )
                    self._signals.rank_updated.emit(evt)
        except asyncio.TimeoutError:
            logger.warning("Gold rank API timeout")
        except Exception as e:
            logger.error(f"Fetch gold rank error: {e}")

    @property
    def is_running(self) -> bool:
        return self._running
