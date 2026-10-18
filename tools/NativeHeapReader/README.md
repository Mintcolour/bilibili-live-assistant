# 原生堆离线配对

仅分析本机已有的 PID 堆 ETL 与模块侧车，不附加进程、不启用追踪、不下载符号，不属于正式应用依赖。

```powershell
dotnet build tools/NativeHeapReader/NativeHeapReader.csproj -c Release
dotnet run --project tools/NativeHeapReader/NativeHeapReader.csproj -c Release -- trace.etl 1234 modules.json new-analysis.json 1,2,4,6,8
```

模块侧车格式：`{"modules":[{"name":"module.dll","address":1234,"size":4096}]}`，地址与大小为十进制整数。必须来自相同进程、相同采集窗口。输出必须是新文件，不覆盖原始证据。输入规模由采集阶段限制；ETL 转换会在本地生成 ETLX，分析工具自身的内存不计入目标应用。

按堆句柄和地址配对，区分内层 Alloc(new)/Free(old) 与外层 ReAlloc(old,new)，原地调整大小保留地址生命周期。输出各时点仍可见的分配、前 200 个栈及不同持有年龄的分组；释放年龄桶只包含追踪中确实配对的分配。

边界：

- 附加之前的分配不可见，未配对释放不自动代表事件丢失。
- 原始字节合计含嵌套事件，不能当作真实分配吞吐。
- 年龄只表示本次追踪中的地址生命周期，不代表业务对象年龄；地址未变但内部对象替换也不会重新计时。
- 末尾仍存活可能是正常缓存，不能直接称为泄漏。堆事件也不覆盖全部提交页、GPU 或分配器保留。
- TraceEvent 的 EventsLost 与 xperf 的 LostBuffers 均需核实；模块偏移不是函数符号。
- ETL 及衍生数据可能含私人内存，只放在忽略目录，不提交或随分享包分发。
