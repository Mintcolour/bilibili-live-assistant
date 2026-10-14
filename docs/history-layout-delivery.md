# 历史主播与界面整理版

发布目录：`artifacts/releases/history-layout`。完整包：`history-layout-win-x64.zip`；扩展包：`history-layout-extension.zip`。

## 本轮改动

- 概览增加带头像、名称、房间号的历史主播选择框，按需读取本地目录并支持搜索；选择后点击开启监视。连接栏独立一行，避免挤占右上角操作按钮。
- 历史页使用头像侧栏、当前主播标题、日期筛选与四块数字摘要。更多筛选默认收起，消息列表占主体空间；资料与归档缺口用图标入口。
- “主播操作”增加删除主播全部存档，并保留日期范围清理与磁盘整理。清理前展示范围和条目数，默认取消，后台保护正在记录的监视段。
- 主播头像复用现有有界缓存，仅可见项加载；下拉关闭、行回收或页面隐藏释放图片引用。没有持续查询或新增定时器。

## 编译与交付核对

Release x64 编译完成。发布继承 `local-history/settings.json`，不输出设置内容；数据库仍位于独立存档目录。ZIP 不包含用户配置、数据库或登录密文。

交付前执行 `scripts/verify-local-history-release.ps1 -ReleaseName history-layout -InheritSettingsFrom local-history`：核对 Release ReadyToRun 程序集、BrowserHost、SQLite 8.0.32、AMD64、PerMonitorV2、继承设置哈希，以及 ZIP 条目与发布目录对应哈希。结果保存在 `artifacts/history-layout-verification/release-verification.json`。

未启动主程序或 BrowserHost，未运行自动测试，未读取或清理真实历史数据库。编译与文件核对不代表界面、删除行为或性能已通过运行验收。

## 手动验证

1. 重启后在概览下拉框选择以前监视的主播；检查头像、名称、搜索、房间填入与开启监视。监视或重连期间不能切换房间。
2. 调整主窗口宽度，检查历史主播侧栏、日期与四块摘要，展开更多筛选并查询／重置；切换主播时不能出现新标题下的旧记录。
3. 在可丢弃的主播存档上点击“主播操作 → 删除此主播全部存档”：先取消，应保留记录；再确认，应移除全部日期和监视记录，并从概览选择框移除。日期范围清理仍只清理选定范围。
4. 正在监视的主播不能删除；另一主播可正常清理。切换页面、关闭下拉、最小化与退出时观察头像资源、CPU 和内存。

详细使用方法见 [本地历史使用说明](local-history.md)。
