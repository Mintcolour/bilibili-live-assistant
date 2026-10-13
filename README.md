# B站直播弹幕助手

## WinUI 3 版本（迁移分支）

项目当前保留原 Python/PyQt5 版本，并新增 `BilibiliLiveAssistant.sln`。
WinUI 3 版本使用 C#/.NET、Windows App SDK 和 Fluent Design，数据链路在
`src/BilibiliLiveAssistant.Infrastructure/` 中重新实现，用户配置写入 exe 目录的
`settings.json`。

开发环境需要 Visual Studio 2022（Windows App SDK/Windows 10 SDK）和 .NET SDK。

```powershell
dotnet restore BilibiliLiveAssistant.sln
dotnet test tests/BilibiliLiveAssistant.Tests/BilibiliLiveAssistant.Tests.csproj
# 在 Visual Studio 或带 Windows App SDK 构建组件的 MSBuild 中构建 WinUI 项目
$msbuild = "C:\\Program Files\\Microsoft Visual Studio\\2022\\Professional\\MSBuild\\Current\\Bin\\MSBuild.exe"
& $msbuild src/BilibiliLiveAssistant.App/BilibiliLiveAssistant.App.csproj /restore /t:Publish /p:PublishProfile=win-x64
```

当前 WinUI 应用随构建产物携带 Windows App SDK；`win-x64` 发布配置还包含 .NET 运行时，
发布目录可以直接压缩分发，无需另装 Windows App Runtime。`settings.json` 会在首次运行时生成。

本地构建并启动（自动查找 Visual Studio MSBuild，包含预览版）：

```powershell
pwsh -NoProfile -File scripts/start-winui.ps1
# 已构建时仅启动
pwsh -NoProfile -File scripts/start-winui.ps1 -SkipBuild
```

启动脚本默认构建 Release。后续开发以低占用和长期稳定为主要方向，详见 [性能策略与测量方法](docs/performance.md)。

启动脚本通过 Windows 资源管理器打开程序，避免从带应用包身份的工具进程启动时继承宿主身份。
也可以在资源管理器中双击生成的 exe。曾出现的 `0x80070032` 不能单凭错误码归因于系统运行时损坏；
排查时应同时检查启动进程是否继承了其他应用的包身份。

WinUI 3 应用在“概览”顶部填写房间号或直播链接，点击“开启监视”，并打开弹幕悬浮窗。
可选 Cookie 仍在“账号设置”填写。

### 匿名读取直播间

在“概览”顶部的读取模式菜单开启“匿名读取”，填写房间号或直播链接后开启监视。
此模式不会发送已保存的 SESSDATA、bili_jct 或账号 Cookie，软件会自动获取访客 buvid；
可接收 B站下发的弹幕、礼物、上舰、醒目留言和互动事件。
用户名和 UID 以 B站实际下发内容为准，匿名弹幕可能显示打码用户名或 UID 0，不能恢复被隐藏的信息。
不填写 SESSDATA 时也会自动按匿名身份读取；已有账号 Cookie 可保留，关闭匿名开关后再使用。
读取方式在开始监听时保存，切换方式前先停止监听。详见 [弹幕连接与协议说明](docs/danmaku-connection.md)。

### 分区显示与外观设置

普通弹幕、礼物/上舰、SC、榜单分别在独立区域滚动，进房/关注固定停留。
在“可视化设置”选择类别，左侧实时预览，右侧调整字体、颜色、表情大小、透明度、速度和对应计时。
桌面缩略图支持拖动位置及缩放区域；修改先保留为草稿，点击“保存并应用”后生效。
“保存并显示示例”保存后生成本地示例。详见 [可视化设置说明](docs/visual-dashboard.md) 和 [分区显示说明](docs/overlay-display.md)。

### 图文概览与 B站表情

概览按普通弹幕、SC、礼物/上舰分列，进房/关注在左侧下方独立显示；拖动列间分隔条可保存列宽。
SC 下方增加可折叠、可调整高度的高能榜／大航海总月周榜；顶部主播摘要可点击展开统计与有上限的人气曲线。
默认显示头像、粉丝牌和舰长/房管标识；支持正文内图片表情和整条大表情，匿名缺失资料使用占位或省略。
礼物/SC 支持已读、已感谢、仅未读筛选，并独立保留有上限的本次记录；悬浮卡片过期不会清除记录。
在“可视化设置 → 概览消息”可调整预设、字体、颜色、文字/背景透明度、排版、图片尺寸及记录上限。
动图默认静态，可开启概览有限播放；桌面悬浮使用静态组合位图以控制重绘开销。
详见 [图文概览说明](docs/dashboard.md)。

### 媒体预览与固定卡片

普通弹幕与 SC 支持 BV／Steam 静态资料预览，默认概览自动内嵌，悬浮附在原消息下方；空间不足时继续显示原文。
概览普通弹幕列头可切换列表／固定卡片（`Alt+C`），重复内容原位计数，悬停保护正在阅读的内容，支持隐藏和可选防刷屏。
在“可视化设置 → 媒体预览／概览卡片”调整对应设置，本地示例即时预览、保存后应用。
新版目录为 `artifacts/releases/media-cards/`，继承 `dpi-aware` 配置；详见 [资源边界和手动验收说明](docs/media-and-cards.md)。

基于 Python + PyQt5 的 B站直播弹幕桌面悬浮工具，实时显示弹幕、用户进入、在线人数变化等信息。

## 功能

- 实时弹幕滚动显示
- 用户进入/关注/分享通知
- 醒目留言(SC) 高亮显示
- 礼物赠送通知
- 上舰通知
- 人气值变化提醒
- 高能榜信息展示
- 桌面悬浮窗口（无边框、置顶、半透明）
- 鼠标穿透模式（不遮挡正常操作）
- 系统托盘快捷操作

## 安装

```bash
pip install PyQt5 qasync aiohttp
```

blivedm 库已集成在 `blivedm_lib/` 目录中。

## 使用

```bash
python main.py
```

首次运行会提示输入直播间ID。之后可以通过系统托盘图标进入设置修改配置。

### 获取 Cookie（可选，用于完整数据）

1. 打开浏览器，登录 [bilibili.com](https://www.bilibili.com)
2. 按 F12 打开开发者工具
3. 切换到 "应用"(Application) → "Cookie" → `https://www.bilibili.com`
4. 找到 `SESSDATA`、`buvid3`、`bili_jct` 的值
5. 在设置对话框中填入

> **注意**: 不填 Cookie 也可以接收弹幕，但用户名会打码、UID 显示为 0。

## 弹幕颜色说明

| 颜色 | 含义 |
|------|------|
| 白色 | 普通弹幕 |
| 绿色 | 用户进入/关注/分享 |
| 金色 | 醒目留言(SC) |
| 粉色 | 礼物/上舰 |
| 蓝色 | 人气值变化 |
| 紫色 | 高能榜信息 |

## 配置文件

配置保存在 `config.json` 中，包含：
- 直播间ID
- Cookie信息
- 显示参数（字体、速度、透明度等）
- 功能开关

## 项目结构

```
├── main.py               # 入口
├── config.py             # 配置管理
├── danmaku_client.py     # 弹幕客户端(WebSocket)
├── live_info.py          # 在线信息轮询(REST API)
├── overlay_window.py     # 悬浮窗口+系统托盘
├── danmaku_renderer.py   # 弹幕渲染引擎
├── blivedm_lib/          # blivedm 弹幕库
└── config.json           # 用户配置（自动生成）
```
