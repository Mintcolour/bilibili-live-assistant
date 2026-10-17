# 紧凑浮窗打开与开关闪退修复

日期：2026-10-17。分支：compact-overlay-obs。修复前版本：79b7de6。

## 原因与证据边界

CompactOverlayWindow.CreateParams 通过 Interactive → Editing 访问 _vm，但 _vm 在派生构造函数体中才赋值。WinForms Control 的基类构造函数计算客户区大小时会虚调用 CreateParams，此时访问 _vm 必然产生空引用异常。原版 OverlayWindow 又在构造时无条件创建紧凑面板，导致普通浮窗也受影响。

概览中的“紧凑”操作捕获了异常，却仍可能留下已勾选的浮窗开关。用户再次点浮窗开关时，Checked 入口没有异常保护；失败构造还可能留下基类的设置事件订阅，使后续设置操作继续访问未完成的窗口。

本机 10:23:45 的 Application Error 记录指向 compact-overlay-obs 发布目录和 Microsoft.UI.Xaml.dll（0xc000027b）；WER 报告含 0x80004003。该日志没有托管调用栈，因此它佐证发生过崩溃，但不能单独证明每一次卡死都只有上述原因。构造顺序缺陷依据代码及 WinForms 构造调用路径确定。

## 修复

- CreateParams 仅读取独立的鼠标穿透状态，禁止在基类构造阶段访问 ViewModel；实例初始化后再同步实际交互状态。
- 紧凑面板按需创建；普通浮窗独立打开。主浮窗构造失败时释放已创建的区域、计时器及事件订阅。
- 打开浮窗的入口捕获并报告异常，释放失败实例；概览开关根据实际打开结果恢复状态，失败时不再开启交互或示例。
- 隐藏与句柄销毁重置绘制排队状态、取消行图片等待并释放位图；重新创建句柄时请求首次绘制。
- 图片异步更新后的绘制回调也捕获异常。单次打开期间相同绘制故障只报告一次。
- 新增 runtime-errors.log，记录阶段、异常类型、HRESULT 和无源文件路径的调用栈；不记录异常消息、URL、弹幕、Cookie 或 OBS key。当前文件达到 256 KiB 后轮换，最多保留当前及一份旧文件。WinUI 未处理异常仅记录，不标记为已处理，也不掩盖致命错误。

日志位置：%LOCALAPPDATA%/BilibiliLiveAssistant/runtime-errors.log。

## 发布与人工验收

发布目录：artifacts/releases/compact-overlay-startup-fix。
分享包：artifacts/releases/compact-overlay-startup-fix-win-x64.zip。
本机目录继承 compact-overlay-obs/settings.json，发布核对确认字节一致；分享包排除用户设置、历史库、日志及 OBS key。

只做静态检查、Release x64 编译、发布文件核对；不启动 App、BrowserHost、OBS 或自动测试。编译通过不代表实际交互和性能已验证。

请手动验证：

1. 普通模式打开、关闭浮窗，确认原有各区显示正常。
2. 点击“桌面紧凑面板”，确认显示；点击“显示示例消息”，确认本地示例可见。
3. 连续开关浮窗及切换普通／紧凑模式，检查无卡死、闪退、重复窗口。
4. 交互调整、阅读列表、拖动缩放、完成后穿透，再次隐藏与恢复。
5. 如仍失败，保留新版本 runtime-errors.log 与发生时间；不要把旧版本日志误认为新包结果。

本轮不改变价格停留规则、列表容量、字体、帧率、历史和登录流程。
