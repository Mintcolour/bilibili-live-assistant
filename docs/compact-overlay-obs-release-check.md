# 紧凑浮窗与 OBS 发布核对

日期：2026-10-17。分支：compact-overlay-obs。实施前基线：99f4f7d；产品最终实现：b0ada53。

## 文件

- 本机发布目录：artifacts/releases/compact-overlay-obs，已继承 room-queue-performance/settings.json，SHA-256 字节一致。
- 主分享包：artifacts/releases/compact-overlay-obs-win-x64.zip，160,022,601 字节，953 个文件。
- 主包 SHA-256：127F4A2E96ECEF682032A9CDE6E87DA145EDF6311BB50E7E39FEF92514D1FA26。
- 扩展包：artifacts/releases/compact-overlay-obs-extension.zip，7 个文件，登录扩展及流程未修改。
- 包内“紧凑浮窗与OBS说明.md”提供操作入口、OBS 接入和人工验收清单。

## 已完成的静态核对

- Release x64 Publish 成功；App、Core、Infrastructure 的发布程序集与 ReadyToRun 产物哈希一致。
- 应用、SQLite 与 BrowserHost 为 AMD64，内嵌 PerMonitorV2 保留；SQLite 8.0.32、BrowserHost Release 文件、分析工具及图标存在。
- Microsoft.UI.Xaml.dll 和 Microsoft.Internal.FrameworkUdk.dll 与 Windows App SDK 1.7.250401001 x64 MSIX 内文件哈希一致。
- 读取 PE 元数据确认 OBS 的 index.html、overlay.js、overlay.css 全部嵌入；未加载或执行应用程序集。
- node --check 通过；新增打包脚本的 PowerShell 语法分析通过；git diff --check 通过。
- 主 ZIP 953 个文件与扩展 ZIP 7 个文件逐项核对发布文件哈希；无 settings.json、历史库、待补缺口、诊断日志、本机 OBS key 或临时文件。
- 最后补充检查包含本地示例消费状态、示例按钮入口及宽表情长边解码档位；每次修改均重新生成最终发布包。

静态证据：artifacts/compact-overlay-obs-verification/release-verification.json、supplementary-static-check.json，以及提取的 app.embedded.manifest。

## 尚未实测

未启动正式软件、BrowserHost、OBS 或隔离诊断程序，未运行自动测试。因此尚未确认实际桌面交互、网页捕获、断线重连、DPI 图文效果及长时间 CPU／内存表现，也没有声明 160 MiB 私有工作集目标已达标。

手动验收请按 compact-overlay-obs-delivery.md 末尾清单执行，特别检查 SC／上舰顺序、价格停留、桌面翻阅与 OBS 独立、示例不进入 OBS、换房和无消费者时资源释放。
