# 紧凑列表动画发布核对

日期：2026-10-17。分支：compact-list-animation。基线：7748704。

实现提交：2675aaf（桌面与设置模型）、f76eba7（OBS）、a058d2f（可视化设置、本地预览及衔接边界）。

- 目录：artifacts/releases/compact-list-animation，未覆盖原版。
- 主包：compact-list-animation-win-x64.zip，160,047,008 字节，953 个文件。
- 主包 SHA-256：369E26539F6403EDDB2A090D96BD49EF5CAE1BA4FAB18DD65EEAD85350A397F1。
- 扩展包：compact-list-animation-extension.zip，7 个文件，扩展与登录流程未修改。
- 本机设置继承 compact-overlay-startup-fix/settings.json，字节哈希相同。

已完成静态核对、node --check、PowerShell 语法分析、Release x64 Build／Publish。未启动 App、BrowserHost、OBS 或浏览器，未运行自动测试。

发布程序集与 ReadyToRun 产物相同；App、SQLite、BrowserHost 为 AMD64；PerMonitorV2、SQLite 8.0.32、BrowserHost Release 文件、图标及性能分析工具保留。Microsoft.UI.Xaml.dll 与 Microsoft.Internal.FrameworkUdk.dll 与 Windows App SDK 1.7.250401001 的 x64 MSIX 文件哈希一致。

通过 PE 元数据及资源区读取核对 OBS 的 HTML、JavaScript、CSS 均为内嵌资源，并逐字节哈希对照当前源码；未执行或加载应用程序集。主包与扩展包逐项对照发布文件哈希，排除用户设置、历史库、诊断日志、本机 OBS key 和临时文件。

复核路径覆盖：构造阶段窗口样式不读未赋值 ViewModel；新消息批量衔接；淡入保留原截止时间；同 ID 更新不重播入场；固定区改变列表高度；空列表第一条；翻阅与恢复直接定位；离场数量和估算像素预算；隐藏／换房／退出清理；预览取消与迟到上传释放。

可重复静态核对：scripts/verify-local-history-release.ps1 与 scripts/verify-compact-overlay-assets.ps1。证据保存在 artifacts/compact-list-animation-verification/：release-verification.json、supplementary-static-check.json、app.embedded.manifest。

实际动画观感、DPI、长消息效果及 CPU／内存表现尚未实测。人工验收见 compact-list-animation-delivery.md；不将编译通过视为性能目标达标。
