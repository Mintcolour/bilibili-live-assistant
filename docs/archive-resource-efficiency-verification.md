# 资源与历史精简版发布核对

发布：`artifacts/releases/archive-resource-efficiency`；分享包：`artifacts/releases/archive-resource-efficiency-win-x64.zip`。

- Release x64 编译和发布成功，App、Core、Infrastructure 与 ReadyToRun 发布产物逐字节一致。
- 保留 AMD64、PerMonitorV2、Microsoft.Data.Sqlite 8.0.32、WinUI 1.7 原生文件及 BrowserHost 发布依赖。
- 本机设置继承 `hot-phrase-bubbles/settings.json`，SHA-256 字节核对一致；未输出设置内容。当前运行进程与用户数据库未改动。
- OBS 九项内嵌资源与源码一致，网页及扩展 JavaScript 语法检查通过。随包保留 CSS 文档、示例及更新后的 `tools/analyze-performance.ps1`。
- 分享包逐项核对文件哈希，排除用户设置、历史库、诊断日志、OBS 密钥及临时文件。最终哈希与清单保存在忽略的 `artifacts/archive-resource-efficiency-verification/`。
- 新旧存储兼容性已静态核对：外观省略项均有模型初始化默认值；可选 null 构造字段读回仍为 null；必需列表、正文、零价格／零数量、表情与图片链接保留。成员比较先统一格式，维护逐批事务、支持取消及重试。
- 旧日志分析成功，新增 SQLite／文字字段在旧日志中返回 null，无伪造的零值。

未启动 App、BrowserHost、浏览器或 OBS，未运行自动测试，也未对用户库执行精简或 VACUUM。实际内存改善、旧库维护及显示兼容留待手动验证；尚不能宣称达到私有工作集 160 MiB。调查数据和操作方法见 `archive-resource-efficiency-delivery.md`（包内「交付核对.md」）。
