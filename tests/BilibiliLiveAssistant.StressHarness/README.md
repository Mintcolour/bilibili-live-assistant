# 高流量弹幕实机压测

该 WinUI 测试程序直接链接正式应用的 ViewModel、主窗口和悬浮窗，使用本地事件源，无须连接 B站。
默认配置：主窗口 1100×720，悬浮层 1920×360，8 条轨道，滚动速度 120 px/s。

顺序运行：预热 5 秒、每秒 10 次测试点击 10 秒、100 条/秒 15 秒、1000 条/秒 15 秒、一次突发 3000 条，随后等待到第 80 秒退出。模拟流量中每 50 条含一条礼物或醒目留言。
每秒记录整个进程 CPU 时间、工作集、私有内存、句柄、GC、活动弹幕、历史和缓冲数量。
最后十秒仍可能有排队过的付费事件在移动，应以活动数、待处理数和动画状态判断是否空闲。

在仓库根目录使用 Visual Studio MSBuild 构建（`dotnet build` 可能缺少 PRI 生成任务）：

```powershell
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$buildTool = & $vswhere -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/MSBuild.exe'
$stressOutput = Join-Path $PWD 'artifacts/stress/local-run/'
& ($buildTool | Select-Object -First 1) tests/BilibiliLiveAssistant.StressHarness/StressApp.csproj /restore /t:Build /p:Configuration=Release /p:Platform=x64 "/p:OutputPath=$stressOutput" /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Stress build failed' }
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"{0}"' -f (Join-Path $stressOutput 'StressApp.exe')) -WindowStyle Hidden
```

通过桌面 shell 启动可避免继承开发工具的包身份。运行中不要构建或截图，退出后检查输出目录的 `stress-results.json`。
平均 CPU 应按 `IntervalSeconds` 加权；峰值是约一秒采样窗口的峰值，不能代表毫秒级瞬时峰值。
原始记录放在 Git 忽略的 `artifacts/` 中。

## 托盘与退出验证

构建时增加 `/p:TraySmoke=true`，并使用独立输出目录（例如 `artifacts/tray-smoke/`），可运行手动 UI 验证模式。
它直接链接正式的 `TrayService`，检查图标和右键菜单注册，模拟隐藏后恢复主窗口，并展示托盘使用的原生菜单。
选择菜单“退出软件”后，检查进程结束以及 `tray-smoke-results.json` 的 `Stage=exited`、`CleanupPassed=true`。
该模式验证菜单命令及资源释放，不代替 Windows 通知区域中图标位置的人工检查。
