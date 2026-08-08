# -*- coding: utf-8 -*-
"""
B站直播弹幕助手 - 主入口
首次运行 → 三步引导向导；之后 → 主控制窗口。使用 qasync 桥接 asyncio 和 PyQt5 事件循环。
"""
import asyncio
import logging
import sys

from PyQt5.QtGui import QIcon
from PyQt5.QtWidgets import QApplication, QDialog
from qasync import QEventLoop

from app_controller import AppController
from config import needs_onboarding, save_config
from main_window import MainWindow
from utils import resource_path
from wizard import FirstRunWizard
import styles

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s %(levelname)s %(name)s] %(message)s",
)
logger = logging.getLogger("main")


def main():
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    styles.apply(app)
    # 应用图标（Icons8 下载，所有窗口继承）
    app.setWindowIcon(QIcon(resource_path("assets/icons/app-64.png")))

    loop = QEventLoop(app)
    asyncio.set_event_loop(loop)

    controller = AppController()
    main_window = MainWindow(controller)
    # 托盘"打开主窗口" → 显示主窗口
    controller.window.open_main_window_requested.connect(main_window.show_and_raise)

    def _rerun_wizard():
        wizard = FirstRunWizard(controller.config)
        if wizard.exec_() == QDialog.Accepted:
            save_config(controller.config)
            main_window.refresh_from_config()
            controller.request_restart()

    main_window.rerun_wizard_requested.connect(_rerun_wizard)

    async def _run():
        if needs_onboarding(controller.config):
            wizard = FirstRunWizard(controller.config)
            accepted = wizard.exec_() == QDialog.Accepted
            if accepted:
                save_config(controller.config)
            if not accepted and controller.config.room_id <= 0:
                # 向导被关闭且没有房间号：进主窗口但不自动连接
                main_window.show()
                return
        main_window.show()
        ok = await controller.start()
        if ok:
            main_window.set_connected(True)

    async def _shutdown():
        await controller.cleanup()

    app.aboutToQuit.connect(lambda: asyncio.ensure_future(_shutdown()))

    with loop:
        loop.create_task(_run())
        loop.run_forever()


if __name__ == "__main__":
    main()
