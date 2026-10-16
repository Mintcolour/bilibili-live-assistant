# 浮窗顺序修复版发布核对

日期：2026-10-17。分支：room-queue-performance。交付说明提交：c1023d2；产品实现提交：8162b3b。先保存基线472ae21，再分批提交队列、绘制、针对性诊断与说明。

发布目录：artifacts/releases/room-queue-performance。

分享包：artifacts/releases/room-queue-performance-win-x64.zip，159,843,547字节。

SHA-256：41ED695D84A031CB48E2981E8B6FB988E5B0C73DD10168F6D461FF111AAA6DB8。

## 已核对

- Release x64发布成功，3个应用程序集与ReadyToRun目录一致，BrowserHost与对应Release产物一致。
- 应用、SQLite及BrowserHost为AMD64，内嵌PerMonitorV2保留，SQLite为8.0.32。
- 两份WinUI原生模块与实测构建的Windows App SDK 1.7.250401001文件哈希一致。
- 本地发布目录继承live-likes/settings.json，仅复制及比较哈希，字节一致。
- 主分享ZIP的952个文件和扩展ZIP的7个文件逐项与发布目录比较哈希一致；不含设置、历史库、待补缺口文件、诊断日志或临时调试数据。
- 22项编号事件及裁剪像素差异检查完成；最终版22748536匿名收尾监视120秒，概览与浮窗持续可见，生成completed.json并退出，没有残留StressApp进程。
- 未启动正式软件、BrowserHost或无关自动测试。通用发布核对中的ranAutomatedTests=false指没有运行常规测试套件，不覆盖本轮单独运行的22项故障诊断；补充证据保存在artifacts/room-queue-performance-verification/incident-verification.json。

本轮确认顺序修复，未证明真实直播CPU显著降低或长期160 MiB内存达标。性能条件、全部对照的取舍与人工验收重点见room-queue-performance-delivery.md。
