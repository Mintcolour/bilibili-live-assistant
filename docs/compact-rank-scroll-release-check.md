# 顶部横排与榜单滚动发布核对

- 分支 `compact-rank-scroll`，基线 `ebab5e5`，功能提交 `8716e3f`。
- Visual Studio MSBuild Release x64 编译与独立发布成功；未启动应用或 BrowserHost。
- Git 差异检查和三份 OBS JavaScript 语法检查通过。
- 设置与 `compact-status-gift-fix/settings.json` 字节一致。
- AMD64、PerMonitorV2、ReadyToRun、SQLite 8.0.32、BrowserHost Release 产物及两份 WinUI 原生 SDK 文件核对通过。
- 9 个 OBS 内嵌资源与源码一致，ZIP 逐项哈希核对通过；主包 957 项，扩展包 7 项。
- 主包大小 160,148,288 字节；SHA256：`892500A1FAC2C315EC110E24E60AC12885A9F8B84970FCB651E003FEB994E4BC`。
- 保留 OBS CSS 定制文档与三份示例，分享包不包含用户设置、数据库、诊断日志、OBS 密钥或导入素材。

证据位于 `artifacts/compact-rank-scroll-verification`。未启动桌面窗口、浏览器或 OBS，未运行自动测试；实际滚动效果、CPU 和内存需要人工验证。
