# 高频气泡发布静态核对结果

- 分支：`hot-phrase-bubbles`；基线空提交 `fecf1ad`。
- 功能提交：`391ff1b`（三十秒统计、表情元数据、设置与布局），`6a53bde`（各端图文气泡、资源生命周期、动画与设置预览）。
- Release x64 编译与发布通过；`git diff --check`、三份 OBS JavaScript 语法检查通过。
- 发布目录：`artifacts/releases/hot-phrase-bubbles`。
- 继承 `hot-phrases-live-stop` 当前设置并核对字节一致，旧目录保留；新气泡设置使用默认值。
- AMD64、ReadyToRun、PerMonitorV2 DPI、SQLite 8.0.32 和 BrowserHost 发布文件匹配。
- 九项 OBS 内嵌资源与源码哈希一致；两个 WinUI 原生文件与 Windows App SDK 1.7.250401001 一致。
- 分享包 957 项、160,240,541 字节，逐项哈希核对通过；不含用户设置、历史库、日志、OBS 密钥及用户导入素材。
- 分享包 SHA256：`66AB6E91C29795B569CF3AAB042D2CD72EC8673F2E4168F81DF75F5748D0C4AD`。
- 核对报告：`artifacts/hot-phrase-bubbles-verification/release-verification.json` 与 `supplementary-static-check.json`。

静态核对了气泡数量上限、离场完成后的清除、无变化不重新测量、图片尺寸变化取消旧等待、共享表情动画调度、隐藏／切房／退出清理，以及 OBS 重连恢复路径。

本轮未启动 App、BrowserHost、浏览器或 OBS，未运行自动测试。实际图文显示、动画流畅度、DPI 和 CPU／内存走势仍需人工验收；未将编译或文件核对作为运行与性能结论。检查项目见 `hot-phrase-bubbles-delivery.md`。
