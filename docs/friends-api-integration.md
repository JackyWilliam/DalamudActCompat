# 共用好友与聊天客户端接入

本阶段提供 DACT / 后续宠物插件共用的账号、好友、在线状态与聊天契约，以及 DACT 好友抽屉、聊天窗口和官方通知。宠物 AI 和支付不在本阶段。现有“好友邀请”仍是注册激活码邀请，与新增好友申请分开。

## 代码入口

- `Infrastructure/Cloud/CloudFriendsApi.cs`：HTTP 调用和 DTO。
- `Infrastructure/Cloud/CloudClientFriends.cs`：使用当前已验证登录账号的服务封装；拒绝未完成验证的保存令牌，丢弃退出/切换账号后的旧响应。
- `Infrastructure/Cloud/CloudFriendConnection.cs`：插件连接心跳、兼容退避和关闭清理。
- `CloudClientService`：验证登录后启动连接；退出、封禁、令牌失效、卸载时取消。
- `CloudChatPolicy.Notice`：当前用户提示，显示于设置与账号页，并由服务端快照返回。

业务界面优先调用 `CloudClientService.ListFriendsAsync`、`LookupFriendAsync`、`RequestFriendAsync`、`AcceptFriendAsync`、`DeclineFriendAsync`、`RemoveFriendAsync`、`SyncChatAsync`、`GetChatAsync`、`SendChatAsync`、`AcknowledgeChatAsync`。直接 HTTP 客户端用于其他共享账号客户端或隔离测试；不要把登录令牌交给视图层。

## 好友与在线

所有用户接口复用账号 Bearer session。基址沿用当前云服务设置，路径以 `api/v1/` 开头：

| 操作 | HTTP 接口 |
|---|---|
| 列表 / 在线数 / 申请 | GET `friends` |
| 按完整账号名查找 | POST `friends/lookup`，`{username}` |
| 添加 | POST `friends/requests`，`{username}` |
| 接受 / 拒绝 | POST `friends/requests/{id}/accept` 或 `/decline`，`{}` |
| 解除 | DELETE `friends/{relationshipId}`，`{}` |
| 在线 / 断开 | PUT / DELETE `friends/presence`，`{clientId}` |

关系中的 `direction` 是相对当前账号的 `incoming/outgoing`；已接受关系包含 `conversationId`。双向主动添加会接受已存在的申请；查找不会自动接受。重复请求返回已有状态。解除会删除该好友会话及消息；重新添加得到新关系与空会话，旧请求 ID 不能影响新关系。

在线来自真实客户端心跳。每次连接用新 UUID，25 秒续约，90 秒未续约则离线。多客户端/多会话取任一有效连接；有效令牌本身不代表在线。关闭只清理自己的连接，不会踢掉另一个插件。每个心跳请求最多 5 秒，关闭清理最多 2 秒。服务端重启后由下一次心跳重建在线状态。

客户端连接旧版服务时，好友 404 不影响登录和配置备份，在线探测每 5 分钟重试。暂时网络失败每 25 秒重试且不注销账号。401 和账号/机器封禁仍执行原有登录失效及封禁处理。客户端未新增高频日志。

服务端初始资源上限为每账号 200 好友、100 条待处理申请、16 个在线连接；查找 20 次/分钟、关系变更 60 次/分钟、发送 120 次/分钟。429 暴露 `CloudApiException.RetryAfterSeconds`，由界面给出稍后重试提示。

## 消息同步与数量限制

`SyncChatAsync` → 各会话摘要和给当前账号的离线待收消息；比较摘要 `Revision` 后用 `GetChatAsync` 获取变化会话。摘要含 `Kind`、`Peer`、`HistoryCount`、`PendingCount`、`LatestMessageId`。完整快照含 `History`、`Pending`、`NextSendSequence`。消息 ID 顺序稳定，时间为 UTC。

- 每对好友双方合计保留最近 **20 条历史**。
- 每对好友双方合计保留最新 **3 条离线待收**；超出实际删除，剩余没有时间有效期。
- 对方在线时新消息直接进入历史；离线消息由接收方消费快照后 ACK，并入历史、再裁剪到 20 条。
- GET 和心跳不会消耗离线消息。仅 FriendsUiManager 注册的活跃消费者在收到有界模型后 ACK；关闭聊天窗保留该消费者，卸载时解除注册。
- `DeliveredAt` 代表进入可用历史的时间，不是已读回执。

界面以新快照替换旧内容，同样最多 20 历史和 3 待收，不建立长期本机聊天正文存档。ACK 最多包含 3 个发给自己的 ID；重复 ACK 安全。若消息在读取与确认之间已被裁剪，返回的 `RetiredIds` 表示它已不再保留，以返回快照校正本地状态，不能重新插入。已读标记是当前电脑当前账号的 ID 水位，仅在对应聊天获得焦点并滚动到底部时更新；不作为双方已读回执，不同步到其他电脑。

## 界面与本机可靠性

主窗口云状态右侧显示好友图标与在线好友数，未读消息/申请单独显示红点。抽屉支持完整账号查找、申请/接受/拒绝和解除关系；聊天窗独立移动。官方身份来自服务端 kind，官方会话为只读。来信只做短暂轻提示，不自动打开聊天或请求焦点；点击入口/提醒后才展开。

FriendsChatController 的单独工作线程处理网络和磁盘，帧线程仅入队和读取不可变快照。每次请求同时携带当前账号代次，在 CloudClientService 捕获凭据的锁内校验，切换账号时旧请求不能使用新凭据，旧响应也不会显示。

发送先取服务器序号，把原序号/UUID/正文写入 DPAPI CurrentUser 加密发件箱，再进行 HTTP 请求。准备失败保留编辑草稿；结果未知时保留原请求，重启仅恢复待确认状态、不自动重发。重试使用原请求；确认已在服务端历史中时清除发件箱。用户可明确放弃，但会告知原消息可能已送达。序号被裁剪后不恢复正文、不自动另起发送。共享配置目录的多进程写入由文件锁和增量合并保护，同一会话出现冲突时禁止覆盖其他窗口待确认发送。

本机 friends-state 只保存最多 201 个会话的待确认发送和已读 ID，不保存完整接收正文，且不在配置云备份白名单内。每文件上限 2 MiB，账号按服务端 UUID 分隔；状态损坏时报告读取错误并停止消费，不静默清空可能尚未确认的发送。

## 发送与重试

从会话读取 `NextSendSequence`，生成 `Guid.NewGuid()`，创建不可变 `CloudChatSendRequest`。普通消息仅填 `Text`；快捷消息仅填 `QuickMessageId`：

```csharp
var view = await cloud.GetChatAsync(conversationId, cancellationToken);
var pending = new CloudChatSendRequest(
    view.NextSendSequence!.Value, Guid.NewGuid(),
    QuickMessageId: CloudChatPolicy.InviteNext);
// 保存 pending，直到收到明确结果；网络错误后重试仍使用这个对象。
var sent = await cloud.SendChatAsync(conversationId, pending, cancellationToken);
```

`invite_next` 发送“下把邀我”，`when_finished` 发送“你什么时候结束”，它们是普通聊天文本，不会执行游戏邀请。正文最多 2,000 个 Unicode 码点，不能空白；服务端拒绝伪造发送者字段。

网络断开、超时、5xx 或后续重试遇到 429 时，原请求可能已提交。必须重用**相同序号、操作 UUID 和内容**，不得自动换新序号重发。保留窗口内的重复请求返回同一个消息和 `Duplicate=true`。

`CloudApiException.NextSendSequence` 保留服务端同步提示：`sequence_conflict` 表示另一个操作占用该序号；`sequence_gap` 表示跳号；`sequence_retired` 表示该序号已处理且正文已经被裁剪。后者不能恢复原文，也不能静默重新发送。多个插件共用一个账号时，竞争同一序号仅有一个不同操作成功；冲突后同步并让用户明确决定下一次新发送。

服务端仅保留每个发送者的一条序号水位，不保存无限的消息回执或正文。幂等保证针对完整原请求，不针对更换序号后的旧 UUID。

## 官方消息与用户提示

官方会话 `Kind == "official"`，消息 `Sender.IsOfficial == true`，名称为“DACT 官方 / 管理员”，用户 ID 为空。必须根据服务端身份字段识别，不能通过昵称判断。普通客户端可读取与 ACK 自己的官方通知，不能冒充官方发信；这类会话的 `NextSendSequence` 为空。管理端直接发信不需要与用户先成为好友，默认同样使用 20/3 限制。

聊天消息由服务端保存以供管理员人工核查；配置备份仍由客户端加密，两者契约不同。本阶段没有自动违规检测。当前提示原文：

> 请勿利用本功能从事洗钱、刷单、诈骗等违规违法活动。涉嫌违规的消息将由管理员核查处理。

## 验证与后续接入

`CloudFriendsSmokeTests` 已纳入 PackageSmokeTests；`--friends-only` 可单独运行序列化/错误处理、会话隔离、连接退出和旧服务/网络失败兼容测试。私有服务测试可通过隔离环境变量启动 C# 真实 HTTP 联调：覆盖好友全流程、20/3、重试、ACK、快捷消息、官方身份；另用升级前服务源码验证登录、加密备份上传/下载/解密和 404 退避。

另有真实调度竞态回归：初始化期间，心跳先返回 401、备份请求后返回旧成功时，界面仍保持登录失效。`SetSignedIn` 同时核对当前令牌和封禁状态，不能靠过期的刷新结果恢复访问。

发布按先服务端后客户端推进，反向升级也已做兼容验证；好友功能整合于 DACT 0.4.1.0。`--friends-ui-only` 验证消费/未读、原请求跨重启重试、磁盘错误、账号切换与共享配置保护；`--friends-native-only` 配合 DACT_TEST_CIMGUI 运行真实 cimgui 顶栏/抽屉/聊天绘制、窗口边界、缩放及焦点检查，明确属于游戏外验证。RPets 与 DACT 两客户端的登录、退出、切号及启动顺序已通过隔离联调；游戏内窗口拖动、实际输入、战斗焦点、实际提示音和不同客户端体验仍需实际验收。
