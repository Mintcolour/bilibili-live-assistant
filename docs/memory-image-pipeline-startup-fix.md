# 图片管线启动修正说明

## 问题与证据

`memory-image-pipeline` 版在启动后读取图片时可能直接退出。2026-10-15 22:53–22:54 的三次 Windows 应用错误均来自该发布目录，WinUI 异常码为 `0xC000027B`；对应 WER 记录包含 `0x80000013`（`RO_E_CLOSED`，对象已关闭）。

离线检查三份既有转储，异常线程的栈内均出现 `RTMediaFrame!SoftwareBitmapImpl::get_PixelHeight+0x4d`。该名称使用与转储模块 GUID、age 匹配的微软公开符号解析。转储未包含完整的 stowed exception 对象，未将栈地址扫描描述为完整调用栈。

代码中 `UiPixelSource.CreateAsync` 在 `SetBitmapAsync` 完成后退出 `using`，提前关闭了底层 `SoftwareBitmap`。WinUI 原生实现会继续保存这一位图，后续表面复制、布局和设备恢复仍可能读取；显示源自身的关闭路径负责最终释放。因此，“上传结束就可单独释放输入”的原交付说明不成立，现已更正。

## 修正

- 成功调用 `SetBitmapAsync` 后，将底层位图交由原生显示源持有与关闭。仅未交出的输入在本地释放。
- 消费者取消后，仍等待已经开始的上传结束，再通过显示源的关闭路径清理过期结果。
- 保留 UI 线程所属、共享租约、可见行激活和 15 秒闲置回收。
- 8 MiB 闲置预算现在按“底层位图＋一份显示表面估算”计费，数量上限仍为 256。正在使用的显示源仍单独统计。
- 诊断增加 `backingBitmapBytes`、`displaySurfaceBytesEstimate` 和 `pixelAccounting=backing-plus-surface-v2`。它们是总估算的分量，不能重复相加；旧版单份像素口径不宜直接比较。分析脚本继续兼容旧日志。

字体、DPI、阴影、透明度、帧率、队列、历史和账号规则沿用既有设置。

## 验证与交付

- 已完成静态释放路径检查、分析脚本语法检查和 Release x64 编译。
- 独立发布目录：`artifacts/releases/memory-image-pipeline-fix`。
- 继承 `memory-image-pipeline/settings.json`，以字节比较核对；原目录保留。
- 核对 ReadyToRun、PerMonitorV2、SQLite 8.0.32、BrowserHost、图标、分析脚本与交付文件。分享包排除设置、历史库、账号数据、诊断日志及崩溃转储。
- 按约定没有启动应用、BrowserHost 或自动测试；发布核对不代表已实测启动成功或内存改善。

请手动打开修正版，先确认主窗口及历史主播头像显示，再检查滚动离屏恢复、显示／隐藏、设置预览及 DPI 切换。若还有退出，可继续利用新产生的 Windows 错误记录定位。

WinUI 生命周期依据：[原生 SoftwareBitmapSource 实现](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/components/imaging/SoftwareBitmapSource.cpp)。
