# -*- coding: utf-8 -*-
"""
弹幕渲染引擎 - 轨道管理、防重叠、位图缓存绘制
"""
import time
from dataclasses import dataclass, field
from typing import List, Optional

from PyQt5.QtCore import Qt, QTimer, QRect, QSize
from PyQt5.QtGui import QPainter, QPixmap, QFont, QFontMetrics, QColor, QPen, QBrush, QPainterPath
from PyQt5.QtWidgets import QWidget

from danmaku_client import DanmakuEvent


@dataclass
class DanmakuItem:
    """单条弹幕"""
    event: DanmakuEvent
    text: str = ""
    pixmap: Optional[QPixmap] = None  # 位图缓存
    x: float = 0.0                    # 当前X坐标（从右往左）
    track: int = -1                   # 所在轨道
    width: int = 0                    # 弹幕宽度
    born_time: float = 0.0            # 创建时间

    def __post_init__(self):
        self.born_time = time.time()


class DanmakuTrack:
    """弹幕轨道"""
    def __init__(self, index: int, y: int, height: int):
        self.index = index
        self.y = y
        self.height = height
        self.items: List[DanmakuItem] = []

    def can_accept(self, new_width: int, screen_width: int, speed: float, margin: int = 20) -> bool:
        """判断是否可以接受新弹幕（避免重叠）

        新弹幕从 screen_width（屏幕右边缘）处进入，与轨道内弹幕同速左移、
        相对距离不变。要避免重叠，最右侧（最新进入）弹幕的右边缘必须已经
        进入屏幕并留出 margin 间距：right_edge + margin <= screen_width。
        """
        if not self.items:
            return True
        leader = max(self.items, key=lambda i: i.x)
        return leader.x + leader.width + margin <= screen_width


class DanmakuRenderer(QWidget):
    """弹幕渲染区域"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self.setAttribute(Qt.WA_TranslucentBackground, True)

        self._tracks: List[DanmakuTrack] = []
        self._queue: List[DanmakuEvent] = []  # 候补队列
        self._max_queue = 100

        # 配置
        self._font_size = 28
        self._scroll_speed = 120  # px/s
        self._max_tracks = 8
        self._track_height = 36

        # 动画定时器
        self._timer = QTimer(self)
        self._timer.setInterval(16)  # ~60fps
        self._timer.timeout.connect(self._tick)
        self._timer.start()

        self._font = QFont("Microsoft YaHei", self._font_size, QFont.Bold)

    def configure(self, display_config):
        """应用显示配置"""
        self._font_size = display_config.font_size
        self._scroll_speed = display_config.scroll_speed
        self._max_tracks = display_config.max_tracks
        self._font = QFont("Microsoft YaHei", self._font_size, QFont.Bold)
        # 轨道高度取配置值与实际渲染高度(字体高度+描边边距)的较大值，
        # 避免字体调大时相邻轨道上下重叠
        fm = QFontMetrics(self._font)
        self._track_height = max(display_config.track_height, fm.height() + 10)
        self._rebuild_tracks()

    def _rebuild_tracks(self):
        """重建轨道"""
        w = self.width()
        h = self.height()
        if w <= 0 or h <= 0:
            return
        self._tracks = []
        track_h = self._track_height
        usable_h = min(h, track_h * self._max_tracks)
        num_tracks = max(1, usable_h // track_h)
        for i in range(num_tracks):
            self._tracks.append(DanmakuTrack(i, i * track_h, track_h))

    def add_danmaku(self, event: DanmakuEvent):
        """添加弹幕事件"""
        if len(self._queue) >= self._max_queue:
            self._queue.pop(0)  # 丢弃最旧的
        self._queue.append(event)

    def _tick(self):
        """每帧更新"""
        self._process_queue()
        self._update_positions()
        self.update()

    def _process_queue(self):
        """处理候补队列"""
        if not self._queue or not self._tracks:
            return
        w = self.width()
        if w <= 0:
            return

        # 尝试将队列中的弹幕分配到轨道
        remaining = []
        for event in self._queue:
            text = self._format_text(event)
            fm = QFontMetrics(self._font)
            text_w = fm.horizontalAdvance(text) + 20  # 加上描边和边距

            placed = False
            for track in self._tracks:
                if track.can_accept(text_w, w, self._scroll_speed):
                    item = DanmakuItem(event=event, text=text)
                    item.width = text_w
                    item.x = w  # 从屏幕右侧外开始
                    item.track = track.index
                    item.pixmap = self._render_to_pixmap(item)
                    track.items.append(item)
                    placed = True
                    break

            if not placed:
                remaining.append(event)

        self._queue = remaining

    def _format_text(self, event: DanmakuEvent) -> str:
        """格式化显示文本"""
        if event.event_type == DanmakuEvent.TYPE_DANMAKU:
            return f"{event.username}: {event.content}"
        elif event.event_type == DanmakuEvent.TYPE_ENTER:
            return f"🎉 {event.username} {event.content}"
        elif event.event_type == DanmakuEvent.TYPE_GIFT:
            return f"🎁 {event.username} {event.content}"
        elif event.event_type == DanmakuEvent.TYPE_SC:
            return f"💰 {event.username}: {event.content}"
        elif event.event_type == DanmakuEvent.TYPE_GUARD:
            return f"⚓ {event.username} {event.content}"
        elif event.event_type == DanmakuEvent.TYPE_ONLINE:
            return f"📊 {event.content}"
        return f"{event.username}: {event.content}"

    def _render_to_pixmap(self, item: DanmakuItem) -> QPixmap:
        """将弹幕渲染为位图缓存"""
        fm = QFontMetrics(self._font)
        text = item.text
        text_w = fm.horizontalAdvance(text) + 20
        text_h = fm.height() + 10

        pixmap = QPixmap(text_w, text_h)
        pixmap.fill(Qt.transparent)

        painter = QPainter(pixmap)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.setFont(self._font)

        # 将文字转为路径：Qt 的 drawText 以画笔颜色绘制字形、不响应画刷填充，
        # 也无法通过笔宽形成描边，因此用 drawPath 做"深色描边 + 彩色填充"两遍画法
        path = QPainterPath()
        path.addText(0, fm.ascent(), self._font, text)
        br = path.boundingRect()
        painter.translate(
            (text_w - br.width()) / 2 - br.x(),
            (text_h - br.height()) / 2 - br.y(),
        )

        # 第一遍：深色粗描边（笔与刷都用深色，形成轮廓）
        painter.setPen(QPen(QColor(0, 0, 0, 180), 4))
        painter.setBrush(QColor(0, 0, 0, 180))
        painter.drawPath(path)

        # 第二遍：彩色填充，覆盖中心区域，仅留深色描边
        color = QColor(item.event.color)
        painter.setPen(Qt.NoPen)
        painter.setBrush(color)
        painter.drawPath(path)

        painter.end()
        return pixmap

    def _update_positions(self):
        """更新弹幕位置"""
        dt = 0.016  # 约60fps
        dx = self._scroll_speed * dt
        w = self.width()

        for track in self._tracks:
            # 更新位置并移除已离开屏幕的弹幕
            track.items = [
                item for item in track.items
                if item.x + item.width > -50  # 留一点余量
            ]
            for item in track.items:
                item.x -= dx

    def paintEvent(self, event):
        """绘制所有弹幕"""
        if not self._tracks:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.SmoothPixmapTransform)
        for track in self._tracks:
            for item in track.items:
                if item.pixmap:
                    painter.drawPixmap(
                        int(item.x), track.y + (track.height - item.pixmap.height()) // 2,
                        item.pixmap
                    )
        painter.end()

    def resizeEvent(self, event):
        """窗口大小变化时重建轨道"""
        self._rebuild_tracks()
        super().resizeEvent(event)

    def clear_all(self):
        """清空所有弹幕"""
        self._queue.clear()
        for track in self._tracks:
            track.items.clear()
        self.update()
