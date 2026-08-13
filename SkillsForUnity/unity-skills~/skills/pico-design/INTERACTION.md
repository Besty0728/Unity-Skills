# PICO Unity SDK — 输入与追踪（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。

## 何时加载本文档

- 要在 PICO 上读手柄按键、摇杆、扳机，或搞清楚 PICO 键值与 Unity XR / XRI 的对应关系。
- 要接入手势追踪（Hand Tracking）：手模预制体、26 关节、射线捏合、与 XR Hands / XRI 的绑定。
- 要接入眼动追踪（Eye Tracking）、面部追踪（Face Tracking / Lipsync）、全身动捕（Body Tracking）。
- 要用 PICO 体感追踪器（PICO Motion Tracker）做独立追踪（Object Tracking）或连外设透传数据。
- 要做手柄振动 / 触觉反馈（含 PHF、PCM、AudioClip 缓冲类触觉）。
- 要唤起系统键盘做文本输入，或配置手柄射线与 World Space Canvas 的交互。

> 通用 XRI 交互（XR Ray Interactor、XR Grab Interactable、Interaction Manager、Locomotion 等）不在本文档，路由到本仓库 `xr` 模块。本文档只写 PICO 特有部分。

## 关键规则

| # | 规则 | 来源 |
|---|---|---|
| 1 | 手柄按键**不走 PICO 私有 API**：PICO Unity Integration SDK 使用 Unity 官方键值做输入映射。取设备用 `InputDevices.GetDeviceAtXRNode(XRNode.LeftHand)`，取键值用 `InputDevice.TryGetFeatureValue(CommonUsages.triggerButton, out v)`。 | [input-mapping](https://developer-cn.picoxr.com/document/unity/input-mapping/) |
| 2 | 截屏/录屏键**无开放键值**；音量增减键是 Android 标准键值（`VOLUME_UP` / `VOLUME_DOWN`）且**系统占用、默认不开放**，Neo3 的 `Home` 键同理。Neo3 头戴确认键 `KeyCode.JoystickButton0` **在新输入系统下取它会报错且识别不到**，必须把 **Edit > Project Settings > Player > Other Settings > Configuration > Active Input Handling** 设为 **Both** 或 **Input Manager (Old)**。 | [input-mapping](https://developer-cn.picoxr.com/document/unity/input-mapping/) |
| 3 | 设备**只支持追踪手柄或手，无法同时追踪**。`Hand Tracking Support` 选 `Controller And Hands` 是自动切换，不是并存。 | [hand-tracking-overview](https://developer-cn.picoxr.com/document/unity/hand-tracking-overview/) · [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) |
| 4 | 用 `HandLeft` / `HandRight` 手模时，**必须先删掉 XR Origin 下的 `LeftHand Controller` 和 `RightHand Controller`**，否则设备上运行会出现手模与手柄模型共存。 | [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) |
| 5 | `PXR_Manager (Script)` 上的 **High Frequency Tracking (60Hz)** 一旦启用，**运行时无法关闭**，且性能开销更大；启用前先验证普通模式是否够用。 | [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) |
| 6 | 手势追踪对齐 OpenXR，固定 **26 个关节**（0 = `Palm`，1 = `Wrist`，…，25 = `Little_tip`），`HandJointLocations.jointCount` 当前返回 26。自适应手模只支持 PICO 4 Ultra；自定义手模需自行调 `PXR_HandTracking.GetHandScale(handType, ref scale)` 缩放。 | [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) · [PXR_HandTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_HandTracking/) |
| 7 | 手势要驱动 XRI，必须在 **XRI Default Input Actions** 里**手动加 Binding**，路径固定为 `<PicoAimHand>{LeftHand}/devicePosition`、`/deviceRotation`、`/aimFlags`、`/indexPressed`、`/pinchStrengthIndex`（右手同理）。SDK 不会自动绑。 | [enable-interactions-…-xr-interaction-toolkit](https://developer-cn.picoxr.com/document/unity/enable-interactions-between-hands-and-3d-objects-using-xr-interaction-toolkit/) |
| 8 | 非缓冲类触觉：`PXR_Input.SendHapticImpulse(VibrateType, amplitude, duration, frequency)`。**停止方式是再次调用该接口并把振幅和振动时长都设为 0**，没有单独的 Stop 接口。振幅 0~1、时长 0~65535 ms、频率 50~500Hz，且**频率越高手柄振感越小**（反直觉）。 | [haptic-feedback](https://developer-cn.picoxr.com/document/unity/haptic-feedback/) · [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 9 | 缓冲类触觉仅支持 **MP3 / WAV / OGG**，**振动时长由音频流决定、不可配置**，且**仅 PICO 4 系列支持**（非缓冲类支持 PICO Neo3 与 PICO 4 系列）。 | [haptic-feedback](https://developer-cn.picoxr.com/document/unity/haptic-feedback/) |
| 10 | 缓冲类触觉必须自己定义并保存 `sourceId`，后续 Update / Stop / Pause / Resume 全靠它。`StopHapticBuffer(sourceId)` 传 `0`（或不传）会**停掉全部**缓冲触觉。用 `CacheType.CacheNoVibrate` 时缓存完还得调 `StartHapticBuffer(sourceId)` 才会振。 | [haptic-feedback](https://developer-cn.picoxr.com/document/unity/haptic-feedback/) · [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 11 | 原点追踪：**Camera Y Offset 仅对 Device 模式生效**；**Stage Mode 选框只有 Tracking Origin Mode = Floor 时才可勾**，勾上后不允许长按 Home 键校准。 | [tracking-origin](https://developer-cn.picoxr.com/document/unity/tracking-origin/) |
| 12 | 眼动追踪：**必须先 `StartEyeTracking` 才能 `GetEyeTrackingData`**；权限弹窗是在**调用 `GetEyeTrackingData` 时**才向用户申请的；目前**仅支持 `EyeTrackingMode.PXR_ETM_BOTH`**。`GetEyeOpenness` / `GetEyePupilInfo` / `GetPerEyePose` / `GetEyeBlink` **仅支持 PICO 4 Enterprise**。 | [eye-tracking](https://developer-cn.picoxr.com/document/unity/eye-tracking/) |
| 13 | 面部追踪必须勾 **Edit > Project Settings > Player > Other Settings > Allow 'unsafe' Code**（`GetFaceTrackingSupported` 签名带 `unsafe`）；`PXR_Manager (Script)` 面板**只提供 Hybrid 选项**，选中后默认是 Hybrid (Viseme)。 | [face-tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 14 | 切换面部追踪模式时，`FaceTrackingStopInfo.pause = 0` 的语义是「**暂停**」而非停止——这是官方推荐的快速切模式手法（Stop(pause=0) → Start(新 mode)），语义反直觉。 | [face-tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/) |
| 15 | `GetFaceTrackingData` 返回**定长 72 的数组** = 0~51 维 BlendShapes + 52~71 维 Viseme。Face Only 模式下 52~71 恒为 0；Lipsync Only 模式下眼部相关维度恒为 0。 | [face-tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/) |
| 16 | 全身动捕和独立追踪**共用 `PXR_Manager (Script)` 上的同一个 `Body Tracking` 勾选框**，但**两种模式互斥**：开启独立追踪后无法输出全身动捕数据。 | [body-tracking](https://developer-cn.picoxr.com/document/unity/body-tracking/) · [object-tracking](https://developer-cn.picoxr.com/document/unity/object-tracking/) |
| 17 | 独立追踪必须**先 `CheckMotionTrackerNumber(MotionTrackerNum.X)` 请求数量**，再在 `RequestMotionTrackerCompleteAction` 回调里拿 `trackerIds`，才能 `GetMotionTrackerLocation`。数量不符或模式不对时 Runtime 会自动拉起「PICO 体感追踪器」应用。 | [object-tracking](https://developer-cn.picoxr.com/document/unity/object-tracking/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 18 | 独立追踪下追踪器**距一体机超过 1 米或进入盲区，数据会不准或不更新**；且 `localLocation` 是**右手坐标系**，赋值给 Unity Transform 必须用 `.ToVector3()` / `.ToQuat()` 转换。 | [object-tracking](https://developer-cn.picoxr.com/document/unity/object-tracking/) |
| 19 | 全身动捕取数据前必须先看 `GetBodyTrackingState` 返回的 `stateCode`，只有 `BodyTrackingStatusCode.BT_VALID` 才可用；未校准时调 `StartMotionTrackerCalibApp()` 引导用户校准（正式版是「一眼校准」）。建议全程监听校准状态变化。 | [body-tracking](https://developer-cn.picoxr.com/document/unity/body-tracking/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 20 | v3.4.0 已标 **(Deprecated)** 的大类：`PXR_Input` 全部旧振动接口与 `GetBodyTrackingPose`/`SetBodyTrackingMode` 等；`PXR_MotionTracking` 的**全部面部追踪 6 接口**、`ExtDev*` 家族、`GetMotionTrackerLocations`/`GetMotionTrackerMode`/`CheckMotionTrackerModeAndNumber`/`GetMotionTrackerConnectStateWithSN`/`GetMotionTrackerDeviceType`。**例外**：`SetExtDevTrackerPassDataState` 与 `ExtDevPassDataAction` 未标废弃，仍是现行透传开关。另：应用显示系统键盘时**会失去输入焦点（Input Focus）**。 | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) · [system-keyboard](https://developer-cn.picoxr.com/document/unity/system-keyboard/) |

## 工作流程

### 1. 读手柄输入（PICO 侧只做设备型号/状态，键值全走 Unity XR）

1. 键值：`InputDevices.GetDeviceAtXRNode(XRNode.LeftHand)` → `device.TryGetFeatureValue(CommonUsages.triggerButton, out bool v)`。
   对应表：菜单键 `menuButton`；扳机 `triggerButton`(布尔) + `trigger`(0~1 力度)；抓握 `gripButton` + `grip`；摇杆 `primary2DAxisClick` + `primary2DAxis`；X/A `primaryButton`；Y/B `secondaryButton`。PICO 4 Ultra / PICO 4 / Neo3 三代映射一致。
2. 设备信息走 PICO：`PXR_Input.GetControllerDeviceType()`（`ControllerDevice`：G2/Neo2/Neo3/PICO_4/G3/PICO_4U/NewController）、`IsControllerConnected(Controller)`、`GetControllerStatus(Controller)`（`ControllerStatus`：Static/SixDof/ThreeDof/Sleep/CollidedIn3Dof/CollidedIn6Dof）。
3. 需要预测位姿（如挥拍/射击补偿）：`GetControllerPredictPosition(controller, predictTime)`、`GetControllerPredictRotation(controller, predictTime)`，`predictTime` 单位毫秒。显示位置与真实位置的偏移用 `SetControllerOriginOffset(controller, offset)`（米）。
4. 手柄模型：SDK 标准模型在 `Packages/PICO Integration/Assets/Resources/Prefabs`，正式运行时会按设备型号自动展示对应模型。
5. 推荐把 XR Interaction Toolkit 升到 **2.1.1 及以上**。XRI 本身的用法路由到 `xr` 模块。

### 2. 接入手势追踪 + 与 XRI 3D 物体交互

环境：PICO Neo3 / PICO 4 / PICO 4 Ultra 系列，系统 **5.11.0 及以上**；SDK 需 **2.3.0 及以上**（v2.3.0 手势算法对标 OpenXR，与旧版不兼容）。XRI 手势交互另需系统 **5.12.0 及以上**、XRI **2.3.0–2.6.2 或 3.x**。

1. 删除 `XR Origin` 下的 `LeftHand Controller` / `RightHand Controller`。
2. 把 `Packages > PICO Integration > Assets > Resources > Prefabs` 下的 `HandLeft`、`HandRight` 拖到 `XR Origin` 下，与 `Main Camera` 同级。手模已按 OpenXR 规范绑好关节，并绑定了 `RayPose (Transform)` 与 `DefaultRay`；`PXR_Hand` 是手模配置脚本。
3. `XR Origin` 上挂 `PXR_Manager`，勾 **Hand Tracking**，在 **Hand Tracking Support** 选 `Controller And Hands`（自动切换）或 `Hands Only`。
4. 可选：勾 **Adaptive Hand Model (PICO)**（写入 `<meta-data android:name="Enable_AdaptiveHandModel" android:value="1" />`，仅 PICO 4 Ultra）、勾 **High Frequency Tracking (60Hz)**（写入 `Hand_Tracking_HighFrequency`，运行时不可关）。
5. 设备端：Neo3/PICO 4 走 **设置 > 实验室 > 手势识别**；PICO 4 Ultra 走 **控制中心 > 设置 > 交互 > 交互方式 = 手势与手柄自动切换**。
6. 要用 XRI 的 Hands Interaction Demo：装 Unity **XR Hands** 包 + 导入 **Hand Visualizer** 示例；导入 XRI 的 **Starter Assets** 与 **Hands Interaction Demo**；打开 `XRI Default Input Actions`，给 `XRI LeftHand` 的 `Aim Position`/`Aim Rotation`/`Aim Flags` 与 `XRI LeftHand Interaction` 的 `Select`/`Select Value`/`UI Press`/`UI Press Value` 加 Binding，路径见规则 8；右手同理。然后打开 `HandsDemoScene`。
7. 运行时读数据：`PXR_HandTracking.GetJointLocations(HandType, ref HandJointLocations)` 拿 26 关节；`PXR_HandTracking.GetAimState(HandType, ref HandAimState)` 拿射线位姿与捏合强度；`PXR_HandTracking.GetActiveInputDevice()` 拿当前输入源。
8. 输入源切换事件：指南写作 `PXR_Plugin.System.InputDeviceChanged`（返回 `0` 头戴 / `1` 手柄 / `2` 手），API 参考写作 `PXR_Input.InputDeviceChanged`（`Action<ActiveInputDevice>`，`HeadActive`/`ControllerActive`/`HandTrackingActive`）。两处并存，接入前用 SDK 源码确认实际可用的那个。
9. 手势设计受识别范围限制（PICO 4）：深度 152mm~500mm，上 57.5°、下 72.5°、左 61.5°、右 60.5°；避免双手/单手关节遮挡。

### 3. 触觉反馈（非缓冲 + 缓冲）

```csharp
// 非缓冲：右手柄，振幅 0.5，时长 500ms，频率 100Hz
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0.5f, 500, 100);
// 停止 = 再调一次，振幅与时长归零
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0f, 0, 100);

// 缓冲（AudioClip）：缓存但不振动，之后手动开
int sourceId = 0;
PXR_Input.SendHapticBuffer(PXR_Input.VibrateType.RightController, audioClip,
    PXR_Input.ChannelFlip.No, ref sourceId, PXR_Input.CacheType.CacheNoVibrate);
PXR_Input.StartHapticBuffer(sourceId);

// 缓冲（PHF 文件）：双手柄，标准振幅
PXR_Input.SendHapticBuffer(PXR_Input.VibrateType.BothController, phf_text,
    PXR_Input.ChannelFlip.No, 1, ref sourceId);

PXR_Input.UpdateHapticBuffer(sourceId, PXR_Input.VibrateType.LeftController, PXR_Input.ChannelFlip.No, 2);
PXR_Input.PauseHapticBuffer(sourceId);
PXR_Input.ResumeHapticBuffer(sourceId);
PXR_Input.StopHapticBuffer(sourceId);   // 传 0 或不传 = 停止全部
```

- `channelFlip = Yes` 时左声道数据驱动右手柄，右声道驱动左手柄。
- `amplitudeScale` 有效值 0~2：0 不振动、1 标准振幅、2 两倍标准振幅。
- 频率建议：打鼓/篮球 50~100Hz；射击/乒乓球 约 170Hz；石头碰撞 约 300Hz。
- 连续调用 `StartHapticBuffer` 时，前一次的振动数据会被后一次覆盖。

### 4. 眼动 / 面部追踪接入

眼动：设备限 PICO Neo3 Pro Eye / PICO 4 Pro / PICO 4 Enterprise，系统 5.4.0+（校准 5.5.0+），推荐 SDK 2.3.0+ 与系统 5.7.0+。

1. `XR Origin` 挂 `PXR_Manager`，勾 **Eye Tracking**（SDK 自动写入 `picovr.software.eye_tracking` 与 `com.picovr.permission.EYE_TRACKING`），可选勾 **Eye Tracking Calibration**。
2. `GetEyeTrackingSupported(ref supported, ref count, ref modes)` → `StartEyeTracking(ref EyeTrackingStartInfo)`（`mode = EyeTrackingMode.PXR_ETM_BOTH`）→ 每帧 `GetEyeTrackingState(ref isTracking, ref state)` + `GetEyeTrackingData(ref EyeTrackingDataGetInfo, ref EyeTrackingData)` → `OnDisable` 里 `StopEyeTracking(ref EyeTrackingStopInfo)`。
3. `EyeTrackingDataGetInfo.displayTime` 保留字段传 0；`flags` 用 `EyeTrackingDataGetFlags.PXR_EYE_DEFAULT | PXR_EYE_POSITION | PXR_EYE_ORIENTATION` 组合。注意 `PXR_EYE_DEFAULT = 0` 的含义是「不返回任何数据」。

面部：Lipsync Only 支持 Neo3 与 PICO 4 系列；Face Only / Hybrid 仅 PICO 4 Pro 与 PICO 4 Enterprise；系统 5.7.0+。

1. `PXR_Manager` 的 **Face Tracking Mode** 选模式（面板只有 Hybrid），Player 设置勾 **Allow 'unsafe' Code**，设备端 **设置 > 实验室 > 表情模拟** 打开。
2. 调用顺序：`WantFaceTrackingService()` → `GetFaceTrackingSupported(...)` → `StartFaceTracking(ref FaceTrackingStartInfo)` → `GetFaceTrackingState` / `GetFaceTrackingData` → `StopFaceTracking(ref FaceTrackingStopInfo)`。
3. 权限按模式自动写入：Hybrid 三条（`picovr.software.face_tracking` + `com.picovr.permission.FACE_TRACKING` + `android.permission.RECORD_AUDIO`）；Face Only 去掉录音；Lipsync Only 去掉 FACE_TRACKING 权限。
4. Hybrid 效果最好但 CPU 占用最高，可按场景降级到 Face Only / Lipsync Only。

### 5. 体感追踪器：全身动捕 vs 独立追踪（二选一）

环境：PICO 4 系列 / PICO 4 Ultra 系列，系统 **5.13.0 及以上**，**PICO 体感追踪器（正式版）**；前提是场景已有 `XR Origin` + `PXR_Manager (Script)`，并勾选 **Body Tracking**。

全身动捕：

1. `GetBodyTrackingSupported(ref supported)` 查支持。
2. `StartBodyTracking(mode, boneLength)` 开始；`BodyTrackingBoneLength` 单位厘米，未设的部位用默认值，且仅 `BTM_FULL_BODY_HIGH` 模式生效。
3. 每帧 `GetBodyTrackingState(ref isTracking, ref state)` 判 `state.stateCode == BodyTrackingStatusCode.BT_VALID`，否则 `StartMotionTrackerCalibApp()`。
4. `GetBodyTrackingData(ref BodyTrackingGetDataInfo, ref BodyTrackingData)` 取 `roleDatas[i].localPose` 的 `PosX/PosY/PosZ`、`RotQx/RotQy/RotQz/RotQw` 赋值骨骼。坐标系与头戴数据相同的世界坐标系，根关节是 `0 (Pelvis)`。
5. 退出场景 `StopBodyTracking()`。
6. 骨骼姿态**存在靠近根节点一侧的父节点**里：例如 `LEFT_KNEE(4)` 存左膝关节位置 + 左小腿骨骼姿态，`LEFT_ANKLE(7)` 存左脚踝位置 + 左脚面姿态。

独立追踪：

1. `PXR_MotionTracking.RequestMotionTrackerCompleteAction += cb;` 然后 `CheckMotionTrackerNumber(MotionTrackerNum.TWO)`（数量范围 [0,3]）。
2. 回调里从 `RequestMotionTrackerCompleteEventData` 取 `trackerCount` 与 `trackerIds`。
3. 每帧 `GetMotionTrackerLocation(trackerId, ref MotionTrackerLocation, ref isValidPose)`，用 `location.pose.Position.ToVector3()` / `location.pose.Orientation.ToQuat()` 赋值。
4. 外设透传：`SetExtDevTrackerPassDataState(true)` + 订阅 `ExtDevPassDataAction`（值 `1` 开始取、`0` 停止），再 `GetExpandDeviceCustomData(out list)` / `SetExpandDeviceCustomData(ref array)`；设备列表 `GetExpandDevice(out devices)`、电量 `GetExpandDeviceBattery`、振动 `SetExpandDeviceVibrate`。自定义数据 16 字节中**仅前 15 个有效**。

### 6. 系统键盘与手柄射线 UI

1. 前置：XRI **2.1.0 及以上** + 导入 **Starter Assets**；场景已有 `XR Origin` 并配好左右手柄。
2. Hierarchy 加 **UI > Event System** 与 **UI > Canvas**（或直接 **XR > UI Canvas**）。
3. Canvas：**Render Mode = World Space**，**Event Camera = 场景主相机**，加 **Tracked Device Graphic Raycaster** 组件（system-keyboard 页写作 “Tracked Device Graphic Raycast”）。
4. 右键 Canvas > **UI > Input Field - TextMeshPro** 添加输入域；射线点击输入框即唤起系统键盘。
5. 选中 `Left Controller` / `Right Controller`，在 **XR Ray Interactor** 上把 **Max Raycast Distance** 调到**大于相机到 UI 的实际距离**，射线接触输入域时变白表示可交互。

## 核心 API 锚点

命名空间：`Unity.XR.PXR`。

### PXR_Input（现行）

```csharp
ControllerStatus GetControllerStatus(Controller controller)
ControllerDevice GetControllerDeviceType()
bool  IsControllerConnected(Controller controller)
void  SetControllerOriginOffset(Controller controller, Vector3 offset)
Quaternion GetControllerPredictRotation(Controller controller, double predictTime)
Vector3    GetControllerPredictPosition(Controller controller, double predictTime)
Action<ActiveInputDevice> InputDeviceChanged;              // 输入源变化回调
GetControllerHandness()                                    // 取系统惯用手，仅 PICO G3

void SendHapticImpulse(VibrateType vibrateType, float amplitude, int duration, int frequency)
int  SendHapticBuffer(VibrateType, AudioClip audioClip, ChannelFlip, ref int sourceId, CacheType)
int  SendHapticBuffer(VibrateType, float[] pcmData, int buffersize, int frequency, int channelMask,
                      ChannelFlip, ref int sourceId, CacheType)
int  SendHapticBuffer(VibrateType, TextAsset phfText, ChannelFlip, float amplitudeScale, ref int sourceId)
int  StopHapticBuffer(int sourceId, bool clearCache)
int  PauseHapticBuffer(int sourceId)
int  ResumeHapticBuffer(int sourceId)
int  StartHapticBuffer(int sourceId)
int  UpdateHapticBuffer(int sourceId, VibrateType, ChannelFlip, float amplitudeScale)
```

枚举：`Controller`（LeftController/RightController）、`ControllerDevice`（G2=3, Neo2, Neo3, PICO_4, G3, PICO_4U, NewController=10）、`ControllerStatus`（Static=0/SixDof/ThreeDof/Sleep/CollidedIn3Dof/CollidedIn6Dof）、`VibrateType`（None=0, LeftController=1, RightController=2, BothController=3, **LeftPICO4U=4, RightPICO4U=8, BothPICO4U=12**）、`VibrateController`（No=0/Left=1/Right=2/LeftAndRight=3）、`CacheType`（DontCache=0/CacheAndVibrate=1/CacheNoVibrate=2）、`ChannelFlip`（No/Yes）、`CacheConfig`（CacheAndVibrate=1/CacheNoVibrate=2）。

**已废弃（勿新写）**：`SetControllerVibration`、`SetControllerVibrationEvent`、`StartControllerVCMotor`、`StopControllerVCMotor`、`SetControllerAmp`、`StartVibrateBySharem`、`SaveVibrateByCache`、`StartVibrateByCache`、`ClearVibrateByCache`、`StartVibrateByPHF`、`PauseVibrate`、`ResumeVibrate`、`UpdateVibrateParams`、`CreateHapticStream`/`WriteHapticStream`/`SetHapticStreamSpeed`/`GetHapticStreamSpeed`/`GetHapticStreamCurrentFrameSequence`/`StartHapticStream`/`StopHapticStream`/`RemoveHapticStream`/`AnalysisHapticStreamPHF`、`GetDominantHand`/`SetDominantHand`、`GetBodyTrackingPose`、`GetMotionTrackerConnectStateWithID`、`GetMotionTrackerBattery`、`GetMotionTrackerCalibState`、`SetBodyTrackingMode`、`SetBodyTrackingBoneLength`、`ResetController`、`SetArmModelParameters`。

### PXR_HandTracking

```csharp
ActiveInputDevice GetActiveInputDevice()
bool GetAimState(HandType hand, ref HandAimState aimState)
bool GetJointLocations(HandType hand, ref HandJointLocations jointLocations)
static bool GetHandScale(HandType hand, ref float scale)
bool GetSettingState()                                     // (Deprecated)
```

枚举：`HandType`（HandLeft=0/HandRight=1）、`ActiveInputDevice`（HeadActive=0/ControllerActive=1/HandTrackingActive=2）、`HandAimStatus`（AimComputed/AimRayValid/AimIndexPinching/AimMiddlePinching/AimRingPinching/AimLittlePinching/AimRayTouched，位标志）、`HandLocationStatus`（OrientationValid/PositionValid/OrientationTracked/PositionTracked）、`HandFinger`（Thumb/Index/Middle/Ring/**Pinky**）。
结构体：`Vector3f`、`Quatf`、`Posef`、`HandAimState`（aimStatus/aimRayPose/pinchStrengthIndex/Middle/Ring/Little/touchStrengthRay）、`HandJointLocation`（locationStatus/pose/radius）、`HandJointLocations`（isActive/jointCount/handScale/jointLocations）。
组件与预制体：`PXR_Hand`（手模配置脚本，含 `Computed`/`RayPose`/`RayValid`/`Pinch`/`PinchStrength`）、`PXR_Hand Pose Generator` + `PXR_Hand Pose`（手势编辑器，v2.1.5 起）、`HandLeft`/`HandRight` 预制体、`RayPose (Transform)`、`DefaultRay`。

### PXR_MotionTracking

```csharp
// 眼动（现行）
int GetEyeTrackingSupported(ref bool supported, ref int supportedModesCount, ref EyeTrackingMode[] supportedModes)
int StartEyeTracking(ref EyeTrackingStartInfo startInfo)
int StopEyeTracking(ref EyeTrackingStopInfo stopInfo)
int GetEyeTrackingState(ref bool isTracking, ref EyeTrackingState state)
int GetEyeTrackingData(ref EyeTrackingDataGetInfo getInfo, ref EyeTrackingData data)
int GetEyeOpenness(ref float leftEyeOpenness, ref float rightEyeOpenness)      // 仅 PICO 4 Enterprise
int GetEyePupilInfo(ref EyePupilInfo eyePupilPosition)                          // 仅 PICO 4 Enterprise
int GetPerEyePose(ref long timestamp, ref Posef leftEyePose, ref Posef rightPose)   // 仅 PICO 4 Enterprise
int GetEyeBlink(ref long timestamp, ref bool isLeftBlink, ref bool isRightBlink)    // 仅 PICO 4 Enterprise

// 面部（v3.4.0 API 参考中全部标 Deprecated，但指南仍以此为主线）
int WantFaceTrackingService()
unsafe int GetFaceTrackingSupported(ref bool supported, ref int supportedModesCount, ref FaceTrackingMode[] supportedModes)
int StartFaceTracking(ref FaceTrackingStartInfo startInfo)
int StopFaceTracking(ref FaceTrackingStopInfo stopInfo)
int GetFaceTrackingState(ref bool isTracking, ref FaceTrackingState state)
int GetFaceTrackingData(ref FaceTrackingDataGetInfo getInfo, ref FaceTrackingData data)

// 全身动捕（现行）
int StartMotionTrackerCalibApp()
int GetBodyTrackingSupported(ref bool supported)
int StartBodyTracking(BodyTrackingMode mode, BodyTrackingBoneLength boneLength)
int StopBodyTracking()
int GetBodyTrackingState(ref bool isTracking, ref BodyTrackingState state)
int GetBodyTrackingData(ref BodyTrackingGetDataInfo getInfo, ref BodyTrackingData data)

// 独立追踪与外设（现行）
CheckMotionTrackerNumber(MotionTrackerNum number)
GetMotionTrackerLocation(long trackerId, ref MotionTrackerLocation location, ref bool isValidPose)
GetMotionTrackerBattery(long trackerId, ref int battery, ref int charger)
GetExpandDevice(out long[] deviceArray) / GetExpandDeviceBattery / SetExpandDeviceVibrate
SetExpandDeviceCustomData(ref ExpandDevicesCustomData[] dataArray) / GetExpandDeviceCustomData(out ...)
int SetExtDevTrackerPassDataState(bool state)
Action<int> ExtDevPassDataAction;
RequestMotionTrackerCompleteAction / MotionTrackerConnectionAction / MotionTrackerPowerKeyAction
ExpandDeviceConnectionAction / ExpandDeviceBatteryAction
```

枚举：`TrackingStateCode`（PXR_MT_SUCCESS=0、PXR_MT_FAILURE=-1、PXR_MT_MODE_NONE=-2、PXR_MT_DEVICE_NOT_SUPPORT=-3、PXR_MT_SERVICE_NEED_START=-4、PXR_MT_ET_PERMISSION_DENIED=-5、PXR_MT_FT_PERMISSION_DENIED=-6、PXR_MT_MIC_PERMISSION_DENIED=-7、PXR_MT_SYSTEM_DENIED=-8、PXR_MT_UNKNOW_ERROR=-9）、`EyeTrackingMode`（PXR_ETM_NONE=-1/PXR_ETM_BOTH=0/PXR_ETM_COUNT=1）、`PerEyeUsage`（LeftEye/RightEye/Combined/EyeCount）、`EyeTrackingDataGetFlags`、`FaceTrackingMode`（PXR_FTM_NONE=-1/PXR_FTM_FACE=0/PXR_FTM_LIPS=1/**PXR_FTM_FACE_LIPS_VIS=2**/**PXR_FTM_FACE_LIPS_BS=3**）、`FaceTrackingDataGetFlags`（PXR_FACE_DEFAULT=0）、`BlendShapeIndex`（0~51 BlendShapes + 52~71 Viseme）、`BodyTrackingMode`（BTM_FULL_BODY_LOW=0/BTM_FULL_BODY_HIGH=1）、`BodyTrackingStatusCode`（BT_INVALID=0/BT_VALID=1/BT_LIMITED=2）、`BodyTrackingErrorCode`（BT_ERROR_INNER_EXCEPTION=0 … BT_ERROR_TRACKING_POSE_ERROR=7）、`BodyTrackingGetDataFlags`（PXR_BODY_NONE/PXR_BODY_POSE/PXR_BODY_ACTION/PXR_BODY_VELO_ACC）、`BodyTrackerRole`（Pelvis=0 … RIGHT_HAND=23）、`MotionTrackerType`（MT_1=Beta/MT_2=正式版）、`MotionTrackerMode`（BodyTracking/MotionTracking）、`MotionTrackerNum`（NONE/ONE/TWO/THREE）、`MotionTrackerConfidence`。
结构体：`EyeTrackingStartInfo`（needCalibration/mode）、`EyeTrackingState`、`EyeTrackingDataGetInfo`、`EyeTrackingData`（`eyeDatas`，长度 `(int)PerEyeUsage.EyeCount`）、`PerEyeData`、`EyePupilInfo`、`FaceTrackingStartInfo`/`FaceTrackingStopInfo`/`FaceTrackingState`/`FaceTrackingDataGetInfo`/`FaceTrackingData`、`BodyTrackingStartInfo`、`BodyTrackingBoneLength`（headLen/neckLen/torsoLen/hipLen/upperLegLen/lowerLegLen/footLen/shoulderLen/upperArmLen/lowerArmLen/handLen，单位 cm）、`BodyTrackingState`、`BodyTrackingGetDataInfo`、`BodyTrackingRoleData`、`BodyTrackingData`、`MotionTrackerLocation`/`MotionTrackerLocations`（localLocation / globalLocation，后者除特殊需求不建议用）、`TrackerSN`（最长 24）、`RequestMotionTrackerCompleteEventData`、`ExpandDevicesCustomData`。

### 其它 PICO 侧锚点

- `PXR_Manager (Script)` 面板开关：`Eye Tracking`、`Eye Tracking Calibration`、`Hand Tracking`（含 `Hand Tracking Support`）、`Adaptive Hand Model (PICO)`、`High Frequency Tracking (60Hz)`、`Face Tracking Mode`、`Body Tracking`、`Tracking Origin Mode`、`Camera Y Offset`、`Stage Mode`。
- `PXR_System.SetTrackingOrigin` / `GetTrackingOrigin`；面部旧接口 `PXR_System.EnableFaceTracking` / `EnableLipSync` / `GetFaceTrackingData` / `SetFaceTrackingStatus`（2.1.4+）。
- 眼动重构前的接口在 `PXR_EyeTracking`；运动追踪新旧接口对照见 [motion-tracker-api-compatibility](https://developer-cn.picoxr.com/document/unity/motion-tracker-api-compatibility/)。
- Unity 侧：`InputDevices.GetDeviceAtXRNode`、`InputDevice.TryGetFeatureValue`、`CommonUsages.*`、`XRNode.LeftHand`、`KeyCode.Escape`/`KeyCode.JoystickButton0`/`KeyCode.Home`、`XR Origin`、`XR Ray Interactor`、`Tracked Device Graphic Raycaster`、`XRI Default Input Actions`、`PicoAimHand`、Unity **XR Hands** 套件。
- 左右眼分屏：主相机 `Target Eye = None (Main Display)`，子相机分别设 `Left`/`Right` 并用 `Culling Mask` 绑 Layer（[how-to-set-up-a-camera-for-each-eye](https://developer-cn.picoxr.com/document/unity/how-to-set-up-a-camera-for-each-eye/)）。

### ⚠ 官方文档自相矛盾处（落地前必须用 SDK 源码复核）

| 指南写法 | API 参考写法 | 处理建议 |
|---|---|---|
| `PXR_MotionTracking.StartBodyTracking(BodyJointSet.BODY_JOINT_SET_BODY_FULL_START, boneLength)` | `StartBodyTracking(BodyTrackingMode mode, BodyTrackingBoneLength boneLength)`，`BTM_FULL_BODY_LOW`/`BTM_FULL_BODY_HIGH` | 以 API 参考签名为准，用 IDE 补全确认枚举实际名 |
| `BodyTrackingStatus bs` / `BodyTrackingMessage.BT_MESSAGE_TRACKER_NOT_CALIBRATED` | `BodyTrackingState state` / `BodyTrackingErrorCode.BT_ERROR_TRACKER_NOT_CALIBRATED` | 同上 |
| `BodyTrackerRole.ROLE_NUM` 作为数组长度 | `(int)BodyTrackerRole.NONE_ROLE` 作为数组长度 | 同上 |
| “要用 Hybrid (Blendshape) 就把 mode 设为 `PXR_FTM_FACE_LIPS_VIS`” | `PXR_FTM_FACE_LIPS_VIS` = Viseme 输出，`PXR_FTM_FACE_LIPS_BS` = Blendshape 输出 | 按枚举语义选，指南这句自相矛盾 |
| `FaceTrackingSupportedMode.PXR_FTM_FACE_LIPS_VIS` | 枚举名是 `FaceTrackingMode` | 用 `FaceTrackingMode` |
| `EyeTrackingStartInfo.needCalibration = 1`（示例） | 结构体说明：`0` = 需要校准，`1` = 不需要 | 按结构体语义显式赋值，别照抄示例 |

## DO NOT

```csharp
// ❌ 用已废弃的振动接口
PXR_Input.SetControllerVibration(0.5f, 500, Controller.RightController);
PXR_Input.SetControllerVibrationEvent(1, 100, 0.5f, 500);
// ✅ v3.x 统一走 SendHapticImpulse
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0.5f, 500, 100);
```

```csharp
// ❌ 找一个不存在的 "StopHapticImpulse" 来停非缓冲振动
PXR_Input.StopHapticImpulse(VibrateType.RightController);
// ✅ 再调一次 SendHapticImpulse，振幅与时长归零
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0f, 0, 100);
```

```csharp
// ❌ 用旧的音频/PHF 振动接口（全部 Deprecated）
PXR_Input.StartControllerVCMotor(path, slot);
PXR_Input.StartVibrateByPHF(phfText, ref sourceId);
// ✅ 统一走 SendHapticBuffer 的三个重载（AudioClip / PCM / PHF TextAsset）
PXR_Input.SendHapticBuffer(PXR_Input.VibrateType.BothController, phfText,
    PXR_Input.ChannelFlip.No, 1, ref sourceId);
```

```csharp
// ❌ 没开始就取眼动数据（也别指望勾了面板就自动开）
PXR_MotionTracking.GetEyeTrackingData(ref info, ref data);
// ✅ 先 Start，权限弹窗在首次 GetEyeTrackingData 时才出现
var startInfo = new EyeTrackingStartInfo { mode = EyeTrackingMode.PXR_ETM_BOTH };
PXR_MotionTracking.StartEyeTracking(ref startInfo);
PXR_MotionTracking.GetEyeTrackingData(ref info, ref data);
```

```csharp
// ❌ 猜方法名（这些在 PXR_HandTracking 里都不存在）
PXR_HandTracking.GetHandJoints(...);
PXR_HandTracking.GetHandJointLocations(...);
PXR_HandTracking.IsHandTracking();
// ✅ 只有这几个
PXR_HandTracking.GetJointLocations(HandType.HandLeft, ref jointLocations);
PXR_HandTracking.GetAimState(HandType.HandLeft, ref aimState);
PXR_HandTracking.GetActiveInputDevice();
PXR_HandTracking.GetHandScale(HandType.HandLeft, ref scale);
```

```csharp
// ❌ 把右手坐标系的位姿直接塞进 Unity Transform
child.localPosition = new Vector3(location.pose.Position.x, location.pose.Position.y, location.pose.Position.z);
// ✅ 用 SDK 自带的转换扩展
child.localPosition = location.pose.Position.ToVector3();
child.localRotation = location.pose.Orientation.ToQuat();
```

```csharp
// ❌ 拿到 trackerId 之前就查位置
PXR_MotionTracking.GetMotionTrackerLocation(0, ref location, ref isValid);
// ✅ 先请求数量，回调里拿 trackerIds
PXR_MotionTracking.RequestMotionTrackerCompleteAction += OnComplete;
PXR_MotionTracking.CheckMotionTrackerNumber(MotionTrackerNum.TWO);
```

```csharp
// ❌ 用已废弃的运动追踪查询接口
PXR_MotionTracking.CheckMotionTrackerModeAndNumber(MotionTrackerMode.MotionTracking, MotionTrackerNum.TWO);
PXR_MotionTracking.GetMotionTrackerLocations(sn, ref locations, ref confidence, 0);
// ✅ v3.4.0 现行版本
PXR_MotionTracking.CheckMotionTrackerNumber(MotionTrackerNum.TWO);
PXR_MotionTracking.GetMotionTrackerLocation(trackerId, ref location, ref isValidPose);
```

- ❌ 期待手势与手柄同时可用（做「一只手拿手柄、一只手裸手」的交互）→ ✅ 设备只能二选一，`Controller And Hands` 是自动切换。
- ❌ 用手模时保留 `LeftHand Controller` / `RightHand Controller` → ✅ 先删掉，否则手模与手柄模型同时出现。
- ❌ 运行时想关掉 60Hz 高频手势追踪 → ✅ 只能在编辑期决定，启用后无法运行时关闭。
- ❌ 同时期望全身动捕数据和独立追踪数据 → ✅ 两模式互斥，按场景切换。
- ❌ 给截屏键、音量键、Home 键写输入绑定 → ✅ 这些无开放键值或被系统占用。
