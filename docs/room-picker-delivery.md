# 概览单行连接版

发布目录：`artifacts/releases/room-picker`。完整包：`room-picker-win-x64.zip`；扩展包：`room-picker-extension.zip`。本机版本继承 `history-layout/settings.json`，历史数据库继续使用原独立存档目录。

## 修改

- 概览顶部改为一行；窄窗口优先保留主播输入与监视按钮，次要统计仍可在详情查看。
- 主播下拉框可直接输入房间号、直播链接或主播名称，不再有第二个房间号输入框。
- 输入后 300 毫秒自动查询本地历史，查询最多返回 50 位；取消旧请求并检查当前输入，避免过时结果覆盖。
- 候选显示头像、名称和房间号；有效的新房间号提供连接候选，点击开启监视后取得真实资料。输入搜索不发送直播接口请求，也不自动开启监视。
- 保留监视期间锁定、头像有界缓存和候选列表可见项加载；防抖计时器为一次性，隐藏页面或退出时停止。

## 验证范围

仅静态检查、Release x64 编译、发布文件核对。核对脚本：`scripts/verify-local-history-release.ps1 -ReleaseName room-picker -InheritSettingsFrom history-layout`；证据：`artifacts/room-picker-verification/release-verification.json`。

核对项目包括 ReadyToRun 程序集、BrowserHost、SQLite 8.0.32、AMD64、PerMonitorV2、设置继承哈希及 ZIP 与目录逐项哈希。分发包不含用户设置、数据库或登录密文。未启动应用或 BrowserHost，未运行自动测试，未访问真实历史数据库；不据编译结果认定界面或性能验证通过。

## 手动检查

1. 顶部确实只有一行，独立房间输入框已移除；缩窄窗口时仍能输入与开启／停止监视。
2. 点击箭头浏览历史主播，输入名称、曾用名、房间号和直播链接，检查自动匹配及候选头像。
3. 输入新房间号，选择新连接候选或直接点击开启监视；短房间号沿用现有连接处理，无效输入提示错误。
4. 快速连续修改输入，确认候选对应最后一次输入；选择历史主播、键盘选择／Enter 后开启监视应连接正确房间。
5. 监视及重连时输入锁定；停止后可换房。关闭列表、切换页面、最小化及退出后观察 CPU 与图片内存。
