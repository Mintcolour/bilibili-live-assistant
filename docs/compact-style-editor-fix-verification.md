# 紧凑样式按钮修复版发布核对

发布目录：`artifacts/releases/compact-style-editor-fix`；分享包：`artifacts/releases/compact-style-editor-fix-win-x64.zip`。

- Release x64 编译及发布成功，App／Core／Infrastructure 与 ReadyToRun 产物逐字节一致。
- 保留 AMD64、PerMonitorV2、SQLite 8.0.32、WinUI 原生依赖及 BrowserHost 发布文件。
- 本机设置继承 `archive-resource-efficiency/settings.json`，字节哈希一致。分享包排除设置、历史库、诊断日志、OBS 密钥与临时文件。
- OBS 九项内嵌资源与源码一致，网页及浏览器扩展 JavaScript 语法核对通过；保留 CSS 文档、示例及最新性能分析脚本。
- 静态检查确认数字范围在 Value 之前设置；动画时长最高 500、表情最高 128，均不再受到控件初始上限 100 的错误限制。顶部与右键共用修复后的入口，打开／保存异常写入现有安全错误日志。
- 历史库、登录流程、显示队列与上一版资源优化未改动。未启动 App、BrowserHost、浏览器或 OBS，未运行自动测试；实际按钮交互由用户按交付说明手动验收。

包内文件逐项哈希与本机产物一致，最终证据位于忽略目录 `artifacts/compact-style-editor-fix-verification/`。
