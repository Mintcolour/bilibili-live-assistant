# 紧凑浮窗启动修复发布核对

日期：2026-10-17。分支：compact-overlay-obs。修复提交：f9b7be8。

- 发布目录：artifacts/releases/compact-overlay-startup-fix。
- 主包：compact-overlay-startup-fix-win-x64.zip，160,022,784 字节，953 个文件。
- SHA-256：FDBD47EDBB41C188F37D88777B74F860D1B31D66DAF48FEF2C125E9360F7422B。
- 扩展包：compact-overlay-startup-fix-extension.zip，7 个文件，扩展及登录逻辑未修改。
- compact-overlay-obs/settings.json 已复制到本机新目录，哈希一致；原目录未覆盖。

已完成：git diff --check、OBS JavaScript 语法检查、打包脚本 PowerShell 语法检查、Release x64 Build／Publish。

发布程序集与 ReadyToRun 产物哈希一致；App、SQLite、BrowserHost 为 AMD64，PerMonitorV2、SQLite 8.0.32、图标及性能分析工具保留。两份 WinUI 原生 DLL 与 Windows App SDK 1.7.250401001 的 x64 MSIX 文件哈希一致。通过 PE 元数据读取确认 OBS 三个网页资源仍内嵌，未加载执行应用程序集。

ZIP 逐项核对文件哈希；包中无个人设置、数据库、诊断日志、本机 OBS key 或临时文件。主包同时包含修复说明与原紧凑浮窗／OBS 使用说明。

静态证据位于 artifacts/compact-overlay-startup-fix-verification/，包括 release-verification.json、supplementary-static-check.json、app.embedded.manifest。

未启动 App、BrowserHost、OBS，未运行自动测试。普通／紧凑切换、开关、交互、图片显示与 CPU／内存表现仍待用户手动验收。此次发布不声称已经通过实际运行验证。
