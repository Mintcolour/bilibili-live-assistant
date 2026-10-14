# 浏览器登录同步交付核对

日期：2026-10-14。分支：`browser-login-sync`。本文件记录实现与静态交付证据，浏览器安装、真实登录、注册表修改及性能测试均未执行，由用户按使用说明手动验收。

## 范围核对

| 计划要求 | 当前源码与交付证据 | 仍需人工确认 |
| --- | --- | --- |
| 自有扩展、本机传输、无云端同步 | `browser-extension`、`BrowserHost/Program.cs`、`BrowserSyncTransport.cs`；无页面注入、HTTP 服务或 TCP 监听 | Chrome／Edge 开发加载及本机通信 |
| 主动打开向导、元数据配对、单活动来源 | `AccountPage.ConnectAsync`、`BrowserAccountController.ReceiveAsync/ApproveAsync`、`BrowserSyncHost.Process`；来源 GUID、代次、令牌、固定 origin 校验 | 初次配对、多配置、过期与断开后重发 |
| 账号卡、操作指引、匿名选择、退出策略 | `AccountPage`、`LiveAssistantViewModel`；刷新本机与浏览器立即同步分开，修复入口、头像释放、匿名模式明确保留 | 账号昵称、运行模式、隐藏与恢复显示 |
| 最小 Cookie 查询与权限不足拒绝 | 仅三个字段、两个 HTTPS URL；目标及其父域共四个确切主机权限，读取前后检查权限，非分区／普通 Store，域路径与冲突筛选 | 真实 Cookie 域、权限撤回、Cookie 删除后重建 |
| 扩展合并、后台补偿、有界退避 | `background.js`：单份发送、metadata 串行写入、dirty／manualPending、2 秒合并、5 秒限频、一次性最少 30 秒 alarm、三次退避后停止 | 休眠、连续事件、通知失败和主动同步重试 |
| 帧上限、序号、原子先写后通知 | `SyncFrames` 先检查 64 KiB，单值 8 KiB；`BrowserSyncHost` 校验序号和请求 ID；`BrowserAuthVault` 互斥及密文原子替换 | 超限、幂等重试、并发退出和重新绑定 |
| 验证身份、迟到结果丢弃、认证请求取消 | `BiliAccountService` 单请求 10 秒／256 KiB；controller 取消、版本比较；VM 一项连接更新、历史保留；概览榜单更新代次 | A→B、断网／403／失效、连续凭据更新 |
| 运行凭据与普通配置分开、旧配置失败保留 | `AppSettings.Cookies` JsonIgnore；独立 runtime cookies；复用同一个 `JsonSettingsStore`；迁移写入／回读后清理敏感配置，失败阻止保存 | 当前配置首次迁移、写盘失败、原字体等保留 |
| DPAPI 当前用户、恢复不静默覆盖 | `BrowserAuthVault`，CurrentUser＋SYSTEM ACL；显式恢复保留一份损坏备份，已有备份时拒绝覆盖 | 不同用户、损坏密文、权限异常、重启迁移 |
| 用户点击时安装、稳定路径、归属检查、完整运行时 | `BrowserHostInstaller`、`scripts/publish-browser-sync.ps1`；HKCU 两视图、自有 owner、协议标记、逐文件哈希、原子版本切换、拒绝重定向路径 | 安装／修复繁忙、注册部分失败、升级、卸载两种选择 |
| 无凭据日志／普通 JSON／扩展持久 Cookie | 源码静态审阅；消息和凭据 record ToString 脱敏；stdout 仅协议帧；扩展 storage 仅元数据；本轮无诊断文件 | 手动操作后核对本机实际设置，不分享旧敏感配置 |
| 退出释放、低占用、有界资源 | pipe／验证／计时器取消；头像可见时持有；最多四个配对请求，单份最新更新，无桥接保活 | 实机退出、空闲与大量弹幕时 CPU／内存曲线 |

## 实际执行的静态验证

- WinUI 主程序 x64 Release MSBuild 编译及 ReadyToRun 发布成功；BrowserHost .NET 8 win-x64 self-contained Release 发布成功。
- `node --check` 后台与弹窗脚本通过；PowerShell 发布脚本 AST 语法检查通过；Git 差异空白检查通过。
- 从 manifest 公钥 SHA-256 推导 ID，核对为 `lnmblcjfmiaeihkogpmkbjmpngnfkjme`；Host 固定 origin 和协议版本与扩展一致。
- 核对主程序、WinUI PRI／XAML 资源、.NET 运行时、桥接 EXE／DLL／运行时、协议标记、扩展文件和图标齐全。
- 发布 EXE 的内嵌 manifest 保留 `PerMonitorV2`；主程序 ReadyToRun DLL 和桥接 DLL 核对各自构建产物。
- 本机新目录 `settings.json` 与上一版 `media-cards` 按字节一致，未输出内容、未运行迁移；ZIP 排除该文件、密文与临时文件。
- 两个 ZIP 的每个文件按 SHA-256 与最终发布目录比较，扩展文件与源码比较；未将真实浏览器 Cookie、私钥或用户状态放入包中。

这些检查不证明真实浏览器流程或性能通过。本轮授权交付范围是实现、静态检查、编译与发布；人工验收表见 [使用说明](browser-login-sync.md)。
