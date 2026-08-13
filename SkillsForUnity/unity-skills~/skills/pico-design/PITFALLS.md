# PICO Unity SDK — 幻觉陷阱清单（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。本文是 pico-design 模块的防幻觉总闸：收录 AI 训练记忆最容易出错的旧 API、语义陷阱、互斥约束与官方文档自身矛盾。逐域细节见各域文档；**废弃 API 的完整清单以 [VERSIONS.md](./VERSIONS.md) 为单一事实源**。

## 一、废弃 / 更名 API 黑名单（AI 记忆重灾区）

写代码前先查此表——左列是 AI 训练语料里常见但**在 3.4.0 已废弃或更名**的写法。

| 记忆中的旧写法 | 3.4.0 现实 | 来源 |
|---|---|---|
| `PXR_EyeTracking.GetCombineEyeGazeVector` / `GetCombineEyeGazePoint` 等整类 | `PXR_EyeTracking` 整类 legacy，现行眼动接口为 `PXR_MotionTracking.StartEyeTracking` / `GetEyeTrackingData` | [PXR_EyeTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_EyeTracking/) |
| `PXR_Input.SetControllerVibration` / `StartVibrateBySharem` / `StartVibrateByPHF` / `CreateHapticStream` 等旧振动全组（3.2.0 一次性废弃 30 个成员） | 现行触觉接口：`PXR_Input.SendHapticImpulse`（缓冲类走 `SendHapticBuffer`） | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) · [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| `PXR_MixedReality.CreateAnchorEntity` / `PersistAnchorEntity` / `LoadAnchorEntityByUuidFilter` / `StartSpatialSceneCapture` 等 AnchorEntity 全套（16 个） | 3.0.0 起改为 `*Async` 单调用（`CreateSpatialAnchorAsync` 等），且一切锚点/场景操作前必须 `StartSenseDataProvider` | [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) · [MR.md](./MR.md) |
| `PXR_System.EnableFaceTracking` / `EnableLipSync` / `GetFaceTrackingData` + 亮度四件套 `SetCommonBrightness` 等 | 3.2.0 已废弃；面部追踪见下文"官方矛盾"节——**语料未给出替代类，勿编造** | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| `PXR_Boundary.UseGlobalPose` | 3.2.0 在 PXR_Boundary 废弃，3.3.0 **搬家**到 `PXR_Enterprise`（企业设备专用） | [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| `PXR_Input.GetMotionTrackerBattery` / `GetFitnessBandBattery` / `GetFitnessBandConnectState` / `SetSwiftMode` 等体感设备组 | 3.2.0 从 PXR_Input 废弃，同版本迁入 `PXR_MotionTracking` | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 菜单路径 `PXR_SDK > ...` | 3.1.0 起 Unity 顶部菜单为 **`PICO`**；所有 `PXR_SDK >` 路径都是旧版 | [v3.1.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-310/) |
| 组件名 `PXR_Over Lay (Script)` | 3.3.0 起显示名为 **`PXR_Composition Layer`**（脚本文件仍是 `PXR_OverLay.cs`），v3.4.0 部分文档页残留旧称 | [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| PICO Unity **OpenXR** SDK ≤1.4.0 的 `PICOScreenFade` / `EnableSeeThroughManual` / `TryGetSupportedDisplayRefreshRates` / `GetDisplayRefreshRateCount` | 迁移后：`PXR_ScreenFade` / `EnableVideoSeeThrough` / `GetDisplayFrequenciesAvailable`；枚举/结构体统一入 `pxr` 命名空间（`PXR_Type.cs`） | [OpenXR 插件](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) |
| 2.5.0 时代 MR 事件三段式（`CreateAnchorEntity` + 监听 `AnchorEntity*` 事件 + `GetAnchorEntityLoadResults`） | 3.0.0 全部废弃；PICO 4 支持矩阵也变了（2.5.0 MR 接口 PICO 4 Ultra 不支持） | [兼容与迁移](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/) |

## 二、签名与语义陷阱（方法存在，但 AI 极易用错）

| # | 陷阱 | 来源 |
|---|---|---|
| 1 | `PXR_MotionTracking` 的方法返回 **int**（`TrackingStateCode`：0=`PXR_MT_SUCCESS`、-1=`PXR_MT_FAILURE`、-5=眼动权限被拒），不是 bool。`if (GetEyeTrackingData(...))` 直接编译失败，判断写 `== 0` | [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 2 | `PXR_FoveationRendering.SetFoveationLevel` 是**两参** `(FoveationLevel level, bool isETFR)`；且 `FoveationLevel.None = -1` 不是 0——用 0 当"关闭"会误设成 Low | [PXR_FoveationRendering](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_FoveationRendering/) |
| 3 | `PXR_Boundary.SetGuardianSystemDisable(bool value)` 方法名与参数语义**相反**（文档：true=enable / false=disable），凭方法名直觉会把边界开关写反 | [PXR_Boundary](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Boundary/) |
| 4 | `PXR_System.GetSensorStatus()` 返回 0=null / 1=3DoF / **3**=6DoF（没有 2）；`SetSystemDisplayFrequency` 只接受 72/90/120，传 60/144 无效 | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 5 | `LeaderboardService.WriteEntry` 默认只保留最好成绩：新成绩更差时**静默丢弃**，覆盖写必须显式 `forceUpdate = true` | [排行榜](https://developer-cn.picoxr.com/document/unity/leaderboard/) |
| 6 | 体感追踪器 `localLocation` 是**右手坐标系**：分量直接塞进 Unity Transform 会镜像/朝向错，必须 `.ToVector3()` / `.ToQuat()` 转换 | [体感追踪器](https://developer-cn.picoxr.com/document/unity/object-tracking/) |
| 7 | 非缓冲触觉**没有 Stop 接口**：停止振动 = 再调 `SendHapticImpulse` 且振幅、时长均为 0；振动频率越高振感越**小**（50~500Hz，反直觉） | [触觉反馈](https://developer-cn.picoxr.com/document/unity/haptic-feedback/) |
| 8 | 眼动追踪必须先 `StartEyeTracking` 再 `GetEyeTrackingData`，且**权限弹窗在 Get 时才出现**；目前仅支持 `EyeTrackingMode.PXR_ETM_BOTH` | [眼动追踪](https://developer-cn.picoxr.com/document/unity/eye-tracking/) |
| 9 | 面部追踪 `FaceTrackingStopInfo.pause = 0` 的语义是"**暂停**"而非"不暂停"，官方快速切模式流程正依赖 pause=0 | [面部追踪](https://developer-cn.picoxr.com/document/unity/face-tracking/) |
| 10 | 删除空间锚点顺序：**先 `UnPersistSpatialAnchorAsync` 再 `DestroyAnchor`**；颠倒会丢 handle，磁盘上的锚点从此找不回 | [空间锚点](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 11 | anchor handle **重启即失效**，跨会话只能存 UUID 再 `QuerySpatialAnchorAsync` 换新 handle；该方法与 `UnPersistSpatialAnchorAsync` 都**不支持并发**，必须串行 | [空间锚点](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 12 | `LocateAnchor` 对场景锚点**不是实时的**：只有再次 `QuerySceneAnchorAsync` 后位姿才更新，每帧调用只会拿旧值（空间锚点才是实时的） | [场景标定](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 13 | `CoreService.AsyncInitialize()` 要判两层：先 `m.IsError`，再判 `m.Data` 是 `Success` **或 `AlreadyInitialized`**；房间/匹配/排行榜/成就/挑战还需要额外 `CoreService.GameInitialize` | [平台服务](https://developer-cn.picoxr.com/document/unity/initialize-the-platform-services/) |
| 14 | 消耗型商品发货后必须 `IAPService.ConsumePurchase(sku)`，否则 `GetViewerPurchases` 一直返回该商品且用户无法复购；`GetProductsBySKU` 每批最多 20 个 SKU | [应用内购](https://developer-cn.picoxr.com/document/unity/in-app-purchase/) |
| 15 | 云存档客户端**只有 `CloudStorageService.StartNewBackup()` 一个接口**（没有读写/上传/下载 API）；总量 ≤100MiB、只备份 4 个固定目录，放 DLC 文件进去会导致备份失败 | [云存档](https://developer-cn.picoxr.com/document/unity/cloud-storage/) |
| 16 | `RtcService.InitRtcEngine()` 必须在平台初始化成功后调用（否则 `SdkNotInitialized(-3)`）；`JoinRoom` 前先 `GetToken`；同一时刻只能 `PublishRoom` 一个房间 | [RTC](https://developer-cn.picoxr.com/document/unity/rtc/) |
| 17 | 应用切后台时 `popMessage` 暂停、心跳断开，断连期间消息**全部丢失**；必须监听 `NetworkService.SetNotification_Game_ConnectionEventCallback` 处理 Closed/KickedByRelogin 等事件 | [房间&匹配](https://developer-cn.picoxr.com/document/unity/matchmaking/) |

## 三、配置与互斥约束（两个都对，同开就错）

| # | 互斥/约束 | 来源 |
|---|---|---|
| 1 | **XR 插件二选一**：同时勾选 PICO XR 插件与 Unity OpenXR 插件时只有 PICO XR 生效；走 OpenXR 路线必须只启用 OpenXR，且勾选其他厂商插件会导致应用无法运行 | [项目设置](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) |
| 2 | **FFR 与 ETFR 互斥**且不能一步切换：先 `SetFoveationLevel(FoveationLevel.None, 当前isETFR)` 关闭再设目标；FFR→ETFR 第二次调用可能失败需第三次 | [ETFR](https://developer-cn.picoxr.com/document/unity/eye-tracked-foveated-rendering/) |
| 3 | 超分辨率与锐化**同一 Eye Buffer 不能同开**（同开只生效超分）；两者都与下采样 Subsampling 互斥；超分只作用于 Eye Buffer 不支持合成层；同一合成层的超采样/锐化/超分也只能开一种 | [超分辨率](https://developer-cn.picoxr.com/document/unity/super-resolution/) |
| 4 | **AppSW × Optimize Buffer Discards = 画面撕裂**（AppSW 需要深度缓冲而后者丢弃它）；**AppSW × 内容保护 = 抖动拖影**；**Late Latching × 合成层 = 层抖动**，且 Late Latching 暂不支持 Unity 2022 | [AppSW](https://developer-cn.picoxr.com/document/unity/application-spacewarp/) · [Late Latching](https://developer-cn.picoxr.com/document/unity/late-latching/) |
| 5 | URP 开 FFR 默认失效（Intermediate Texture 抢占 Eye Buffer）：必须禁用 Post-Processing / HDR / 相关 Renderer Feature | [FFR](https://developer-cn.picoxr.com/document/unity/fixed-foveated-rendering/) |
| 6 | URP 的 `pipelineAsset.renderScale` 会让自适应分辨率的 Max Adaptive Resolution Scale 失效（官方已知问题）；动态调分辨率应改 `XRSettings.renderViewportScale`（0–1，不重分配纹理）而非 `eyeTextureResolutionScale`（每次改都重分配、>2.0 无效） | [自适应分辨率](https://developer-cn.picoxr.com/document/unity/adaptive-resolution/) · [渲染视口调节](https://developer-cn.picoxr.com/document/unity/render-viewport-scaling/) |
| 7 | 合成层数量：性能建议 ≤4；`PXR_Composition Layer` 单场景上限 7；VR 合成层硬上限 15，超出**直接不渲染**；每场景 Equirect 层和 Cylinder 层各最多 1 个 | [合成层概览](https://developer-cn.picoxr.com/document/unity/compositor-layer-overview/) · [FAQ](https://developer-cn.picoxr.com/document/unity/max-vr-compositor-layers-supported/) |
| 8 | Underlay 不是拖进场景就显示：依赖渲染目标 alpha，需 `PXR_UnderlayHole` shader 在 Eye Buffer 挖洞；URP 下还必须禁用 HDR | [合成层参数](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) |
| 9 | 视频透视四前置：禁用所有后处理；Vulkan+URP/Built-in 还要禁 HDR；主相机 Clear Flags=Solid Color；Background RGBA 全 0。`EnableVideoSeeThrough` 生效有延迟，精确状态监听 `VstDisplayStatusChanged`；应用 resume 后需重新调用 | [视频透视](https://developer-cn.picoxr.com/document/unity/seethrough/) |
| 10 | **手柄与裸手不能同时追踪**：`Hand Tracking Support = Controller And Hands` 是自动切换不是并存，"一手手柄一手裸手"的设计必然失败 | [手势追踪](https://developer-cn.picoxr.com/document/unity/hand-tracking/) |
| 11 | 全身动捕与体感追踪器独立追踪**互斥**（共用 Body Tracking 勾选框）；`High Frequency Tracking (60Hz)` 启用后**运行时无法关闭** | [身体追踪](https://developer-cn.picoxr.com/document/unity/body-tracking/) |
| 12 | SecureMR：全局 tensor 不能直接绑 operator（必须 pipeline 内 `CreateTensorReference` 占位 + 提交时映射）；glTF tensor 只能是全局 tensor；operand/result 名**带空格且大小写敏感**（如 `"texture ID"`、`"world pose"`），写驼峰绑定失败 | [SecureMR](https://developer-cn.picoxr.com/document/unity/securemr-key-concepts/) |
| 13 | SecureMR Readback 权限是 **session 级**：session 内任一 pipeline 用了相机类 operator（即使从不执行），整个 session 就必须已授予相机权限 | [Readback Tensor](https://developer-cn.picoxr.com/document/unity/use-the-readback-tensor/) |

## 四、构建与工程陷阱

| # | 陷阱 | 来源 |
|---|---|---|
| 1 | Unity 2022 下 **Vulkan + Development Build 稳定崩溃**；启用 Minify 相关选项也会崩溃 | [已知问题](https://developer-cn.picoxr.com/document/unity/known-issues/) |
| 2 | Release 包卡 Loading 界面：根因是 Java 侧 minifyRelease 剔除代码（不是 IL2CPP 裁剪）——Custom Proguard File 中 keep `com.psmart.aosoperation.**` 与 `com.pxr.xrlib.**` | [FAQ](https://developer-cn.picoxr.com/document/unity/stuck-on-loading-screen-when-running--demo-built-with-release-mode/) |
| 3 | 包名 8 个被禁前缀（`com.unity.` / `com.DefaultCompany.` / `com.test.` / `com.vr.` / `com.xr.` 等）无法上架；Unity 模板默认包名还会触发"版权校验不通过：签名非法" | [包名规范](https://developer-cn.picoxr.com/document/unity/naming-conventions-for-app-package-name/) |
| 4 | 设备系统 5.11.0 起**一个签名最多关联 50 个 APK**，超限 APK 无法运行 | [FAQ](https://developer-cn.picoxr.com/document/unity/number-of-apks-associated-with-a-key-exceeds-limit/) |
| 5 | PICO 4 Ultra（Android 14）Target API Level >32 时 Unity 外部存储读写失效；`MANAGE_EXTERNAL_STORAGE` 无法用 `Permission.RequestUserPermission()` 申请（要么 Target API ≤32，要么 Java 插件跳系统授权页；后者 Project Validation 仍报错属预期） | [读写权限](https://developer-cn.picoxr.com/document/unity/set-up-read-and-write-permission-for-pico-4-ultra/) |
| 6 | 弹系统 UI/失焦后追踪停止不是 bug：新 Input System 的 `Tracked Pose Driver (Input System)` 行为——勾 Run in Background **并且** Input System 的 Background Behavior 设为 Ignore Focus | [FAQ](https://developer-cn.picoxr.com/document/unity/tracking-is-disabled-on-application-focus-lost/) |
| 7 | 长按 Home Recenter 失效：XR Origin 缺 `PXR_Manager` 或 Tracking Origin Mode 不是 Device/Floor，属场景配置问题不是代码问题 | [FAQ](https://developer-cn.picoxr.com/document/unity/app-recenter-failure/) |
| 8 | SDK 3.1.0 起仅支持 64 位：IL2CPP + 仅 ARM64；PXR_Manager 必须挂**每一个场景**（含加载场景）；`pvr.app.type=vr` 与 `pvr.display.orientation=180` 两条 metadata 需手动添加 | [SETUP.md](./SETUP.md) |
| 9 | SDK 历史版本**没有公开下载入口**（消费者商店必须用最新版；企业场景联系企业支持获取）；SDK Demo 不随包分发，在 GitHub `Pico-Developer` 组织单独下载 | [FAQ](https://developer-cn.picoxr.com/document/unity/where-to-download-an-older-version-of-sdk/) |
| 10 | PICO 设备默认只出 INFO 日志，抓 DEBUG 需 `adb shell setprop persist.log.tag V`；设备日志开关打开后**必须重启设备**，日志在内部存储 `logcatch`（不是 logcat）文件夹 | [FAQ](https://developer-cn.picoxr.com/document/unity/how-to-get-debug-logs/) |

## 五、官方文档自身矛盾与笔误（连原文都不能盲信的地方）

| # | 矛盾/笔误 | 处置建议 | 来源 |
|---|---|---|---|
| 1 | 面部追踪：v3.4.0《面部追踪》指南仍教 `PXR_MotionTracking.StartFaceTracking` 等，但同版本 API 参考已全部标 `(Deprecated)`（PXR_System 与 PXR_MotionTracking 两套都废弃），3.2.0 changelog 也列为废弃 | 语料未给出替代类。可沿用指南写法但注明废弃状态，**勿编造"新面部追踪 API"** | [面部追踪](https://developer-cn.picoxr.com/document/unity/face-tracking/) |
| 2 | XRI 版本口径冲突：《创建一个 XR 场景》称暂不支持 XRI 3.x，《PICO Building Blocks》称 3.1.0+ 基于 XRI 3.x 开发 | Building Blocks 用 XRI 3.x；手搭场景保守用 XRI 2.x；注意 XRI 3.x 的 XR Origin 无左右手柄预置 | [Building Blocks](https://developer-cn.picoxr.com/document/unity/pico-building-blocks/) |
| 3 | `QuerySpatialAnchorObjectsAsync` 在官方 API 页的小节标题被截断成 `QuerySpatialAnchorObjectsAsyn` | 以代码签名为准，照抄标题会写出不存在的方法 | [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 4 | v3.2.0 changelog"`ExtDevBatteryAction` 已废弃，替换为 `ExtDevBatteryAction`"是自我替换笔误 | 按上下文应为 `ExpandDeviceBatteryAction` | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 5 | 官方成就示例把自己的 MonoBehaviour 命名为 `AchievementsService`（与 SDK 类同名），且出现 `Achievements.GetProgressByName`、`AchievmentsService`（拼写错）等错误类名 | SDK 真实类只有 `Pico.Platform.AchievementsService`，示例中同名脚本请改名 | [成就](https://developer-cn.picoxr.com/document/unity/achievements/) |
| 6 | 官方《成就-场景教学》排行榜示例 `GetEntries(name, 0, 5, ...)` 的第二参是 pageSize=0，**取不到任何条目** | 正确签名 `(leaderboardName, pageSize, pageIdx, filter, startAt)`，pageSize 传正数 | [排行榜](https://developer-cn.picoxr.com/document/unity/leaderboard/) |
| 7 | SecureMR 双套命名并存（`RenderTextConfiguration`+`FontTypeFace` vs `RenderTextOperatorConfiguration`+`SecureMRFontTypeface`）；`ArithmeticComposeOperator` 的 operand 名两处文档分别写 `"operand0"` 与 `"{0}"` | 以《完整可运行示例》页代码为准 | [SecureMR](https://developer-cn.picoxr.com/document/unity/securemr-operators/) |
| 8 | body-tracking 指南与 API 参考的枚举/类型名不一致（`BODY_JOINT_SET_BODY_FULL_START` 系 vs API 参考命名）；face-tracking 指南对 `PXR_FTM_FACE_LIPS_VIS` 的模式解释与 API 枚举注释冲突 | 以 API 参考的枚举定义为准 | [身体追踪](https://developer-cn.picoxr.com/document/unity/body-tracking/) |
| 9 | 官方 Building Blocks 页把 Unity 组件 `Tracked Device Graphic Raycaster` 误写为 `Tracker Device Graphic Raycast` | Unity 真实组件名为 `Tracked Device Graphic Raycaster` | [Building Blocks](https://developer-cn.picoxr.com/document/unity/pico-building-blocks/) |

## 六、能力边界（AI 常见的过度承诺）

| # | 边界 | 来源 |
|---|---|---|
| 1 | PICO SDK **仅支持 Android**，不支持 PC VR 应用开发 | [FAQ](https://developer-cn.picoxr.com/document/unity/is-desktop-app-dev-supported/) |
| 2 | SDK 没有内置 Avatar 全身 IK 方案（官方指路第三方 Final IK）；场景交互官方路线是 **XR Interaction Toolkit**（或 VRTK），不存在"PICO 专有交互组件套装" | [FAQ](https://developer-cn.picoxr.com/document/unity/how-to-achieve-user-scene-interaction/) |
| 3 | PICO 商店**不负责下载 DLC**：应用内用 `AssetFileService.DownloadById/ByName` 自行实现，仅"非消耗品" Add-on 能关联 DLC 文件 | [DLC](https://developer-cn.picoxr.com/document/unity/downloadable-content/) |
| 4 | `PXR_Enterprise` 的 257 个方法**仅企业设备可用**，且必须 `InitEnterpriseService` → `BindEnterpriseService` 后才能调用；普通消费者设备（PICO 4/4 Ultra 零售版）不可用 | [PXR_Enterprise](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/) |
| 5 | AR Foundation 路线**不支持图片追踪（Image Tracking）**，且版本强绑定（SDK 3.0.x→Unity 2022.3+ARF 5.1；SDK 3.1.0→Unity 6+ARF 6.0） | [AR Foundation](https://developer-cn.picoxr.com/document/unity/ar-foundation-for-pico-unity-integration-sdk/) |
| 6 | 空间锚点建议放头戴 3 米内、找回半径上限 5 米；空间网格实时读取只覆盖头戴周围约 5 米，更大范围需应用自行存储 | [空间锚点](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [空间网格](https://developer-cn.picoxr.com/document/unity/spatial-mesh/) |
| 7 | Access Token 从开发者平台控制台获取（平台服务 > API 测试），**没有运行时 C# 接口**直接返回它 | [FAQ](https://developer-cn.picoxr.com/document/unity/how-to-get-app-access-token/) |

## 使用方式

- 生成任何 PICO 代码前：先扫"一、黑名单"确认没在用废弃 API，再查对应域文档核对签名。
- 版本迁移（2.x→3.x、跨 3.x 小版本）：读 [VERSIONS.md](./VERSIONS.md) 的逐版本方法级增删表。
- 语料中查不到的 API：宁可告诉用户"官方文档未记载"，也不要按记忆补全。
