# interaction-trade-filters发布核对

2026-10-16，分支interaction-trade-filters，功能与交付说明提交61a008f。发布目录artifacts/releases/interaction-trade-filters，分享包interaction-trade-filters-win-x64.zip。

- Release x64发布完成；App、Core、Infrastructure与ReadyToRun产物哈希一致。
- 主程序、SQLite原生库和BrowserHost为AMD64，SQLite为8.0.32，内嵌DPI为PerMonitorV2。
- BrowserHost与Release产物一致；只编译核对，未启动。
- 两份WinUI原生DLL与最终隔离构建的1.7.250401001 SDK文件哈希一致。
- runtime-resource-budget-v12/settings.json与本机发布目录中的继承文件字节一致；未查看设置内容。
- 分享主包952个文件、扩展包7个文件，所有ZIP条目均与发布文件哈希一致；无设置、数据库、诊断日志或调试文件。
- 原性能分析脚本保留。主程序SHA256：495E61881A0F87A8A6956E6CFA32523D5DDAB0A99DA7DE711CD21DFD2C530E64。

核对命令：scripts/verify-local-history-release.ps1 -ReleaseName interaction-trade-filters -InheritSettingsFrom runtime-resource-budget-v12。机器报告保存在artifacts/interaction-trade-filters-verification/release-verification.json。该报告的未启动／未运行测试字段仅描述发布核对过程；此前已执行的隔离TradeProbe及42062真实直播记录详见交付说明。

发布核对没有启动正式App或BrowserHost，不能代替用户实际运行验收。42062实测私有工作集峰值185.32 MiB，160 MiB目标仍未达成；本版不声称性能已达标。
