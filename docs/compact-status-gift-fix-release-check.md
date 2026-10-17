# 紧凑布局修复发布核对

- 分支：`compact-status-gift-fix`；基线提交 `cd53e3d`，功能提交 `d81d1a4`。
- Release x64 编译成功。使用 Visual Studio MSBuild 发布，没有启动应用或 BrowserHost。
- `node --check` 检查 OBS 的 `overlay.js`、`preview.js`、`theme.js` 通过；Git 差异空白检查、发布脚本 PowerShell 语法检查通过。
- 发布目录 `artifacts/releases/compact-status-gift-fix` 的设置与 `obs-css-themes/settings.json` 字节一致。
- AMD64、PerMonitorV2、ReadyToRun、SQLite 8.0.32、BrowserHost Release 产物核对通过。
- 9 个 OBS 内嵌资源与当前源码一致；两份 WinUI 原生组件与 Windows App SDK 1.7.250401001 一致。
- 主 ZIP 957 项、160,141,457 字节，所有包内文件均与发布目录哈希一致；扩展 ZIP 7 项核对通过。
- 主 ZIP SHA256：`7A6DD36335B69400AEE14C7BABD8D174B7C9C6BA10C61E94CF6293D5C7D0A496`。
- 主包保留紧凑浮窗／OBS 说明、OBS 样式说明及三份 CSS 示例；不包含用户设置、数据库、日志、OBS 密钥或用户素材。

检查证据：`artifacts/compact-status-gift-fix-verification/release-verification.json` 和 `supplementary-static-check.json`。

没有运行自动测试，没有打开桌面窗口、浏览器预览或 OBS。实际布局、礼物正文显示及性能尚待按交付说明人工验收。
