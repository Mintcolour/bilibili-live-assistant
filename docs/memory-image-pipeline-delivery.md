# 图片内存管线重构交付说明

2026-10-15 的启动问题已在独立目录 `artifacts/releases/memory-image-pipeline-fix` 修正，见 [启动修正说明](memory-image-pipeline-startup-fix.md)。原发布目录保留，请使用修正版。

## 范围与证据边界

本轮处理实际可见行、静态像素传递、共享显示源及释放路径。没有新增用户设置项，没有修改字体、阴影、透明度、目标帧率、消息容量、礼物合并、历史数据库或账号登录流程。

实施前保存基线 `2ad95cb`，从 `performance-runtime` 创建 `memory-image-pipeline` 分支，按诊断、可见行、像素管线、共享资源生命周期、交付说明分批提交。像素迁移这一批包含共享服务基础，随后一批完善跨页面可见性、回收及诊断统计。

旧版真实日志仅用于确定调查方向：2026-10-15 21:02–21:46 共 512 个样本，21:15–21:39 私有内存回归斜率约 0.033 MiB/分钟；21:40 后多次显示主窗口，私有内存峰值约 363.62 MiB，托管内存峰值约 42.12 MiB。该现象支持优先检查界面图片，不足以证明所有增长都来自图片，也没有把已有页面卸载清理描述为永久泄漏。

**本版没有启动应用做运行测量，尚不能宣称降低了多少内存或 CPU。编译通过不等于性能改善得到验证。**

## 静态图片数据流

- `BiliPixelFrame` 独立拥有 BGRA8、预乘 Alpha、紧密排列的只读像素，步长为宽度乘 4。
- 普通静态图片由 Windows 解码器缩放后直接取像素。GIF 首帧继续通过原有 GDI+ 画布合成，再读取像素；静态分支在 PNG 编码之前返回。
- 静态头像、表情、默认 GIF 首帧、头像装饰和媒体封面使用同一管线。64／128／256 px 解码档位、原图尺寸上限、不放大原图及较大解码复用规则继续保留。
- WinUI 显示源改为 `SoftwareBitmapSource`。启动修正版中，成功传入的 `SoftwareBitmap` 由原生显示源持有并在关闭时释放；上传结束后仍可能被后续布局、表面复制和恢复读取，不能在此时单独释放。取消时也等待已经开始的上传结束，再关闭过期显示源。
- 需要二次缩小时直接缩放像素。浮窗仅在一次消息合成期间复制为 `Format32bppPArgb` 位图，用完释放，没有新增常驻 GDI 图片缓存。
- 普通设置预览和媒体设置预览直接上传合成位图的像素。新预览取消旧预览，清空时先解除图片绑定，再释放旧显示源；全局像素转换／上传最多两路。
- 动画资产仍保存原有紧凑编码帧和延迟。GIF 合成、40 帧上限、长动画退回首帧、默认关闭、每个既有时钟最多 4 个动画及最高 10 FPS 的规则没有改变。暂停或离屏后控件丢弃动画资产和动画显示帧引用，恢复共享静态首帧；缓存中的动画仍受原有资产预算约束。

静态控件显示后只保存显示源租约及必要元数据，不再持有静态像素资产。`MessageAnimationClock` 只负责动画调度，不再维护首帧缓存。

## 共享显示源和可见性

`LiveAssistantViewModel.UiImages` 提供应用级、UI 线程所属的共享显示源服务；图片缓存保留 `GetStaticForViewAsync`、`TryGetStaticFrame`、`GetAnimationForViewAsync`，服务提供 `AcquireStaticSourceAsync`。

- 键由规范化 URL、目标物理宽度和解码档位组成；不含 UID、昵称或控件身份。相同键共享显示源和创建任务。
- 可见控件持有租约；解除绑定后归还。最后一个租约归还前不会销毁显示源。
- 无租约的闲置资源按 LRU 限制为 **8 MiB／256 项**。仍有等待者、但尚未交出租约的完成结果暂时受保护，单独计入 reserved，防止在调用方取得租约前被淘汰。
- 全部共享图片消费者退出后，启动一次性的 **15 秒**回收；重新取得资源时取消。没有持续回收轮询。
- 服务最多保留 32 个待创建键、两路创建任务。容量满时可取消地等待，不把容量压力记作永久图片失败；一个等待者取消不会取消其他等待者，最后一个等待者取消后停止尚未执行的工作。
- 控件、预览与动画各有请求代次。迟到结果先核对代次及可见状态，再绑定；过期租约和上传结果立即释放。

概览普通弹幕、礼物、SC、互动及高能／大航海列表按 `ItemsStackPanel` 的实际可见索引管理图片。滚动、容器变更、尺寸、DPI 和数据同步触发一次布局回调；同一轮请求合并，初始范围尚未就绪时再等一次布局，不逐帧查询。预加载容器仍可保存文字，但不会因此激活图片。

消息卡片继续作为图文资源生命周期入口，不依赖行内表情自身的 Loaded 状态。固定网格保持既有槽位规则；历史、主播选择和媒体使用既有可见性判断，图片统一接入共享租约。

主窗口隐藏、最小化或离开页面时立即解除当前页面图片租约，文字记录和滚动位置保留。恢复只激活当前可见行；重复窗口状态通知不会反复执行整页恢复。每页可见条件同时检查它仍是当前页面，防止离开后的异步通知重新激活旧页面。DPI 改变会重新选择物理尺寸。

换房同步取消旧消息行、榜单及浮窗消费者，清理闲置显示源；取消后的解码结果不再写回资产缓存。退出先停止页面与浮窗消费者，再关闭共享服务；已开始的上传完成后释放，不允许新请求。显示源均在所属 UI 线程释放，没有强制 GC、工作集裁剪或手动 WinRT COM 释放。

下载仍为两路、进行中最多 32 个；图片资产缓存共 **24 MiB**，磁盘缓存仍为 **128 MiB／512 文件**，域名白名单和请求头规则保留。

## schema 3 诊断账目

沿用“性能诊断”页手动开启，默认关闭，约 5 秒采样，四个文件合计约 8 MiB。新增字段全部是数量、估算字节或耗时，不记录消息、URL、账号或 Cookie。日志仍位于 `%LOCALAPPDATA%/BilibiliLiveAssistant/diagnostics`。

| 字段 | 含义及关系 |
| --- | --- |
| `images.staticPixelBytes` | 当前资产缓存中静态首帧的实际托管像素字节 |
| `images.animationEncodedBytes` / `animationPixelBytes` | 动画的编码帧字节／额外保留的首帧像素字节 |
| `images.estimatedBytes` | 24 MiB 缓存的预算计费；静态按像素，动画按编码加保守解码估算。不是进程全部原生内存 |
| `imageResources.views` / `activeViews` | 已创建图片控件／当前激活控件；离屏仍保留文字控件不等于保留图片 |
| `uiImages.sources` / `leases` | 服务中独立静态显示源／被控件借用的租约。多个租约可共用一个源 |
| `uiImages.activeSources` / `idleSources` / `reservedSources` | 正在使用、可淘汰闲置、完成但尚待领取；三类互斥，总和等于 sources |
| `uiImages.activeBytes` / `idleBytes` / `reservedBytes` | 上述三类字节估算，互斥；启动修正版将底层位图和一份显示表面同时计费，idle 才受 8 MiB 闲置预算约束 |
| `uiImages.pending` / `queued` / `creating` / `waiters` | 受 32 键限制的待完成或待领取项、其中尚未开始项、正在创建项及等待消费者数 |
| `uiImages.created` / `released` | 进入共享服务的源及其释放累计数；差值等于服务 sources |
| `uiImages.cancelled` / `aborted` / `stale` | 因最后一个消费者离开而取消的共享任务、取消退出的工厂、工厂完成后被代次判断丢弃的结果 |
| `imageResources.nativeSources` / `nativePixelBytesEstimate` | 全部受管理的 SoftwareBitmapSource，包括服务源、正在上传的源及设置预览；uiImages 是其子集，**不能相加** |
| `imageResources.backingBitmapBytes` / `displaySurfaceBytesEstimate` | 底层位图像素字节及一份显示表面的估算；修正版两者已包含在 nativePixelBytesEstimate 中，不能重复相加。pixelAccounting=backing-plus-surface-v2 标识新计费口径 |
| `imageResources.created` / `released` / `staleUploads` | 所有上述源的创建、释放、上传完成后因取消立即丢弃的累计数；包含未进入共享缓存的过期上传 |
| `imageResources.pendingUploads` / `activeUploads` | 全局像素转换／上传等待与执行数，执行最多 2 个 |
| `imageResources.decodeCount` / `decodeMs` 等 | 诊断开启期间的解码、转换、上传、释放次数和墙钟耗时。可含取消或失败的尝试，不能相加当作 CPU 时间 |
| `window.mainVisible` / `page` | 当前主窗口状态及页面，可与资源数对照 |

字节估算不覆盖 WinUI 内部图像副本、动画 BitmapImage、驱动、图形内存和分配器的全部保留空间，不能据此要求进程私有内存恰好等于这些字段之和。动画原生帧仍按既有方式解除引用。

分析脚本保留旧字段，增加像素、源、队列、取消和耗时的峰值／增量；旧日志没有的新字段返回 null。已用真实旧日志分析核对兼容，未构造软件运行样本：

```powershell
./tools/analyze-performance.ps1 -FromTime '2026-10-15T21:15:00+08:00' -OutputPath ./performance-report.json
```

## 开发侧验证与发布

本轮仅做静态检查、脚本语法解析、既有真实日志分析、Release x64 编译及发布文件核对。**未启动软件、BrowserHost、自动测试或压力示例。**

静态核对包括：静态分支不再 PNG 编码，旧帧读取已迁移且没有把像素当成编码图片；预览取消、离屏、换房、DPI、上传失败和退出路径有对应释放；显示参数、队列、历史及登录协议没有修改。

- 独立发布目录：`artifacts/releases/memory-image-pipeline`。
- 私有本地目录继承 `performance-runtime/settings.json`，只核对哈希和字节一致，不打印内容。历史库仍使用原位置，本轮未打开历史库。
- 发布核对保留 ReadyToRun、AMD64、PerMonitorV2 DPI、SQLite 8.0.32、BrowserHost、图标、扩展和分析工具。
- 分享包：`artifacts/releases/memory-image-pipeline-win-x64.zip`。不带用户设置、历史库、账号存储或诊断日志；单独扩展包仍保留。
- 本地发布核对证据：`artifacts/memory-image-pipeline-verification/release-verification.json`，不进入分享包或 Git。

## 用户手动验收

1. 检查头像、头像框、透明表情、GIF 首帧、动画开启、媒体封面和无效图片；分别在 100%／150%／200% DPI 下查看，保持原字体、阴影与透明度。
2. 滚动离屏再返回、拖动布局、列表／网格切换、榜单折叠、换房，以及请求未完成时退出。图像不得消失、串到其他消息或重新出现旧房间内容。
3. 主窗口隐藏且没有其他 UI 图片消费者时，约 20 秒内检查 `activeViews`、`uiImages.leases`、`pending`、闲置 sources 与全局上传任务是否归零；浮窗继续正常显示。底层 24 MiB 资产缓存允许按预算保留。
4. 显示／隐藏主窗口 30 次，观察创建和释放差值、独立显示源、租约及队列能否回到稳定水平，而非按操作次数累计。
5. 预热后分别记录主窗可见、仅浮窗、大量消息三种场景。比较相近吞吐、相同布局、头像与帧率下的私有内存、工作集、分配速率、Gen2、CPU P95 和图片出现延迟。

验收以资源释放正确、长期走势稳定、显示没有退化为准。当前没有新版运行日志，具体节省量和真实场景稳定性仍待上述手动验证。
