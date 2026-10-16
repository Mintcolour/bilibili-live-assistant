# live-likes发布核对

2026-10-16，分支live-likes，功能提交b1ea18a、6862883。发布目录artifacts/releases/live-likes，分享主包live-likes-win-x64.zip。

- Release x64编译发布完成，App／Core／Infrastructure与ReadyToRun产物一致。
- 主程序、SQLite和BrowserHost为AMD64，SQLite为8.0.32，内嵌manifest保留PerMonitorV2。
- 两份WinUI原生DLL与已核对的SDK 1.7.250401001哈希一致；BrowserHost匹配Release产物。
- 本机继承interaction-trade-filters设置，复制后字节一致，未读取内容。
- 主包952项、扩展包7项逐一匹配发布文件哈希，排除设置、历史库、日志、调试及临时文件；分析工具保留。
- 主程序SHA256：1C9A7891988D898D617F1640F77667DEBAFF1DBDFDBC6574B6A03CF059D5E28A。

执行scripts/verify-local-history-release.ps1 -ReleaseName live-likes -InheritSettingsFrom interaction-trade-filters，结果保存在artifacts/live-likes-verification/release-verification.json。

本轮未启动软件、BrowserHost、自动测试或真实直播模拟。点赞真实显示、上游推送频率及性能需要用户手动验收；编译与文件核对不代表运行验收通过。
