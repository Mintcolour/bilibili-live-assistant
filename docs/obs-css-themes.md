# OBS 网页样式定制

入口：可视化设置 → 紧凑面板 / OBS / 金额停留 → OBS 网页样式 · 模板与 CSS。

选择「现有紧凑／透明纯文字／聊天气泡／简洁卡片」模板。需要用滑块改网页外观时，关闭「基础外观沿用共享设置」，再展开「网页基础外观」。模板、外观参数和自定义 CSS 单独保存于当前设置中的 `compact.obsTheme`，不改变桌面原生浮窗。

修改只更新草稿。点击「打开真实网页预览」，在默认浏览器查看本地示例；回到软件修改，约 300 毫秒合并更新。满意后「保存并应用」，已连接的 OBS 自动接收样式，不重置 SC／礼物的停留计时。左侧原生示例仍用于桌面浮窗，不能预览 CSS。

OBS 网页源启用、地址复制和输出宽高设置仍位于上方「OBS 网页源」。OBS 中添加「浏览器」来源，使用软件复制的地址，宽高与软件一致。默认 480 × 800。无需登录 OBS 或设置 OBS WebSocket。

## CSS 如何叠加

正常声明的层叠顺序为 `bla-base → bla-defaults → bla-template → bla-appearance → bla-custom`：基础结构、共享设置、内置模板、网页专用外观、用户 CSS。脚本只为画布尺寸、缩放和动画保留必要的内联几何属性；事件粉丝牌颜色使用内部变量，不强制写入外观属性。

将 CSS 直接写入编辑框，不必自行套 `@layer`。软件将其放入 `bla-custom` 层。OBS 浏览器源属性的「自定义 CSS」也可使用这些选择器；其中不分层的普通声明可覆盖软件分层的普通声明。`!important` 仍遵循 CSS 自身的层叠与优先级规则，不保证任意第三方 CSS 都能被另一条普通声明覆盖。

高级编辑区的复制／导出只包含该编辑框的自定义 CSS；模板和滑块参数由软件保存。导入支持 UTF-8 `.css`，上限 256 KiB；读取失败保留原内容。浏览器按标准 CSS 规则忽略无效声明，这里没有完整的语法诊断器。出现空白时可在原生设置中「恢复默认网页样式」后保存，或「还原修改」回到当前已保存值。

参考 LAPLACE 的模板＋CSS 工作流，当前 DOM 与其不同；LAPLACE 的现成 CSS 不能保证直接兼容。首版不包含云端／远程模板、任意 JavaScript 或网页代码注入。

## 稳定接口 v1

根节点 `.bla-overlay[data-style-version="1"]`。保留旧的 `#panel`、`#status`、`#sc`、`#gift`、`#list`、`#entry`、`#follow`、`.message`、`.name`、`.body`、`.badge`、`.avatar`、`.emote` 选择器。

| 选择器 | 用途 |
| --- | --- |
| `.bla-content` | 一条消息的外观、背景、边框、阴影和内容变换 |
| `.bla-name`、`.bla-body` | 昵称、正文 |
| `.avatar-wrap`、`.avatar`、`.avatar-frame` | 头像、头像框 |
| `.emote`、`.emote.large` | 行内表情、整条大表情 |
| `.bla-fan-medal` | 粉丝牌 |
| `.bla-identity`、`.bla-guard`、`.bla-admin` | 身份、航海等级、房管 |
| `.bla-type-badge`、`.bla-price` | 类型徽章、金额 |
| `.bla-gift-details`、`.bla-gift-name`、`.bla-quantity` | 可选结构化礼物明细、名称、数量 |
| `.bla-audience`、`.bla-watched` | 房间人数、看过人数 |
| `.bla-metric-icon`、`.bla-metric-value` | 统计图标、数值 |
| `.bla-rank-member`、`.bla-rank-index` | 榜单成员、名次 |

`.bla-message[data-type="…"]` 中的类型为 `Danmaku`、`SuperChat`、`Gift`、`Guard`、`Enter`、`Follow`、`Like`。固定区和列表使用相同消息内容结构，用父区域限定即可分别调整，例如 `#sc .bla-content` 与 `#list .bla-message[data-type="SuperChat"] .bla-content`。

消息包含 `data-guard-level="0|1|2|3"`（0 无可靠身份，1 总督，2 提督，3 舰长）、`data-amount-known="true|false"`。可靠金额另有 `data-amount-milli-yuan`，单位为千分之一元。`data-price-tier` 是当前停留规则中的一基档位编号，未知为 0；不是 B站官方档位，不将未知价格归零。榜单成员包含 `data-rank` 与 `data-guard-level`。

礼物名称和数量有可靠字段时才提供 `.bla-gift-details`，默认隐藏以保留原正文。需要结构化排版时：

```css
.bla-message[data-has-gift-details="true"] .bla-body { display: none; }
.bla-gift-details { display: inline; }
.bla-gift-name { color: #ffc857; }
.bla-quantity { margin-left: 4px; }
.bla-gift-details .bla-price { margin-left: 8px; }
```

## 常用变量和示例

根节点常用变量：`--bla-font-size`、`--bla-avatar-size`、`--bla-rank-avatar-size`、`--bla-emote-size`、`--bla-message-gap`、`--bla-panel-padding`、`--bla-content-padding`、`--bla-line-height`、`--bla-radius`、`--bla-border-width`、`--bla-border-color`、`--bla-message-background`、`--bla-panel-background`、`--bla-name-color`、`--bla-opacity`、`--bla-sc-lines`。

消息节点常用变量：`--bla-message-color`、`--bla-message-font`、`--bla-message-weight`、`--bla-message-style`、`--bla-message-shadow`、`--bla-large-emote-size`。覆盖事件原有字体／颜色时，直接修改 `.bla-content` 的标准 CSS 属性更直观。粉丝牌可用 `.bla-fan-medal { --bla-badge-background: #456789; }` 改背景，不需要覆盖内部事件变量。

```css
.bla-overlay {
  --bla-font-size: 20px;
  --bla-avatar-size: 30px;
  --bla-message-gap: 8px;
  --bla-sc-lines: 6;
}
.bla-content {
  font-family: "Microsoft YaHei UI", sans-serif;
  padding: 8px 12px;
  background: rgba(24, 28, 40, .78);
  border-radius: 12px;
  text-shadow: 0 1px 2px rgba(0, 0, 0, .8);
}
.bla-message[data-type="SuperChat"] .bla-content { border-left: 4px solid #ffc857; }
.bla-message[data-guard-level="3"] .bla-name { color: #8ec5ff; }
.bla-fan-medal { display: none; }
```

包内 `OBS样式示例` 提供三份可导入的 CSS。CSS 只改外观，不更改消息队列、顺序、历史或按金额决定的展示时间。外层 `.bla-message` 的 `transform`、`opacity`、定位属性及 `#panel` 的缩放由运行时管理；请在内层 `.bla-content` 添加变换、透明度或装饰，避免和出入动画争用。

## 本地素材

在「本地素材」导入 PNG／JPEG 图片或 TTF／OTF／WOFF2 字体，选择素材并复制代码到 CSS。每个输入最多 4 MiB；图片不超过 2048 × 2048，静态化为 PNG；处理后单文件最多 8 MiB；整个目录最多 64 个文件、32 MiB。

素材位于 `%LOCALAPPDATA%/BilibiliLiveAssistant/obs-assets`，由内容哈希命名，经本机服务的 `assets/<哈希>.<扩展名>` 提供。服务只接受受控文件名，不开放任意本地路径，素材读取与图片共用两路并发。支持打开素材目录，由用户手动清理不再使用的文件；清理正在使用的素材会让该图片／字体不可用。导出 CSS 不打包素材，分享时需另行提供素材并在目标机器导入。

网页维持仅本机资源策略，外部 `@import`、远程图片／字体和 `file://` 不可用。系统已安装字体可以直接指定 `font-family`。

## 预览与资源规则

真实预览只在点击后建立随机端口、随机路径的独立本机服务，最多一个预览连接，OBS 未启用时也能使用。样本只来自内置静态消息和本地 SVG，画面上的姓名、金额及人数均为示例。预览不启动真实监视、不请求 B站图片、不写历史、不占真实 OBS 的两个消费者名额。

CSS／滑块修改在可见预览连接存在时约 300 毫秒合并推送，不轮询。预览有棋盘／深色／浅色背景切换和手动「示例入场」。离开紧凑设置、隐藏主窗口或退出会关闭预览服务；浏览器检测断线后解除图片、清空样式和动画。再次使用请在软件重新打开，并关闭旧预览页。关闭浏览器标签会停止连接和样本推送；在紧凑设置仍打开时只保留空闲本机监听和最新草稿引用。

网页画面保留原有 32 个列表 DOM 节点、最多 4 个临时离场节点和其像素估算预算；底层混合时间线仍为 100 条。CSS 不随约 100 毫秒的弹幕批次重复发送，仅变化与重连时发送。字体、图片、窗口或 CSS 引起尺寸变化时使用合并的 ResizeObserver 回调，不新增逐帧布局检测。原生预览跳过纯网页样式修改的重绘。

内置模板不含无限动画、大面积模糊或持续特效。用户自定义无限动画、昂贵滤镜、大背景图仍可能提高 OBS 浏览器的 CPU／GPU／内存开销；文件限制不是实际原生内存上限。运行表现需分别观察主程序和 OBS，本次未运行性能实测。

软件网页预览不包含另外粘贴到 OBS 属性中的 CSS；该部分以 OBS 最终画面为准。

参考：[LAPLACE 模板说明](https://laplace.live/chat/templates)、[OBS Browser Source](https://obsproject.com/kb/browser-source)。
