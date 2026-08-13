# PICO Unity SDK — 平台服务（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。本域为**精要模式**：每个 Service 只收录核心签名与调用模式，完整参数/错误码请走来源 URL。

## 何时加载本文档

- 接入任何 `Pico.Platform` 服务（账号、好友、成就、排行榜、IAP、DLC、云存档、房间匹配、RTC、挑战、高光时刻）之前，先确认初始化姿势与前置开关。
- 排查"接口返回 -100011 SDK not initialized / 回调不触发 / 权限弹窗反复弹 / 好友列表为空"。
- 设计商业化闭环：Add-on / SKU / 订阅 / DLC 下载与履约。
- 设计联机玩法：匹配池、房间生命周期、房间内消息收发、断线重连。
- 做合规相关工作：用户权限校验（版权保护）、敏感词检测、内容保护（防截屏录屏）。
- 需要在 PC 上调试平台服务而不想反复打 APK。

## 关键规则

| # | 规则 | 来源 |
|---|---|---|
| 1 | **平台服务仅支持开发 64 位应用**；所有平台服务接口必须在初始化成功之后再调用。 | [platform-services-overview](https://developer-cn.picoxr.com/document/unity/platform-services-overview/) |
| 2 | 同步 `CoreService.Initialize()` **失败会抛异常**，必须 `try/catch (UnityException e)`；它内部有网络请求，可能造成启动短暂卡顿。 | [initialization](https://developer-cn.picoxr.com/document/unity/initialization/) |
| 3 | 异步 `CoreService.AsyncInitialize()` 要**判两层**：先 `m.IsError`，再判 `m.Data` 必须是 `PlatformInitializeResult.Success` **或** `AlreadyInitialized`；且后续调用**必须写在 `OnComplete` 回调内部（或 `await` 之后）**——官方把"`AsyncInitialize().OnComplete(...)` 后面并列写 `UserService.GetLoggedInUser()`"明确列为错误示例。 | [initialization](https://developer-cn.picoxr.com/document/unity/initialization/) |
| 4 | 游戏模块（**房间&匹配、排行榜、成就、挑战**）需要额外调 `CoreService.GameInitialize`，且前提是已在 PICO 开发者平台**启用匹配服务**（Matchmaking）。结果为 `GameInitializeResult.Success` 才算成功。 | [initialization](https://developer-cn.picoxr.com/document/unity/initialization/) · [leaderboards-platform-service-setups](https://developer-cn.picoxr.com/document/unity/leaderboards-platform-service-setups/) |
| 5 | 用游戏模块**必须**监听 `NetworkService.SetNotification_Game_ConnectionEventCallback`：`Closed`/`GameLogicError`/`KickedByRelogin`/`KickedByGameServer` 都要**重新初始化**，`Lost` 时暂停发请求并提示"重连中"，`Resumed` 才恢复。另外应用切后台时 `popMessage` 被暂停 → 心跳断连 → **断连期间消息会丢失**。 | [initialization](https://developer-cn.picoxr.com/document/unity/initialization/) · [matchmaking](https://developer-cn.picoxr.com/document/unity/matchmaking/) |
| 6 | 异步接口两种写法：`Task<T>.OnComplete(handler)` 或（2.1.4 起）`await task.Async()` 得到 `Message<T>`。**任何回包第一步都是判 `m.IsError`**，再取 `m.Data`。 | [platform-services-overview](https://developer-cn.picoxr.com/document/unity/platform-services-overview/) |
| 7 | 用户权限要**批量一次性申请**：`UserService.RequestUserPermissions(params string[])`，入参用 `Models.Permissions` 常量（`UserInfo`/`FriendRelation`/`SportsUserInfo`/`SportsSummaryData`/`RecordHighlight`）；逐个申请会反复弹窗。`UserService.GetFriends()` 只在**双方都用过该应用且都授权好友关系**时才返回数据。 | [accounts-and-friends](https://developer-cn.picoxr.com/document/unity/accounts-and-friends/) |
| 8 | 版权保护用 `UserService.EntitlementCheck(bool killApp)`：`killApp=true` 由系统弹窗并退出；`false` 时**必须自己处理** `HasEntitlement`。付费应用不开启**无法通过审核**；要求 `targetSdkVersion >= 23`，SDK ≥ 2.1.5。用户验证（openID + Access Token 走 S2S）**不能取代**用户权限校验。 | [user-entitlement-check](https://developer-cn.picoxr.com/document/unity/user-entitlement-check/) · [accounts-and-friends](https://developer-cn.picoxr.com/document/unity/accounts-and-friends/) |
| 9 | 成就/排行榜的 **API Name 必须与开发者平台配置完全一致**，且**暂不支持在 Unity 编辑器中测试成就服务和排行榜服务**（需真机或 PC 调试工具）。 | [achievements-platform-service-setups](https://developer-cn.picoxr.com/document/unity/achievements-platform-service-setups/) · [leaderboards-platform-service-setups](https://developer-cn.picoxr.com/document/unity/leaderboards-platform-service-setups/) |
| 10 | `LeaderboardService.GetEntries` 的参数顺序是 **(leaderboardName, pageSize, pageIdx, filter, startAt)**，`pageIdx` 从 0 开始、`pageSize` 取值 [0,100]；`filter` 的 `UserIds` 与 `Unknown` 是**无效类型不返回条目**，按 ID 查请用 `GetEntriesByIds`。 | [LeaderboardService](https://developer-cn.picoxr.com/reference/unity/client-api/LeaderboardService/) · [leaderboards-parameter-details](https://developer-cn.picoxr.com/document/unity/leaderboards-parameter-details/) |
| 11 | `WriteEntry` **默认非强制更新**（只保留最好成绩），要覆盖写必须显式传 `forceUpdate = true`。每个用户在一个排行榜只有一个条目。 | [leaderboards-use-cases-and-code-samples](https://developer-cn.picoxr.com/document/unity/leaderboards-use-cases-and-code-samples/) |
| 12 | 成就三类型对应三套写法：`Count` 用 `AddCount`、`Bitfield`（完成率）用 `AddFields`（传 `"100011"` 这类 0/1 位串，**位一旦解锁不可更改**）、`Simple` 只能 `Unlock`。Count/Bitfield 达标会**自动解锁**，`Unlock` 用于提前解锁。 | [achievements-use-cases-and-code-samples](https://developer-cn.picoxr.com/document/unity/achievements-use-cases-and-code-samples/) · [AchievementsService](https://developer-cn.picoxr.com/reference/unity/client-api/AchievementsService/) |
| 13 | IAP：`GetProductsBySKU` **每批最多 20 个 SKU**，超出自行分批；`LaunchCheckoutFlow2(Product)` 的入参必须来自 `GetProductsBySKU`；**禁止在代码里写死货币单位**（非中国大陆按用户地区换算汇率）；消耗型商品服务端履约后**必须**调 `ConsumePurchase(sku)`，否则 `GetViewerPurchases` 一直返回且无法复购。 | [in-app-purchase](https://developer-cn.picoxr.com/document/unity/in-app-purchase/) |
| 14 | 可用性硬约束：中国大陆"游戏"类应用**未取版号不能用 IAP**；**订阅仅支持非中国大陆**；**高光时刻仅限上架中国大陆商店的应用**；**内容安全（敏感词检测）仅中国大陆可用**；**运动数据授权处于实验阶段**，需提交应用 ID 开通（`GetSummary` 限最近 24 小时、`GetDailySummary` 限最近 90 天）。 | [in-app-purchase](https://developer-cn.picoxr.com/document/unity/in-app-purchase/) · [subscription](https://developer-cn.picoxr.com/document/unity/subscription/) · [highlights](https://developer-cn.picoxr.com/document/unity/highlights/) · [content-detection](https://developer-cn.picoxr.com/document/unity/content-detection/) · [exercise-data-authorization](https://developer-cn.picoxr.com/document/unity/exercise-data-authorization/) |
| 15 | DLC：**PICO 商店不负责下载**，下载必须在应用内用 `AssetFileService.DownloadById/ByName` 实现；只有 **"非消耗品"** 类型 Add-on 能关联 DLC 文件；发起下载前应校验 `AssetDetails.IapStatus == "entitled"`；文件落在应用的 **OBB 目录**，卸载即删。 | [downloadable-content](https://developer-cn.picoxr.com/document/unity/downloadable-content/) |
| 16 | 云存档客户端**只有 `CloudStorageService.StartNewBackup()` 一个接口**（SDK ≥ 2.4.0）。被动备份需同时满足：平台+设备均已开启云存档、距上次成功备份 >24 小时、备份目录内容有变化、应用已关闭；单应用总备份 **≤100MiB**；只备份 4 个固定目录（及其子目录），**DLC 文件不要放进去**。 | [cloud-storage](https://developer-cn.picoxr.com/document/unity/cloud-storage/) |
| 17 | 房间：`maxUsers` 上限 **100**；房主离开后所有权自动转给在房间时间最长的用户；**所有人离开房间即销毁**；`Room.DataStore` key ≤32 字节、value ≤64 字节，`Description` ≤128 字节。匹配成功走 `Notification_Matchmaking_MatchFound` 通知，再 `RoomService.Join2` 进房。房内消息 `NetworkService.SendPacket` **单条 ≤512 字节、频率 ≤1000 次/秒**。 | [matchmaking](https://developer-cn.picoxr.com/document/unity/matchmaking/) · [RoomService](https://developer-cn.picoxr.com/reference/unity/client-api/RoomService/) · [NetworkService](https://developer-cn.picoxr.com/reference/unity/client-api/NetworkService/) |
| 18 | RTC 初始化顺序不可颠倒：**先平台 SDK 初始化成功 → 再 `RtcService.InitRtcEngine()`**，否则返回 `RtcEngineInitResult.SdkNotInitialized(-3)`；`JoinRoom` 前必须先 `GetToken`；**同一时刻只能在一个房间发布音频流**（换房先 `UnPublishRoom` 再 `PublishRoom`）；退房后还需 `DestroyRoom` 才彻底释放资源。 | [rtc](https://developer-cn.picoxr.com/document/unity/rtc/) · [RtcService](https://developer-cn.picoxr.com/reference/unity/client-api/RtcService/) |
| 19 | RTC 数值边界：音量 **0~400**（100 为原始音量）；`roomId` 为 ≤128 字节非空串，仅允许 `A-Z a-z 0-9 _ @ -`；自定义消息 ≤64KB；随帧信息 ≤255 字节；耳返默认关闭。RTC 需**手动在 AndroidManifest.xml 补齐** `RECORD_AUDIO`、`MODIFY_AUDIO_SETTINGS`、`BLUETOOTH` 等权限。 | [rtc](https://developer-cn.picoxr.com/document/unity/rtc/) |
| 20 | 高光时刻三前提缺一不可：申请 `Permissions.RecordHighlight` → 开发者平台启用服务 → Unity 内 `PICO > Platform Settings` 勾"开启高光时刻"（SDK 自动写入 `use_record_highlight_feature` metadata）。截屏/录屏前**必须先 `StartSession()`**；单次录屏最长 **15 分钟**，超时系统自动停止并回调 `SetOnRecordStopHandler`。 | [highlights](https://developer-cn.picoxr.com/document/unity/highlights/) |

### 文档内部口径冲突（照抄官方示例前先核对）

- **`LeaderboardService.GetEntries` 的示例参数顺序自相矛盾**：《排行榜-场景教学》写 `GetEntries("...", 5, 0, ...)`（pageSize=5, pageIdx=0，正确）；《成就-场景教学》同一段代码写成 `GetEntries("yourLeaderboardName", 0, 5, ...)`（等价于 pageSize=0），会取不到条目。**以 API 参考签名为准**。来源：[leaderboards-use-cases](https://developer-cn.picoxr.com/document/unity/leaderboards-use-cases-and-code-samples/) vs [achievements-use-cases](https://developer-cn.picoxr.com/document/unity/achievements-use-cases-and-code-samples/)
- **成就示例里的类名/调用名有错**：官方示例把自己的 MonoBehaviour 命名为 `AchievementsService`（与 SDK 类同名冲突），并出现 `Achievements.GetProgressByName`、`AchievmentsService.GetDefinitionsByName` 等拼写。正确类名只有 **`AchievementsService`**，且不要用它给自己的脚本命名。来源：[achievements-use-cases](https://developer-cn.picoxr.com/document/unity/achievements-use-cases-and-code-samples/)
- **同一接口在不同页拼写不一致**，均以 API 参考为准：`RtcService`（指南/PC 调试页写 `RTCService`）；`ChallengesService`（PC 调试页写 `ChallengeService`）；`MatchmakingService.Cancel`（指南写 `MatchmakingCancel2`、PC 调试页写 `Cancel2`）；`MatchmakingService.ReportResultsInsecure`（PC 调试页写 `ReportResultInsecure`）；`RoomService.SetDescription`（指南写 `RoomService.etDescription`）；`RoomService.GetJoinOrCreateNamedRoomOptions`（指南写 `GetCreateNamedRoomOptions`）。来源：[pc-end-debugging-tool](https://developer-cn.picoxr.com/document/unity/pc-end-debugging-tool/) · [matchmaking](https://developer-cn.picoxr.com/document/unity/matchmaking/) vs 各 API 参考页
- **DLC 指南提到 `GetAssetFileDownloadResult()` / `GetAssetFileDownloadCancelResult()`，但 `AssetFileService` 的成员表里没有这两个函数**；成员表提供的是 `SetOnDownloadUpdateCallback`（进度/完成状态）与各 `Download*` 的返回值。以 API 参考为准。来源：[downloadable-content](https://developer-cn.picoxr.com/document/unity/downloadable-content/) vs [AssetFileService](https://developer-cn.picoxr.com/reference/unity/client-api/AssetFileService/)
- **账号关联的 Token 有效期**：概念表写 Access Token 15 天、Refresh Token 30 天，但示例响应里 `expires_in=5184000`、`refresh_expires_in=15552000`（60 天/180 天）。以实际返回值为准，不要把天数写死。来源：[account-linking](https://developer-cn.picoxr.com/document/unity/account-linking/)
- **Platform Settings 的菜单路径两种写法**：《高光时刻》写 `PICO > Platform Settings`，《PC 端调试工具》写 `PXR_SDK > Platform Settings`。以项目实际 SDK 版本的菜单为准（两处指同一个面板：APP ID / User Entitlement Check / 开启高光时刻）。来源：[highlights](https://developer-cn.picoxr.com/document/unity/highlights/) vs [pc-end-debugging-tool](https://developer-cn.picoxr.com/document/unity/pc-end-debugging-tool/)

## 工作流程

### A. 平台服务初始化（一切平台服务的前提）

1. **配置 APP ID**：`PICO > Platform Settings` 填入开发者平台"API 测试"页的 APP ID（`Initialize` 不传 appId 时会读取该编辑器配置）。
2. **二选一初始化**：
   ```csharp
   using Pico.Platform;
   using Pico.Platform.Models;

   // 同步：失败抛异常
   try { CoreService.Initialize(); }
   catch (UnityException e) { Debug.Log($"Init Platform SDK error:{e}"); throw; }

   // 异步：判两层
   CoreService.AsyncInitialize().OnComplete(m =>
   {
       if (m.IsError) { Debug.Log($"code={m.GetError().Code} message={m.GetError().Message}"); return; }
       if (m.Data != PlatformInitializeResult.Success &&
           m.Data != PlatformInitializeResult.AlreadyInitialized) { return; }
       // 只有到这里才允许调用其它平台服务
   });
   ```
3. **批量申请权限**：`UserService.RequestUserPermissions(Permissions.UserInfo, Permissions.FriendRelation)`，回包里读 `AuthorizedPermissions`。
4. **（可选）版权校验**：`UserService.EntitlementCheck(true)`，或 `false` 时自行处理 `HasEntitlement` / `StatusMessage` / `Application.Quit()`。
5. **（游戏类服务）初始化游戏模块**：`CoreService.GameInitialize(accessToken)`（token 来自 `UserService.GetAccessToken()`）或无参 `CoreService.GameInitialize()`；随后 `NetworkService.SetNotification_Game_ConnectionEventCallback(OnGameConnectionEvent)` 处理 8 种 `GameConnectionEvent`。退出时 `CoreService.GameUninitialize()`。
6. **初始化失败兜底**：官方建议两选一——提供离线模式，或引导到"重载页面"让用户点刷新重新初始化。
   来源：[initialization](https://developer-cn.picoxr.com/document/unity/initialization/) · [CoreService](https://developer-cn.picoxr.com/reference/unity/client-api/CoreService/)

### B. 排行榜 + 成就联动（游戏模块典型闭环）

1. 开发者平台建排行榜：配 **API Name**（即代码里的 `leaderboardName`）、排序方式（越高越好/越低越好）、排序字段类型（得分/时间/距离/百分比）、数据写入权限（客户端可写/仅服务端可写）、是否关联 Destination、是否启用好友排行与超越通知。
2. 开发者平台建成就：配 **API Name**、成就类型（简单/计数/完成率）、写入权限、是否隐藏、是否启用解锁通知（Toast + 通知中心）。
3. **启用匹配服务** → `CoreService.Initialize/AsyncInitialize` → `CoreService.GameInitialize`。
4. 写分：`LeaderboardService.WriteEntry(name, score, extraData, forceUpdate)`；需要决胜局指标时用 `WriteEntryWithSupplementaryMetric(name, score, supplementaryMetric, extraData, forceUpdate)`。
5. 读榜：`GetEntries(name, pageSize, pageIdx, LeaderboardFilterType.None, LeaderboardStartAt.Top)` → 遍历 `LeaderboardEntryList`，用 `item.User.ID` 判定名次。
6. 联动解锁：命中目标名次后 `AchievementsService.Unlock(achievementName, null)`，回包 `Message<AchievementUpdate>` 用 `Data.JustUnlocked` 判定是否刚解锁。
   来源：[leaderboards-platform-service-setups](https://developer-cn.picoxr.com/document/unity/leaderboards-platform-service-setups/) · [achievements-use-cases-and-code-samples](https://developer-cn.picoxr.com/document/unity/achievements-use-cases-and-code-samples/)

### C. IAP + DLC 购买下载闭环

1. 开发者平台 `变现 > 附加内容` 创建 Add-on：定 **SKU**（创建后不可改）、类型（非消耗品/消耗品/订阅类）、发布渠道；新增版本配价格/图片/**DLC 文件**（单文件 ≤4GB，最多 25 个，并选最低兼容 Build 版本）后提交审核。
2. 应用内取商品：`IAPService.GetProductsBySKU(skus)`（≤20 个/批）→ 展示 `Product.Price` + `Product.Currency`（**不要写死币种**）。
3. 拉起支付：`IAPService.LaunchCheckoutFlow2(product)`；需要写订单备注用 `LaunchCheckoutFlow3(product, orderComment)`。
4. 查已购：`IAPService.GetViewerPurchases()`；订阅类再查 `GetSubscriptionStatus(sku)` 看 `EntitlementStatus`（`Valid`/`GracePeriod`/`Pause`/`Expired`/`Cancel`）。
5. 消耗型履约：自有服务端发放 → `IAPService.ConsumePurchase(sku)`。
6. DLC 下载：`AssetFileService.GetList()` → 判 `IapStatus == "entitled"` → `DownloadById/DownloadByName` → 进度用 `SetOnDownloadUpdateCallback`（`Transferred` 字节数 + `CompleteStatus`），取消用 `DownloadCancelById/ByName`，查状态用 `StatusById/StatusByName`（`DownloadStatus` 为 `downloaded`/`available`/`in-progress`），删除用 `DeleteById/DeleteByName`。文件被篡改时系统自动删除并回调 `SetOnDeleteForSafetyCallback`。
7. 测试：开发者账号可测未提交的 Add-on；必须绑定真实支付方式；用当地最小支付金额（如中国大陆 0.1 CNY、美国 0.01 USD）；Add-on 配置页可开关"支付测试"模拟成功/失败。
   来源：[in-app-purchase](https://developer-cn.picoxr.com/document/unity/in-app-purchase/) · [downloadable-content](https://developer-cn.picoxr.com/document/unity/downloadable-content/) · [subscription](https://developer-cn.picoxr.com/document/unity/subscription/)

### D. 房间 & 匹配 + RTC 语音

1. 平台侧：启用匹配服务 → 建匹配池（Key、最少/推荐/最多用户数、是否管理房间、匹配度阈值、匹配保留时间、建议冷却时间）→ 加自定义数据（Data Key + 类型 + 默认值）→ 加 Query（表达式 + 权重：必要=0、高=0.55、中=0.75、低=0.9）。三种模式：基本匹配 / 高级匹配 / 浏览模式。
2. 代码侧：初始化平台 + `GameInitialize` → `MatchmakingService.Enqueue2(pool, options)` 或 `CreateAndEnqueueRoom2` → `SetMatchFoundNotificationCallback` 收到匹配 → `RoomService.Join2(roomId, options)`。放弃匹配用 `MatchmakingService.Cancel()`。
3. 房间管理：`CreateAndJoinPrivate2(policy, maxUsers, roomOptions)`、`JoinOrCreateNamedRoom(...)`、`UpdateDataStore`、`UpdateMembershipLockStatus`、`UpdateOwner`、`KickUser`、`Leave`；房间事件用 `SetUpdateNotificationCallback` / `SetLeaveNotificationCallback` / `SetKickUserNotificationCallback` 等一组回调。
4. RTC：`Edit > Project Settings > Player > Publishing Settings > Build` 勾 `Custom Main Manifest` + `Custom Main Gradle Template`，在 `Assets/Plugins/Android/AndroidManifest.xml` 补 RTC 权限 → 平台初始化成功后 `RtcService.InitRtcEngine()`（返回值必须 `RtcEngineInitResult.Success`）→ `EnableAudioPropertiesReport(interval)` → `GetToken(roomId, userId, ttl, privileges)`（privileges 是 `Dictionary<RtcPrivilege, int>`）→ `JoinRoom(roomId, userId, token, RtcRoomProfileType.Game, isAutoSubscribeAudio)`（返回 0 成功，-1 参数非法 / -2 已在房 / -3 引擎为空 / -4 建房失败）→ `PublishRoom` 发言 → `LeaveRoom` → `DestroyRoom`。
   来源：[matchmaking](https://developer-cn.picoxr.com/document/unity/matchmaking/) · [rtc](https://developer-cn.picoxr.com/document/unity/rtc/)

### E. 社交互动：Presence 邀请与启动参数解析

1. 平台侧建 **Destination**：配 API Name、Deeplink Message（JSON）、是否启用 DeepLink、可见范围；并上传应用包（接受邀请时靠包名唤起应用）。
2. 设置位置：`PresenceService.Set(options)`，`PresenceOptions` 用 `SetDestinationApiName` / `SetIsJoinable` / `SetLobbySessionId` / `SetMatchSessionId` / `SetExtra`；**离开房间或退出应用时 `PresenceService.Clear()`**。
3. 发邀请：系统面板 `PresenceService.LaunchInvitePanel()` / `RoomService.LaunchInvitableUserFlow(roomId)` / `ChallengesService.LaunchInvitableUserFlow(challengeID)`；自定义面板 `PresenceService.SendInvites(userIds)` / `RoomService.InviteUser(roomId, inviteToken)` / `ChallengesService.Invite(challengeID, userIds)`（`inviteToken` 来自 `RoomService.GetInvitableUsers2` 返回的 `User.InviteToken`）。
4. 收邀请：`PresenceService.SetJoinIntentReceivedNotificationCallback` 取 `PresenceJoinIntent`（`DestinationApiName`/`MatchSessionId`/`LobbySessionId`/`DeeplinkMessage`）；房间邀请用 `RoomService.SetRoomInviteAcceptedNotificationCallback`（回调 `Message<string>`，内容是 roomId）。
5. 启动参数：**冷启动**直接 `ApplicationService.GetLaunchDetails()`；**热启动**先收 `ApplicationService.SetLaunchIntentChangedCallback` 再调 `GetLaunchDetails()`，按 `LaunchDetails.LaunchType`（`Normal`/`RoomInvite`/`Deeplink`/`ChallengeInvite`）分支处理。两者都要做。
6. 应用跳转：`ApplicationService.LaunchApp(packageName, options)` / `LaunchAppByAppId(appId, options)`，**必须传 `ApplicationOptions` 并 `SetDeeplinkMessage(...)`**；跳商店用 `LaunchStore()`（可先用 `GetVersion()` 比对 `CurrentCode < LatestCode`）。
   来源：[social-interaction-use-cases](https://developer-cn.picoxr.com/document/unity/social-interaction-use-cases/) · [social-interaction-key-concepts](https://developer-cn.picoxr.com/document/unity/social-interaction-key-concepts/)

### F. 编辑器侧开关：内容保护 与 PC 端调试

1. **内容保护**（让截屏/录屏/投屏画面变黑）：场景里选中 **`XR Origin`** → `Add Component` 加 **`PXR_Manager`** → 勾选 **`Use Content Protect`**。已知问题：**与应用空间扭曲（AppSW）同时开启会导致画面抖动和拖影**。
2. **PC 端调试**（仅 Windows）：开发者平台"API 测试"页按需勾权限并获取 Access Token → Unity 内 `PICO > PC Debug Settings` 配 `Region`（`Cn` / `I18n`）与 Access Token（写入 `Assets/Resources/PicoSdkPCConfig.json`）→ 打开对应 Demo 场景点运行。支持账号&好友、语音聊天、社交互动、多人游戏、IAP 五类服务；**所有 Notification 相关功能不可用**，**IAP 全部是模拟接口**（数据非真实）。改配置后须退出 Unity 编辑器与 Hub 并在任务管理器结束全部 Unity.exe 才生效；短时间重复调试要在关闭调试界面 **至少 5 秒**后再点运行；日志在 `/{项目文件夹}/Logs`。
   来源：[content-protection](https://developer-cn.picoxr.com/document/unity/content-protection/) · [pc-end-debugging-tool](https://developer-cn.picoxr.com/document/unity/pc-end-debugging-tool/)

## 核心 API 锚点

命名空间：`Pico.Platform`（各 Service 与 `Task<T>`/`Message<T>`）、`Pico.Platform.Models`（`User`、`Product`、`Room`、`Permissions` 等数据结构）。

### CoreService — 初始化（一切服务的前提）

```csharp
bool IsInitialized();
string GetAppID(string appId);
void Initialize(string appId);                                  // 同步，失败抛 UnityException
Task<PlatformInitializeResult> AsyncInitialize(string appId);   // 异步
Task<GameInitializeResult> GameInitialize(string accessToken);  // 游戏模块（房间/匹配/网络）
Task<GameInitializeResult> GameInitialize();                    // 无 token 重载
bool GameUninitialize();
```

### 各 Service 核心签名（精要，完整列表见来源 URL）

| Service | 核心签名（3-6 条） | 来源 |
|---|---|---|
| `UserService` | `GetLoggedInUser()` · `Get(string userId)` · `GetAccessToken()` · `GetFriends()` · `RequestUserPermissions(params string[])` · `EntitlementCheck(bool killApp)` · `GetIdToken()` · `GetOrgScopedID(string userID)` · `LaunchFriendRequestFlow(string userId)` | [UserService](https://developer-cn.picoxr.com/reference/unity/client-api/UserService/) |
| `AchievementsService` | `GetAllDefinitions(int pageIdx, int pageSize)` · `GetDefinitionsByName(string[] names)` · `GetProgressByName(string[] names)` · `AddCount(string name, long count, byte[] extraData)` · `AddFields(string name, string fields, byte[] extraData)` · `Unlock(string name, byte[] extraData)` | [AchievementsService](https://developer-cn.picoxr.com/reference/unity/client-api/AchievementsService/) |
| `LeaderboardService` | `Get(string leaderboardName)` · `GetEntries(string, int pageSize, int pageIdx, LeaderboardFilterType, LeaderboardStartAt)` · `GetEntriesAfterRank(string, int pageSize, int pageIdx, ulong afterRank)` · `GetEntriesByIds(string, int pageSize, int pageIdx, LeaderboardStartAt, string[] userIDs)` · `WriteEntry(string, long score, byte[] extraData, bool forceUpdate)` · `WriteEntryWithSupplementaryMetric(...)` | [LeaderboardService](https://developer-cn.picoxr.com/reference/unity/client-api/LeaderboardService/) |
| `ChallengesService` | `GetList(ChallengeOptions, int pageIdx, int pageSize)` · `Get(UInt64 challengeID)` · `Join/Leave(UInt64 challengeID)` · `GetEntries(UInt64 challengeID, LeaderboardFilterType, LeaderboardStartAt, int pageIdx, int pageSize)` · `Invite(UInt64, string[] userID)` · `SetChallengeInviteAcceptedOrLaunchAppNotificationCallback(Message<string>.Handler)` | [ChallengesService](https://developer-cn.picoxr.com/reference/unity/client-api/ChallengesService/) |
| `IAPService` | `GetProductsBySKU(string[] skus)` · `LaunchCheckoutFlow2(Product)` · `LaunchCheckoutFlow3(Product, string orderComment)` · `GetViewerPurchases()` · `ConsumePurchase(string sku)` · `GetSubscriptionStatus(string sku)` | [IAPService](https://developer-cn.picoxr.com/reference/unity/client-api/IAPService/) |
| `AssetFileService`（DLC） | `GetList()` · `DownloadById(ulong)` / `DownloadByName(string)` · `StatusById/StatusByName` · `DownloadCancelById/ByName` · `DeleteById/DeleteByName` · `SetOnDownloadUpdateCallback(Message<AssetFileDownloadUpdate>.Handler)` · `SetOnDeleteForSafetyCallback(...)` | [AssetFileService](https://developer-cn.picoxr.com/reference/unity/client-api/AssetFileService/) |
| `CloudStorageService` | `StartNewBackup()` —— **该类只有这一个接口** | [CloudStorageService](https://developer-cn.picoxr.com/reference/unity/client-api/CloudStorageService/) |
| `RoomService` | `CreateAndJoinPrivate2(RoomJoinPolicy, uint maxUsers, RoomOptions)` · `JoinOrCreateNamedRoom(RoomJoinPolicy, bool createIfNotExist, uint maxUsers, RoomOptions)` · `Join2(UInt64 roomId, RoomOptions)` · `Leave(UInt64)` · `UpdateDataStore(UInt64, Dictionary<string,string>)` · `KickUser(UInt64, string userId, int kickDuration)` · `SetUpdateNotificationCallback(Message<Room>.Handler)` | [RoomService](https://developer-cn.picoxr.com/reference/unity/client-api/RoomService/) |
| `MatchmakingService` | `Enqueue2(string pool, MatchmakingOptions)` · `CreateAndEnqueueRoom2(string pool, MatchmakingOptions)` · `Browse2(string pool, MatchmakingOptions)` · `Cancel()` · `StartMatch(UInt64 roomId)` · `SetMatchFoundNotificationCallback(Message<Room>.Handler)` | [MatchmakingService](https://developer-cn.picoxr.com/reference/unity/client-api/MatchmakingService/) |
| `NetworkService` | `SendPacket(string userId, byte[] bytes, bool reliable)` · `SendPacketToCurrentRoom(byte[] bytes, bool reliable)` · `ReadPacket()` · `SetNotification_Game_ConnectionEventCallback(Message<GameConnectionEvent>.Handler)` · `SetNotification_Game_StateResetCallback(Message.Handler)` | [NetworkService](https://developer-cn.picoxr.com/reference/unity/client-api/NetworkService/) |
| `RtcService` | `InitRtcEngine()` · `GetToken(string roomId, string userId, int ttl, Dictionary<RtcPrivilege,int>)` · `JoinRoom(string, string, string, RtcRoomProfileType, bool)` · `PublishRoom/UnPublishRoom(string roomId)` · `LeaveRoom(string)` · `DestroyRoom(string)` · `SetOnJoinRoomResultCallback(Message<RtcJoinRoomResult>.Handler)` | [RtcService](https://developer-cn.picoxr.com/reference/unity/client-api/RtcService/) |
| `PresenceService` | `Set(PresenceOptions)` · `Clear()` · `LaunchInvitePanel()` · `SendInvites(string[] userIds)` · `GetDestinations()` · `SetJoinIntentReceivedNotificationCallback(Message<PresenceJoinIntent>.Handler)` · `ShareVideo(string videoPath, string videoThumbPath)` · `ShareVideoByImages(List<string>)` | [PresenceService](https://developer-cn.picoxr.com/reference/unity/client-api/PresenceService/) |
| `ApplicationService` | `GetLaunchDetails()`（同步返回 `LaunchDetails`）· `SetLaunchIntentChangedCallback(Message<string>.Handler)` · `LaunchApp(string packageName, ApplicationOptions)` · `LaunchAppByAppId(string appId, ApplicationOptions)` · `LaunchStore()` · `GetVersion()` · `GetSystemInfo()`（同步返回 `SystemInfo`） | [ApplicationService](https://developer-cn.picoxr.com/reference/unity/client-api/ApplicationService/) |
| `HighlightService` | `StartSession()` · `CaptureScreen()` · `StartRecord()` / `StopRecord()` · `ListMedia(string sessionId)` · `SaveMedia(string jobId, string sessionId)` · `ShareMedia(string jobId, string sessionId)` · `SetOnRecordStopHandler(Message<RecordInfo>.Handler)` | [HighlightService](https://developer-cn.picoxr.com/reference/unity/client-api/HighlightService/) |
| `SpeechService` | `InitAsrEngine()` · `StartAsr(bool autoStop, bool showPunctual, int vadMaxDurationInSeconds)` · `StopAsr()` · `SetOnAsrResultCallback(Message<AsrResult>.Handler)` · `SetOnSpeechErrorCallback(Message<SpeechError>.Handler)` | [SpeechService](https://developer-cn.picoxr.com/reference/unity/client-api/SpeechService/) |
| `ComplianceService` | `DetectSensitive(DetectSensitiveScene scene, string content)` —— **该类只有这一个接口**，返回 `DetectSensitiveResult`（`FilteredText` + `Proposal`） | [ComplianceService](https://developer-cn.picoxr.com/reference/unity/client-api/ComplianceService/) |
| `SportService` | `GetUserInfo()` · `GetSummary(DateTime beginTime, DateTime endTime)` · `GetDailySummary(DateTime beginTime, DateTime endTime)` | [SportService](https://developer-cn.picoxr.com/reference/unity/client-api/SportService/) |
| `NotificationService` | `GetRoomInviteNotifications(int pageIdx, int pageSize)` · `MarkAsRead(UInt64 notificationID)` | [NotificationService](https://developer-cn.picoxr.com/reference/unity/client-api/NotificationService/) |

### 非 Service 类路由（账号关联 / 企业服务 / 服务端接口）

- **账号关联（SSO/OIDC）不走 Service 类，是 Web 授权流**：开发者平台"SSO配置"填 https 重定向域名 → 引导用户到 `https://$pico_auth_domain/oauth/authorize?client_key=$app_id&redirect_uri=$url` → 302 回调取 `code` → POST `.../passport/open/access_token/` 换 `access_token` + `open_id` → POST `.../passport/open/userinfo/` 取昵称头像 → POST `.../passport/open/refresh_token/` 续期。`$pico_auth_domain`：中国大陆 `openid.picovr.com`，非中国大陆 `open-global.picoxr.com`。[account-linking](https://developer-cn.picoxr.com/document/unity/account-linking/)
- **登录 Unity 游戏服务（UGS）走 OIDC**：`ApplicationService.GetSystemInfo().IsCnDevice` 选 Provider（`oidc-pico-cn` / `oidc-pico-global`）→ `UserService.GetIdToken()` → `AuthenticationService.Instance.SignInWithOpenIdConnectAsync(provider, idToken)` / `LinkWithOpenIdConnectAsync(...)`。[accounts-and-friends](https://developer-cn.picoxr.com/document/unity/accounts-and-friends/)
- **企业服务**（设备信息、设备控制、系统配置、系统开关、应用管理、投屏、大空间）是面向 PICO 企业级设备的独立接口集，类名为 **`PXR_Enterprise`**，不属于平台服务体系，也不需要 `CoreService` 初始化。[enterprise_service](https://developer-cn.picoxr.com/document/unity/enterprise_service/) · [PXR_Enterprise](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/)
- **服务端接口另有一套文档**（创建/更新成就与排行榜、写入用户进度、验证用户、查询社交关系、创建挑战、敏感词检测等），入口：[服务端 API 参考](https://developer-cn.picoxr.com/reference/unity-server/latest/)。客户端错误码总表：[platform-service-client-api-error-codes](https://developer-cn.picoxr.com/reference/unity/client-api/platform-service-client-api-error-codes/)（如 `-100011 SDK not initialized`、`-100002 user no login`、`-100007 have no this api permission`）。

### 关键枚举（原样写法）

- `PlatformInitializeResult`：`Success`(0)、`AlreadyInitialized`(-1)、`InvalidParams`(-2)、`InternalError`(-3)、`LoadImplFailed`(-4)、`MissingImpl`(-5)、`NetError`(-6)、`Unknown`(-999)
- `GameInitializeResult`：`Success`(0)、`Uninitialized`、`NetworkError`、`InvalidCredentials`、`ServiceNotAvaliable`（**官方即为此拼写**）、`Unknown`、`InvalidServerAddr`、`DupInitialize`
- `GameConnectionEvent`：`Connected`、`Closed`、`Lost`、`Resumed`、`KickedByRelogin`、`KickedByGameServer`、`GameLogicError`、`Unknown`
- `LeaderboardFilterType`：`None`(0)、`Friends`(1)、`Unknown`(2)、`UserIds`(3)｜`LeaderboardStartAt`：`Top`(0)、`CenteredOnViewer`(1)、`CenteredOnViewerOrTop`(2)
- `AchievementType`：`Unknown`、`Simple`、`Count`、`Bitfield`｜`AchievementWritePolicy`
- `AddonsType`：`Invalid`(-1)、`Durable`(0)、`Consumable`(1)、`Subscription`(2)｜`EntitlementStatus`：`None`、`Valid`、`Invalid`、`GracePeriod`、`Pause`、`Expired`、`Cancel`
- `RoomJoinPolicy`：`None`、`Everyone`、`FriendsOfMembers`、`FriendsOfOwner`、`InvitedUsers`、`Unknown`｜`RoomType`：`Unknown`、`Matchmaking`、`Moderated`、`Private`、`Named`｜`RoomJoinability`：`AreIn`、`AreKicked`、`CanJoin`、`IsFull`、`NoViewer`、`PolicyPrevents`｜`RoomMembershipLockStatus`：`Lock`/`Unlock`
- `RtcEngineInitResult`：`Success`(0)、`AlreadyInitialized`(-1)、`InvalidConfig`(-2)、`SdkNotInitialized`(-3)、`Unknown`(-999)｜`RtcRoomProfileType`：`Communication`、`LiveBroadcasting`、`Game`、`CloudGame`、`LowLatency`｜`RtcPrivilege`：`PublishStream`、`PublishAudioStream`、`PublishVideoStream`、`SubscribeStream`｜`RtcAudioScenarioType`：`Music`、`HighQualityCommunication`、`Communication`、`Media`、`GameStreaming`
- `LaunchType`：`Unknown`(0)、`Normal`(1)、`RoomInvite`(2)、`Deeplink`(4)、`ChallengeInvite`(5)（**没有 3**）
- `AsrEngineInitResult`：`Success`(0)、`AlreadyInitialized`(-1)、`InvalidConfig`(-2)、`Arch32BitNotSupported`(-3)、`Unknown`(-999)
- `DetectSensitiveScene`：`UserName`(1)、`RoomName`(2)、`RoomChat`(3)｜`AssetFileDownloadCompleteStatus`：`Downloading`、`Succeed`、`Failed`｜`ChallengeVisibility`：`InviteOnly`、`Public`、`Private`｜`ChallengeViewerFilter`：`AllVisible`、`Participating`、`Invited`、`ParticipatingOrInvited`

### 关键数据结构字段（易记错处）

- `Permissions`（常量类）：`UserInfo`、`FriendRelation`、`SportsUserInfo`、`SportsSummaryData`、`RecordHighlight`
- `PermissionResult`：`AuthorizedPermissions`、`AccessToken`、`UserID`｜`EntitlementCheckResult`：`HasEntitlement`、`StatusCode`、`StatusMessage`
- `User`：`ID`（openID）、`DisplayName`、`ImageUrl`、`InviteToken`、`PresenceDestinationApiName`、`PresenceLobbySessionId`、`PresenceMatchSessionId`、`PresenceExtra`、`PresenceIsJoinable`
- `LaunchDetails`：`LaunchType`、`DeeplinkMessage`、`DestinationApiName`、**`LobbySessionID` / `MatchSessionID`**（大写 ID）；而 `PresenceJoinIntent` 里是 **`LobbySessionId` / `MatchSessionId`**（小写 d）
- `AssetDetails`：`AssetId`、`Filename`、`Filepath`、`DownloadStatus`、`IapStatus`、`IapSku`、`Version`｜`IapStatus` 常量：`Entitled`/`NotEntitled`（字符串值 `entitled`/`not-entitled`）｜`DownloadStatus` 常量：`Downloaded`/`Available`/`InProgress`
- `Product`：`SKU`、`Price`、`Currency`、`AddonsType`、`PeriodType`、`TrialPeriodUnit`、`OuterId`｜`Purchase`：`SKU`、`ID`、`ExpirationTime`、`GrantTime`、`OuterId`
- `SystemInfo`：`ROMVersion`、`Locale`、`ProductName`、`IsCnDevice`、`MatrixVersionName`
- `Room`：`RoomId`、`DataStore`、`MaxUsers`、`RoomJoinPolicy`、`RoomJoinability`、`OwnerOptional`、`UsersOptional`（后两者**可能为 null，用前必判**）

### 菜单路径 / 编辑器配置

`PICO > Platform Settings`（APP ID、User Entitlement Check、开启高光时刻）｜`PICO > PC Debug Settings`（Region = `Cn`/`I18n` + Access Token，写入 `Assets/Resources/PicoSdkPCConfig.json`）｜`Edit > Project Settings > Player > Publishing Settings > Build`（RTC 需要的 `Custom Main Manifest` + `Custom Main Gradle Template`）｜`Edit > Project Settings > Services > Authentication`（OIDC：`oidc-pico-cn` / `oidc-pico-global`，Issuer 分别为 `https://platform-cn.picovr.com` / `https://platform-us.picovr.com`）

## DO NOT

| ❌ 错误写法 | ✅ 正确写法 |
|---|---|
| `AsyncInitialize().OnComplete(...)` 之后**并列**写 `UserService.GetLoggedInUser()` | 所有后续调用放进 `OnComplete` 回调内，或 `await task.Async()` 之后 |
| 异步初始化只判 `m.IsError` 就当成功 | 再判 `m.Data == PlatformInitializeResult.Success \|\| AlreadyInitialized` |
| `CoreService.Initialize()` 不包 try/catch | 同步初始化失败**抛异常**，必须 `catch (UnityException e)` |
| 以为 `Initialize()` 之后就能用房间/匹配/排行榜/成就 | 这四类属游戏模块，还要 `CoreService.GameInitialize`（且平台已启用匹配服务） |
| 用游戏模块却不监听网络事件 | `NetworkService.SetNotification_Game_ConnectionEventCallback`，按 `GameConnectionEvent` 分支重连或重初始化 |
| 每次要用权限时单独 `RequestUserPermissions` | 初始化完成后一次性批量申请，入参用 `Permissions.*` 常量 |
| `GetEntries(name, 0, 5, ...)` 以为是"第 0 页 5 条" | 签名是 `(name, pageSize, pageIdx, filter, startAt)`，应写 `(name, 5, 0, ...)` |
| 用 `LeaderboardFilterType.UserIds` 按 ID 过滤排行榜 | 该值无效不返回条目，改用 `LeaderboardService.GetEntriesByIds` |
| `WriteEntry(name, score, null)` 以为总会覆盖旧分 | 默认只保留最好成绩；要覆盖传 `forceUpdate: true` |
| 对 `Simple` 成就调 `AddCount` / 对 `Count` 成就只调 `Unlock` 记进度 | `Count`→`AddCount`，`Bitfield`→`AddFields`，`Simple`→`Unlock`；进度靠 Add* 累积后自动解锁 |
| 把自己的 MonoBehaviour 命名为 `AchievementsService` | 换个名字；`AchievementsService` 是 SDK 类名（官方示例此处有误） |
| 写 `Achievements.GetProgressByName` / `AchievmentsService.*` / `RTCService.*` / `ChallengeService.*` | 正确类名：`AchievementsService`、`RtcService`、`ChallengesService` |
| `LaunchCheckoutFlow2` 里手填价格/币种，或代码里写死 `CNY` | 价格与币种必须来自 `GetProductsBySKU` 返回的 `Product` |
| 一次性把 50 个 SKU 传进 `GetProductsBySKU` | 每批 ≤20 个，超出自行分批多次调用 |
| 消耗型商品发货后不调 `ConsumePurchase` | 必须履约上报，否则 `GetViewerPurchases` 一直返回且无法复购 |
| 期待 PICO 商店帮用户下载 DLC | 商店只负责购买，下载必须应用内 `AssetFileService.DownloadById/ByName` 实现 |
| 给"消耗品" Add-on 挂 DLC 文件 | 只有**非消耗品**类型的 Add-on 支持关联 DLC |
| 不校验 `IapStatus` 直接允许使用 DLC 内容 | 判 `AssetDetails.IapStatus == "entitled"`，防止非法安装绕过购买 |
| 把 DLC 大文件塞进云存档备份目录 | 备份总量 ≤100MiB，DLC 会撑爆导致备份失败 |
| 以为云存档有"上传/下载/读写"一整套 API | 客户端只有 `CloudStorageService.StartNewBackup()`，其余由系统被动完成 |
| 先 `RtcService.InitRtcEngine()` 再初始化平台 SDK | 顺序反了会得到 `SdkNotInitialized(-3)`；平台 SDK 成功后再初始化 RTC 引擎 |
| 不取 Token 直接 `JoinRoom` / 同时在两个房间 `PublishRoom` | 先 `GetToken`；换房发言前先 `UnPublishRoom` |
| `LeaveRoom` 后就认为资源已释放 | 还需 `DestroyRoom(roomId)` 才彻底释放；Token 将过期时用 `UpdateToken` 续期 |
| RTC 依赖 SDK 自动写权限 | RTC 的录音/蓝牙/网络权限需**手动**写进 `Assets/Plugins/Android/AndroidManifest.xml` |
| 单包塞 >512 字节或高频刷 `SendPacket` | 单条 ≤512 字节、频率 ≤1000/s |
| 不 `StartSession` 直接 `CaptureScreen` / `StartRecord` | 高光时刻的截屏录屏必须先 `HighlightService.StartSession()` |
| 只处理冷启动的 `GetLaunchDetails()` | 冷启动 + 热启动（`SetLaunchIntentChangedCallback` 后再取）都要处理 |
| `LaunchApp("com.x.y", null)` | 必须传 `ApplicationOptions` 并 `SetDeeplinkMessage(...)` |
| 玩家离开房间/退出应用后不清 Presence | 及时 `PresenceService.Clear()`，否则好友看到的位置是脏数据 |
| 用截屏黑屏功能时去找平台服务接口 | 内容保护在 `XR Origin` 的 `PXR_Manager` 勾 `Use Content Protect`（且勿与 AppSW 同开） |
| 在 PC 调试工具里验证通知回调或真实支付 | Notification 全部不可用、IAP 为模拟接口，需真机验证 |
