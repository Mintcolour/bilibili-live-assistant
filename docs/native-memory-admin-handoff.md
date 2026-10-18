# 原生内存追踪续查

本轮正常观察结束后，用户已同意以管理员身份重新打开 Codex，回到当前对话继续。目标是查明预热后缓慢增长与停止后高位保留的分配来源；尚未确认根因，不将隔离解码实验当成正式修复。

## 开始前

- 仓库为 `D:\Codex\Project\biliAssistant\bilibili-live-assistant`，分支 `live-native-memory`。先检查 Git 状态和本轮最终结果。
- 确认当前进程拥有管理员 token。此前 WPR 以 `0x80070005` 拒绝访问；用户同意重开不等于当前权限已改变。
- 查询已有 WPR 会话，不取消或接管其他工具的追踪。查询独立测试进程名的 HeapTracingConfig 并记录原值。
- 保留正式旧程序及其账号、设置、历史库；只对独立且明确命名的诊断进程启用追踪，不针对通用的 `BilibiliLiveAssistant.App.exe` 配置所有同名进程。

## 有界采集

先核对本机 WPR 帮助及可用 profile。已确认的命令形式为 `wpr -HeapTracingConfig <process name> [enable|disable]`，省略 enable/disable 可查询配置；Heap 与 VirtualAllocation profile 可用。追踪应在隔离进程启动前准备，以覆盖分配和释放。

按启动、预热、真实流量及停止隐藏几个阶段记录时间标记，采集有界的 Heap／VirtualAllocation 调用栈。结合未释放分配、分配与释放差额、堆空闲和提交页，区分活跃对象、堆保留与碎片；仅凭工作集、对象名称或内存指针命中不能归因。

所有成功启用的配置均须在 finally 中恢复；正常结束时停止并保存自己创建的追踪，失败时也只清理本轮会话。不要强制 GC、裁剪工作集或手动释放既有 WinRT COM 引用。

追踪本身会改变性能，该轮只用于定位，不作为稳定性验收。ETL 与 dump 只留在 Git 忽略的本地诊断目录，可能包含私人内存，不上传、不随发布包分享。确认具体分配来源并修复后，再做不附加追踪的真实使用观察。

## 既有证据

- 最终结果与比较边界：`docs/live-native-memory-progress.md`。
- 候选状态：`docs/live-native-memory-delivery.md`，仍未通过稳定性验收。
- 最终数字分析：`artifacts/live-memory-30858592/room1713546334-final-analysis.json`。
- 原始数字日志：`artifacts/memory-soak/room1713546334-account-baseline-v16` 与 `room1713546334-account-owned-wic-v18`。
- v17 在 20:43 原生堆诊断之后的样本已排除；v19 为短期界面诊断，均不替代正常一小时验收。
- 旧正式进程停止后的十分钟外部采样：`artifacts/live-memory-30858592/original-stopped-late-analysis.json`。私有工作集约 626.59 MiB、提交量约 697.29 MiB，当前十分钟高位保留，没有继续快速上涨；此前增长仍未解决。
- 既有 dump 离线查询中，抽取的已卸载概览页和已释放历史页未找到强根，不能把快照中存在旧对象认定为永久页面泄漏。

本轮不启动新一小时实验，不发布“修复完成”包；管理员续查从核实权限与会话开始。
