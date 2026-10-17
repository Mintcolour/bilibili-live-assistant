# 概览、可视化设置与浮窗体验更新

日期：2026-10-18。分支：`overview-overlay-experience`，基线 `6005145`。发布目录：`artifacts/releases/overview-overlay-experience`，沿用 `compact-style-editor-fix` 的当前设置；本次界面验收使用单独的构建目录设置，不进入发布包。

## 本次变化

- 概览顶部集中放置「信息」「榜单」「变化动态」，榜单点击展开，不再占 SC 列。统计图继续使用房间人数。匿名读取直接可见。原顶部示例和详情统计按钮移除，调试示例保留在「更多」中。
- 「编辑浮窗」「显示浮窗」与「滚动／紧凑」分开；全新设置默认紧凑，已有用户模式按原设置继承。
- 可视化设置提供常驻对象导航、搜索、当前对象标题和分类属性组。紧凑面板、OBS 网页源与 CSS、金额停留分别进入各自分类。切换保留草稿，切换分类时预览与属性回到顶部；保存后应用、还原修改沿用原规则。
- 紧凑和独立区域编辑器采用统一深色样式，提供滑块和精确数值、明确保存与取消。消息正文左键不再任意弹出详情，查看详情改用明确的右键入口；网格的隐藏与次数明细同样使用明确操作。
- 原生绘制按文本元素识别 emoji，并用 Segoe UI Emoji 显示相应字形；中文和其他文字仍沿用原描边／阴影。没有新增常驻图片缓存。
- 概览、原生浮窗和 OBS 的 SC 外观使用一致的六档色阶：不足 50、50、100、500、1000、2000 元起，分别为蓝、青、金、橙、粉、红。未知金额另用灰蓝色，正文保持可读，标签明确金额。档位外观与可编辑停留时间独立。预览可切换六档；独立 SC 编辑示例补齐结构化金额。
- 变化动态读取已有本地历史，支持当天／7 天／30 天；有取消和条数上限，不新增上游轮询。OBS CSS 控件延迟创建。未修改登录、数据库格式、消息容量、排队顺序、价格时长、历史底账和资源预算。

## 已实际操作的界面验证

已通过资源管理器正常启动本地 Release x64 构建，未开启直播监视，未用示例写入真实历史。不是只编译后推断界面正确。

| 操作 | 观察结果 |
|---|---|
| 概览信息、榜单、变化动态入口 | 能打开；未监视时显示暂无数据。榜单包含高能／大航海及总／月／周切换入口，SC 保持完整一列 |
| 滚动／紧凑模式切换、进入编辑 | 模式标签更新；滚动示例实际移动，紧凑示例按面板显示 |
| 紧凑顶部「样式」重复打开 | 能打开，没有新增运行错误 |
| 字号 18 调到 48 后取消 | 示例立即变大，取消恢复 18，重开仍为 18 |
| 动画时长改为 500 毫秒并保存 | 保存关闭；仅独立验收配置中的相应字段更新 |
| 可视化设置字号、切换类别、还原 | 预览即时变化，切换后草稿保留，还原恢复保存值 |
| 字体选择与属性分组 | Microsoft YaHei UI 正常显示；相关属性按组展示 |
| 搜索 OBS、金额分类，展开 CSS | 搜索可定位对象，各类属性分开；CSS 延迟展开正常 |
| OBS 属性滚到底后切换金额分类 | 布局完成后右侧滚动位置为 0，SC 档位表从顶部展示 |
| SC 预览切到 2000 元 | 红色档位、白色正文与醒目金额标签显示正常 |
| 滚动／紧凑示例 emoji | 普通 emoji 可见；紧凑示例中的肤色与 ZWJ 组合无方框 |

本地证据：`artifacts/overview-ui-review/overview-sc-2000.png` 为实际界面截图；`emoji-and-tiers.png` 为生产绘制器的离屏图像核对，二者不是性能压测。

## 限制与后续人工核对

- Windows 当前字体环境将旗帜显示为 CN／JP 字母，原生 emoji 为单色字形。本轮修复方框，不承诺与浏览器全彩 emoji 一致。
- 实际操作覆盖上述入口；独立浮窗的完整菜单／详情、每种模式逐项保存取消、窄窗口和所有 DPI 的组合尚未全部验收。原生浮窗覆盖主窗口时，界面工具对相关窗口和菜单的定位不稳定，这些不能算作通过。
- 未运行自动化测试、真实直播高频场景、长期内存／CPU 压测或 OBS／CEF 实际捕获。OBS JavaScript 语法与嵌入资源核对通过，不等于浏览器画面已验证。本轮不宣称性能改善。
- 工具直接启动 exe 时出现一次 XAML 初始化失败（0x802B000A），事件记录带有 Codex 包身份；同一构建通过资源管理器正常启动多次，后续样式操作未产生新的 runtime-errors.log。将此记为启动环境差异，不据此更改应用启动代码。
- 界面实测对象为构建输出；最终发布目录只做产物核对，不再用继承的真实配置启动，避免变更用户设置或历史。

请关闭旧版后从新目录启动，重点回看真实头像／表情、六档 SC、窗口遮挡时的编辑、OBS 样式及高负载资源占用。

## LAPLACE 云历史研究

源码文档为 [laplace-cloud-event-research.md](laplace-cloud-event-research.md)，发布目录附带「LAPLACE云历史研究.md」。主要结论：后台 Event Fetcher 可在网页关闭时持续采集，网页随后同步；浏览器 IndexedDB 是另一份本地记录，Login Sync 只同步登录状态，实时 Bridge 本身不等于持久化历史。

已核对官方文档；未取得 21452505 当前云接口的实际响应，因此不推断该房间的具体服务地址、SC 覆盖、去重键或保存期限。本轮没有增加云同步功能。

## 构建与发布核对

```powershell
./scripts/publish-local-history.ps1 -ReleaseName overview-overlay-experience -InheritSettingsFrom compact-style-editor-fix -DeliveryDocument overview-overlay-experience-delivery.md
./scripts/finalize-compact-overlay-release.ps1 -ReleaseName overview-overlay-experience -DeliveryDocument overview-overlay-experience-delivery.md
./scripts/verify-local-history-release.ps1 -ReleaseName overview-overlay-experience -InheritSettingsFrom compact-style-editor-fix
./scripts/verify-compact-overlay-assets.ps1 -ReleaseName overview-overlay-experience
```

发布核对结果存放 `artifacts/overview-overlay-experience-verification`：ReadyToRun 三个程序集、AMD64、PerMonitorV2、SQLite 8.0.32、BrowserHost、WinUI 原生文件、OBS 内嵌资源、扩展、ZIP 字节哈希及设置继承。脚本中的 `startedApplication=false` 仅表示核对脚本未启动程序；上方实际界面验收另行记录。

本机新目录按字节继承旧版设置；分享 ZIP 排除设置、历史库、诊断日志、OBS 密钥与临时文件。保留旧发布目录，未删除用户数据。
