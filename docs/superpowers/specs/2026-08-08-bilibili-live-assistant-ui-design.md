# B站直播弹幕助手 — 新手友好界面升级设计

日期：2026-08-08
状态：待用户审阅

## 1. 背景与目标

B站直播弹幕助手现有：弹幕悬浮窗 + 系统托盘 + 设置对话框。对新手不友好：

- 启动要双击 `run.bat`，弹出黑色控制台窗口（`python main.py` + `pause`）
- 配置入口藏在托盘右键菜单，Cookie 要手动复制粘贴，没有引导
- 需要安装 Python 环境

**目标**：升级为新手友好的桌面应用——主控制窗口 + 首次运行三步向导 + 统一界面美化 + 打包成双击即用的单文件 exe（无控制台、无需装 Python）。

**范围边界**：弹幕数据链路（blivedm → Qt 信号 → 渲染引擎）不改动；本次只动 UI 层与启动/打包。

## 2. 架构

保持单进程、单事件循环（qasync）。新增/调整文件：

```
main.py              # 入口：首次运行判定 → 向导 or 主窗口
app_controller.py    # 从 main.py 抽出：应用控制器，管理客户端生命周期、跨窗口信号
main_window.py       # 主控制窗口（QMainWindow）
wizard.py            # 首次运行三步向导（QWizard）
settings_dialog.py   # 设置对话框（从 overlay_window.py 抽出，主窗口/托盘共用）
overlay_window.py    # 悬浮窗（仅弹幕绘制微调，逻辑不变）
danmaku_renderer.py  # 渲染引擎（不动）
styles.py            # 统一 QSS 主题
config.py            # 改造：配置路径支持 exe 场景
build.bat            # PyInstaller 打包脚本
B站弹幕助手.spec     # PyInstaller 配置
```

窗口关系：
- 主窗口 = 控制台（普通窗口），悬浮窗 = 置顶穿透（现状不变）
- **关闭主窗口 = 隐藏到托盘，不退出**；托盘菜单增加"打开主窗口"
- `SettingsDialog` 从 `overlay_window.py` 抽出为独立文件，主窗口与托盘共用，避免两套设置逻辑分叉；两处保存后统一触发 `restart_requested` 信号（重启连接）

## 3. 配置持久化（单文件 exe 关键点）

PyInstaller `--onefile` 运行时解压到临时目录，`os.path.dirname(os.path.abspath(__file__))` 指向临时目录，config.json 写那里重启即丢。改为：

```python
def get_config_dir():
    if getattr(sys, "frozen", False):            # 打包后
        return os.path.dirname(sys.executable)   # exe 所在目录（便携式）
    return os.path.dirname(os.path.abspath(__file__))  # 开发时项目目录

def get_config_path():
    p = os.path.join(get_config_dir(), "config.json")
    if not os.access(get_config_dir(), os.W_OK):  # 不可写（如 Program Files）→ 回退 %APPDATA%
        p = os.path.join(os.environ.get("APPDATA", "."), "BilibiliLiveAssistant", "config.json")
        os.makedirs(os.path.dirname(p), exist_ok=True)
    return p
```

开发时行为不变；打包后配置跟随 exe，重启不丢，不可写时自动回退并可在主窗口状态区提示配置文件位置。

## 4. 主控制窗口（main_window.py）

启动时显示。卡片式布局，浅色主题 + B站蓝 `#00A1D6`：

```
┌──────────────────────────────────────────────┐
│  🎬 B站直播弹幕助手      ● 已连接  直播间 12722549 │
├──────────────────────────────────────────────┤
│  [直播间]                                      │
│  房间号: [ 12722549 ]（支持粘贴直播链接自动提取）│
│  [ 开始监听 ] [ 停止 ]     人气: 24            │
├──────────────────────────────────────────────┤
│  [功能开关]  弹幕☑ 进入☑ 礼物☑ SC☑ 高能榜☑       │
├──────────────────────────────────────────────┤
│  [显示设置]  字体28 速度120px/s 透明度85% 轨道8  │
│             [ 打开详细设置… ]                  │
├──────────────────────────────────────────────┤
│  ☑ 显示悬浮窗        [ 最小化到托盘 ] [ 退出 ]   │
└──────────────────────────────────────────────┘
```

行为：
- 状态区：● 已连接 / ○ 未连接（颜色区分）+ 当前房间号；人气值跟随 `live_info_signals.online_updated` 实时刷新
- 连接控制：开始/停止复用 `DanmakuClient.start/stop` + `LiveInfoFetcher.start/stop`
- 功能开关：直接改 `config.display` 对应项并调 `renderer.configure()` 热生效（重建轨道会清空当前屏幕弹幕，可接受），无需重启连接
- 显示悬浮窗开关：勾选显示、取消隐藏（不停止连接）
- 退出按钮 = 真正退出（关闭客户端、保存配置、quit）
- 关闭事件 = 隐藏到托盘；托盘菜单增加"打开主窗口"

## 5. 首次运行向导（wizard.py）

**判定**：`room_id <= 0` 且 Cookie 全空 → 首次运行 → 启动即显示向导；完成后进入主窗口并自动连接。主窗口"帮助 → 重新运行向导"可随时再次打开。

- **步骤 1「直播间」**：输入框支持粘贴 `live.bilibili.com/12345` 或带查询参数的完整链接，正则 `live\.bilibili\.com/(\d+)` 自动提取；留空则提示"从直播页地址栏获取数字"
- **步骤 2「登录 Cookie」**：SESSDATA（密码显示）、buvid3、bili_jct 三个输入框 + 可折叠"如何获取 Cookie"图文步骤（F12 → 应用 → Cookie）+ "不填也能用，只是用户名会打码"提示；允许留空跳过
- **步骤 3「显示设置」**：字体大小、滚动速度、透明度（与设置对话框同字段）
- 完成 → 保存配置 → 关闭向导、打开主窗口、自动开始连接
- 中途关闭向导 → 不保存，下次启动再引导；主窗口仍可用

## 6. 设置对话框（settings_dialog.py）

从 `overlay_window.py` 抽出，字段不变（房间号、Cookie、字体、速度、透明度、轨道数、轮询间隔、功能开关），增加"重置为默认"按钮；套用 QSS 主题。保存后触发 `restart_requested` 重启连接。

## 7. 美化（styles.py）

- 统一 QSS：浅色卡片式布局、B站蓝主色、圆角、悬停反馈；应用于主窗口、设置对话框、向导
- **QSS 不套透明悬浮窗**（会破坏透明背景）；悬浮窗仅改弹幕绘制：描边由纯黑 `QColor(0,0,0,200)` 改为更柔和的深色描边 + 轻微阴影，提升彩色文字清晰度
- 渲染引擎逻辑与轨道高度逻辑不动

## 8. 错误处理

| 场景 | 处理 |
|------|------|
| 配置目录不可写 | 回退 `%APPDATA%`，状态区提示配置位置 |
| 连接失败/断线 | 状态区显示原因，提供"重连"按钮 |
| 向导中途关闭 | 不保存，下次启动再引导 |
| 房间号输入非法 | 输入框即时校验，红色提示 |

**连接失败原因透出**（本设计的缺口修补）：`danmaku_client.py` 目前只发"正在连接/已断开"，blivedm 连接异常被吞。需在 `DanmakuClient.start()` 的客户端协程上捕获异常（直播间不存在、网络错误等），经现有 `connection_status` 信号透出具体原因文本，主窗口据此显示。

## 9. 打包（PyInstaller 单文件 exe）

- `build.bat` 封装命令，`.spec` 固化配置：
  - `--onefile --windowed --name "B站弹幕助手"`（无控制台窗口）
  - `--paths blivedm_lib` 收集内嵌 vendored 库
  - 显式 hiddenimports：aiohttp、qasync、pure-protobuf 等
- 产物 `dist/B站弹幕助手.exe`（约 60-80MB，双击即用）
- 打包后验证清单：配置读写（frozen 路径）、向导、连接、悬浮窗、托盘

## 10. 测试

- **单元测试（脚本）**：
  - 配置路径逻辑（`sys.frozen` 模拟：exe 目录、不可写回退 %APPDATA%）
  - URL 提取房间号正则（完整链接、带参数、纯数字）
  - 向导保存逻辑（三步 → config.json 字段正确）
- **手工验证清单**：首次向导全流程 → 连接 → 悬浮窗 → 开关热生效 → 设置对话框重启连接 → 托盘打开主窗口 → 打包后 exe 全流程复测

## 11. 改动文件清单

| 文件 | 改动 |
|------|------|
| `main.py` | 入口逻辑：首次运行判定、构造主窗口/向导 |
| `app_controller.py` | 新增：控制器（从 main.py 抽出），跨窗口信号接线 |
| `main_window.py` | 新增：主控制窗口 |
| `wizard.py` | 新增：三步向导 |
| `settings_dialog.py` | 新增：从 overlay_window.py 抽出并美化 |
| `styles.py` | 新增：QSS 主题 |
| `overlay_window.py` | 移除 SettingsDialog（移至独立文件）；弹幕描边微调；托盘加"打开主窗口" |
| `danmaku_client.py` | 连接失败原因经 connection_status 透出 |
| `config.py` | 配置路径支持 frozen 场景 |
| `build.bat`、`B站弹幕助手.spec` | 新增：打包 |
