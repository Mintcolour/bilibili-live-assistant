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
& $msbuild src/BilibiliLiveAssistant.App/BilibiliLiveAssistant.App.csproj /t:Publish /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=false /p:WindowsAppSDKSelfContained=false
```

当前 WinUI 应用使用 Windows App SDK 的框架依赖模式，发布前需要在目标机器安装
Windows App Runtime 1.7（x64）。发布目录可以直接压缩分发，`settings.json` 会在首次运行时生成。

WinUI 3 应用首次启动后在“直播间”页面填写房间号，在“账号设置”填写可选 Cookie，
再从“概览”页面打开弹幕悬浮窗。

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
