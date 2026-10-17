# OBS CSS 主题发布核对

日期：2026-10-17（Asia/Shanghai）。分支：`obs-css-themes`。基线：`c6d6f5e`。

实现提交：`a9e95e6`（样式接口与模板）、`ed4e0a8`（编辑与素材）、`238f3d7`（隔离预览与生命周期）、`917ad21`（绝对到期与去重）、`4940f3f`（画布尺寸与窄屏预览）。

- 独立目录：`artifacts/releases/obs-css-themes`，没有覆盖上一版。
- 主包：`obs-css-themes-win-x64.zip`，160,135,045 字节、957 个文件。
- 主包 SHA-256：`D687BD7857B3167800D4E31FFEA5CBD4F52B633DF8391864716B11F3324E107B`。
- 扩展包：`obs-css-themes-extension.zip`，7 个文件；浏览器登录扩展未修改。
- 本机设置从 `compact-list-animation/settings.json` 继承，字节哈希相同。新字段通过运行时默认值兼容，没有预先改写用户设置。

验证全部属于静态检查与编译／发布文件检查。未启动 App、BrowserHost、浏览器或 OBS，未运行自动测试，未作运行性能或外观验收。

Release x64 Build／Publish 通过，使用 Visual Studio MSBuild。最初直接 `dotnet build` 因 .NET SDK 路径缺少 WinUI PRI 任务失败，随后使用项目既有 VS 构建入口成功；没有修改 SDK、依赖版本或登录流程。

发布的 App／Core／Infrastructure 程序集与 ReadyToRun 产物哈希一致；App、SQLite 和 BrowserHost 为 AMD64；PerMonitorV2、SQLite 8.0.32、BrowserHost Release、图标和性能分析脚本保留。两份 WinUI 原生 DLL 与 Windows App SDK 1.7.250401001 的 x64 MSIX 一致。

通过 PE 元数据与资源区读取，九个内嵌文件均与当前源码逐字节哈希一致：index.html、overlay.js、overlay.css、theme.js、preview.html、preview.js、preview.css、sample-avatar.svg、sample-emote.svg。没有加载执行应用程序集。三个网页脚本 `node --check` 通过；五份构建／核对 PowerShell 脚本的 AST 语法分析通过。

主包、扩展包逐项对照发布目录哈希。主包包含 OBS 样式说明及三份 CSS 示例；说明、示例、图标、性能脚本与源文件哈希一致。分享包排除设置、历史库、日志、本机 OBS key、临时文件和用户素材。

静态复核覆盖：CSS 不随消息批次发送、未变化主题不重新序列化、原生位图预览跳过纯 CSS 修改、独立预览不访问真实时间线、单预览连接与有界资源读取、样式／尺寸变更不改变绝对到期时刻、隐藏／离页／退出取消与清理，以及运动外层与内容样式内层的区分。

可复核脚本：`scripts/verify-local-history-release.ps1`、`scripts/verify-compact-overlay-assets.ps1`。证据位于 `artifacts/obs-css-themes-verification/`：release-verification.json、supplementary-static-check.json、extra-static-check.json、app.embedded.manifest。

实际 OBS 样式层叠、字体支持、动画观感、DPI、布局变化及 CPU／内存趋势待用户人工验证，不将编译通过视为达到 160 MB 目标。人工验收清单见 `docs/obs-css-themes-delivery.md`。
