# 弹幕连接与匿名读取

参考用户提供的 [blivedm dev/sample.py](https://github.com/xfgryujk/blivedm/blob/dev/sample.py)，并对照其
[web 客户端](https://github.com/xfgryujk/blivedm/blob/dev/blivedm/clients/web.py)、
[WebSocket 协议](https://github.com/xfgryujk/blivedm/blob/dev/blivedm/clients/ws_base.py)、
[事件映射](https://github.com/xfgryujk/blivedm/blob/dev/blivedm/handlers.py) 和
[protobuf 字段](https://github.com/xfgryujk/blivedm/blob/dev/blivedm/models/pb.py)。参考日期：2026-10-09。
继续使用现有 C#/.NET 实现，无须额外 Python 进程。

## 匿名模式

“直播间”页面的“匿名读取（无需登录）”在开始监听时保存。
开启后不使用已保存的任何账号 Cookie，HTTP 客户端也不自动保留服务器的登录 Cookie。
软件从 B站首页自动取得访客 buvid3，以 UID 0 连接。
没有 SESSDATA 时同样使用匿名身份；若仅填写 buvid3，则仅使用该访客字段，不发送 bili_jct。
关闭匿名开关并填写有效 SESSDATA 后，从 nav 接口取得实际账号 UID 后进行认证。

匿名身份可接收服务器下发的弹幕、礼物、醒目留言、上舰与互动事件。
弹幕用户名可能被打码、UID 可能为 0；这些限制由 B站决定。只展示实际下发内容，不尝试恢复隐藏身份。
“账号设置”保存的 Cookie 不会因启用匿名模式被删除。

## 连接修正

- 先将短房间号解析为真实房间号，用真实 ID 获取服务器信息和认证。
- nav、获取 token、WebSocket 使用相同 User-Agent；获取服务器信息的 type 为 0。
- WBI 密钥缓存约 11 小时，刷新避免并发请求；接口返回 -352 时重新获取密钥并重试一次。
- 认证响应按 JSON 的 code 判断，只有 code=0 后界面才显示“已连接”。原实现误将 JSON 前四字节视为整数。
- 认证成功后立即发送一次心跳，此后每 30 秒发送。连接和认证均有 10 秒等待上限；持续没有响应则重新连接。
- 连接失败在服务器列表中轮换，按 2、4、8、16、30 秒退避；认证失败和多次连接失败会重新获取 token。
- 请求和接收使用取消令牌。停止监听中断本地 socket，不等待服务器完成关闭握手。

## 事件兼容与开销

支持 `DANMU_MSG`（含命令后缀）、`SEND_GIFT`、`SEND_GIFT_V2`（protobuf 礼物批次）、
`SUPER_CHAT_MESSAGE`、`USER_TOAST_MSG_V2`、`INTERACT_WORD`、`INTERACT_WORD_V2`、`WATCHED_CHANGE`。
保留旧代码的 `USER_TOAST_V2` 别名兼容；上舰按新 toast 事件展示，并忽略 source=2 的重复赠送通知，
不同时展示 `GUARD_BUY`，避免同一购买的两类广播重复显示。
礼物保留礼物 ID、数量、瓜子类型、实付瓜子总额和事务 ID；仅 gold 瓜子按 1000=1 元显示金额。
弹幕与礼物缺失用户名时显示“匿名用户”。单个格式不兼容的事件跳过，继续处理同一包内的其他事件。

未压缩的帧和包直接从接收缓冲解析，压缩输入也复用原数组视图，解压临时缓冲从数组池借还；
复用事件回调，无额外信息的事件共享只读空字典，减少高流量下的临时分配。
仍使用有界消息缓冲、批量 UI 更新和悬浮层显示上限。
WebSocket 消息上限 4 MiB、单个解压包上限 16 MiB、压缩嵌套深度受限，访客 buvid 在同一客户端中复用。

本轮按用户要求不启动自动界面或直播间测试。编译检查通过不代表实时接口已验证；
匿名连接、收到的事件种类以及网络重连表现由用户在实际直播间手动确认。
