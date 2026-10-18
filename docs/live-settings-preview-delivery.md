# 单行概览与动态设置预览

日期：2026-10-18。分支：`live-settings-preview`。实施前基线：`6a2902b`。

## 本次变化

- 概览的房间选择、监视、主播信息、榜单、变化动态及浮窗操作集中在同一行。直播时长和房间人数的图标及数值收进主播信息按钮，点击详情也可查看。
- 窄窗口收起标题、缩短房间选择框、将浮窗编辑和显示按钮改为图标；极窄空间仍可横向滚动访问，不新增第二排。
- 可视化设置主分类合并为「概览」「独立浮窗」「紧凑面板」「OBS 网页源」「历史存储」。概览包含消息外观、列表／固定卡片及媒体；独立浮窗的各类区域、高频气泡与金额停留改为组内入口；紧凑面板也能访问同一份金额停留草稿。
- 搜索保留各类关键词，切换类别保留草稿及当前组内选择。修改即时预览，保存并应用后修改实际窗口；还原修改恢复已保存值。
- 消息外观默认持续接收混合示例，包括图文、图片表情及六档 SC，可手动聚焦单一示例。只保留最多四个概览示例控件。
- 独立浮窗按当前滚动、列表或网格／固定卡片模式演示；滚动沿用草稿速度，固定显示持续轮换示例。SC／礼物卡片使用草稿金额停留规则，互动使用其停留时长。预览是有界示例，并非真实直播的完整排队复刻。
- 概览卡片模拟固定槽位更新与重复计数；关闭卡片模式时演示列表更新。尺寸和实际可容纳列数即时更新。
- 紧凑面板持续演示混合列表出入，复用有界图片池；关闭动画仍会更新示例。高频气泡自动演示出入和换位。媒体预览使用本地资料，悬浮示例按普通弹幕草稿速度滚动，不访问视频或游戏资料接口。
- 隐藏、切换分类或离开设置时停止相应定时器与动画，解除图片引用。预览高度变化不再触发整块重建。没有改动真实消息容量、队列、历史底账、数据库或登录流程。

## 验证记录

已通过资源管理器启动独立配置的 Release x64 构建，未开启直播监视，没有将示例写入真实历史。原发布版本保持运行。

| 操作 | 观察结果 |
|---|---|
| 约 1010 px 宽主窗口的概览 | 顶栏保持一行，时长及人数图标在主播信息内，浮窗操作保持可访问 |
| 合并后的设置导航 | 五个主分类及其组内入口可显示、切换 |
| 概览混合示例 | 表情图片可见，连续观察到 SC 金额及消息内容更新 |
| 普通弹幕滚动切到固定列表 | 预览随模式变化，滚动实际移动，列表持续换入示例 |
| 概览固定卡片开关 | 从列表切为固定槽位，次数与最后更新时间变化 |
| 卡片高度从 180 调到 268 px | 预览高度同步变化，无需保存 |
| 悬浮媒体草稿启用 | 本地媒体示例随消息滚动 |
| 还原修改 | 显示还原成功，恢复已保存设置 |
| 紧凑面板分类 | 原生榜单滚动及混合示例可显示，列表演示改为自动持续播放 |

上述界面观察后，补充了窄窗口编辑图标不被状态刷新覆盖、卡片实际列数提示和列表示例追加顺序的静态修正，并重新编译。未将这些后续细节描述为另一次完整界面验收。

`git diff --check` 与 Release x64 编译通过。未运行自动化测试、真实直播高流量场景、长期 CPU／内存验收、全部 DPI 或 OBS／CEF 捕获。设置内原生预览不解释 OBS 自定义 CSS；CSS 继续使用「打开真实网页预览」。历史存储展示配置及占用文字，没有无意义的动画。本轮不宣称性能改善或内存达到特定值。

## 发布与设置

独立发布目录：`artifacts/releases/live-settings-preview`，本机设置按字节继承 `overview-overlay-experience`。独立界面验收配置不会复制进发布目录。

```powershell
./scripts/publish-local-history.ps1 -ReleaseName live-settings-preview -InheritSettingsFrom overview-overlay-experience -DeliveryDocument live-settings-preview-delivery.md
./scripts/finalize-compact-overlay-release.ps1 -ReleaseName live-settings-preview -DeliveryDocument live-settings-preview-delivery.md
./scripts/verify-local-history-release.ps1 -ReleaseName live-settings-preview -InheritSettingsFrom overview-overlay-experience
./scripts/verify-compact-overlay-assets.ps1 -ReleaseName live-settings-preview
```

发布核对覆盖 AMD64、ReadyToRun、PerMonitorV2、SQLite、BrowserHost、WinUI 原生依赖、OBS 资源、设置继承及分享包字节哈希。核对脚本不启动发布版；实际界面检查使用独立构建配置。

分享 ZIP 不包含用户设置、历史库、诊断日志或 OBS 密钥。请关闭旧版后，从新目录启动使用。仍需人工回看真实头像／表情、各种模式及 DPI、保存后的实际窗口和 OBS 样式。
