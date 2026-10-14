# 本地历史存档交付核对

日期：2026-10-15。分支：`local-history`。实施前基线提交 `54181c4`，基于浏览器登录同步版 `98dbc0a`。本版只实施已批准的本地存档功能，保留既有登录同步、媒体、字体、布局与 DPI 设置。

## 实现对应

| 范围 | 源码位置与行为 |
| --- | --- |
| 类型与设置 | `Core/LocalHistory.cs`、`LiveModels.cs`：默认启用、10 GiB、独立目录；明确上下文、事件字段、查询、统计和缺口 |
| 数据库 | `Infrastructure/SqliteHistoryDatabase.cs`：Microsoft.Data.Sqlite 8.0.32、WAL、版本 1 事务建表、主播/房间/曾用名、监视段、事件、资料/榜单快照、分日汇总、缺口及查询索引 |
| 后台归档 | `SqliteLocalHistory.cs`：8192 条/约 16 MiB 包含在途批次；1024 条重要互动预留，500 条或 1 秒事务，空闲等待；监视时 60 秒快照与心跳，故障重试和有界缺口辅助文件 |
| 采集与分段 | `LiveAssistantViewModel.cs`：每次连接的处理器捕获固定房间与段，先归档后显示；测试/示例排除；主动停止结束，账号重连不拆段 |
| 金额与唯一 ID | `BiliEventParser.cs`：可用上游时间、礼物原始字段、SC ID 与整数金额；缺失价格留空，交易/连击标识不用于去重，不保存完整包或凭据 |
| 查询与回看 | `HistoryPage.cs`、`HistoryMessageList.cs`：独立主播/日期/监视段查询、底账、每页 100、取消与 5 秒超时；虚拟化、可见行图片、只提示刷新、不展开媒体 |
| 维护与设置 | `HistorySettingsEditor.cs`、`DisplayPage.cs`、`SqliteHistoryMaintenance.cs`：草稿保存应用、占用与提醒、目录选择；清理确认、批次同步汇总、停止后压缩与迁移、原库保留 |
| 故障与退出 | 概览/历史告警、中断段统计不完整、写入所有权锁、独立进程缺口辅助文件；退出限时提交及未确认落盘提示 |
| 发布 | `scripts/publish-local-history.ps1`：独立目录、继承 `browser-login-sync-v1/settings.json`、保留完整浏览器同步组件；ZIP 排除用户配置及存档 |

## 验证范围

已完成 Core、Infrastructure 与 WinUI 主程序 x64 Release 编译、Git 静态差异检查及 PowerShell 发布/核对脚本 AST 语法检查。发布目录为 `artifacts/releases/local-history`，桌面与 BrowserHost 均为自包含 Release；已核对 SQLite 8.0.32 托管/原生依赖、AMD64 PE 架构、WinUI PRI/资源、主程序/Core/Infrastructure ReadyToRun 构建产物、BrowserHost Release DLL、内嵌 PerMonitorV2 manifest及设置字节继承。

`local-history-win-x64.zip` 的 951 个文件与 `local-history-extension.zip` 的 7 个文件逐项 SHA-256 对应最终发布目录，扩展文件与源码一致；包中没有用户设置、数据库、密文或缺口辅助文件。本机发布目录的设置来自 `browser-login-sync-v1`，按字节保留既有 DPI、字体、布局、媒体及账号设置，未输出其内容。

核对脚本：`scripts/verify-local-history-release.ps1`。机器可读证据：`artifacts/local-history-verification/release-verification.json`；提取的内嵌 manifest 也在此证据目录中。该脚本只读取构建与打包产物，不启动应用或访问真实数据库。

不启动主程序或 BrowserHost，不安装/注册组件，不访问真实历史数据库，不读取或展示用户凭据，不执行自动测试。运行、故障恢复、迁移、图文与 CPU/内存验证由用户按 [使用说明](local-history.md) 手动完成；编译结果不证明运行或性能验收通过。
