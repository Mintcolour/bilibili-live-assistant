# 发布静态核对结果

- 分支：`hot-phrases-live-stop`。
- Release x64 编译通过；`git diff --check` 和三份 OBS JavaScript 语法检查通过。
- 发布目录：`artifacts/releases/hot-phrases-live-stop`。
- 继承 `compact-rank-scroll` 当前设置，字节一致；旧目录保留。
- AMD64、ReadyToRun、PerMonitorV2 DPI、SQLite 8.0.32 和 BrowserHost 发布文件匹配。
- 九项 OBS 内嵌资源与源码哈希一致，两个 WinUI 原生文件与 SDK 匹配。
- 分享包共 957 项、160,171,840 字节，逐项哈希核对通过；不包含用户设置、历史库、日志、OBS 密钥与用户素材。
- 分享包 SHA256：`B88CE13B9D3385203C8BFE21C7DE830E24B9A622E89F1E8A17EE11388641104B`。
- 核对报告位于 `artifacts/hot-phrases-live-stop-verification`。

本轮未启动 App、BrowserHost、浏览器或 OBS，未运行自动测试。显示效果、下播自动停止、CPU 和内存仍需人工验收，不以编译通过代替运行验证。人工检查项目见 `hot-phrases-live-stop-delivery.md`。
