# PICO Unity SDK — XR 核心类 API 签名总表（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。
> 本文只收录 `client-api` 下的 **XR 核心类**（9 类）+ Legacy 处置说明。平台服务类（`*Service`）不在本文范围。

来源根地址（下文简称「文档」）：`https://developer-cn.picoxr.com/reference/unity/client-api/<类名>/`

## 何时加载本文档

- 需要写 / 改 / 审 任何 `PXR_*` 静态类调用，且不确定方法名、参数顺序或返回类型时。
- 需要判断某个 API 在 v3.4.0 是否已 **Deprecated**、是否还应写进新代码时。
- 迁移旧工程（v2.x / v3.0 时期代码）到 v3.4.0，需要一次性识别过时调用面时。
- 需要确认某能力属于 **消费设备可用** 还是 **企业设备专用**（`PXR_Enterprise`）时。
- AI 生成的 PICO 代码需要逐个方法名核对、防止编造 API 时。

## 关键规则

| # | 规则 | 来源 |
|---|---|---|
| 1 | 这些 API 是 **类静态方法**，调用形如 `PXR_System.GetSDKVersion()`；文档签名多处显式写了 `public static`（如 `public static ControllerStatus GetControllerStatus(Controller controller)`，以及 `PXR_CameraImage` 的全部方法）。不要 `new`，也不要当成组件去 `GetComponent`。 | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) / [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 2 | 标 `(Deprecated)` 的方法**在新代码中一律不要用**。v3.4.0 中 Deprecated 面非常大：`PXR_Input` 的旧振动/体追踪全组、`PXR_MixedReality` 的整套 `AnchorEntity` API、`PXR_System` 的面部追踪全组、`PXR_MotionTracking` 的面部追踪全组均已标记。 | 各类文档页 |
| 3 | `PXR_EyeTracking` **整类都是 legacy**，文档首句即写明「For the latest ones, refer to the `PXR_MotionTracking` class」。新代码走 `PXR_MotionTracking` 的 eye 系接口。 | [PXR_EyeTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_EyeTracking/) |
| 4 | `PXR_MotionTracking` 的方法返回 **`int`（`TrackingStateCode`）而不是 `bool`**：`PXR_MT_SUCCESS = 0`、`PXR_MT_FAILURE = -1`、`PXR_MT_DEVICE_NOT_SUPPORT = -3`、`PXR_MT_SERVICE_NEED_START = -4`、`PXR_MT_ET_PERMISSION_DENIED = -5`。写 `if (GetEyeTrackingData(...))` 编译不过。 | [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 5 | `PXR_MixedReality` 的方法返回 **`PxrResult`** 枚举，`SUCCESS = 0`，失败为负值（`ERROR_PERMISSION_INSUFFICIENT = -1000710000`、`ERROR_SPATIAL_SENSING_SERVICE_UNAVAILABLE = -1005`、`ERROR_HANDLE_INVALID = -12` 等）。异步接口返回 `Task<...>` 元组，必须 `await`。 | [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 6 | `QuerySpatialAnchorAsync` **同一时间只能有一次调用在途**，必须等上一次完成才能再调。 | [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 7 | `PXR_Enterprise` **仅企业设备可用**（PICO Neo2 / Neo2 Eye / Neo3 Pro / Neo3 Pro Eye / G2 4K、4K E、4K Plus 系统 4.0.3+ / PICO 4 Enterprise），文档明确「Do not use them on consumer devices」。消费机上不要调。 | [PXR_Enterprise](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/) |
| 8 | 企业 API 有强制前置顺序：先 `InitEnterpriseService(bool isCamera)`（Must be called before calling other enterprise APIs），再 `BindEnterpriseService(Action<bool> callback)`（Must be called before calling other system related functions），退出时 `UnBindEnterpriseService()`。 | [PXR_Enterprise](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/) |
| 9 | `PXR_FoveationRendering.SetFoveationLevel` 是 **两参** `(FoveationLevel level, bool isETFR)`，不是单参。`FoveationLevel.None = -1`（不是 0）。`SetFoveationParameters` 已 Deprecated。 | [PXR_FoveationRendering](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_FoveationRendering/) |
| 10 | `PXR_System.SetSystemDisplayFrequency(float rate)` 只接受 **72 / 90 / 120**，文档写明「Other values are invalid」。不要传 60/144。 | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 11 | `PXR_System.GetSensorStatus()` 返回 **0=null / 1=3DoF / 3=6DoF**（**没有 2**）。不要按 0/1/2 判断。 | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 12 | `PXR_System.SetExtraLatencyMode` 既已 Deprecated，又限制「Call this function once only」。 | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 13 | `PXR_Boundary.GetDimensions(BoundaryType)` **只能传 `PlayArea`**；stationary boundary 下返回 `(0,1,0)`。`GetGeometry(PlayArea)` 对 stationary boundary 返回空。 | [PXR_Boundary](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Boundary/) |
| 14 | `PXR_Boundary.EnableSeeThroughManual(bool)` 有两个硬前置：相机 clear flags 必须为 solid color、背景色 alpha 必须为 0；且 app 暂停后该功能会停止，resume 后**必须重新调用**。 | [PXR_Boundary](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Boundary/) |
| 15 | `PXR_CameraImage` 的相机图像必须成对释放：`AcquireCameraImage` → `GetCameraImageData` → `ReleaseCameraImage`。无新图时 runtime 返回 `XR_CAMERA_IMAGE_NO_UPDATE_PICO`，这是正常状态不是错误。 | [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 16 | `PXR_CameraImage` 只有 `CreateCameraDeviceAsync` / `CreateCameraCaptureSessionAsync` 是 `async Task<PxrResult>`，其余全是同步 `PxrResult`。不要给同步方法加 `await`。 | [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 17 | `PXR_Input.GetControllerStatus(Controller)` 返回 **`ControllerStatus` 枚举**（`Static`/`SixDof`/`ThreeDof`/`Sleep`/`CollidedIn3Dof`/`CollidedIn6Dof`），不是 `bool`；判断连接用 `IsControllerConnected(Controller)`。 | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 18 | `PXR_Input` 中 `VibrateController` 与 `VibrateType` 是**两个不同枚举**：旧（Deprecated）振动 API 用 `VibrateController`，现行 Haptic API 用 `VibrateType`（含 `LeftPICO4U=4`/`RightPICO4U=8`/`BothPICO4U=12`）。不要混用。 | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 19 | `PXR_Boundary.SetGuardianSystemDisable(bool value)` 的方法名与文档参数说明语义相反（文档写 `true: enable / false: disable`）。以文档参数表为准并实测确认，不要凭方法名推断。 | [PXR_Boundary](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Boundary/) |
| 20 | 面部追踪相关 API 在 v3.4.0 **两处都已 Deprecated**：`PXR_System` 的 `EnableFaceTracking`/`EnableLipSync`/`GetFaceTrackingData`/`SetFaceTrackingStatus`，以及 `PXR_MotionTracking` 的 `WantFaceTrackingService`/`StartFaceTracking`/`StopFaceTracking`/`GetFaceTrackingState`/`GetFaceTrackingData`。文档未在本组语料中给出替代类，不要自行编造替代 API。 | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) / [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |

## 工作流程

### 1. 核对一个 PICO API 是否存在（防幻觉查表）

1. 先定位类：手柄/触觉 → `PXR_Input`；头显系统/音量/亮度/刷新率 → `PXR_System`；边界 → `PXR_Boundary`；注视点渲染 → `PXR_FoveationRendering`；手势 → `PXR_HandTracking`；眼动/面部/体追踪/运动追踪器 → `PXR_MotionTracking`；空间锚点/场景/VST 效果 → `PXR_MixedReality`；相机图像 → `PXR_CameraImage`；设备管控 → `PXR_Enterprise`。
2. 在本文对应小节的方法总表里查方法名。**表里没有 = 语料未收录，按"不存在"处理**，不要凭印象补写。
3. 看 Deprecated 列：`是` → 换现行接口（见「Legacy API 处置」）；`否` → 按签名列逐字抄参数顺序与 `ref`/`out` 修饰。
4. 仍不确定时打开该类的来源 URL 复核，不要猜。

### 2. 常用调用序列（本文只留签名，落地步骤见对应域文档）

- 手柄触觉振动：`SendHapticImpulse` / `SendHapticBuffer`（AudioClip / PCM / PHF 三重载）+ `sourceId` 控制 `Start/Pause/Resume/Stop/UpdateHapticBuffer`；旧 `SetControllerVibration` / `StartVibrateBySharem` / `CreateHapticStream` 一族全部 Deprecated（见「Legacy API 处置」）。完整步骤见 INTERACTION.md「触觉反馈」。
- 空间锚点：`StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor)` → `GetSenseDataProviderState`（期望 `Running`）→ `CreateSpatialAnchorAsync` → `PersistSpatialAnchorAsync` → `QuerySpatialAnchorAsync`（一次一个请求）→ `LocateAnchor` → `UnPersistSpatialAnchorAsync` → `DestroyAnchor` → `StopSenseDataProvider`。完整步骤见 MR.md「空间锚点生命周期」。
- 眼动追踪：`GetEyeTrackingSupported` → `StartEyeTracking` → `GetEyeTrackingState` / `GetEyeTrackingData`（`flags` 取自 `EyeTrackingDataGetFlags`）→ `StopEyeTracking`，返回值按 `TrackingStateCode` 判错（`-5` = 眼动权限被拒）。完整步骤见 INTERACTION.md「眼动 / 面部追踪接入」。

### 3. 企业设备管控接口调用（仅企业设备）

1. `PXR_Enterprise.InitEnterpriseService(bool isCamera)`（`isCamera` 控制是否开启 video seethrough，默认 `false`）。
2. `PXR_Enterprise.BindEnterpriseService(Action<bool> callback)`，在回调 `true` 之后才允许调用其它系统相关接口。
3. 调用目标接口（见下方分类清单）。
4. 应用退出/不再使用时 `PXR_Enterprise.UnBindEnterpriseService()`。

## 核心 API 锚点

> 签名列均取自各类文档页的代码块；Deprecated 列取自文档「Member functions / Functions」表中的 `(Deprecated)` 前缀。

### PXR_Boundary

**职责**：安全边界的可见性、配置状态、几何数据与触发检测。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| SetVisible | `void SetVisible(bool value)` | 设置边界可见（可被系统/用户设置覆盖） | 否 |
| GetVisible | `bool GetVisible()` | 边界是否可见 | 否 |
| GetConfigured | `bool GetConfigured()` | 边界是否已配置（未配置时边界相关接口不可用） | 否 |
| GetEnabled | `bool GetEnabled()` | 边界是否启用 | 否 |
| TestNode | `PxrBoundaryTriggerInfo TestNode(BoundaryTrackingNode node, BoundaryType boundaryType)` | 检测追踪节点是否触发边界 | 否 |
| TestPoint | `PxrBoundaryTriggerInfo TestPoint(PxrVector3f point, BoundaryType boundaryType)` | 检测某个点是否触发边界 | 否 |
| GetGeometry | `Vector3[] GetGeometry(BoundaryType boundaryType)` | 获取边界点集合 | 否 |
| GetDimensions | `Vector3 GetDimensions(BoundaryType boundaryType)` | 获取 PlayArea 尺寸（仅传 PlayArea） | 否 |
| EnableSeeThroughManual | `void EnableSeeThroughManual(bool value)` | 以相机图像作环境背景 | 否 |
| GetSeeThroughTrackingState | `PxrTrackingState GetSeeThroughTrackingState()` | 获取 seethrough 追踪状态 | **是** |
| SetGuardianSystemDisable | `void SetGuardianSystemDisable(bool value)` | 开关边界系统（见关键规则 19） | 否 |
| UseGlobalPose | `void UseGlobalPose(bool value)` | 使用全局位姿 | **是** |

枚举：`BoundaryType{OuterBoundary, PlayArea}`、`BoundaryTrackingNode{HandLeft, HandRight, Head}`、`PxrTrackingState{LostNoReason, LostCamera, LostHighLight, LostLowLight, LostLowFeatureCount, LostReLocation, LostInitialization, LostNoCamera, LostNoIMU, LostIMUJitter, LostUnknown}`。
结构体：`PxrBoundaryTriggerInfo{isTriggering, closestDistance, closestPoint, closestPointNormal, valid}`。

### PXR_FoveationRendering

**职责**：注视点渲染等级与参数设置。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| SetFoveationLevel | `bool SetFoveationLevel(FoveationLevel level, bool isETFR)` | 设置注视点渲染等级，`isETFR` 区分是否眼动注视点渲染 | 否 |
| GetFoveationLevel | `FoveationLevel GetFoveationLevel()` | 获取当前等级 | 否 |
| SetFoveationParameters | `void SetFoveationParameters(float foveationGainX, float foveationGainY, float foveationArea, float foveationMinimum)` | 设置注视点渲染细节参数 | **是** |

枚举：`FoveationLevel{None = -1, Low, Med, High, TopHigh}`。

### PXR_HandTracking

**职责**：手势追踪的输入源、射线/捏合状态、关节位置与手型缩放。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| GetSettingState | `bool GetSettingState()` | 手势追踪是否开启 | **是** |
| GetActiveInputDevice | `ActiveInputDevice GetActiveInputDevice()` | 当前激活的输入设备 | 否 |
| GetAimState | `bool GetAimState(HandType hand, ref HandAimState aimState)` | 取射线/手指捏合状态与强度 | 否 |
| GetJointLocations | `bool GetJointLocations(HandType hand, ref HandJointLocations jointLocations)` | 取指定手的关节位置 | 否 |
| GetHandScale | `public static bool GetHandScale(HandType hand, ref float scale)` | 取手模型缩放比例 | 否 |

枚举：`HandType{HandLeft=0, HandRight=1}`、`ActiveInputDevice{HeadActive=0, ControllerActive=1, HandTrackingActive=2}`、`HandAimStatus{AimComputed, AimRayValid, AimIndexPinching, AimMiddlePinching, AimRingPinching, AimLittlePinching, AimRayTouched}`（位标志）、`HandLocationStatus{OrientationValid, PositionValid, OrientationTracked, PositionTracked}`、`HandFinger{Thumb=0, Index, Middle, Ring, Pinky}`。
结构体：`HandAimState`、`HandJointLocation`、`HandJointLocations`（`jointCount` 当前返回 **26**）、`Posef`、`Quatf`、`Vector3f`。

### PXR_System

**职责**：SDK 版本、传感器状态、刷新率、性能等级、追踪原点、电量/音量服务。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| GetSDKVersion | `string GetSDKVersion()` | 取 SDK 版本 | 否 |
| GetPredictedDisplayTime | `double GetPredictedDisplayTime()` | 取预测显示时间（毫秒） | 否 |
| SetExtraLatencyMode | `bool SetExtraLatencyMode(int mode)` | 额外延迟模式（0/1/2），只能调一次 | **是** |
| GetSensorStatus | `int GetSensorStatus()` | 传感器状态：0 null / 1 3DoF / 3 6DoF | 否 |
| SetSystemDisplayFrequency | `void SetSystemDisplayFrequency(float rate)` | 设置刷新率，仅 72/90/120 | 否 |
| GetSystemDisplayFrequency | `float GetSystemDisplayFrequency()` | 取当前刷新率 | 否 |
| GetPredictedMainSensorStateNew | `int GetPredictedMainSensorStateNew(ref PxrSensorState2 sensorState, ref int sensorFrameIndex)` | 取主传感器预测状态 | 否 |
| ContentProtect | `int ContentProtect(int data)` | 开关内容保护（0 关 / 1 开）。**注意：文档有详情节但未列入方法总表** | 否 |
| EnableFaceTracking | `void EnableFaceTracking(bool enable)` | 开关面部追踪 | **是** |
| EnableLipSync | `void EnableLipSync(bool enable)` | 开关唇动同步 | **是** |
| GetFaceTrackingData | `void GetFaceTrackingData(Int64 ts, GetDataType flags, ref PxrFaceTrackingInfo faceTrackingInfo)` | 取面部追踪数据 | **是** |
| SetPerformanceLevels | `int SetPerformanceLevels(PxrPerfSettings which, PxrSettingsLevel level)` | 设置 CPU/GPU 性能等级 | 否 |
| GetPerformanceLevels | `PxrSettingsLevel GetPerformanceLevels(PxrPerfSettings which)` | 取 CPU/GPU 性能等级 | 否 |
| SetEyeFOV | `int SetEyeFOV(EyeType eye, float fovLeft, float fovRight, float fovUp, float fovDown)` | 设置单眼四方向 FOV | **是** |
| SetFaceTrackingStatus | `int SetFaceTrackingStatus(PxrFtLipsyncValue value)` | 切换面部追踪模式 | **是** |
| SetTrackingOrigin | `void SetTrackingOrigin(PxrTrackingOrigin originMode)` | 设置追踪原点模式（Device / Floor） | 否 |
| GetTrackingOrigin | `void GetTrackingOrigin(out PxrTrackingOrigin originMode)` | 取追踪原点模式 | 否 |
| StartBatteryReceiver | `bool StartBatteryReceiver(string objName)` | 为指定对象开启电量服务 | 否 |
| StopBatteryReceiver | `bool StopBatteryReceiver()` | 关闭电量服务 | 否 |
| SetCommonBrightness | `bool SetCommonBrightness(int brightness)` | 设置头显亮度 [0,255] | **是** |
| GetCommonBrightness | `int GetCommonBrightness()` | 取头显亮度 | **是** |
| GetScreenBrightnessLevel | `int[] GetScreenBrightnessLevel()` | 取屏幕亮度档位数组 | **是** |
| SetScreenBrightnessLevel | `void SetScreenBrightnessLevel(int brightness, int level)` | 设置屏幕亮度档位 | **是** |
| StartAudioReceiver | `bool StartAudioReceiver(string objName)` | 为指定对象开启音量服务 | 否 |
| StopAudioReceiver | `bool StopAudioReceiver()` | 关闭音量服务 | 否 |
| GetMaxVolumeNumber | `int GetMaxVolumeNumber()` | 取最大音量 | 否 |
| GetCurrentVolumeNumber | `int GetCurrentVolumeNumber()` | 取当前音量 [0,15] | 否 |
| VolumeUp | `bool VolumeUp()` | 音量+ | 否 |
| VolumeDown | `bool VolumeDown()` | 音量- | 否 |
| SetVolumeNum | `bool SetVolumeNum(int volume)` | 设置音量 [0,15] | 否 |
| GetDisplayFrequenciesAvailable | 文档未给出签名，仅示出实现 `PXR_Plugin.System.UPxr_GetDisplayFrequenciesAvailable()` | 取可用刷新率列表（Hz） | 否 |

枚举/参数类型（文档提及）：`PxrPerfSettings{CPU, GPU}`、`PxrSettingsLevel{POWER_SAVINGS, SUSTAINED_LOW, SUSTAINED_HIGH, BOOST}`、`PxrTrackingOrigin`（Device / Floor）、`EyeType{LeftEye, RightEye, BothEye}`、`GetDataType{PXR_GET_FACE_DATA_DEFAULT, PXR_GET_FACE_DATA, PXR_GET_LIP_DATA, PXR_GET_FACELIP_DATA}`、`PxrFtLipsyncValue{STOP_FT, STOP_LIPSYNC, START_FT, START_LIPSYNC}`、结构体 `PxrSensorState2`、`PxrFaceTrackingInfo`。

### PXR_CameraImage

**职责**：查询相机能力、创建设备与采集会话、取图像原始 buffer 与内外参。全部返回 `PxrResult`。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| GetAvailableCameras | `public static PxrResult GetAvailableCameras(out XrCameraIdPICO[] cameraIds)` | 取可用相机 ID | 否 |
| GetCameraPropertyTypesAvailable | `public static PxrResult GetCameraPropertyTypesAvailable(XrCameraIdPICO cameraId, out XrCameraPropertyTypePICO[] propertyTypes)` | 取支持的属性类型 | 否 |
| GetCameraFacingProperties | `public static PxrResult GetCameraFacingProperties(XrCameraIdPICO cameraId, out XrCameraFacingPICO propertyValue)` | 取朝向属性 | 否 |
| GetCameraPositionProperties | `public static PxrResult GetCameraPositionProperties(XrCameraIdPICO cameraId, out XrCameraPositionPICO propertyValue)` | 取位置属性 | 否 |
| GetCameraCameraTypeProperties | `public static PxrResult GetCameraCameraTypeProperties(XrCameraIdPICO cameraId, out XrCameraTypePICO propertyValue)` | 取相机类型属性 | 否 |
| GetCameraCapabilityAvailable | `public static PxrResult GetCameraCapabilityAvailable(XrCameraIdPICO cameraId, out XrCameraCapabilityTypePICO[] capabilitys)` | 取支持的能力类别 | 否 |
| GetCameraImageFpsCapability | `public static PxrResult GetCameraImageFpsCapability(XrCameraIdPICO cameraId, out XrCameraImageFpsPICO[] imageFps)` | 取支持帧率 | 否 |
| GetCameraCameraModelCapability | `public static PxrResult GetCameraCameraModelCapability(XrCameraIdPICO cameraId, out XrCameraModelPICO[] cameraModels)` | 取支持相机模型 | 否 |
| GetCameraDataTransferTypeCapability | `public static PxrResult GetCameraDataTransferTypeCapability(XrCameraIdPICO cameraId, out XrCameraDataTransferTypePICO[] dataTransferTypes)` | 取支持传输类型 | 否 |
| GetCameraImageFormatCapability | `public static PxrResult GetCameraImageFormatCapability(XrCameraIdPICO cameraId, out XrCameraImageFormatPICO[] formats)` | 取支持图像格式 | 否 |
| GetCameraImageResolutionCapability | `public static PxrResult GetCameraImageResolutionCapability(XrCameraIdPICO cameraId, out PxrExtent2Di[] resolutions)` | 取支持分辨率 | 否 |
| CreateCameraDeviceAsync | `public static async Task<PxrResult> CreateCameraDeviceAsync(XrCameraIdPICO cameraId, CancellationToken token = default)` | 异步创建相机设备句柄 | 否 |
| CreateCameraCaptureSessionAsync | `public static async Task<PxrResult> CreateCameraCaptureSessionAsync(XrCameraIdPICO cameraId, int width, int height, XrCameraImageFpsPICO fps, XrCameraImageFormatPICO format, XrCameraDataTransferTypePICO transferType, XrCameraModelPICO model, CancellationToken token = default)` | 异步创建采集会话 | 否 |
| DestroyCameraDevice | `public static PxrResult DestroyCameraDevice(XrCameraIdPICO cameraId)` | 销毁相机设备 | 否 |
| DestroyCameraCaptureSession | `public static PxrResult DestroyCameraCaptureSession(XrCameraIdPICO cameraId)` | 销毁采集会话 | 否 |
| GetCameraIntrinsics | `public static PxrResult GetCameraIntrinsics(XrCameraIdPICO cameraId, out XrCameraIntrinsics intrinsics)` | 取相机内参 | 否 |
| GetCameraExtrinsics | `public static PxrResult GetCameraExtrinsics(XrCameraIdPICO cameraId, out XrCameraExtrinsics extrinsics)` | 取相机外参 | 否 |
| BeginCameraCapture | `public static PxrResult BeginCameraCapture(XrCameraIdPICO cameraId)` | 开始采集 | 否 |
| EndCameraCapture | `public static PxrResult EndCameraCapture(XrCameraIdPICO cameraId)` | 结束采集 | 否 |
| AcquireCameraImage | `public static PxrResult AcquireCameraImage(XrCameraIdPICO deviceId, Int64 lastCaptureTime, out ulong imageId, out Int64 captureTime)` | 取 `lastCaptureTime` 之后的最新一帧（传 0 取最早可用帧） | 否 |
| ReleaseCameraImage | `public static PxrResult ReleaseCameraImage(XrCameraIdPICO deviceId, ulong imageId)` | 释放已取得的图像 | 否 |
| GetCameraImageData | `public static PxrResult GetCameraImageData(XrCameraIdPICO deviceId, ulong imageId, out XrCameraImageDataRawBuffer rawBufferData)` | 取原始 buffer 数据 | 否 |

枚举：`XrCameraIdPICO{XR_CAMERA_ID_RGB_LEFT_PICO=1, XR_CAMERA_ID_RGB_RIGHT_PICO=2}`、`XrCameraPropertyTypePICO`、`XrCameraFacingPICO{XR_CAMERA_FACING_WORLD_PICO=1}`、`XrCameraPositionPICO{UNSPECIFIED=1, LEFT=2, RIGHT=3}`、`XrCameraTypePICO{XR_CAMERA_TYPE_PASSTHROUGH_COLOR_PICO=1}`、`XrCameraCapabilityTypePICO`、`XrCameraDataTransferTypePICO{XR_CAMERA_DATA_TRANSFER_TYPE_RAW_BUFFER_PICO=1}`、`XrCameraImageFormatPICO{XR_CAMERA_IMAGE_FORMAT_RGBA_8888_PICO=1}`、`XrCameraModelPICO{XR_CAMERA_MODEL_PINHOLE_PICO=1}`、`XrCameraImageFpsPICO{XR_CAMERA_IMAGE_FPS_30_PICO=1, XR_CAMERA_IMAGE_FPS_60_PICO=2}`。

### PXR_Input

**职责**：手柄状态/位姿预测/连接检测，以及触觉振动（现行 Haptic 组 + 大量已废弃的旧振动组）。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| GetControllerStatus | `public static ControllerStatus GetControllerStatus(Controller controller)` | 取手柄状态枚举 | 否 |
| InputDeviceChanged | `public static Action<ActiveInputDevice> InputDeviceChanged;` | 输入源切换回调（手/手柄） | 否 |
| GetDominantHand | `Controller GetDominantHand()` | 取主手手柄 | **是** |
| SetDominantHand | `void SetDominantHand(Controller controller)` | 设置主手手柄 | **是** |
| SetControllerVibration | `void SetControllerVibration(float strength, int time, Controller controller)` | 旧振动接口 | **是** |
| GetControllerDeviceType | `ControllerDevice GetControllerDeviceType()` | 取设备型号 | 否 |
| IsControllerConnected | `bool IsControllerConnected(Controller controller)` | 手柄是否连接 | 否 |
| SetControllerOriginOffset | `void SetControllerOriginOffset(Controller controller, Vector3 offset)` | 设置手柄显示位置偏移 | 否 |
| GetControllerPredictRotation | `Quaternion GetControllerPredictRotation(Controller controller, double predictTime)` | 预测手柄朝向 | 否 |
| GetControllerPredictPosition | `Vector3 GetControllerPredictPosition(Controller controller, double predictTime)` | 预测手柄位置 | 否 |
| SetControllerVibrationEvent | `int SetControllerVibrationEvent(UInt32 hand, int frequency, float strength, int time)` | 事件式振动 | **是** |
| StopControllerVCMotor | `int StopControllerVCMotor(int sourceId)` | 停止音频驱动振动 | **是** |
| StartControllerVCMotor | `int StartControllerVCMotor(string file, VibrateController vibrateController)` | 由音频文件驱动振动 | **是** |
| SetControllerAmp | `int SetControllerAmp(float mode)` | 设置音频振动幅度 | **是** |
| StartVibrateBySharem | `int StartVibrateBySharem(AudioClip audioClip, VibrateController vibrateController, ChannelFlip channelFlip, ref int sourceId)` | AudioClip 驱动振动 | **是** |
| StartVibrateBySharem | `int StartVibrateBySharem(float[] data, VibrateController vibrateController, int buffersize, int frequency, int channelMask, ChannelFlip channelFlip, ref int sourceId)` | PCM 驱动振动（重载） | **是** |
| SaveVibrateByCache | `int SaveVibrateByCache(AudioClip audioClip, VibrateController vibrateController, ChannelFlip channelFlip, CacheConfig cacheConfig, ref int sourceId)` | 缓存振动数据 | **是** |
| SaveVibrateByCache | `int SaveVibrateByCache(float[] data, VibrateController vibrateController, int buffersize, int frequency, int channelMask, ChannelFlip channelFlip, CacheConfig cacheConfig, ref int sourceId)` | 缓存振动数据（重载） | **是** |
| StartVibrateByCache | `int StartVibrateByCache(int sourceId)` | 播放缓存振动 | **是** |
| ClearVibrateByCache | `int ClearVibrateByCache(int sourceId)` | 清除缓存振动 | **是** |
| StartVibrateByPHF | `int StartVibrateByPHF(TextAsset phfText, ref int sourceId, VibrateController vibrateController, ChannelFlip channelFlip, float amp)` | PHF 驱动振动 | **是** |
| PauseVibrate | `int PauseVibrate(int sourceId)` | 暂停 PHF 振动 | **是** |
| ResumeVibrate | `int ResumeVibrate(int sourceId)` | 恢复 PHF 振动 | **是** |
| UpdateVibrateParams | `int UpdateVibrateParams(int sourceId, VibrateController vibrateController, ChannelFlip channelFlip, float amp)` | 动态更新振动参数 | **是** |
| GetBodyTrackingPose | `int GetBodyTrackingPose(double predictTime, ref BodyTrackerResult bodyTrackerResult)` | 取全身骨骼位姿 | **是** |
| GetMotionTrackerConnectStateWithID | `public static int GetMotionTrackerConnectStateWithID(ref PxrMotionTracker1ConnectState state)` | 取追踪器连接数与 ID | **是** |
| GetMotionTrackerBattery | `public static int GetMotionTrackerBattery(int trackerId, ref int battery)` | 取追踪器电量 | **是** |
| GetMotionTrackerCalibState | `public static int GetMotionTrackerCalibState(ref int calibrated)` | 追踪器是否完成标定 | **是** |
| SetBodyTrackingMode | `public static int SetBodyTrackingMode(BodyTrackingMode mode)` | 设置体追踪模式 | **是** |
| SetBodyTrackingBoneLength | `public static int SetBodyTrackingBoneLength(BodyTrackingBoneLength boneLength)` | 设置骨骼长度 | **是** |
| SendHapticImpulse | `void SendHapticImpulse(VibrateType vibrateType, float amplitude, int duration, int frequency)` | 发送触觉脉冲（现行） | 否 |
| SendHapticBuffer | `int SendHapticBuffer(VibrateType vibrateType, AudioClip audioClip, ChannelFlip channelFlip, ref int sourceId, CacheType cacheType)` | AudioClip 触觉缓冲（现行） | 否 |
| SendHapticBuffer | `int SendHapticBuffer(VibrateType vibrateType, float[] pcmData, int buffersize, int frequency, int channelMask, ChannelFlip channelFlip, ref int sourceId, CacheType cacheType)` | PCM 触觉缓冲（重载） | 否 |
| SendHapticBuffer | `int SendHapticBuffer(VibrateType vibrateType, TextAsset phfText, ChannelFlip channelFlip, float amplitudeScale, ref int sourceId)` | PHF 触觉缓冲（重载） | 否 |
| StopHapticBuffer | `int StopHapticBuffer(int sourceId, bool clearCache)` | 停止指定触觉缓冲 | 否 |
| PauseHapticBuffer | `int PauseHapticBuffer(int sourceId)` | 暂停 | 否 |
| ResumeHapticBuffer | `int ResumeHapticBuffer(int sourceId)` | 恢复 | 否 |
| StartHapticBuffer | `int StartHapticBuffer(int sourceId)` | 开始播放 | 否 |
| UpdateHapticBuffer | `int UpdateHapticBuffer(int sourceId, VibrateType vibrateType, ChannelFlip channelFlip, float amplitudeScale)` | 更新参数 | 否 |
| CreateHapticStream | `int CreateHapticStream(string phfVersion, UInt32 frameDurationMs, ref VibrateInfo hapticInfo, float speed, ref int id)` | 创建触觉流 | **是** |
| WriteHapticStream | `int WriteHapticStream(int id, ref PxrPhfParamsNum frames, UInt32 numFrames)` | 写入触觉流 | **是** |
| SetHapticStreamSpeed | `int SetHapticStreamSpeed(int id, float speed)` | 设置流速 | **是** |
| GetHapticStreamSpeed | `int GetHapticStreamSpeed(int id, ref float speed)` | 取流速 | **是** |
| GetHapticStreamCurrentFrameSequence | `int GetHapticStreamCurrentFrameSequence(int id, ref UInt64 frameSequence)` | 取当前播放帧序号 | **是** |
| StartHapticStream | `int StartHapticStream(int source_id)` | 开始传输 | **是** |
| StopHapticStream | `int StopHapticStream(int source_id)` | 停止传输 | **是** |
| RemoveHapticStream | `int RemoveHapticStream(int source_id)` | 移除流 | **是** |
| AnalysisHapticStreamPHF | `PxrPhfFile AnalysisHapticStreamPHF(TextAsset phfText)` | 解析 PHF 文件 | **是** |
| ResetController | 文档方法表列出，但页面**无详情节、无签名** | PICO G3 上重置手柄 | **是** |
| SetArmModelParameters | 文档方法表列出，但页面**无详情节、无签名** | PICO G3 上设置手臂模型参数 | **是** |
| GetControllerHandness | 文档方法表列出，但页面**无详情节、无签名** | PICO G3 上取系统主手 | 否 |

枚举：`ControllerDevice{G2=3, Neo2, Neo3, PICO_4, G3, PICO_4U, NewController=10}`、`Controller{LeftController, RightController}`、`VibrateController{No=0, Left=1, Right=2, LeftAndRight=3}`、`VibrateType{None=0, LeftController=1, RightController=2, BothController=3, LeftPICO4U=4, RightPICO4U=8, BothPICO4U=12}`、`CacheType{DontCache=0, CacheAndVibrate=1, CacheNoVibrate=2}`、`ChannelFlip{No, Yes}`、`CacheConfig{CacheAndVibrate=1, CacheNoVibrate=2}`、`ControllerStatus{Static=0, SixDof, ThreeDof, Sleep, CollidedIn3Dof, CollidedIn6Dof}`。

### PXR_MixedReality

**职责**：Sense Data Provider 生命周期、空间锚点（本地/共享）、场景锚点数据、VST 效果。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| StartSenseDataProvider | `async Task<PxrResult> StartSenseDataProvider(PxrSenseDataProviderType type, CancellationToken token)` | 启动 SpatialAnchor / SceneCapture 提供方 | 否 |
| GetSenseDataProviderState | `PxrResult GetSenseDataProviderState(PxrSenseDataProviderType type, out PxrSenseDataProviderState state)` | 取提供方状态 | 否 |
| StopSenseDataProvider | `PxrResult StopSenseDataProvider(PxrSenseDataProviderType type)` | 停止提供方 | 否 |
| CreateSpatialAnchorAsync | `async Task<(PxrResult result, ulong anchorHandle, Guid uuid)> CreateSpatialAnchorAsync(Vector3 position, Quaternion rotation, CancellationToken token)` | 在内存中创建空间锚点 | 否 |
| PersistSpatialAnchorAsync | `async Task<PxrResult> PersistSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)` | 持久化到本地磁盘 | 否 |
| UnPersistSpatialAnchorAsync | `async Task<PxrResult> UnPersistSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)` | 取消持久化 | 否 |
| DestroyAnchor | `PxrResult DestroyAnchor(ulong anchorHandle)` | 销毁内存中的锚点 | 否 |
| GetAnchorUuid | `PxrResult GetAnchorUuid(ulong anchorHandle, out Guid uuid)` | 取锚点 UUID | 否 |
| LocateAnchor | `PxrResult LocateAnchor(ulong anchorHandle, out Vector3 position, out Quaternion rotation)` | 取锚点实时位姿 | 否 |
| QuerySpatialAnchorAsync | `async Task<(PxrResult result, List<ulong> anchorHandleList)> QuerySpatialAnchorAsync(Guid[] uuids, CancellationToken token)` | 加载空间锚点（同时只能一次） | 否 |
| QuerySpatialAnchorObjectsAsync | `public static async Task<(PxrResult result, List<GameObject> spatialAnchorObjects)> QuerySpatialAnchorObjectsAsync(Guid[] uuids = null, CancellationToken token = default)` | 查询挂了 PXR_Spatial Anchor (Script) 的锚点对象。**注意文档方法表里写作 `QuerySpatialAnchorObjectsAsyn`（缺尾 c），签名以代码块为准** | 否 |
| StartSceneCaptureAsync | `async Task<PxrResult> StartSceneCaptureAsync(CancellationToken token)` | 拉起 Room Capture 应用 | 否 |
| QuerySceneAnchorAsync | `async Task<(PxrResult result, List<ulong> anchorHandleList)> QuerySceneAnchorAsync(PxrSemanticLabel[] labels, CancellationToken token)` | 按语义标签加载场景锚点 | 否 |
| QuerySceneAnchorAsync | `async Task<(PxrResult result, Dictionary<ulong, Guid> anchorDictionary)> QuerySceneAnchorAsync(CancellationToken token)` | 加载全部场景锚点（重载） | 否 |
| GetSceneAnchorComponentTypes | `PxrResult GetSceneAnchorComponentTypes(ulong anchorHandle, out PxrSceneComponentType[] types)` | 取场景锚点组件类型 | 否 |
| GetSceneSemanticLabel | `PxrResult GetSceneSemanticLabel(ulong anchorHandle, out PxrSemanticLabel label)` | 取语义标签 | 否 |
| GetSceneBox3DData | `PxrResult GetSceneBox3DData(ulong anchorHandle, out Vector3 position, out Quaternion rotation, out Vector3 extent)` | 取 3D box 信息 | 否 |
| GetSceneBox2DData | `PxrResult GetSceneBox2DData(ulong anchorHandle, out Vector2 offset, out Vector2 extent)` | 取 2D box 信息 | 否 |
| GetScenePolygonData | `PxrResult GetScenePolygonData(ulong anchorHandle, out Vector2[] vertices)` | 取多边形信息 | 否 |
| UploadSpatialAnchorAsync | `async Task<(PxrResult result, Guid uuid)> UploadSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)` | 上传为共享空间锚点 | 否 |
| DownloadSharedSpatialAnchorAsync | `async Task<PxrResult> DownloadSharedSpatialAnchorAsync(Guid uuid, CancellationToken token)` | 下载共享空间锚点 | 否 |
| CreateAnchorEntity | `PxrResult CreateAnchorEntity(Vector3 position, Quaternion rotation, out ulong taskId)` | 旧版创建锚点实体 | **是** |
| DestroyAnchorEntity | `PxrResult DestroyAnchorEntity(ulong handle)` | 旧版销毁锚点实体 | **是** |
| GetAnchorPose | `PxrResult GetAnchorPose(ulong handle, out Quaternion orientation, out Vector3 position)` | 旧版取锚点位姿 | **是** |
| GetAnchorEntityUuid | `PxrResult GetAnchorEntityUuid(ulong handle, out Guid uuid)` | 旧版取 UUID | **是** |
| PersistAnchorEntity | `PxrResult PersistAnchorEntity(ulong[] anchorHandles, PxrPersistLocation location, out ulong taskId)` | 旧版持久化 | **是** |
| UnPersistAnchorEntity | `PxrResult UnPersistAnchorEntity(ulong[] anchorHandles, PxrPersistLocation location, out ulong taskId)` | 旧版取消持久化 | **是** |
| ClearPersistedAnchorEntity | `PxrResult ClearPersistedAnchorEntity(PxrPersistLocation location, out ulong taskId)` | 旧版清空持久化 | **是** |
| GetAnchorComponentFlags | `PxrResult GetAnchorComponentFlags(ulong anchorHandle, out PxrAnchorComponentTypeFlags[] flags)` | 旧版取组件标志 | **是** |
| LoadAnchorEntityByUuidFilter | `PxrResult LoadAnchorEntityByUuidFilter(out ulong taskId, Guid[] uuids)` | 旧版按 UUID 加载 | **是** |
| LoadAnchorEntityBySceneFilter | `PxrResult LoadAnchorEntityBySceneFilter(PxrSpatialSceneDataTypeFlags[] flags, out ulong taskId)` | 旧版按场景类型加载 | **是** |
| GetAnchorEntityLoadResults | `PxrResult GetAnchorEntityLoadResults(ulong taskId, uint count, out Dictionary<ulong, Guid> loadedAnchors)` | 旧版取加载结果 | **是** |
| StartSpatialSceneCapture | `PxrResult StartSpatialSceneCapture(out ulong taskId)` | 旧版拉起房间标定 | **是** |
| GetAnchorVolumeInfo | `PxrResult GetAnchorVolumeInfo(ulong anchorHandle, out Vector3 center, out Vector3 extent)` | 旧版取体积信息 | **是** |
| GetAnchorPlanePolygonInfo | `PxrResult GetAnchorPlanePolygonInfo(ulong anchorHandle, out Vector3[] vertices)` | 旧版取多边形信息 | **是** |
| GetAnchorPlaneBoundaryInfo | `PxrResult GetAnchorPlaneBoundaryInfo(ulong anchorHandle, out Vector3 center, out Vector2 extent)` | 旧版取矩形边界 | **是** |
| GetAnchorSceneLabel | `PxrResult GetAnchorSceneLabel(ulong anchorHandle, out PxrSceneLabel label)` | 旧版取场景标签 | **是** |
| EnableVideoSeeThrough | `int EnableVideoSeeThrough(bool state)` | 开关 video seethrough | **是** |
| EnableVideoSeeThroughEffect | `int EnableVideoSeeThroughEffect(bool value)` | 开关 VST 效果 | 否 |
| SetVideoSeeThroughEffect | `int SetVideoSeeThroughEffect(PxrLayerEffect type, float value, float duration)` | 设置 VST 效果参数 | 否 |
| SetVideoSeeThroughLut | `int SetVideoSeeThroughLut(Texture2D texture, int row, int col)` | 设置 VST 的 LUT 贴图 | 否 |

枚举：`PxrSenseDataProviderType{SpatialAnchor, SceneCapture}`（取自参数说明）、`PxrSenseDataProviderState{Initialized, Running, Stopped}`、`PxrSceneComponentType{Location=0, Semantic, Box2D, Polygon, Box3D, TriangleMesh=5}`、`PxrSemanticLabel{Unknown=0, Floor, Ceiling, Wall, Door, Window, Opening, Table, Sofa, Chair, Human=10, VirtualWall=18}`、`PxrResult`（见关键规则 5）。

### PXR_MotionTracking

**职责**：眼动 / 面部 / 全身动捕 / PICO Motion Tracker 与外接设备。返回值统一为 `int`（`TrackingStateCode`）。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| WantEyeTrackingService | `int WantEyeTrackingService()` | 申请眼动服务 | **是** |
| GetEyeTrackingSupported | `int GetEyeTrackingSupported(ref bool supported, ref int supportedModesCount, ref EyeTrackingMode[] supportedModes)` | 设备是否支持眼动 | 否 |
| StartEyeTracking | `int StartEyeTracking(ref EyeTrackingStartInfo startInfo)` | 启动眼动 | 否 |
| StopEyeTracking | `int StopEyeTracking(ref EyeTrackingStopInfo stopInfo)` | 停止眼动 | 否 |
| GetEyeTrackingState | `int GetEyeTrackingState(ref bool isTracking, ref EyeTrackingState state)` | 取眼动状态 | 否 |
| GetEyeTrackingData | `int GetEyeTrackingData(ref EyeTrackingDataGetInfo getInfo, ref EyeTrackingData data)` | 取眼动数据 | 否 |
| GetEyeOpenness | `int GetEyeOpenness(ref float leftEyeOpenness, ref float rightEyeOpenness)` | 取双眼睁闭程度 | 否 |
| GetEyePupilInfo | `int GetEyePupilInfo(ref EyePupilInfo eyePupilPosition)` | 取瞳孔信息 | 否 |
| GetPerEyePose | `int GetPerEyePose(ref long timestamp, ref Posef leftEyePose, ref Posef rightPose)` | 取左右眼位姿 | 否 |
| GetEyeBlink | `int GetEyeBlink(ref long timestamp, ref bool isLeftBlink, ref bool isRightBlink)` | 取眨眼状态 | 否 |
| WantFaceTrackingService | `int WantFaceTrackingService()` | 申请面部追踪服务 | **是** |
| GetFaceTrackingSupported | `unsafe int GetFaceTrackingSupported(ref bool supported, ref int supportedModesCount, ref FaceTrackingMode[] supportedModes)` | 是否支持面部追踪 | **是** |
| StartFaceTracking | `int StartFaceTracking(ref FaceTrackingStartInfo startInfo)` | 启动面部追踪 | **是** |
| StopFaceTracking | `int StopFaceTracking(ref FaceTrackingStopInfo stopInfo)` | 停止面部追踪 | **是** |
| GetFaceTrackingState | `int GetFaceTrackingState(ref bool isTracking, ref FaceTrackingState state)` | 取面部追踪状态 | **是** |
| GetFaceTrackingData | `int GetFaceTrackingData(ref FaceTrackingDataGetInfo getInfo, ref FaceTrackingData data)` | 取面部追踪数据 | **是** |
| BodyTrackingAbnormalCalibrationData | `public static Action<int, int> BodyTrackingAbnormalCalibrationData;` | 标定异常回调 | **是** |
| BodyTrackingStateError | `public static Action<BodyTrackingStatusCode, BodyTrackingErrorCode> BodyTrackingStateError;` | 体追踪状态/错误码回调 | **是** |
| BodyTrackingAction | `public static Action<int, BodyActionList> BodyTrackingAction;` | 骨骼节点动作变化回调 | **是** |
| StartMotionTrackerCalibApp | `int StartMotionTrackerCalibApp()` | 拉起 PICO Motion Tracker 标定应用 | 否 |
| GetBodyTrackingSupported | `int GetBodyTrackingSupported(ref bool supported)` | 是否支持体追踪 | 否 |
| StartBodyTracking | `int StartBodyTracking(BodyTrackingMode mode, BodyTrackingBoneLength boneLength)` | 启动体追踪 | 否 |
| StopBodyTracking | `int StopBodyTracking()` | 停止体追踪 | 否 |
| GetBodyTrackingState | `int GetBodyTrackingState(ref bool isTracking, ref BodyTrackingState state)` | 取体追踪状态与异常原因 | 否 |
| GetBodyTrackingData | `int GetBodyTrackingData(ref BodyTrackingGetDataInfo getInfo, ref BodyTrackingData data)` | 取体追踪数据 | 否 |
| MotionTrackerNumberOfConnections | `public static Action<int, int> MotionTrackerNumberOfConnections;` | 连接数变化回调 | **是** |
| MotionTrackerBatteryLevel | `public static Action<int, int> MotionTrackerBatteryLevel;` | 电量变化回调 | **是** |
| MotionTrackerKeyAction | `public static Action<MotionTrackerEventData> MotionTrackerKeyAction;` | 按键回调 | **是** |
| MotionTrackingModeChangedAction | `public static Action<MotionTrackerMode> MotionTrackingModeChangedAction;` | 追踪模式变化回调 | **是** |
| GetMotionTrackerConnectStateWithSN | `int GetMotionTrackerConnectStateWithSN(ref MotionTrackerConnectState connectState)` | 取连接数与 SN | **是** |
| GetMotionTrackerDeviceType | `MotionTrackerType GetMotionTrackerDeviceType()` | 取追踪器版本 | **是** |
| CheckMotionTrackerModeAndNumber | `int CheckMotionTrackerModeAndNumber(MotionTrackerMode mode, MotionTrackerNum number)` | 校验模式与数量 | **是** |
| GetMotionTrackerMode | `MotionTrackerMode GetMotionTrackerMode()` | 取当前追踪模式 | **是** |
| GetMotionTrackerLocations | `int GetMotionTrackerLocations(TrackerSN trackerSN, ref MotionTrackerLocations locations, ref MotionTrackerConfidence confidence, double predictTime)` | 取追踪器位姿（旧） | **是** |
| ExtDevConnectAction | `public static Action<ExtDevConnectEventData> ExtDevConnectAction;` | 外接设备连接回调 | **是** |
| ExtDevBatteryAction | `public static Action<ExtDevBatteryEventData> ExtDevBatteryAction;` | 外接设备电量回调 | **是** |
| ExtDevPassDataAction | `public static Action<int> ExtDevPassDataAction;` | 透传数据事件，需监听后再调 `GetExtDevTrackerByPassData` | 否 |
| GetExtDevTrackerConnectState | `int GetExtDevTrackerConnectState(ref ExtDevTrackerConnectState connectState)` | 取外接设备连接状态 | **是** |
| SetExtDevTrackerMotorVibrate | `int SetExtDevTrackerMotorVibrate(ref ExtDevTrackerMotorVibrate motorVibrate)` | 外接设备振动 | **是** |
| SetExtDevTrackerPassDataState | `int SetExtDevTrackerPassDataState(bool state)` | 设置透传接口状态 | 否 |
| SetExtDevTrackerByPassData | `int SetExtDevTrackerByPassData(ref ExtDevTrackerPassData passData)` | 向外接设备透传数据 | **是** |
| GetExtDevTrackerByPassData | `int GetExtDevTrackerByPassData(ref ExtDevTrackerPassDataArray passData, ref int realLength)` | 取透传数据 | **是** |
| GetExtDevTrackerBattery | `int GetExtDevTrackerBattery(ref TrackerSN trackerSN, ref int battery, ref int charger)` | 取外接设备电量 | **是** |
| GetExtDevTrackerKeyData | `int GetExtDevTrackerKeyData(ref TrackerSN trackerSN, ref ExtDevTrackerKeyData keyData)` | 取外接设备按键值 | **是** |
| RequestMotionTrackerCompleteAction | 回调，文档**未给出泛型参数签名** | 追踪器是否连接成功 | 否 |
| MotionTrackerConnectionAction | 回调，文档**未给出泛型参数签名** | 追踪器连接状态变化 | 否 |
| MotionTrackerPowerKeyAction | 回调，文档**未给出泛型参数签名** | 追踪器电源键事件 | 否 |
| CheckMotionTrackerNumber | 文档仅给出实现体 `UPxr_CheckMotionTrackerNumber((int)number)`；参数 `number`（期望数量 [0,3]） | 校验已连接追踪器数量 | 否 |
| GetMotionTrackerLocation | 文档仅给出实现体 `UPxr_GetMotionTrackerLocation(trackerid, ref location, ref isValidPose)` | 取单个追踪器位姿（现行） | 否 |
| GetMotionTrackerBattery | 文档仅给出实现体 `UPxr_GetMotionTrackerBatteryState(trackerid, ref battery, ref charger)` | 取追踪器电量（现行） | 否 |
| ExpandDeviceConnectionAction | 回调，文档**未给出泛型参数签名**；返回追踪器 SN + 连接状态（0 断 / 1 连） | 外接设备连接状态变化 | 否 |
| ExpandDeviceBatteryAction | 回调，文档**未给出泛型参数签名** | 外接设备电量/充电状态变化 | 否 |
| SetExpandDeviceVibrate | 文档仅给出实现体 `UPxr_SetExpandDeviceVibrate(deviceid, motorVibrate)` | 外接设备振动（现行） | 否 |
| GetExpandDevice | 文档仅给出实现体 `UPxr_GetExpandDevice(out deviceArray)` | 取外接设备 SN 数组 | 否 |
| SetExpandDeviceCustomData | 文档仅给出实现体 `UPxr_SetExpandDeviceCustomData(ref dataArray)` | 向外接设备发送自定义数据 | 否 |
| GetExpandDeviceCustomData | 文档仅给出实现体 `UPxr_GetExpandDeviceCustomData(out dataArray)` | 取外接设备自定义数据 | 否 |
| GetExpandDeviceBattery | 文档仅给出实现体 `UPxr_GetExpandDeviceBattery(deviceid, ref battery, ref charger)` | 取外接设备电量 | 否 |

枚举：`TrackingStateCode{PXR_MT_SUCCESS=0, PXR_MT_FAILURE=-1, PXR_MT_MODE_NONE=-2, PXR_MT_DEVICE_NOT_SUPPORT=-3, PXR_MT_SERVICE_NEED_START=-4, PXR_MT_ET_PERMISSION_DENIED=-5, PXR_MT_FT_PERMISSION_DENIED=-6, PXR_MT_MIC_PERMISSION_DENIED=-7, PXR_MT_SYSTEM_DENIED=-8, PXR_MT_UNKNOW_ERROR=-9}`、`EyeTrackingMode{PXR_ETM_NONE=-1, PXR_ETM_BOTH=0, PXR_ETM_COUNT=1}`、`PerEyeUsage{LeftEye=0, RightEye=1, Combined=2, EyeCount=3}`、`EyeTrackingDataGetFlags{PXR_EYE_DEFAULT=0, PXR_EYE_POSITION=1<<0, PXR_EYE_ORIENTATION=1<<1}`、`FaceTrackingMode{PXR_FTM_NONE=-1, PXR_FTM_FACE=0, PXR_FTM_LIPS=1, PXR_FTM_FACE_LIPS_VIS=2, PXR_FTM_FACE_LIPS_BS=3}`、`BodyTrackingMode{BTM_FULL_BODY_LOW=0, BTM_FULL_BODY_HIGH=1}`、`BodyTrackingGetDataFlags{PXR_BODY_NONE=0, PXR_BODY_POSE=1<<0, PXR_BODY_ACTION=1<<1, PXR_BODY_VELO_ACC=1<<2}`、`MotionTrackerType{MT_1=1（Beta）, MT_2（Official）}`、`MotionTrackerMode{BodyTracking, MotionTracking}`、`MotionTrackerNum{NONE=0, ONE, TWO, THREE}`、`MotionTrackerConfidence{PXR_STATIC_ACCURATE=0, PXR_6DOF_ACCURATE, PXR_3DOF_NOT_ACCURATE, PXR_6DOF_NOT_ACCURATE}`、`BlendShapeIndex`（面部 BS 索引表，见来源页）。

### PXR_EyeTracking（legacy 类）

**职责**：旧版眼动数据读取；文档首句已声明为 legacy，新代码请用 `PXR_MotionTracking`。全部方法返回 `bool`（true 成功）。

| 方法 | 签名 | 说明 | Deprecated |
|---|---|---|---|
| GetHeadPosMatrix | `bool GetHeadPosMatrix(out Matrix4x4 matrix)` | 取头部 PosMatrix | 类级 legacy |
| GetCombineEyeGazePoint | `bool GetCombineEyeGazePoint(out Vector3 point)` | 取双眼中心点（Unity 相机坐标系，返回值已除以 1000） | 类级 legacy |
| GetCombineEyeGazeVector | `bool GetCombineEyeGazeVector(out Vector3 vector)` | 取双眼合并注视方向 | 类级 legacy |
| GetLeftEyeGazeOpenness | `bool GetLeftEyeGazeOpenness(out float openness)` | 左眼睁闭度 [0.0, 1.0] | 类级 legacy |
| GetRightEyeGazeOpenness | `bool GetRightEyeGazeOpenness(out float openness)` | 右眼睁闭度 [0.0, 1.0] | 类级 legacy |
| GetLeftEyePoseStatus | `bool GetLeftEyePoseStatus(out uint status)` | 左眼数据是否可用（位标志 `EyePoseStatus`） | 类级 legacy |
| GetRightEyePoseStatus | `bool GetRightEyePoseStatus(out uint status)` | 右眼数据是否可用 | 类级 legacy |
| GetCombinedEyePoseStatus | `bool GetCombinedEyePoseStatus(out uint status)` | 合并眼数据是否可用（0 否 / 1 是） | 类级 legacy |
| GetLeftEyePositionGuide | `bool GetLeftEyePositionGuide(out Vector3 position)` | 左眼内眼角图像坐标（左上 (0,0)、右下 (1,1)） | 类级 legacy |
| GetRightEyePositionGuide | `bool GetRightEyePositionGuide(out Vector3 position)` | 右眼内眼角图像坐标 | 类级 legacy |
| GetFoveatedGazeDirection | `bool GetFoveatedGazeDirection(out Vector3 direction)` | View 坐标系（OpenXR 右手系）下的注视方向 | 类级 legacy |
| GetFoveatedGazeTrackingState | `bool GetFoveatedGazeTrackingState(out uint state)` | 注视点数据是否可用（0 否 / 1 是） | 类级 legacy |

`EyePoseStatus` 位标志（来自参数说明）：`GazePointValid=1<<0, GazeVectorValid=1<<1, EyeOpennessValid=1<<2, EyePupilDilationValid=1<<3, EyePositionGuideValid=1<<4, EyePupilPositionValid=1<<5, EyeConvergenceDistanceValid=1<<6, EyeGazePointValid=1<<7, EyeGazeVectorValid=1<<8, PupilDistanceValid=1<<9, ConvergenceDistanceValid=1<<10, PupilDiameterValid=1<<11`。

### PXR_Enterprise（企业设备专用，普通消费设备不可用）

**职责**：企业设备的系统管控面（开关机、按键、网络、投屏、大空间、相机、弹窗等），共 **257 个方法**。文档明确：仅 PICO Neo2 / Neo2 Eye / Neo3 Pro / Neo3 Pro Eye / G2 4K、4K E、4K Plus（系统 4.0.3+）/ PICO 4 Enterprise 支持，**消费设备上不要调用**。本类**不在本文展开签名**，签名请查 [PXR_Enterprise 来源页](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/)。文档方法表中**没有任何 `(Deprecated)` 标记**。

- **服务生命周期（3）**：`InitEnterpriseService`、`BindEnterpriseService`、`UnBindEnterpriseService`
- **设备信息与状态（11）**：`StateGetDeviceInfo`、`GetCpuUsages`、`GetDeviceTemperatures`、`GetHeadTrackingConfidence`、`GetHeadTrackingStatus`、`GetRunningAppProcesses`、`GetFocusedApp`、`GetCurrentLauncher`、`GetDeviceOwner`、`SetDeviceOwner`、`IsInitSettingComplete`
- **电源 / 定时开关机 / 休眠（24）**：`ControlSetDeviceAction`、`ScreenOn`、`ScreenOff`、`AcquireWakeLock`、`ReleaseWakeLock`、`PropertySetScreenOffDelay`、`PropertySetSleepDelay`、`GetScreenOffDelay`、`GetSleepDelay`、`SetSystemAutoSleepTime`、`TimingStartup`、`TimingShutdown`、`OpenTimingStartup`、`CloseTimingStartup`、`OpenTimingShutdown`、`CloseTimingShutdown`、`GetTimingStartupStatus`、`GetTimingShutdownStatus`、`GetTimingStartupStatusTwo`、`GetTimingShutDownStatusTwo`、`SetPowerManageMode`、`GetPowerManageMode`、`SetPowerOffWithUSBCable`、`GetPowerOffWithUSBCable`
- **按键管控（23）**：`PropertySetHomeKey`、`PropertySetHomeKeyAll`、`RemoveControllerHomeKey`、`GetHomeKeyStatus`、`PropertyDisablePowerKey`、`GetPowerKeyStatus`、`EnableEnterKey`、`DisableEnterKey`、`GetEnterKeyStatus`、`EnableVolumeKey`、`DisableVolumeKey`、`GetVolumeKeyStatus`、`EnableBackKey`、`DisableBackKey`、`GetBackKeyStatus`、`ResetAllKeyToDefault`、`SetSystemKeyUsability`、`SwitchVolumeToHomeAndEnter`、`IsVolumeChangeToHomeAndEnter`、`SetHMDVolumeKeyFunc`、`GetHMDVolumeKeyFunc`、`GetControllerKeyState`、`SetControllerKeyState`
- **手柄配对 / 振动 / 电量（12）**：`SetControllerPairTime`、`GetControllerPairTime`、`StartLeftControllerPair`、`MakeLeftControllerUnPair`、`StartRightControllerPair`、`MakeRightControllerUnPair`、`StopControllerPair`、`SetControllerPreferHand`、`SetControllerVibrateAmplitude`、`GetControllerVibrateAmplitude`、`GetControllerBattery`、`GetControllerConnectState`
- **应用与系统更新（22）**：`ControlAPPManager`、`SetAPPAsHome`、`SetLauncher`、`KillAppsByPidOrPackageName`、`KillBackgroundAppsWithWhiteList`、`AppKeepAlive`、`KeepAliveBackground`、`StartActivity`、`StartApp`、`StartService`、`StartForegroundService`、`SendBroadcast`、`SendOrderedBroadcast`、`InjectEvent`、`CustomizeAppLibrary`、`GetAppLibraryHideList`、`AppCopyrightVerify`、`InstallOTAPackage`、`OnlineSystemUpdate`、`OfflineSystemUpdate`、`SetBrowserHomePage`、`GetBrowserHomePage`
- **网络 / Wi-Fi / USB（16）**：`ControlSetAutoConnectWIFI`、`ControlClearAutoConnectWIFI`、`ControlSetAutoConnectWIFIWithErrorCodeCallback`、`GetAutoConnectWiFiConfig`、`ConfigWifi`、`GetConfiguredWifi`、`ConnectWifi`、`SetStaticIpConfigurationtoConnectWifi`、`AllowWifiAutoJoin`、`SetWifiP2PDeviceName`、`GetWifiP2PDeviceName`、`SetUsbTetheringStaticIP`、`GetUsbTetheringStaticIPLocal`、`GetUsbTetheringStaticIPClient`、`SwitchSetUsbConfigurationOption`、`GetUsbConfigurationOption`
- **投屏 Miracast / PICOCast（28）**：`OpenMiracast`、`IsMiracastOn`、`CloseMiracast`、`StartScan`、`StopScan`、`ConnectWifiDisplay`、`DisConnectWifiDisplay`、`ForgetWifiDisplay`、`RenameWifiDisplay`、`SetWDModelsCallback`、`SetWDJsonCallback`、`UpdateWifiDisplays`、`GetConnectedWD`、`GetAutoMiracastConfig`、`PICOCastInit`、`PICOCastSetShowAuthorization`、`PICOCastGetShowAuthorization`、`PICOCastGetUrl`、`PICOCastStopCast`、`PICOCastSetOption`、`PICOCastGetOptionOrStatus`、`SetPicoCastMediaFormat`、`SetScreenCastAudioOutput`、`GetScreenCastAudioOutput`、`SetAcceptCastMode`、`GetAcceptCastMode`、`SetScreenCastMode`、`GetScreenCastMode`
- **截屏 / 录屏（8）**：`Capture`、`Record`、`SetScreenRecordShotRatio`、`GetScreenRecordShotRatio`、`SetScreenResolution`、`GetScreenResolution`、`SetScreenRecordFrameRate`、`GetScreenRecordFrameRate`
- **大空间与地图（15）**：`SwitchLargeSpaceScene`、`GetSwitchLargeSpaceStatus`、`SaveLargeSpaceMaps`、`ExportMaps`、`ImportMaps`、`ImportMapByPath`、`IsMapInEffect`、`SetLargeSpaceMapScale`、`GetLargeSpaceBoundsInfo`、`GetLargeSpaceBoundsInfoWithType`、`OpenLargeSpaceQuickMode`、`CloseLargeSpaceQuickMode`、`SetOriginOfLargeSpaceQuickMode`、`SetBoundaryOfLargeSpaceQuickMode`、`GetLargeSpaceQuickModeInfo`
- **边界 / 地面 / 房间标定（18）**：`StartSetSecureBorder`、`SetDistanceSensitivity`、`GetDistanceSensitivity`、`SetSpeedSensitivity`、`GetSpeedSensitivity`、`SetMRCollisionAlertSensitivity`、`GetMRCollisionAlertSensitivity`、`SetFenceColor`、`GetFenceColor`、`SetFloorHeight`、`GetFloorHeight`、`GotoSeeThroughFloorSetting`、`GotoEnvironmentTextureCheck`、`StartRoomMark`、`ClearRoomMark`、`FreezeScreen`、`Recenter`、`ResetTracking`
- **相机 / VST / 4U 相机组（16）**：`OpenVSTCamera`、`CloseVSTCamera`、`GetCameraParameters`、`AcquireVSTCameraFrame`、`AcquireVSTCameraFrameAntiDistortion`、`SetMarkerInfoCallback`、`ScanQRCode`、`OpenCameraAsyncfor4U`、`Configurefor4U`、`StartPreviewfor4U`、`SetCameraFrameBufferfor4U`、`StartGetImageDatafor4U`、`CloseCamerafor4U`、`GetCameraIntrinsicsfor4U`、`GetCameraExtrinsicsfor4U`、`GetCameraParametersNewfor4U`
- **位姿与 IMU（12）**：`GetPredictedDisplayTime`、`GetPredictedMainSensorState`、`GetPredictedMainSensorState2`、`GetHeadPose`、`GetControllerPose`、`GetSwiftPose`、`GetSwiftTrackerDevices`、`GetHeadIMUData`、`GetControllerIMUData`、`GetSwiftIMUData`、`UseGlobalPose`、`ConvertPoseCoordinate`
- **运动追踪器（6）**：`StartSwiftTrackerPairing`、`UnBondSwiftTracker`、`SetMotionTrackerPredictionCoefficient`、`GetMotionTrackerPredictionCoefficient`、`StartMotionTrackerApp`、`SetMotionTrackerAutoStart`
- **眼动 / IPD / 显示（15）**：`ClearEyeTrackData`、`SetEyeTrackRate`、`GetEyeTrackRate`、`SetTrackFrequency`、`GetTrackFrequency`、`SetIPD`、`OpenIPDDetectionPage`、`SetSingleEyeSource`、`GetSingleEyeSource`、`SetViewVisual`、`GetViewVisual`、`SetScreenBrightness`、`SetPowerOnOffLogo`、`SetVirtualEnvironment`、`GetVirtualEnvironment`
- **虚拟显示（3）**：`CreateVirtualDisplay`、`ReleaseVirtualDisplay`、`SetVirtualDisplaySurface`
- **系统设置 / 本地化 / 文件（15）**：`SetSystemLanguage`、`GetSystemLanguage`、`SetSystemCountryCode`、`GetSystemCountryCode`、`SetTimeZone`、`SetSystemDate`、`SetSystemTime`、`SetSkipInitSettingPage`、`GetSkipInitSettingPage`、`StartVrSettingsItem`、`UPxr_CustomizeSettingsTabStatus`、`UPxr_GetCustomizeSettingsTabStatus`、`SwitchSystemFunction`、`GetSwitchSystemFunctionStatus`、`FileCopy`
- **全局弹窗（10）**：`ShowGlobalMessageDialog`、`HideGlobalMessageDialog`、`ShowGlobalTipsDialog`、`HideGlobalTipsDialog`、`ShowGlobalBigStatusDialog`、`HideGlobalBigStatusDialog`、`ShowGlobalSmallStatusDialog`、`HideGlobalSmallStatusDialog`、`ShowGlobalDialogByType`、`HideGlobalDialogByType`

> 注意：`UPxr_CustomizeSettingsTabStatus` / `UPxr_GetCustomizeSettingsTabStatus` 在文档方法表中就是带 `UPxr_` 前缀的，不要"顺手"去掉前缀。

## DO NOT

| 错误写法 | 正确写法 / 说明 |
|---|---|
| `if (PXR_MotionTracking.GetEyeTrackingData(ref a, ref b)) { }` | 返回 `int`，写 `if (PXR_MotionTracking.GetEyeTrackingData(ref a, ref b) == 0)`（`PXR_MT_SUCCESS`）。 |
| `if (PXR_Input.GetControllerStatus(Controller.LeftController)) { }` | 返回 `ControllerStatus` 枚举；判断连接用 `PXR_Input.IsControllerConnected(...)`。 |
| `PXR_FoveationRendering.SetFoveationLevel(FoveationLevel.High)` | 必须两参：`SetFoveationLevel(FoveationLevel.High, isETFR)`。 |
| `PXR_Input.SetControllerVibration(0.5f, 100, Controller.LeftController)` | 已 Deprecated；用 `PXR_Input.SendHapticImpulse(VibrateType.LeftController, amplitude, duration, frequency)`。 |
| `PXR_MixedReality.CreateAnchorEntity(pos, rot, out taskId)` | 整套 AnchorEntity API 已 Deprecated；用 `StartSenseDataProvider` + `CreateSpatialAnchorAsync`。 |
| `PXR_System.EnableFaceTracking(true)` / `PXR_MotionTracking.StartFaceTracking(...)` | 两处的面部追踪 API 在 v3.4.0 均已 Deprecated；本组语料未给替代类，不要编造替代 API，需查 INTERACTION 域文档确认。 |
| `PXR_EyeTracking.GetCombineEyeGazeVector(out v)` 当作现行接口 | 该类整体为 legacy；现行走 `PXR_MotionTracking.GetEyeTrackingData` / `GetPerEyePose`。 |
| `PXR_Boundary.GetDimensions(BoundaryType.OuterBoundary)` | 该方法只接受 `PlayArea`。 |
| `PXR_System.SetSystemDisplayFrequency(60f)` | 只支持 72 / 90 / 120，其它值无效。 |
| 直接 `PXR_Enterprise.StateGetDeviceInfo(...)` | 必须先 `InitEnterpriseService` → `BindEnterpriseService` 回调成功。且仅企业设备可用。 |
| `PXR_CameraImage.AcquireCameraImage(...)` 后不调 `ReleaseCameraImage` | 每次 Acquire 必须配对 Release，否则图像资源不释放。 |
| `await PXR_CameraImage.GetCameraIntrinsics(...)` | 只有 `CreateCameraDeviceAsync` / `CreateCameraCaptureSessionAsync` 是异步，其余是同步 `PxrResult`。 |
| 并发调用两次 `QuerySpatialAnchorAsync` | 同一时间只允许一次，必须等前一次完成。 |
| `PXR_HandTracking.GetHandScale()` 无参 / 挂在手部组件上 | 静态方法且带参：`PXR_HandTracking.GetHandScale(HandType hand, ref float scale)`。 |
| 用 `VibrateController` 调现行 Haptic API | 现行 `SendHapticImpulse` / `SendHapticBuffer` 用的是 `VibrateType`（两个枚举名字与取值都不同）。 |
| 按 `SetGuardianSystemDisable(true)` = 关闭边界来写代码 | 方法名与文档参数说明（`true: enable`）冲突，必须实测确认后再固化行为。 |

## Legacy API 处置

**判定口径**：文档方法表里带 `(Deprecated)` 前缀 = 已废弃；`PXR_EyeTracking` 是**整类 legacy**（无逐方法标记，但文档首句声明）。

| Legacy 面 | 涉及方法 | 处置 |
|---|---|---|
| `PXR_EyeTracking` 全类 | 12 个 `Get*` 眼动方法 | 只读旧工程时可参考；新代码改用 `PXR_MotionTracking` 的 eye 系接口。来源页首句即给出该指引。 |
| `PXR_Input` 旧振动组 | `SetControllerVibration`、`SetControllerVibrationEvent`、`Start/StopControllerVCMotor`、`SetControllerAmp`、`StartVibrateBySharem`×2、`SaveVibrateByCache`×2、`StartVibrateByCache`、`ClearVibrateByCache`、`StartVibrateByPHF`、`PauseVibrate`、`ResumeVibrate`、`UpdateVibrateParams` | 统一迁到 `SendHapticImpulse` / `SendHapticBuffer` + `Start/Stop/Pause/Resume/UpdateHapticBuffer`。 |
| `PXR_Input` haptic stream 组 | `CreateHapticStream`、`WriteHapticStream`、`Set/GetHapticStreamSpeed`、`GetHapticStreamCurrentFrameSequence`、`Start/Stop/RemoveHapticStream`、`AnalysisHapticStreamPHF` | 全组 Deprecated，不要在新工程引入。 |
| `PXR_Input` 体追踪 / 追踪器组 | `GetBodyTrackingPose`、`GetMotionTrackerConnectStateWithID`、`GetMotionTrackerBattery`、`GetMotionTrackerCalibState`、`SetBodyTrackingMode`、`SetBodyTrackingBoneLength` | 迁到 `PXR_MotionTracking` 的 body / motion tracker 接口（`StartBodyTracking`、`GetBodyTrackingData`、`GetMotionTrackerLocation`、`GetMotionTrackerBattery` 等）。 |
| `PXR_Input` G3 专属 | `ResetController`、`SetArmModelParameters` | 已 Deprecated；且文档页无签名详情节，不要凭方法名硬写调用。 |
| `PXR_MixedReality` AnchorEntity 组 | `CreateAnchorEntity`、`DestroyAnchorEntity`、`GetAnchorPose`、`GetAnchorEntityUuid`、`Persist/UnPersist/ClearPersistedAnchorEntity`、`GetAnchorComponentFlags`、`LoadAnchorEntityByUuidFilter`、`LoadAnchorEntityBySceneFilter`、`GetAnchorEntityLoadResults`、`StartSpatialSceneCapture`、`GetAnchorVolumeInfo`、`GetAnchorPlanePolygonInfo`、`GetAnchorPlaneBoundaryInfo`、`GetAnchorSceneLabel` | 全组 Deprecated；改用 Sense Data Provider + `*Async` 组（见工作流程 2 与 MR.md「空间锚点生命周期」）。 |
| `PXR_MixedReality` VST | `EnableVideoSeeThrough` | 已 Deprecated。同页现存 `EnableVideoSeeThroughEffect` / `SetVideoSeeThroughEffect` / `SetVideoSeeThroughLut`，但文档**未声明**它们是前者的等价替代，开启 VST 的正确路径需查 MR 域文档。 |
| `PXR_System` 面部追踪组 | `EnableFaceTracking`、`EnableLipSync`、`GetFaceTrackingData`、`SetFaceTrackingStatus` | 全组 Deprecated。 |
| `PXR_MotionTracking` 面部追踪组 | `WantFaceTrackingService`、`GetFaceTrackingSupported`、`StartFaceTracking`、`StopFaceTracking`、`GetFaceTrackingState`、`GetFaceTrackingData` | 同样全组 Deprecated：v3.4.0 中面部追踪**两个类都已废弃**，本组语料未给出替代类。 |
| `PXR_MotionTracking` 旧追踪器 / 外接设备组 | `GetMotionTrackerConnectStateWithSN`、`GetMotionTrackerDeviceType`、`CheckMotionTrackerModeAndNumber`、`GetMotionTrackerMode`、`GetMotionTrackerLocations`、`ExtDev*` 系（`ExtDevConnectAction`、`ExtDevBatteryAction`、`GetExtDevTrackerConnectState`、`SetExtDevTrackerMotorVibrate`、`SetExtDevTrackerByPassData`、`GetExtDevTrackerByPassData`、`GetExtDevTrackerBattery`、`GetExtDevTrackerKeyData`）以及 4 个 `MotionTracker*` 回调 | 迁到现行组：`CheckMotionTrackerNumber`、`GetMotionTrackerLocation`（无 s）、`GetMotionTrackerBattery`、`MotionTrackerConnectionAction`、`MotionTrackerPowerKeyAction`、`RequestMotionTrackerCompleteAction`、`ExpandDevice*` 系。注意新旧命名只差单复数或 `ExtDev` → `ExpandDevice`，极易写错。 |
| `PXR_System` 其它 | `SetExtraLatencyMode`、`SetEyeFOV`、`SetCommonBrightness`、`GetCommonBrightness`、`GetScreenBrightnessLevel`、`SetScreenBrightnessLevel` | 已 Deprecated；亮度类需求在企业设备上可考虑 `PXR_Enterprise.SetScreenBrightness`（仅企业设备）。 |
| `PXR_Boundary` | `GetSeeThroughTrackingState`、`UseGlobalPose` | 已 Deprecated。`PXR_Enterprise` 中另有同名 `UseGlobalPose`（企业设备专用，未标 Deprecated），两者不是同一个 API。 |
| `PXR_FoveationRendering` | `SetFoveationParameters` | 已 Deprecated；用 `SetFoveationLevel(level, isETFR)` 代替等级控制。 |
| `PXR_HandTracking` | `GetSettingState` | 已 Deprecated。 |
