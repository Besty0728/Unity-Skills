# PICO Unity SDK — 版本差异与迁移（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。
> 方法级增删数据来自各版本 API 参考页对比，复核方式：同一页面加 `?v=<版本号>`，例如 `https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/?v=3.1.0` 与 `?v=3.2.0`。

## 何时加载本文档

- 要把老项目的 PICO Unity Integration SDK 从 2.x 升到 3.x，或在 3.x 内部跨版本升级。
- 代码里某个 PICO API 编译不过 / IDE 划删除线，需要确认它是被**删除**、被标 **(Deprecated)**、还是被 changelog 标"不支持"。
- 要判断"某功能最早从哪个 SDK 版本开始有"，避免把新功能写进老 SDK 的项目。
- 要接入 Unity OpenXR 插件，或从 PICO Unity OpenXR SDK 迁到 PICO Unity Integration SDK。
- 要核对 SDK 版本与设备系统版本（PUI）、Unity 版本、设备型号的搭配要求。
- 写代码时对某个 API 名字"有印象但不确定"——本文的移除/废弃/改名清单就是用来推翻错误记忆的。

## 关键规则

| # | 规则 | 来源 |
|---|---|---|
| 1 | 当前锚定版本 **v3.4.0，上线 2026 年 02 月 27 日**。官方只公开提供最新版 SDK 下载；历史版本需联系 PICO 企业支持团队获取，为消费者商店开发必须用最新版。 | [v3.4.0](https://developer-cn.picoxr.com/document/updates-unity/3i0euapg/) · [历史版本](https://developer-cn.picoxr.com/document/unity/where-to-download-an-older-version-of-sdk/) |
| 2 | **3.0.0 是第一个断代点**：空间锚点接口、场景标定接口整体重构，2.5.0 及更早的 `*AnchorEntity*` 系列全部转为 Deprecated，事件驱动流程改成 async 流程。 | [v3.0.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-300/) · [MR 迁移](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/) |
| 3 | **3.2.0 是第二个断代点**：一次性标记废弃 `PXR_Input` 30 个成员、`PXR_MotionTracking` 27 个成员，外加 `PXR_Boundary` 2 个、`PXR_FoveationRendering` 1 个、`PXR_HandTracking` 1 个。AI 记忆里的振动/体感追踪写法基本都落在这批里。 | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 4 | 真正**从文档中消失（removed）** 的方法极少：只有 3.0.0 从 `PXR_Input` 删掉 6 个 Fitness/Swift 方法、从 `PXR_Enterprise` 删掉 `WriteConfigFileToDataLocal`。其余绝大多数是"保留但标 (Deprecated)"。 | [PXR_Input?v=2.5.0](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/?v=2.5.0) vs [?v=3.0.0](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/?v=3.0.0) |
| 5 | 64 位：**平台服务自 2.3.0 起仅支持 64 位应用**；**整个 SDK 自 3.1.0 起仅支持开发 64 位应用**（Target Architectures 必须 ARM64、Scripting Backend 必须 IL2CPP，3.1.0 起由 Project Validation 强制检查）。 | [v2.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-230/) · [v3.1.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-310/) |
| 6 | **3.1.0 起顶部菜单栏 `PXR_SDK` 改名为 `PICO`**。任何 `PXR_SDK > ...` 的菜单路径在 3.1.0 及以上都是错的。 | [v3.1.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-310/) |
| 7 | **3.3.0 起合成层组件 `PXR_Over Lay` 更名为 `PXR_Composition Layer`**；但脚本文件名仍是 `PXR_OverLay.cs`、编辑器类仍是 `PXR_OverLayEditor`，且 v3.4.0 文档里仍有 `PXR_Over Lay (Script)` 的残留写法。 | [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) · [合成层参数](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) |
| 8 | **Unity OpenXR 插件自 3.3.0 起支持**。启用后应改查《PICO Unity OpenXR SDK 开发指南》，而不是 Integration SDK 的功能文档。3.3.0 同时让 PICO Building Blocks 兼容该插件。 | [OpenXR 插件](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) · [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| 9 | Unity 引擎侧支持是 **3.1.0** 才补齐的：Unity 6、XR Interaction Toolkit 3.x、AR Foundation 6.0、macOS 开发环境。3.0.x 及更早不要假设这些可用。 | [v3.1.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-310/) |
| 10 | 一批 `PXR_System` 接口在 3.2.0 被 changelog 标为**"不支持"**：`SetExtraLatencyMode`、`EnableFaceTracking`、`EnableLipSync`、`GetFaceTrackingData`、`SetEyeFOV`、`SetFaceTrackingStatus`、`SetCommonBrightness`、`GetCommonBrightness`、`GetScreenBrightnessLevel`、`SetScreenBrightnessLevel`。**它们在 v3.4.0 API 参考里没有 (Deprecated) 标记、仍能搜到**，是最容易误用的一类。 | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) · [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 11 | `GetDisplayFrequenciesAvailable`（`PXR_System`，获取设备支持的所有刷新率）是 **3.2.0 新增**，3.1.0 及更早不存在。 | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 12 | 3.2.0 起 `PXR_Boundary.GetDimensions` **只能在 StageLevel 调用**；同版本 `SessionStateChanged` 由 `Action<int>` 改为 `Action<XrSessionState>`。 | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 13 | **同名 API 会跨类搬家**：`UseGlobalPose` 在 `PXR_Boundary` 于 3.2.0 废弃，3.3.0 作为新接口出现在 `PXR_Enterprise`；`GetMotionTrackerBattery` 在 `PXR_Input` 于 3.2.0 废弃，同一版本作为新接口出现在 `PXR_MotionTracking`。只记方法名不记类名必然写错。 | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) · [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| 14 | **官方文档自身存在版本矛盾**：v3.4.0 的《面部追踪》指南仍在教 `PXR_MotionTracking.StartFaceTracking` / `GetFaceTrackingData` 等，而 API 参考已给它们打 (Deprecated)、3.2.0 changelog 已列为废弃；更老的 `PXR_System` 那套（2.1.4+）则被标"不支持"。落地前以 SDK 实际源码为准。 | [面部追踪](https://developer-cn.picoxr.com/document/unity/face-tracking/) · [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 15 | 设备系统版本（PUI）要求随 SDK 单调递增，**装了新 SDK 但设备系统旧 = 功能直接不可用**。3.3.0 要求 PICO 4 系列 5.13.0、PICO 4 Ultra 系列 5.14.0 及以上。 | [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| 16 | 3.2.0 的设备声明是"**PICO 4 Ultra 系列**（即将支持 PICO Neo3 和 PICO 4 系列）"；直到 **3.4.0** 才明确空间锚点、共享空间锚点、空间网格、场景标定、MR 安全防护支持 PICO 4 系列。为 PICO 4 做 MR 时必须核对这条时间线。 | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) · [v3.4.0](https://developer-cn.picoxr.com/document/updates-unity/3i0euapg/) |
| 17 | 3.4.0 的 API 页面级唯一新增是 **`PXR_CameraImage`**（用户设备相机图像数据管理）；其余版本（2.5.0→3.3.2）没有新增或删除任何 XR 类页面。 | [v3.4.0](https://developer-cn.picoxr.com/document/updates-unity/3i0euapg/) · [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 18 | MR 版本兼容矩阵：**2.5.0 及更早的 MR 接口 PICO 4 支持、PICO 4 Ultra 不支持**；**3.0.0 在 PICO 4 上仅支持空间锚点（不含共享）与场景标定**，PICO 4 Ultra 全部支持。视频透视不在此次重构范围内。 | [MR 迁移](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/) |
| 19 | 2.1.3 的**触觉反馈接口整体重命名**（同时涉及新增和废弃）；2.0.5 **删除了"支付"和"成就"功能**并改由平台服务提供。2.x 老代码里的这两类调用都需要重写。 | [v2.1.x](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-211-to-215/) · [v2.0.x](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-201-to-207/) |
| 20 | **2.0.6 没有公开 release note**（官方记录从 2.0.5 直接跳到 2.0.7）；不要编造该版本的变更内容。 | [v2.0.x](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-201-to-207/) |

## 工作流程

### 1. 定位版本代际（时间线总表）

任何迁移任务的第 0 步：确认项目当前版本、目标版本，并把两者之间的所有断代点列出来。

| 版本 | 上线日期 | 设备系统版本要求 | 关键变更 |
|---|---|---|---|
| 2.0.1 | 2021-10-12 | — | 大版本迭代，UnityXR SDK (Legacy) / UnitySDK (Deprecated) 功能并入；新增 Vulkan、Metrics Tool、`GetSensorStatus` |
| 2.0.2 | 2021-12-09 | — | 大空间地图共享接口 `SaveLargeSpaceMaps` / `ExportMaps` / `ImportMaps`；`KillBackgroundAppsWithWhiteList` |
| 2.0.3 | 2022-01-28 | — | 空间音频渲染器、120Hz 刷新率、混合现实捕捉（MRC） |
| 2.0.4 | 2022-04-01 | — | Preview Tool、焦点感知；输入系统升级到 Unity Input System |
| 2.0.5 | 2022-05-30 | — | **删除"支付"和"成就"功能**（改由平台服务提供）；帐号&好友 / 语音聊天 / 房间&匹配；DRM 保护 |
| 2.0.7 | 2022-09-09 | — | 平台服务补齐（社交互动/排行榜/应用内购/成就/运动数据授权）；合成层支持 Cubemap、Equirect |
| 2.1.1 | 2022-09-27 | — | 支持 PICO 4 系列；手势追踪、触感反馈、DLC；**UI 文案 Pico → PICO** |
| 2.1.2 | 2022-11-07 | PUI 5.1.0+（PICO 4 系列）/ 4.9.0+（PICO Neo3 系列） | 云存档、挑战、PC 端调试工具；MRC 选项移入 `PXR_Manager` 面板 |
| 2.1.3 | 2023-01-12 | PUI 5.3.0+ | **触觉反馈接口整体重命名**（含新增与废弃）；Optimize Buffer Discards (Vulkan)；`SetEyeFOV` |
| 2.1.4 | 2023-03-02 | PUI 5.4.0+ | 应用空间扭曲（AppSW）、延迟锁定（Late Latching）、眼动追踪、面部追踪、Metrics HUD、PDC |
| 2.1.5 | 2023-05-17 | PUI 5.5.0+ | 启动画面、EAC 合成层、FFR 下采样；手势编辑器（`PXR_Hand Pose Generator` / `PXR_Hand Pose`）；人体追踪；`SetFaceTrackingStatus` |
| 2.2.0 | 2023-06-29 | PUI 5.6.0+ | Render Viewport Scale；Late Latching Debug；OIDC 授权登录 |
| 2.3.0 | 2023-08-17 | PUI 5.7.0+ | 空间锚点、空间标定、ETFR、自适应分辨率；**重构眼动追踪与面部追踪接口**；**平台服务仅支持 64 位** |
| 2.4.0 | 2023-11-16 | PUI 5.8.0+ | 超分辨率、锐化、EAC；RGB 相机系列接口（企业）；`GetPerEyePose` / `GetEyeBlink` |
| 2.5.0 | 2024-01-24 | 5.9.0+ | External Surface 层 HDR 视频；XR Interaction Toolkit 手与 3D 物体交互 |
| 3.0.0 | 2024-09-02 | 5.11.0+ | **MR 接口大重构**（空间锚点/场景标定）；共享空间锚点、空间网格、MR 安全防护、空间数据权限；独立追踪；项目配置验证 |
| 3.0.5 | 2024-11-05 | 5.12.0+ | PICO Building Blocks；Blurred Quad 层；`GetControllerStatus` / `InputDeviceChanged`；场景标定新增 10 个语义标签 |
| 3.1.0 | 2025-02-18 | 5.12.0+ | 支持 Unity 6 / XRIT 3.x / AR Foundation 6.0 / macOS；**仅支持 64 位**；**菜单 `PXR_SDK` → `PICO`**；`PXR_Spatial Anchor (Script)`、`PXR_Scene Capture Manager (Script)`；企业相机 `*for4U` 系列 |
| 3.2.0 | 2025-05-29 | 5.13.0+（设备：PICO 4 Ultra 系列） | **大规模废弃**（`PXR_Input` 30 项、`PXR_MotionTracking` 27 项等）；PICO XR Portal；SecureMR；`UPxr_SetSuperResolutionOrSharpening`；共享锚点带进度接口 |
| 3.3.0 | 2025-09-18 | PICO 4 系列 5.13.0 / PICO 4 Ultra 系列 5.14.0+ | **支持 Unity OpenXR 插件**；**`PXR_Over Lay` → `PXR_Composition Layer`**；PICO Debugger；企业新增 20 个接口 |
| 3.3.2 | 2025-11-11 | — | Eye Buffer 上限控制；SecureMR 新增 SVD / NORM / SWAP_HWC_CHW operator 及更多运算符 |
| 3.4.0 | 2026-02-27 | — | **平面检测**；空间锚点/共享锚点/空间网格/场景标定/MR 安全防护支持 PICO 4 系列；`PXR_CameraImage`；SecureMR JavaScript Operator / Dynamic Texture / Readback Tensor |

### 2. 2.5.0 及更早 → 3.0.0：MR 接口迁移

来源：[MR 功能兼容性和迁移说明](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/)

1. 先查兼容矩阵（关键规则 18），确认目标设备在目标 SDK 上到底支持哪些 MR 能力。
2. 面板开关改名：`PXR_Manager (Script)` 上原来的 **Anchor** 选框 → **Spatial Anchor**；场景标定新增 **Scene Capture** 选框（2.5.0 时代没有）。
3. 生命周期变了：3.0.0 起**所有锚点/场景标定操作前必须先 `StartSenseDataProvider`，结束后 `StopSenseDataProvider`**；2.5.0 没有这一步。
4. 事件驱动 → async：旧写法"调接口 + 监听 `AnchorEntity*` 事件 + 再调 `Get*Results`"三段式，3.0.0 全部收敛成单个 `*Async` 调用。
5. 逐个替换接口（完整映射见"核心 API 锚点 → 重命名 / 迁移映射"）。
6. 运动追踪同步走《[运动追踪接口兼容性说明](https://developer-cn.picoxr.com/document/unity/motion-tracker-api-compatibility/)》，注意 PICO 体感追踪器 Beta 版与正式版适用接口不同。

### 3. 3.1.0 → 3.2.0 及以上：输入 / 运动追踪 / 触觉的废弃潮

1. 用"核心 API 锚点 → 3.2.0 新增废弃清单"逐项过一遍代码；这批数量大（`PXR_Input` 30 + `PXR_MotionTracking` 27），建议全局搜方法名而不是靠编译警告。
2. changelog 明确给出替代关系的先处理：
   - `CheckMotionTrackerModeAndNumber` → `CheckMotionTrackerNumber`
   - `GetMotionTrackerLocations` → `GetMotionTrackerLocation`
   - `SetExtDevTrackerMotorVibrate` → `SetExpandDeviceVibrate`
   - `SetExtDevTrackerByPassData` → `SetExpandDeviceCustomData`
   - `GetExtDevTrackerByPassData` → `GetExpandDeviceCustomData`
   - `GetExtDevTrackerBattery` → `GetExpandDeviceBattery`
   - `ExtDevConnectAction` → `ExpandDeviceConnectionAction`
   - `MotionTrackerNumberOfConnections` / `GetExtDevTrackerConnectState` / `GetMotionTrackerConnectStateWithSN` → 改用 `MotionTrackerConnectionAction` 事件
   - `MotionTrackerKeyAction` → `MotionTrackerPowerKeyAction`
   - `MotionTrackerBatteryLevel` → `GetMotionTrackerBattery`（注意是 `PXR_MotionTracking` 上的新接口）
   - `BodyTrackingAbnormalCalibrationData` / `BodyTrackingStateError` → 改用 `GetBodyTrackingState`
3. changelog 未给替代关系的（振动/触觉流 `*HapticStream*`、`*Vibrate*`、`GetDominantHand` / `SetDominantHand` 等），不要自行编造替代 API——查当前 SDK 源码或 [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) 现行方法列表。
4. 注意 3.2.0 的 changelog 里 `ExtDevBatteryAction` 一行写的是"已废弃。替换为回调事件 `ExtDevBatteryAction`"——**这是官方文档的笔误**（自我替换），按上下文应为 `ExpandDeviceBatteryAction`。以源码为准。

### 4. 接入 Unity OpenXR 插件 / 从 PICO Unity OpenXR SDK 迁移（3.3.0+）

来源：[新增支持 Unity OpenXR 插件](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/)

1. 导入 PICO Unity Integration SDK **3.3.0 或更高版本**，按《完成项目配置》里的"启用 Unity OpenXR 插件"部分启用插件。
2. 之后功能开发查 [PICO Unity OpenXR SDK 开发指南](https://developer-cn.picoxr.com/document/unity-openxr/)，不要再照搬 Integration SDK 的组件流程。
3. 若原项目用的是 **PICO Unity OpenXR SDK 1.4.0 及更低版本**，按下表适配：

| 功能 | 旧（PICO Unity OpenXR SDK ≤1.4.0） | 新（PICO Unity Integration SDK 3.3.0+） |
|---|---|---|
| 场景渐变 | `PICOScreenFade` | `PXR_ScreenFade` |
| 屏幕刷新率 | `TryGetSupportedDisplayRefreshRates`、`GetDisplayRefreshRateCount`（均已废弃） | `GetDisplayFrequenciesAvailable` |
| 透视 | 接入 Passthrough Layer Feature 需加 `PICO Manager (Script)` 组件 | 不需要该组件 |
| 透视开关 | `EnableSeeThroughManual`（已废弃） | `EnableVideoSeeThrough` |
| 合成层 | 所有接口已废弃 | 直接按《合成层》文档使用 |
| 命名空间 | `SecureContentFlag`、`BodyJointSet`、`BodyTrackingData`、`BodyTrackingDataInfo`、`BodyTrackingBoneLength`、`GeometryInstanceTransform`、`PassThroughStyle` 各自独立 | 统一改为 `pxr` 命名空间，迁移到 `PXR_Type.cs`，需要改 using |

> 该文档正文写"使用 PICO Unity Integration SDK **3.0.0** 及更高版本后需要适配"，而对照表表头写的是 **3.3.0** 及更高版本——这是原文的不一致处，引用时按表头（3.3.0）理解并注明存疑。

### 5. "这个 API 到底还在不在"排查序列

1. 打开当前版本 API 参考页 `https://developer-cn.picoxr.com/reference/unity/client-api/<类名>/`，搜方法名。
2. 找不到 → 可能被删（对照本文"真正被移除"清单）或从来不在这个类里（对照"跨类搬家"）。
3. 找到但标题带 **(Deprecated)** → 已废弃，查对应版本 changelog 拿替代方案。
4. 找到且没有 (Deprecated) → 仍要交叉查 3.2.0 changelog 的"不支持"清单（这类不带标记，见关键规则 10）。
5. 想确认引入版本 → 逐个版本加 `?v=` 复核，例如 `?v=3.1.0` 有、`?v=3.0.5` 没有，即 3.1.0 引入。
6. 指南文档与 API 参考冲突时（如面部追踪），**以 SDK 源码为准**，两边都要在结论里注明。

## 核心 API 锚点

### 真正被移除的方法（文档中彻底消失，调用必然编译失败）

| 版本 | 类 | 被移除 |
|---|---|---|
| 3.0.0 | `PXR_Input` | `GetFitnessBandBattery`、`GetFitnessBandCalibState`、`GetFitnessBandConnectState`、`OpenFitnessBandCalibrationAPP`、`SetControllerEnableKey`、`SetSwiftMode` |
| 3.0.0 | `PXR_Enterprise` | `WriteConfigFileToDataLocal` |

> 其中 5 个 Fitness/Swift 方法并非消失，而是**改名后搬到了 `PXR_MotionTracking` / `PXR_Input` 的新名字下**，见下方重命名表。`SetControllerEnableKey` 与 `WriteConfigFileToDataLocal` 在 v3.4.0 文档中无对应项。

### 新增废弃（newly_deprecated）清单

**3.0.0（`PXR_MixedReality`，17 项，MR 重构）**
`ClearPersistedAnchorEntity`、`CreateAnchorEntity`、`DestroyAnchorEntity`、`EnableVideoSeeThrough`、`GetAnchorComponentFlags`、`GetAnchorEntityLoadResults`、`GetAnchorEntityUuid`、`GetAnchorPlaneBoundaryInfo`、`GetAnchorPlanePolygonInfo`、`GetAnchorPose`、`GetAnchorSceneLabel`、`GetAnchorVolumeInfo`、`LoadAnchorEntityBySceneFilter`、`LoadAnchorEntityByUuidFilter`、`PersistAnchorEntity`、`StartSpatialSceneCapture`、`UnPersistAnchorEntity`

**3.2.0（`PXR_Input`，30 项）**
振动/触觉：`SetControllerVibration`、`SetControllerVibrationEvent`、`StartControllerVCMotor`、`StopControllerVCMotor`、`SetControllerAmp`、`StartVibrateBySharem`、`SaveVibrateByCache`、`StartVibrateByCache`、`ClearVibrateByCache`、`StartVibrateByPHF`、`PauseVibrate`、`ResumeVibrate`、`UpdateVibrateParams`、`CreateHapticStream`、`WriteHapticStream`、`SetHapticStreamSpeed`、`GetHapticStreamSpeed`、`GetHapticStreamCurrentFrameSequence`、`StartHapticStream`、`StopHapticStream`、`RemoveHapticStream`、`AnalysisHapticStreamPHF`
其它：`GetDominantHand`、`SetDominantHand`、`GetBodyTrackingPose`、`GetMotionTrackerConnectStateWithID`、`GetMotionTrackerBattery`、`GetMotionTrackerCalibState`、`SetBodyTrackingMode`、`SetBodyTrackingBoneLength`
（changelog 另列 `ResetController`、`SetArmModelParameters` 为已废弃）

**3.2.0（`PXR_MotionTracking`，27 项）**
`WantEyeTrackingService`、`WantFaceTrackingService`、`GetFaceTrackingSupported`、`StartFaceTracking`、`StopFaceTracking`、`GetFaceTrackingState`、`GetFaceTrackingData`、`BodyTrackingAbnormalCalibrationData`、`BodyTrackingStateError`、`BodyTrackingAction`、`MotionTrackerNumberOfConnections`、`MotionTrackerBatteryLevel`、`MotionTrackerKeyAction`、`MotionTrackingModeChangedAction`、`GetMotionTrackerConnectStateWithSN`、`GetMotionTrackerDeviceType`、`CheckMotionTrackerModeAndNumber`、`GetMotionTrackerMode`、`GetMotionTrackerLocations`、`ExtDevConnectAction`、`ExtDevBatteryAction`、`GetExtDevTrackerConnectState`、`GetExtDevTrackerKeyData`、`SetExtDevTrackerMotorVibrate`、`SetExtDevTrackerByPassData`、`GetExtDevTrackerByPassData`、`GetExtDevTrackerBattery`

**3.2.0（其它类）**
`PXR_Boundary.GetSeeThroughTrackingState`、`PXR_Boundary.UseGlobalPose`、`PXR_FoveationRendering.SetFoveationParameters`、`PXR_HandTracking.GetSettingState`

### 逐版本新增（挑高价值项，完整清单见对应 changelog）

| 版本 | 类 | 新增 |
|---|---|---|
| 3.0.0 | `PXR_MixedReality` | `StartSenseDataProvider`、`StopSenseDataProvider`、`GetSenseDataProviderState`、`CreateSpatialAnchorAsync`、`DestroyAnchor`、`PersistSpatialAnchorAsync`、`UnPersistSpatialAnchorAsync`、`QuerySpatialAnchorAsync`、`GetAnchorUuid`、`LocateAnchor`、`UploadSpatialAnchorAsync`、`DownloadSharedSpatialAnchorAsync`、`StartSceneCaptureAsync`、`QuerySceneAnchorAsync`、`GetSceneSemanticLabel`、`GetSceneAnchorComponentTypes`、`GetSceneBox2DData`、`GetSceneBox3DData`、`GetScenePolygonData`、`PxrResult`、`PxrSemanticLabel`、`PxrSenseDataProviderState` |
| 3.0.0 | `PXR_MotionTracking` | `StartBodyTracking`、`StopBodyTracking`、`GetBodyTrackingState`、`GetBodyTrackingData`、`GetBodyTrackingSupported`、`StartMotionTrackerCalibApp`、`BodyTrackingMode`、`EyeTrackingData`、`FaceTrackingData`、`MotionTrackerLocations` 等 65 项 |
| 3.0.0 | `PXR_HandTracking` | `GetHandScale`、`HandAimState`、`HandJointLocations`、`HandFinger`、`HandType`、`ActiveInputDevice`、`Posef`、`Quatf`、`Vector3f` 等 |
| 3.0.5 | `PXR_Input` | `GetControllerStatus`、`InputDeviceChanged` |
| 3.1.0 | `PXR_MixedReality` | `QuerySpatialAnchorObjectsAsync`（**注意**：API 参考页的标题被截断成 `QuerySpatialAnchorObjectsAsyn`，签名里才是完整名） |
| 3.1.0 | `PXR_Enterprise` | `OpenCameraAsyncfor4U`、`Configurefor4U`、`StartPreviewfor4U`、`SetCameraFrameBufferfor4U`、`StartGetImageDatafor4U`、`CloseCamerafor4U`、`GetCameraIntrinsicsfor4U`、`GetCameraExtrinsicsfor4U`、`GetCameraParametersNewfor4U`、`RequestUserPermission` |
| 3.2.0 | `PXR_System` | `GetDisplayFrequenciesAvailable` |
| 3.2.0 | `PXR_MotionTracking` | `CheckMotionTrackerNumber`、`GetMotionTrackerLocation`、`GetMotionTrackerBattery`、`MotionTrackerConnectionAction`、`MotionTrackerPowerKeyAction`、`RequestMotionTrackerCompleteAction`、`ExpandDeviceConnectionAction`、`ExpandDeviceBatteryAction`、`GetExpandDevice`、`GetExpandDeviceBattery`、`SetExpandDeviceVibrate`、`SetExpandDeviceCustomData`、`GetExpandDeviceCustomData` |
| 3.2.0 | `PXR_Enterprise` | `SetDeviceOwner`、`GetDeviceOwner`、`SetBrowserHomePage`、`GetBrowserHomePage`、`SetMotionTrackerAutoStart`、`AllowWifiAutoJoin`、`GetLargeSpaceBoundsInfoWithType` |
| 3.2.0 | `PXR_MixedReality` | `UploadSpatialAnchorWithProgressAsync`、`DownloadSharedSpatialAnchorWithProgressAsync` |
| 3.3.0 | `PXR_Enterprise` | `GetHeadTrackingStatus`、`GetHeadPose`、`GetControllerPose`、`GetSwiftPose`、`GetSwiftTrackerDevices`、`GetHeadIMUData`、`GetControllerIMUData`、`GetSwiftIMUData`、`StartSwiftTrackerPairing`、`UnBondSwiftTracker`、`ResetTracking`、`SetFenceColor`、`GetFenceColor`、`SetUsbTetheringStaticIP`、`GetUsbTetheringStaticIPLocal`、`GetUsbTetheringStaticIPClient`、`SetLargeSpaceMapScale`、`GetPredictedMainSensorState2`、`UseGlobalPose`、`ConvertPoseCoordinate` |
| 3.4.0 | `PXR_CameraImage` | 整类新增：`GetAvailableCameras`、`CreateCameraDeviceAsync`、`CreateCameraCaptureSessionAsync`、`BeginCameraCapture`、`AcquireCameraImage`、`GetCameraImageData`、`ReleaseCameraImage`、`EndCameraCapture`、`DestroyCameraCaptureSession`、`DestroyCameraDevice`、`GetCameraIntrinsics`、`GetCameraExtrinsics` 等 |

### 重命名 / 迁移映射（曾用名 → 现名）

**MR（2.5.0 及更早 → 3.0.0）**
`CreateAnchorEntity` → `CreateSpatialAnchorAsync`｜`DestroyAnchorEntity` → `DestroyAnchor`｜`PersistAnchorEntity` → `PersistSpatialAnchorAsync`｜`UnPersistAnchorEntity` → `UnPersistSpatialAnchorAsync`｜`LoadAnchorEntityByUuidFilter` → `QuerySpatialAnchorAsync`｜`GetAnchorEntityUuid` → `GetAnchorUuid`｜`GetAnchorPose` → `LocateAnchor`｜`StartSpatialSceneCapture` → `StartSceneCaptureAsync`｜`LoadAnchorEntityBySceneFilter` → `QuerySceneAnchorAsync`｜`GetAnchorSceneLabel` → `GetSceneSemanticLabel`｜`GetAnchorComponentFlags` → `GetSceneAnchorComponentTypes`｜`GetAnchorVolumeInfo` → `GetSceneBox3DData`｜`GetAnchorPlaneBoundaryInfo` → `GetSceneBox2DData`｜`GetAnchorPlanePolygonInfo` → `GetScenePolygonData`

**运动追踪 / 体感追踪器（曾用名见官方"曾用名"标注）**
`GetFitnessBandConnectState` → `GetMotionTrackerConnectStateWithID`｜`GetFitnessBandBattery` → `GetMotionTrackerBattery`｜`GetFitnessBandCalibState` → `GetMotionTrackerCalibState`｜`SetSwiftMode` → `SetBodyTrackingMode`｜`FitnessBandNumberOfConnections` → `MotionTrackerNumberOfConnections`｜`FitnessBandElectricQuantity` → `MotionTrackerBatteryLevel`｜`FitnessBandAbnormalCalibrationData` → `BodyTrackingAbnormalCalibrationData`｜`OpenFitnessBandCalibrationAPP` / `StartBodyTrackingCalibApp` → `StartMotionTrackerCalibApp`｜`GetMotionTrackerConnectState` → `GetMotionTrackerConnectStateWithSN`｜`GetMotionTrackerType` → `GetMotionTrackerDeviceType`
（上述"现名"中除 `StartMotionTrackerCalibApp`、`GetBodyTrackingState` 等外，多数在 3.2.0 又被再次废弃，需要接着按 3.2.0 映射走第二跳）

**组件 / 菜单 / 面板**
`PXR_Over Lay`（组件显示名，脚本文件仍为 `PXR_OverLay.cs`）→ `PXR_Composition Layer`（3.3.0）｜菜单 `PXR_SDK` → `PICO`（3.1.0）｜`PXR_Manager (Script)` 上的 **Anchor** 选框 → **Spatial Anchor**（3.0.0）

### 版本敏感的类 / 组件名（防止张冠李戴）

- 稳定存在于 2.5.0 ~ 3.4.0 的 XR 类：`PXR_Boundary`、`PXR_Enterprise`、`PXR_EyeTracking`、`PXR_FoveationRendering`、`PXR_HandTracking`、`PXR_Input`、`PXR_MixedReality`、`PXR_MotionTracking`、`PXR_System`。
- 3.4.0 才有：`PXR_CameraImage`。
- 3.1.0 才有的组件：`PXR_Spatial Anchor (Script)`、`PXR_Scene Capture Manager (Script)`、`SpatialAnchor` 预制体。
- 2.1.5 引入的手势编辑器脚本：`PXR_Hand Pose Generator`、`PXR_Hand Pose`；2.3.0 引入 `HandPoseGenerator` 预制体。
- 2.4.0 空间音频：`PXR_Audio_Spatializer_Audio Source` 脚本**新增** Reflection Gain DB / Directivity Alpha / Directivity Order 参数（脚本本身更早就有）；同版本**新增** `PXR_Audio_Spatializer_Audio Listener` 脚本。
- 3.0.5 引入 PICO Building Blocks；3.2.0 引入 PICO XR Portal；3.3.0 引入 PICO Debugger、`PICO Spatial Anchor Sample` / `PICO Spatial Mesh` / `PICO Scene Capture` / `PICO Composition Layer Overlay` / `PICO Composition Layer Underlay` 五个 Building Blocks 子模块。

## DO NOT

```csharp
// ❌ 沿用 2.5.0 时代的锚点写法（3.0.0 起已废弃，且缺少 Provider 启停）
PXR_MixedReality.CreateAnchorEntity(pos, rot, out var taskId);
// 监听 AnchorEntityCreated 事件 ...
// ✅ 3.x：先起 Provider，再走 async 接口
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);
var (result, handle, uuid) = await PXR_MixedReality.CreateSpatialAnchorAsync(pos, rot);
```

```csharp
// ❌ 记忆里的体感追踪器接口（2.5.0 时代，3.0.0 已从文档中删除）
PXR_Input.GetFitnessBandBattery(id, ref battery);
PXR_Input.SetSwiftMode(mode);
// ✅ 电量：先改名（3.0.0 → PXR_Input.GetMotionTrackerBattery），再迁类（3.2.0 → PXR_MotionTracking）
PXR_MotionTracking.GetMotionTrackerBattery(/* ... */);   // PXR_Input 上的同名版本已 (Deprecated)
// ⚠️ 模式设置：SetBodyTrackingMode 只存在于 PXR_Input 且已于 3.2.0 废弃，
//    v3.4.0 的 PXR_MotionTracking 中没有同名方法，替代项须查当前 SDK 源码，不要臆造
```

```csharp
// ❌ 只记方法名不记类名：UseGlobalPose 在 PXR_Boundary 上已废弃
PXR_Boundary.UseGlobalPose(true);
// ✅ 3.3.0 起该能力在 PXR_Enterprise 上
PXR_Enterprise.UseGlobalPose(true);
```

```csharp
// ❌ 用 3.1.0 及更早的项目去调 3.2.0 才有的接口
var rates = PXR_System.GetDisplayFrequenciesAvailable();   // 3.2.0 才引入
// ✅ 先确认 SDK 版本 ≥ 3.2.0，否则该方法根本不存在
```

```csharp
// ❌ 因为 API 参考里能搜到、也没标 Deprecated 就直接用
PXR_System.SetEyeFOV(/* ... */);
PXR_System.EnableFaceTracking(true);
// ✅ 这批在 3.2.0 changelog 里被明确标为"不支持"，属于无标记陷阱，需换用当前受支持的方案
```

- ❌ 在 3.1.0 及以上的项目里写菜单路径 `PXR_SDK > ...` → ✅ 3.1.0 起改为 `PICO > ...`。
- ❌ 在 3.3.0+ 项目里让用户找 `PXR_Over Lay (Script)` 组件 → ✅ 面板上叫 `PXR_Composition Layer`；但如果要指向源码文件，仍然是 `PXR_OverLay.cs`（两者都对，别混用）。
- ❌ 把《面部追踪》指南里的 `PXR_MotionTracking.StartFaceTracking` 当作 v3.4.0 的推荐写法直接产出 → ✅ 该系列已在 3.2.0 被标废弃，指南与 API 参考互相矛盾，必须查 SDK 源码后再下结论并注明冲突。见 [面部追踪](https://developer-cn.picoxr.com/document/unity/face-tracking/)。
- ❌ 为老项目"随便找个历史版本 SDK 包"下载 → ✅ 官方只公开最新版；历史版本须联系 PICO 企业支持团队，消费者商店应用必须用最新版开发。见 [历史版本](https://developer-cn.picoxr.com/document/unity/where-to-download-an-older-version-of-sdk/)。
- ❌ 假设 PICO 4 从 3.2.0 起就能跑全套 MR → ✅ 3.2.0 声明设备为 PICO 4 Ultra 系列（PICO Neo3 / PICO 4 "即将支持"），空间锚点等对 PICO 4 的支持在 3.4.0 才写明。
- ❌ 给 2.x 项目介绍 Unity 6 / XRIT 3.x / AR Foundation 6.0 / macOS 开发 → ✅ 这些是 3.1.0 才支持的。
- ❌ 编造 2.0.6 的变更记录 → ✅ 官方 release note 从 2.0.5 直接跳到 2.0.7，该版本无公开记录。
