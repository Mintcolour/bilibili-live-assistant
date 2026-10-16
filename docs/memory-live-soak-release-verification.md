# 一小时内存优化版发布核对

发布目录：`artifacts/releases/memory-live-soak`。

一小时结果见 [交付说明](memory-live-soak-delivery.md)，诊断与分批实施见 [进展记录](memory-live-soak-progress.md)。结果仅适用于所记录的远程桌面、默认布局和模拟流量，未完成两小时或实体显示器高刷新率验收。

- 正式 App 的 Release x64／ReadyToRun 发布成功；三个应用程序集与 R2R 输出哈希一致。
- BrowserHost Release 发布成功，发布程序集哈希与构建输出一致；没有启动 BrowserHost。
- 原 SDK `1.7.250401001` 的两项 XAML 原生 DLL 与已验证的模拟构建逐字节一致。
- `settings.json` 从 `room-resource-release` 继承，SHA-256 一致；未显示或修改设置内容。
- SQLite `Microsoft.Data.Sqlite 8.0.32`、`e_sqlite3.dll` 及依赖完整，PE 核对为 AMD64。
- 可执行文件内嵌 manifest 保留 `PerMonitorV2`，图标、PRI、扩展和 `tools/analyze-performance.ps1` 均存在。
- 分享 ZIP 按条目与发布目录核对哈希，排除用户设置、历史数据库、诊断日志、PDB 和临时文件。
- 正式 App 没有启动；此前运行的是隔离 StressApp，已按用户的一小时观察口径结束，没有继续执行两小时模拟。

现有核对脚本：

```powershell
./scripts/verify-local-history-release.ps1 -ReleaseName memory-live-soak -InheritSettingsFrom room-resource-release
```

机读发布核对保存在 `artifacts/memory-live-soak-verification/release-verification.json`。本版的核心修改为稳定列表行与消息控件复用，不靠减少消息容量、降低画质或强制回收来取得结果。
