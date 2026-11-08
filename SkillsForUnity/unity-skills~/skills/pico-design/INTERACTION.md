# PICO Unity SDK — Input and Tracking (v3.4.0)

> Anchor: PICO official documentation v3.4.0 (fetched 2026-08-13). Every rule can be verified against the source URLs.

## When to Load This Document

- Reading controller buttons, thumbsticks and triggers on PICO, or working out how PICO key values map to Unity XR / XRI.
- Integrating hand tracking (Hand Tracking): hand model prefabs, 26 joints, pinch raycasts, binding to XR Hands / XRI.
- Integrating eye tracking (Eye Tracking), face tracking (Face Tracking / Lipsync), or full-body motion capture (Body Tracking).
- Using the PICO motion tracker (PICO Motion Tracker) for independent tracking (Object Tracking) or passing through peripheral data.
- Implementing controller vibration / haptic feedback (including PHF, PCM, and AudioClip buffered haptics).
- Summoning the system keyboard for text input, or configuring controller-ray interaction with World Space Canvas.

> General XRI interaction (XR Ray Interactor, XR Grab Interactable, Interaction Manager, Locomotion, etc.) is not covered in this document; it routes to the `xr` module of this repository. This document covers only the PICO-specific parts.

## Key Rules

| # | Rule | Source |
|---|---|---|
| 1 | Controller buttons **do not go through a PICO-private API**: the PICO Unity Integration SDK uses Unity's official key values for input mapping. Get the device with `InputDevices.GetDeviceAtXRNode(XRNode.LeftHand)` and the key value with `InputDevice.TryGetFeatureValue(CommonUsages.triggerButton, out v)`. | [input-mapping](https://developer-cn.picoxr.com/document/unity/input-mapping/) |
| 2 | The screenshot/recording key **has no exposed key value**; the volume up/down keys are standard Android key values (`VOLUME_UP` / `VOLUME_DOWN`), but they are **reserved by the system and not exposed by default**; the Neo3 `Home` key is the same. On the Neo3 headset, the confirm key `KeyCode.JoystickButton0` **raises an error and is not recognized under the new input system**; you must set **Edit > Project Settings > Player > Other Settings > Configuration > Active Input Handling** to **Both** or **Input Manager (Old)**. | [input-mapping](https://developer-cn.picoxr.com/document/unity/input-mapping/) |
| 3 | The device **supports tracking either controllers or hands, not both at the same time**. Selecting `Controller And Hands` for `Hand Tracking Support` is an automatic switch, not coexistence. | [hand-tracking-overview](https://developer-cn.picoxr.com/document/unity/hand-tracking-overview/) · [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) |
| 4 | When using the `HandLeft` / `HandRight` hand models, you **must first delete `LeftHand Controller` and `RightHand Controller` under XR Origin**; otherwise the hand model and controller model will both appear when running on the device. | [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) |
| 5 | Once **High Frequency Tracking (60Hz)** on `PXR_Manager (Script)` is enabled, it **cannot be turned off at runtime**, and it has higher performance overhead; before enabling it, verify whether normal mode is sufficient. | [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) |
| 6 | Hand tracking is aligned with OpenXR and has a fixed **26 joints** (0 = `Palm`, 1 = `Wrist`, …, 25 = `Little_tip`); `HandJointLocations.jointCount` currently returns 26. Adaptive hand models are supported only on PICO 4 Ultra; custom hand models must scale themselves by calling `PXR_HandTracking.GetHandScale(handType, ref scale)`. | [hand-tracking](https://developer-cn.picoxr.com/document/unity/hand-tracking/) · [PXR_HandTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_HandTracking/) |
| 7 | For hand gestures to drive XRI, you must **manually add Bindings** in **XRI Default Input Actions**, with fixed paths `<PicoAimHand>{LeftHand}/devicePosition`, `/deviceRotation`, `/aimFlags`, `/indexPressed`, `/pinchStrengthIndex` (the right hand is the same). The SDK does not bind them automatically. | [enable-interactions-…-xr-interaction-toolkit](https://developer-cn.picoxr.com/document/unity/enable-interactions-between-hands-and-3d-objects-using-xr-interaction-toolkit/) |
| 8 | Non-buffered haptics: `PXR_Input.SendHapticImpulse(VibrateType, amplitude, duration, frequency)`. **To stop, call the interface again with both amplitude and duration set to 0**; there is no separate Stop interface. Amplitude 0~1, duration 0~65535 ms, frequency 50~500 Hz, and **the higher the frequency, the weaker the controller vibration feels** (counterintuitive). | [haptic-feedback](https://developer-cn.picoxr.com/document/unity/haptic-feedback/) · [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 9 | Buffered haptics support only **MP3 / WAV / OGG**; **vibration duration is determined by the audio stream and cannot be configured**; and it is **supported only on the PICO 4 series** (non-buffered haptics support PICO Neo3 and the PICO 4 series). | [haptic-feedback](https://developer-cn.picoxr.com/document/unity/haptic-feedback/) |
| 10 | For buffered haptics you must define and store the `sourceId` yourself; all later Update / Stop / Pause / Resume calls depend on it. Passing `0` (or nothing) to `StopHapticBuffer(sourceId)` **stops all** buffered haptics. With `CacheType.CacheNoVibrate`, after caching you must still call `StartHapticBuffer(sourceId)` before it vibrates. | [haptic-feedback](https://developer-cn.picoxr.com/document/unity/haptic-feedback/) · [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 11 | Origin tracking: **Camera Y Offset takes effect only in Device mode**; the **Stage Mode checkbox can be ticked only when Tracking Origin Mode = Floor**, and once ticked, long-pressing the Home key to calibrate is not allowed. | [tracking-origin](https://developer-cn.picoxr.com/document/unity/tracking-origin/) |
| 12 | Eye tracking: **you must call `StartEyeTracking` before `GetEyeTrackingData`**; the permission prompt is requested from the user **when `GetEyeTrackingData` is called**; currently **only `EyeTrackingMode.PXR_ETM_BOTH` is supported**. `GetEyeOpenness` / `GetEyePupilInfo` / `GetPerEyePose` / `GetEyeBlink` **are supported only on PICO 4 Enterprise**. | [eye-tracking](https://developer-cn.picoxr.com/document/unity/eye-tracking/) |
| 13 | Face tracking requires ticking **Edit > Project Settings > Player > Other Settings > Allow 'unsafe' Code** (the `GetFaceTrackingSupported` signature carries `unsafe`); the `PXR_Manager (Script)` panel **offers only the Hybrid option**, which defaults to Hybrid (Viseme) once selected. | [face-tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 14 | When switching face-tracking modes, `FaceTrackingStopInfo.pause = 0` means "**pause**" rather than stop. This is the officially recommended fast mode-switching technique (Stop(pause=0) → Start(new mode)), and its semantics are counterintuitive. | [face-tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/) |
| 15 | `GetFaceTrackingData` returns a **fixed-length array of 72** = dimensions 0~51 BlendShapes + dimensions 52~71 Viseme. In Face Only mode, 52~71 are always 0; in Lipsync Only mode, the eye-related dimensions are always 0. | [face-tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/) |
| 16 | Full-body motion capture and independent tracking **share the same `Body Tracking` checkbox on `PXR_Manager (Script)`**, but **the two modes are mutually exclusive**: once independent tracking is enabled, full-body motion capture data cannot be output. | [body-tracking](https://developer-cn.picoxr.com/document/unity/body-tracking/) · [object-tracking](https://developer-cn.picoxr.com/document/unity/object-tracking/) |
| 17 | Independent tracking must **first request the count with `CheckMotionTrackerNumber(MotionTrackerNum.X)`**, then get `trackerIds` in the `RequestMotionTrackerCompleteAction` callback, before `GetMotionTrackerLocation` can be used. If the count does not match or the mode is wrong, the Runtime automatically launches the "PICO motion tracker" app. | [object-tracking](https://developer-cn.picoxr.com/document/unity/object-tracking/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 18 | In independent tracking, when a tracker is **more than 1 meter from the headset or enters a blind zone, its data becomes inaccurate or stops updating**; also, `localLocation` is in a **right-handed coordinate system**, so assigning it to a Unity Transform requires `.ToVector3()` / `.ToQuat()` conversion. | [object-tracking](https://developer-cn.picoxr.com/document/unity/object-tracking/) |
| 19 | Before reading full-body motion capture data, check the `stateCode` returned by `GetBodyTrackingState`; only `BodyTrackingStatusCode.BT_VALID` is usable. If not calibrated, call `StartMotionTrackerCalibApp()` to guide the user through calibration (the official release uses "one-glance calibration"). It is recommended to listen for calibration state changes throughout. | [body-tracking](https://developer-cn.picoxr.com/document/unity/body-tracking/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 20 | Major categories marked **(Deprecated)** in v3.4.0: all legacy vibration interfaces in `PXR_Input` and `GetBodyTrackingPose` / `SetBodyTrackingMode`, etc.; **all 6 face-tracking interfaces** in `PXR_MotionTracking`, the `ExtDev*` family, and `GetMotionTrackerLocations` / `GetMotionTrackerMode` / `CheckMotionTrackerModeAndNumber` / `GetMotionTrackerConnectStateWithSN` / `GetMotionTrackerDeviceType`. **Exception**: `SetExtDevTrackerPassDataState` and `ExtDevPassDataAction` are not marked deprecated and remain the current pass-through switches. Also: when the app shows the system keyboard, it **loses Input Focus**. | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) · [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) · [system-keyboard](https://developer-cn.picoxr.com/document/unity/system-keyboard/) |

## Workflow

### 1. Reading Controller Input (PICO Side Handles Only Device Model/State; All Key Values Go Through Unity XR)

1. Key values: `InputDevices.GetDeviceAtXRNode(XRNode.LeftHand)` → `device.TryGetFeatureValue(CommonUsages.triggerButton, out bool v)`.
   Mapping table: menu key `menuButton`; trigger `triggerButton` (boolean) + `trigger` (0~1 force); grip `gripButton` + `grip`; thumbstick `primary2DAxisClick` + `primary2DAxis`; X/A `primaryButton`; Y/B `secondaryButton`. The mapping is identical across PICO 4 Ultra / PICO 4 / Neo3.
2. Device information comes from PICO: `PXR_Input.GetControllerDeviceType()` (`ControllerDevice`: G2/Neo2/Neo3/PICO_4/G3/PICO_4U/NewController), `IsControllerConnected(Controller)`, `GetControllerStatus(Controller)` (`ControllerStatus`: Static/SixDof/ThreeDof/Sleep/CollidedIn3Dof/CollidedIn6Dof).
3. When predicted poses are needed (such as swing/shot compensation): `GetControllerPredictPosition(controller, predictTime)` and `GetControllerPredictRotation(controller, predictTime)`, where `predictTime` is in milliseconds. To offset the displayed position from the real position, use `SetControllerOriginOffset(controller, offset)` (meters).
4. Controller models: the SDK's standard models are at `Packages/PICO Integration/Assets/Resources/Prefabs`; at runtime the model matching the device type is shown automatically.
5. It is recommended to upgrade XR Interaction Toolkit to **2.1.1 or later**. For how to use XRI itself, see the `xr` module.

### 2. Integrating Hand Tracking + 3D Object Interaction With XRI

Environment: PICO Neo3 / PICO 4 / PICO 4 Ultra series, system **5.11.0 or later**; SDK requires **2.3.0 or later** (the v2.3.0 hand algorithm aligns with OpenXR and is incompatible with older versions). XRI hand gesture interaction additionally requires system **5.12.0 or later** and XRI **2.3.0–2.6.2 or 3.x**.

1. Delete `LeftHand Controller` / `RightHand Controller` under `XR Origin`.
2. Drag `HandLeft` and `HandRight` under `Packages > PICO Integration > Assets > Resources > Prefabs` under `XR Origin`, at the same level as `Main Camera`. The hand models are already bound to joints per the OpenXR specification, and are bound to `RayPose (Transform)` and `DefaultRay`; `PXR_Hand` is the hand model configuration script.
3. Attach `PXR_Manager` to `XR Origin`, tick **Hand Tracking**, and set **Hand Tracking Support** to `Controller And Hands` (automatic switching) or `Hands Only`.
4. Optional: tick **Adaptive Hand Model (PICO)** (writes `<meta-data android:name="Enable_AdaptiveHandModel" android:value="1" />`, PICO 4 Ultra only), and tick **High Frequency Tracking (60Hz)** (writes `Hand_Tracking_HighFrequency`; cannot be turned off at runtime).
5. On the device: Neo3 / PICO 4 go to **"设置 > 实验室 > 手势识别"** (Settings > Lab > Gesture recognition); PICO 4 Ultra goes to **"控制中心 > 设置 > 交互 > 交互方式 = 手势与手柄自动切换"** (Control Center > Settings > Interaction > Interaction mode = Automatic switch between gestures and controllers). The quoted Chinese strings are the device's on-screen menu labels.
6. To use XRI's Hands Interaction Demo: install the Unity **XR Hands** package + import the **Hand Visualizer** sample; import XRI's **Starter Assets** and **Hands Interaction Demo**; open `XRI Default Input Actions`, and add Bindings for `Aim Position` / `Aim Rotation` / `Aim Flags` of `XRI LeftHand` and `Select` / `Select Value` / `UI Press` / `UI Press Value` of `XRI LeftHand Interaction`, with paths per rule 7; the right hand is the same. Then open `HandsDemoScene`.
7. Reading data at runtime: `PXR_HandTracking.GetJointLocations(HandType, ref HandJointLocations)` gets the 26 joints; `PXR_HandTracking.GetAimState(HandType, ref HandAimState)` gets the ray pose and pinch strength; `PXR_HandTracking.GetActiveInputDevice()` gets the current input source.
8. Input source change event: the guide writes it as `PXR_Plugin.System.InputDeviceChanged` (returns `0` headset / `1` controller / `2` hands), while the API reference writes it as `PXR_Input.InputDeviceChanged` (`Action<ActiveInputDevice>`, `HeadActive` / `ControllerActive` / `HandTrackingActive`). Both coexist; before integrating, use the SDK source to confirm which one is actually usable.
9. Gesture design is limited by the recognition range (PICO 4): depth 152 mm~500 mm, up 57.5°, down 72.5°, left 61.5°, right 60.5°; avoid occlusion between joints of both hands or of a single hand.

### 3. Haptic Feedback (Non-Buffered + Buffered)

```csharp
// Non-buffered: right controller, amplitude 0.5, duration 500 ms, frequency 100 Hz
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0.5f, 500, 100);
// To stop = call again, with amplitude and duration zeroed
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0f, 0, 100);

// Buffered (AudioClip): cache without vibrating, then start manually
int sourceId = 0;
PXR_Input.SendHapticBuffer(PXR_Input.VibrateType.RightController, audioClip,
    PXR_Input.ChannelFlip.No, ref sourceId, PXR_Input.CacheType.CacheNoVibrate);
PXR_Input.StartHapticBuffer(sourceId);

// Buffered (PHF file): both controllers, standard amplitude
PXR_Input.SendHapticBuffer(PXR_Input.VibrateType.BothController, phf_text,
    PXR_Input.ChannelFlip.No, 1, ref sourceId);

PXR_Input.UpdateHapticBuffer(sourceId, PXR_Input.VibrateType.LeftController, PXR_Input.ChannelFlip.No, 2);
PXR_Input.PauseHapticBuffer(sourceId);
PXR_Input.ResumeHapticBuffer(sourceId);
PXR_Input.StopHapticBuffer(sourceId);   // Pass 0 or nothing = stop all
```

- When `channelFlip = Yes`, the left audio channel drives the right controller and the right audio channel drives the left controller.
- Valid `amplitudeScale` values are 0~2: 0 no vibration, 1 standard amplitude, 2 twice the standard amplitude.
- Frequency suggestions: drums / basketball 50~100 Hz; shooting / ping-pong about 170 Hz; stone collision about 300 Hz.
- When `StartHapticBuffer` is called consecutively, the vibration data of the earlier call is overwritten by the later one.

### 4. Integrating Eye / Face Tracking

Eye tracking: device limited to PICO Neo3 Pro Eye / PICO 4 Pro / PICO 4 Enterprise, system 5.4.0+ (calibration 5.5.0+); SDK 2.3.0+ and system 5.7.0+ are recommended.

1. Attach `PXR_Manager` to `XR Origin`, tick **Eye Tracking** (the SDK automatically writes `picovr.software.eye_tracking` and `com.picovr.permission.EYE_TRACKING`), and optionally tick **Eye Tracking Calibration**.
2. `GetEyeTrackingSupported(ref supported, ref count, ref modes)` → `StartEyeTracking(ref EyeTrackingStartInfo)` (`mode = EyeTrackingMode.PXR_ETM_BOTH`) → each frame `GetEyeTrackingState(ref isTracking, ref state)` + `GetEyeTrackingData(ref EyeTrackingDataGetInfo, ref EyeTrackingData)` → call `StopEyeTracking(ref EyeTrackingStopInfo)` in `OnDisable`.
3. Pass `0` for the reserved `EyeTrackingDataGetInfo.displayTime` field; combine `flags` using `EyeTrackingDataGetFlags.PXR_EYE_DEFAULT | PXR_EYE_POSITION | PXR_EYE_ORIENTATION`. Note that `PXR_EYE_DEFAULT = 0` means "return no data at all".

Face tracking: Lipsync Only supports Neo3 and the PICO 4 series; Face Only / Hybrid is only on PICO 4 Pro and PICO 4 Enterprise; system 5.7.0+.

1. Choose a mode in **Face Tracking Mode** on `PXR_Manager` (the panel only offers Hybrid); tick **Allow 'unsafe' Code** in Player settings, and on the device turn on **"设置 > 实验室 > 表情模拟"** (Settings > Lab > Expression simulation). The quoted Chinese strings are the device's on-screen menu labels.
2. Call order: `WantFaceTrackingService()` → `GetFaceTrackingSupported(...)` → `StartFaceTracking(ref FaceTrackingStartInfo)` → `GetFaceTrackingState` / `GetFaceTrackingData` → `StopFaceTracking(ref FaceTrackingStopInfo)`.
3. Permissions are written automatically per mode: Hybrid writes three entries (`picovr.software.face_tracking` + `com.picovr.permission.FACE_TRACKING` + `android.permission.RECORD_AUDIO`); Face Only drops the audio recording permission; Lipsync Only drops the FACE_TRACKING permission.
4. Hybrid gives the best results but the highest CPU usage; you can downgrade to Face Only / Lipsync Only depending on the scene.

### 5. Motion Trackers: Full-Body Motion Capture vs Independent Tracking (Choose One)

Environment: PICO 4 series / PICO 4 Ultra series, system **5.13.0 or later**, **PICO motion tracker (official release)**; prerequisites are that the scene already has `XR Origin` + `PXR_Manager (Script)`, with **Body Tracking** ticked.

Full-body motion capture:

1. `GetBodyTrackingSupported(ref supported)` to check support.
2. `StartBodyTracking(mode, boneLength)` to start; `BodyTrackingBoneLength` is in centimeters, parts not set use default values, and it takes effect only in `BTM_FULL_BODY_HIGH` mode.
3. Each frame, `GetBodyTrackingState(ref isTracking, ref state)` and check `state.stateCode == BodyTrackingStatusCode.BT_VALID`; otherwise call `StartMotionTrackerCalibApp()`.
4. `GetBodyTrackingData(ref BodyTrackingGetDataInfo, ref BodyTrackingData)` reads `PosX/PosY/PosZ` and `RotQx/RotQy/RotQz/RotQw` of `roleDatas[i].localPose` to assign bones. The coordinate system is the same world coordinate system as headset data, and the root joint is `0 (Pelvis)`.
5. Call `StopBodyTracking()` when leaving the scene.
6. Bone poses are **stored in the parent node on the side closer to the root**: for example, `LEFT_KNEE(4)` stores the left knee joint position + the left shin bone pose, and `LEFT_ANKLE(7)` stores the left ankle position + the left foot pose.

Independent tracking:

1. `PXR_MotionTracking.RequestMotionTrackerCompleteAction += cb;` then `CheckMotionTrackerNumber(MotionTrackerNum.TWO)` (count range [0,3]).
2. In the callback, read `trackerCount` and `trackerIds` from `RequestMotionTrackerCompleteEventData`.
3. Each frame, `GetMotionTrackerLocation(trackerId, ref MotionTrackerLocation, ref isValidPose)`, then assign using `location.pose.Position.ToVector3()` / `location.pose.Orientation.ToQuat()`.
4. Peripheral pass-through: `SetExtDevTrackerPassDataState(true)` + subscribe to `ExtDevPassDataAction` (value `1` starts reading, `0` stops), then `GetExpandDeviceCustomData(out list)` / `SetExpandDeviceCustomData(ref array)`; device list `GetExpandDevice(out devices)`, battery `GetExpandDeviceBattery`, vibration `SetExpandDeviceVibrate`. Of the 16-byte custom data, **only the first 15 bytes are valid**.

### 6. System Keyboard and Controller-Ray UI

1. Prerequisites: XRI **2.1.0 or later** + import **Starter Assets**; the scene already has `XR Origin` with left and right controllers configured.
2. Add **UI > Event System** and **UI > Canvas** in the Hierarchy (or directly **XR > UI Canvas**).
3. Canvas: **Render Mode = World Space**, **Event Camera = scene main camera**, add the **Tracked Device Graphic Raycaster** component (the system-keyboard page writes it as "Tracked Device Graphic Raycast").
4. Right-click the Canvas > **UI > Input Field - TextMeshPro** to add an input field; clicking the input field with the ray summons the system keyboard.
5. Select `Left Controller` / `Right Controller`, and on the **XR Ray Interactor** set **Max Raycast Distance** to **greater than the actual distance from the camera to the UI**; when the ray touches the input field, it turns white to indicate it is interactive.

## Core API Anchors

Namespace: `Unity.XR.PXR`.

### PXR_Input (Current)

```csharp
ControllerStatus GetControllerStatus(Controller controller)
ControllerDevice GetControllerDeviceType()
bool  IsControllerConnected(Controller controller)
void  SetControllerOriginOffset(Controller controller, Vector3 offset)
Quaternion GetControllerPredictRotation(Controller controller, double predictTime)
Vector3    GetControllerPredictPosition(Controller controller, double predictTime)
Action<ActiveInputDevice> InputDeviceChanged;              // Input source change callback
GetControllerHandness()                                    // Gets the system dominant hand; PICO G3 only
 
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

Enums: `Controller` (LeftController/RightController), `ControllerDevice` (G2=3, Neo2, Neo3, PICO_4, G3, PICO_4U, NewController=10), `ControllerStatus` (Static=0/SixDof/ThreeDof/Sleep/CollidedIn3Dof/CollidedIn6Dof), `VibrateType` (None=0, LeftController=1, RightController=2, BothController=3, **LeftPICO4U=4, RightPICO4U=8, BothPICO4U=12**), `VibrateController` (No=0/Left=1/Right=2/LeftAndRight=3), `CacheType` (DontCache=0/CacheAndVibrate=1/CacheNoVibrate=2), `ChannelFlip` (No/Yes), `CacheConfig` (CacheAndVibrate=1/CacheNoVibrate=2).

**Deprecated (do not write new code with these)**: `SetControllerVibration`, `SetControllerVibrationEvent`, `StartControllerVCMotor`, `StopControllerVCMotor`, `SetControllerAmp`, `StartVibrateBySharem`, `SaveVibrateByCache`, `StartVibrateByCache`, `ClearVibrateByCache`, `StartVibrateByPHF`, `PauseVibrate`, `ResumeVibrate`, `UpdateVibrateParams`, `CreateHapticStream`/`WriteHapticStream`/`SetHapticStreamSpeed`/`GetHapticStreamSpeed`/`GetHapticStreamCurrentFrameSequence`/`StartHapticStream`/`StopHapticStream`/`RemoveHapticStream`/`AnalysisHapticStreamPHF`, `GetDominantHand`/`SetDominantHand`, `GetBodyTrackingPose`, `GetMotionTrackerConnectStateWithID`, `GetMotionTrackerBattery`, `GetMotionTrackerCalibState`, `SetBodyTrackingMode`, `SetBodyTrackingBoneLength`, `ResetController`, `SetArmModelParameters`.

### PXR_HandTracking

```csharp
ActiveInputDevice GetActiveInputDevice()
bool GetAimState(HandType hand, ref HandAimState aimState)
bool GetJointLocations(HandType hand, ref HandJointLocations jointLocations)
static bool GetHandScale(HandType hand, ref float scale)
bool GetSettingState()                                     // (Deprecated)
```

Enums: `HandType` (HandLeft=0/HandRight=1), `ActiveInputDevice` (HeadActive=0/ControllerActive=1/HandTrackingActive=2), `HandAimStatus` (AimComputed/AimRayValid/AimIndexPinching/AimMiddlePinching/AimRingPinching/AimLittlePinching/AimRayTouched, bit flags), `HandLocationStatus` (OrientationValid/PositionValid/OrientationTracked/PositionTracked), `HandFinger` (Thumb/Index/Middle/Ring/**Pinky**).
Structs: `Vector3f`, `Quatf`, `Posef`, `HandAimState` (aimStatus/aimRayPose/pinchStrengthIndex/Middle/Ring/Little/touchStrengthRay), `HandJointLocation` (locationStatus/pose/radius), `HandJointLocations` (isActive/jointCount/handScale/jointLocations).
Components and prefabs: `PXR_Hand` (hand model configuration script, including `Computed` / `RayPose` / `RayValid` / `Pinch` / `PinchStrength`), `PXR_Hand Pose Generator` + `PXR_Hand Pose` (gesture editor, since v2.1.5), `HandLeft` / `HandRight` prefabs, `RayPose (Transform)`, `DefaultRay`.

### PXR_MotionTracking

```csharp
// Eye tracking (current)
int GetEyeTrackingSupported(ref bool supported, ref int supportedModesCount, ref EyeTrackingMode[] supportedModes)
int StartEyeTracking(ref EyeTrackingStartInfo startInfo)
int StopEyeTracking(ref EyeTrackingStopInfo stopInfo)
int GetEyeTrackingState(ref bool isTracking, ref EyeTrackingState state)
int GetEyeTrackingData(ref EyeTrackingDataGetInfo getInfo, ref EyeTrackingData data)
int GetEyeOpenness(ref float leftEyeOpenness, ref float rightEyeOpenness)      // PICO 4 Enterprise only
int GetEyePupilInfo(ref EyePupilInfo eyePupilPosition)                          // PICO 4 Enterprise only
int GetPerEyePose(ref long timestamp, ref Posef leftEyePose, ref Posef rightPose)   // PICO 4 Enterprise only
int GetEyeBlink(ref long timestamp, ref bool isLeftBlink, ref bool isRightBlink)    // PICO 4 Enterprise only

// Face (in the v3.4.0 API reference all are marked Deprecated, but the guide still treats them as the main line)
int WantFaceTrackingService()
unsafe int GetFaceTrackingSupported(ref bool supported, ref int supportedModesCount, ref FaceTrackingMode[] supportedModes)
int StartFaceTracking(ref FaceTrackingStartInfo startInfo)
int StopFaceTracking(ref FaceTrackingStopInfo stopInfo)
int GetFaceTrackingState(ref bool isTracking, ref FaceTrackingState state)
int GetFaceTrackingData(ref FaceTrackingDataGetInfo getInfo, ref FaceTrackingData data)

// Full-body motion capture (current)
int StartMotionTrackerCalibApp()
int GetBodyTrackingSupported(ref bool supported)
int StartBodyTracking(BodyTrackingMode mode, BodyTrackingBoneLength boneLength)
int StopBodyTracking()
int GetBodyTrackingState(ref bool isTracking, ref BodyTrackingState state)
int GetBodyTrackingData(ref BodyTrackingGetDataInfo getInfo, ref BodyTrackingData data)

// Independent tracking and peripherals (current)
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

Enums: `TrackingStateCode` (PXR_MT_SUCCESS=0, PXR_MT_FAILURE=-1, PXR_MT_MODE_NONE=-2, PXR_MT_DEVICE_NOT_SUPPORT=-3, PXR_MT_SERVICE_NEED_START=-4, PXR_MT_ET_PERMISSION_DENIED=-5, PXR_MT_FT_PERMISSION_DENIED=-6, PXR_MT_MIC_PERMISSION_DENIED=-7, PXR_MT_SYSTEM_DENIED=-8, PXR_MT_UNKNOW_ERROR=-9), `EyeTrackingMode` (PXR_ETM_NONE=-1/PXR_ETM_BOTH=0/PXR_ETM_COUNT=1), `PerEyeUsage` (LeftEye/RightEye/Combined/EyeCount), `EyeTrackingDataGetFlags`, `FaceTrackingMode` (PXR_FTM_NONE=-1/PXR_FTM_FACE=0/PXR_FTM_LIPS=1/**PXR_FTM_FACE_LIPS_VIS=2**/**PXR_FTM_FACE_LIPS_BS=3**), `FaceTrackingDataGetFlags` (PXR_FACE_DEFAULT=0), `BlendShapeIndex` (0~51 BlendShapes + 52~71 Viseme), `BodyTrackingMode` (BTM_FULL_BODY_LOW=0/BTM_FULL_BODY_HIGH=1), `BodyTrackingStatusCode` (BT_INVALID=0/BT_VALID=1/BT_LIMITED=2), `BodyTrackingErrorCode` (BT_ERROR_INNER_EXCEPTION=0 … BT_ERROR_TRACKING_POSE_ERROR=7), `BodyTrackingGetDataFlags` (PXR_BODY_NONE/PXR_BODY_POSE/PXR_BODY_ACTION/PXR_BODY_VELO_ACC), `BodyTrackerRole` (Pelvis=0 … RIGHT_HAND=23), `MotionTrackerType` (MT_1=Beta / MT_2=official release), `MotionTrackerMode` (BodyTracking/MotionTracking), `MotionTrackerNum` (NONE/ONE/TWO/THREE), `MotionTrackerConfidence`.
Structs: `EyeTrackingStartInfo` (needCalibration/mode), `EyeTrackingState`, `EyeTrackingDataGetInfo`, `EyeTrackingData` (`eyeDatas`, length `(int)PerEyeUsage.EyeCount`), `PerEyeData`, `EyePupilInfo`, `FaceTrackingStartInfo` / `FaceTrackingStopInfo` / `FaceTrackingState` / `FaceTrackingDataGetInfo` / `FaceTrackingData`, `BodyTrackingStartInfo`, `BodyTrackingBoneLength` (headLen/neckLen/torsoLen/hipLen/upperLegLen/lowerLegLen/footLen/shoulderLen/upperArmLen/lowerArmLen/handLen, in cm), `BodyTrackingState`, `BodyTrackingGetDataInfo`, `BodyTrackingRoleData`, `BodyTrackingData`, `MotionTrackerLocation` / `MotionTrackerLocations` (localLocation / globalLocation; the latter is not recommended unless there is a special need), `TrackerSN` (up to 24), `RequestMotionTrackerCompleteEventData`, `ExpandDevicesCustomData`.

### Other PICO-Side Anchors

- `PXR_Manager (Script)` panel toggles: `Eye Tracking`, `Eye Tracking Calibration`, `Hand Tracking` (including `Hand Tracking Support`), `Adaptive Hand Model (PICO)`, `High Frequency Tracking (60Hz)`, `Face Tracking Mode`, `Body Tracking`, `Tracking Origin Mode`, `Camera Y Offset`, `Stage Mode`.
- `PXR_System.SetTrackingOrigin` / `GetTrackingOrigin`; legacy face interfaces `PXR_System.EnableFaceTracking` / `EnableLipSync` / `GetFaceTrackingData` / `SetFaceTrackingStatus` (2.1.4+).
- Eye tracking interfaces from before the refactor are in `PXR_EyeTracking`; for the old-vs-new motion tracking interface comparison, see [motion-tracker-api-compatibility](https://developer-cn.picoxr.com/document/unity/motion-tracker-api-compatibility/).
- Unity side: `InputDevices.GetDeviceAtXRNode`, `InputDevice.TryGetFeatureValue`, `CommonUsages.*`, `XRNode.LeftHand`, `KeyCode.Escape` / `KeyCode.JoystickButton0` / `KeyCode.Home`, `XR Origin`, `XR Ray Interactor`, `Tracked Device Graphic Raycaster`, `XRI Default Input Actions`, `PicoAimHand`, the Unity **XR Hands** package.
- Per-eye split screen: the main camera uses `Target Eye = None (Main Display)`; child cameras are set to `Left` / `Right` respectively and bind layers via `Culling Mask` ([how-to-set-up-a-camera-for-each-eye](https://developer-cn.picoxr.com/document/unity/how-to-set-up-a-camera-for-each-eye/)).

### ⚠ Contradictions in the Official Docs (Verify Against SDK Source Before Shipping)

| Guide form | API reference form | Recommended handling |
|---|---|---|
| `PXR_MotionTracking.StartBodyTracking(BodyJointSet.BODY_JOINT_SET_BODY_FULL_START, boneLength)` | `StartBodyTracking(BodyTrackingMode mode, BodyTrackingBoneLength boneLength)`, `BTM_FULL_BODY_LOW` / `BTM_FULL_BODY_HIGH` | Follow the API reference signature; confirm the actual enum names with IDE completion |
| `BodyTrackingStatus bs` / `BodyTrackingMessage.BT_MESSAGE_TRACKER_NOT_CALIBRATED` | `BodyTrackingState state` / `BodyTrackingErrorCode.BT_ERROR_TRACKER_NOT_CALIBRATED` | Same as above |
| `BodyTrackerRole.ROLE_NUM` used as the array length | `(int)BodyTrackerRole.NONE_ROLE` used as the array length | Same as above |
| "To use Hybrid (Blendshape), set mode to `PXR_FTM_FACE_LIPS_VIS`" | `PXR_FTM_FACE_LIPS_VIS` = Viseme output, `PXR_FTM_FACE_LIPS_BS` = Blendshape output | Choose by enum semantics; this sentence of the guide is self-contradictory |
| `FaceTrackingSupportedMode.PXR_FTM_FACE_LIPS_VIS` | The enum name is `FaceTrackingMode` | Use `FaceTrackingMode` |
| `EyeTrackingStartInfo.needCalibration = 1` (example) | Struct description: `0` = calibration required, `1` = not required | Assign explicitly per the struct semantics; do not copy the example verbatim |

## DO NOT

```csharp
// ❌ Using deprecated vibration interfaces
PXR_Input.SetControllerVibration(0.5f, 500, Controller.RightController);
PXR_Input.SetControllerVibrationEvent(1, 100, 0.5f, 500);
// ✅ v3.x uniformly uses SendHapticImpulse
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0.5f, 500, 100);
```

```csharp
// ❌ Looking for a nonexistent "StopHapticImpulse" to stop non-buffered vibration
PXR_Input.StopHapticImpulse(VibrateType.RightController);
// ✅ Call SendHapticImpulse again, with amplitude and duration zeroed
PXR_Input.SendHapticImpulse(VibrateType.RightController, 0f, 0, 100);
```

```csharp
// ❌ Using old audio/PHF vibration interfaces (all Deprecated)
PXR_Input.StartControllerVCMotor(path, slot);
PXR_Input.StartVibrateByPHF(phfText, ref sourceId);
// ✅ Uniformly use the three SendHapticBuffer overloads (AudioClip / PCM / PHF TextAsset)
PXR_Input.SendHapticBuffer(PXR_Input.VibrateType.BothController, phfText,
    PXR_Input.ChannelFlip.No, 1, ref sourceId);
```

```csharp
// ❌ Reading eye data without starting (and do not expect ticking the panel to start it automatically)
PXR_MotionTracking.GetEyeTrackingData(ref info, ref data);
// ✅ Start first; the permission prompt appears only on the first GetEyeTrackingData call
var startInfo = new EyeTrackingStartInfo { mode = EyeTrackingMode.PXR_ETM_BOTH };
PXR_MotionTracking.StartEyeTracking(ref startInfo);
PXR_MotionTracking.GetEyeTrackingData(ref info, ref data);
```

```csharp
// ❌ Guessing method names (none of these exist on PXR_HandTracking)
PXR_HandTracking.GetHandJoints(...);
PXR_HandTracking.GetHandJointLocations(...);
PXR_HandTracking.IsHandTracking();
// ✅ Only these exist
PXR_HandTracking.GetJointLocations(HandType.HandLeft, ref jointLocations);
PXR_HandTracking.GetAimState(HandType.HandLeft, ref aimState);
PXR_HandTracking.GetActiveInputDevice();
PXR_HandTracking.GetHandScale(HandType.HandLeft, ref scale);
```

```csharp
// ❌ Pushing a right-handed coordinate system pose directly into a Unity Transform
child.localPosition = new Vector3(location.pose.Position.x, location.pose.Position.y, location.pose.Position.z);
// ✅ Use the SDK's built-in conversion extensions
child.localPosition = location.pose.Position.ToVector3();
child.localRotation = location.pose.Orientation.ToQuat();
```

```csharp
// ❌ Querying position before obtaining a trackerId
PXR_MotionTracking.GetMotionTrackerLocation(0, ref location, ref isValid);
// ✅ Request the count first; get trackerIds in the callback
PXR_MotionTracking.RequestMotionTrackerCompleteAction += OnComplete;
PXR_MotionTracking.CheckMotionTrackerNumber(MotionTrackerNum.TWO);
```

```csharp
// ❌ Using deprecated motion tracking query interfaces
PXR_MotionTracking.CheckMotionTrackerModeAndNumber(MotionTrackerMode.MotionTracking, MotionTrackerNum.TWO);
PXR_MotionTracking.GetMotionTrackerLocations(sn, ref locations, ref confidence, 0);
// ✅ The current version in v3.4.0
PXR_MotionTracking.CheckMotionTrackerNumber(MotionTrackerNum.TWO);
PXR_MotionTracking.GetMotionTrackerLocation(trackerId, ref location, ref isValidPose);
```

- ❌ Expecting hand gestures and controllers to be usable at the same time (an interaction where one hand holds a controller and the other is bare-handed) → ✅ The device supports only one of the two; `Controller And Hands` is an automatic switch.
- ❌ Keeping `LeftHand Controller` / `RightHand Controller` while using hand models → ✅ Delete them first; otherwise the hand model and controller model appear together.
- ❌ Trying to turn off 60Hz high-frequency hand tracking at runtime → ✅ It can only be decided at edit time; once enabled, it cannot be turned off at runtime.
- ❌ Expecting full-body motion capture data and independent tracking data at the same time → ✅ The two modes are mutually exclusive; switch according to the scene.
- ❌ Writing input bindings for the screenshot key, volume keys, or Home key → ✅ These have no exposed key values or are reserved by the system.
