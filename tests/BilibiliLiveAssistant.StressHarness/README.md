# 隔离内存与高流量模拟

压力程序链接正式应用的 ViewModel、WinUI 概览、全部悬浮区域、图片管线和 SQLite 存档。替换直播传输、图片 HTTP 与设置存储，不读取真实设置、Cookie 或历史库，不启动 BrowserHost、账号监听或真实网络连接。

默认每秒 50 条消息，轮换 20,000 个 UID。混合普通弹幕、进房、礼物、上舰与长 SC；每三条中一条含两个透明表情。头像 URL 按 UID 独立，128 个表情 URL，由本地处理器返回 8 种固定 PNG。解码、缓存与显示代码采用正式实现。默认概览＋浮窗同时显示，字体、阴影、容量与帧率使用正式默认设置。

额外概览榜单 HTTP 面板折叠，避免真实请求；浮窗人数、榜单和主播资料由本地事件更新。模拟不覆盖真实网络解析、外部服务延迟、登录、完整榜单分页或所有图片格式。不能视为真实直播全部场景的性能保证。

## 构建和启动

使用 Visual Studio MSBuild，保持与正式发布相同的 Release x64、SelfContained 与 ReadyToRun；压力项目会复制 PRI/XBF 资源。

```powershell
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$buildExe = & $vswherePath -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/MSBuild.exe' | Select-Object -First 1
$outputDirectory = Join-Path $PWD 'artifacts/memory-soak/published/'
& $buildExe 'tests/BilibiliLiveAssistant.StressHarness/StressApp.csproj' '/restore' '/t:Publish' '/p:Configuration=Release' '/p:Platform=x64' '/p:SelfContained=true' '/p:PublishReadyToRun=true' "/p:PublishDir=$outputDirectory" '/v:minimal'
if ($LASTEXITCODE -ne 0) { throw '压力程序发布失败' }
& './scripts/start-memory-soak.ps1' -Name 'both-2h-50ps' -Seconds 7200 -Rate 50 -Users 20000 -View both -BinarySource $outputDirectory
```

每次使用新的 Name。脚本复制独立程序，避免编译改动正在运行的程序集。图片、数据库、配置和采样文件只写入 Git 忽略的 artifacts/memory-soak/<Name>。通过 Explorer 启动避免继承开发工具包身份；launchRequested 仅表示启动请求，应确认日志头 PID、后续采样和 completed.json。

预热 10 秒后按真实墙钟产生指定秒数的消息，随后验证停播、主动停止、同房间重启保留内容，以及换房清空。两小时意味着真实 7,200 秒，约 360,000 条消息，不以加速回放代替。只有完整流量结束、检查断言且 completed.json 存在才算完成。中断或失败不得标作通过。

`-View overlay`／`dashboard` 仅用于区分浮窗和概览开销。`-Images shared` 使用少量共享头像 URL；`avatars`／`emotes` 分别仅显示头像／表情；`none` 关闭头像和表情显示，避免空头像触发资料补取。它们均为诊断对照，不能代替默认 `both`＋`unique` 的最终验收，也不修改正式软件设置。

`-Snapshot` 在流量开始 20 秒及结束刷新后分别保存 `overview.png`、`overview-settled.png`，用于核对头像、表情和布局。截图会额外分配内存，截图运行不作为最终性能验收。候选版尽量单独运行；多程序显示、转储和 EventPipe 会干扰结果，最终验收不做这些操作。

## 分析结果

```powershell
& './scripts/analyze-memory-soak.ps1' -Path './artifacts/memory-soak/both-2h-50ps'
```

每约 5 秒记录私有提交量、总工作集、Windows PROCESS_MEMORY_COUNTERS_EX2 私有工作集、托管内存、GC、分配量、CPU、句柄、图片资源、队列、帧间隔与存档写入。采样峰值不是连续测量的瞬时峰值。MB=1,000,000 字节，MiB=1,048,576 字节，分析同时列出两种 180 阈值。旧日志缺私有工作集时返回空值，不当作零。

悬浮队列按正式过时/拥堵规则省略的消息单列统计；存档遗漏、显示缓冲遗漏和写入失败分别报告。不能通过减少真实历史或消息容量来满足指标。不强制 GC、不裁剪工作集、不手工释放 COM。

资源计数描述应用持有的租约与引用，原生字节为估算；解除引用不表示 WinUI、驱动和分配器立即将全部提交内存返还系统。须结合长期曲线判断。

## 诊断采集

短测可用 dotnet-trace 的 gc-verbose 配置采集 15 秒分配栈，或用 dotnet-dump 查看存活对象和根引用。工具放在 Git 忽略目录。转储会暂停进程，诊断管道增加内存，因此与最终验收分开。

原始日志、转储和数据库不加入 Git 或分享包；交付说明记录参数、提交版本、结果和限制。
