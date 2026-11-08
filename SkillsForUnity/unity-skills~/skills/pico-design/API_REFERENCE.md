# PICO Unity SDK - XR Core Class API Signature Reference (v3.4.0)

> Anchored to the PICO official documentation for v3.4.0 (crawled 2026-08-13). Every rule can be checked against its source URL.
> This file covers only the **XR core classes** (9 classes) under `client-api`, plus the Legacy disposition notes. Platform service classes (`*Service`) are out of scope.

Source root (referred to below as "the docs"): `https://developer-cn.picoxr.com/reference/unity/client-api/<ClassName>/`

## When to load this document

- Writing, changing or reviewing any `PXR_*` static class call, when unsure of the method name, parameter order or return type.
- Checking whether an API is already **Deprecated** in v3.4.0, and whether it should still appear in new code.
- Migrating an older project (v2.x or v3.0 era code) to v3.4.0, when you need to identify every outdated call.
- Confirming whether a capability is **available on consumer devices** or is **enterprise-only** (`PXR_Enterprise`).
- Checking method names one by one in AI-generated PICO code, to prevent invented APIs.

## Key Rules

| # | Rule | Source |
|---|------|--------|
| 1 | These APIs are **class static methods**, called as `PXR_System.GetSDKVersion()`. Many signatures in the docs are explicitly `public static` (for example `public static ControllerStatus GetControllerStatus(Controller controller)`, and all methods of `PXR_CameraImage`). Do not use `new`, and do not treat them as components with `GetComponent`. | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) / [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 2 | Methods marked `(Deprecated)` **must not be used in new code**. The deprecated surface in v3.4.0 is large: the whole legacy vibration and body-tracking group on `PXR_Input`, the whole `AnchorEntity` API on `PXR_MixedReality`, the whole face-tracking group on `PXR_System`, and the whole face-tracking group on `PXR_MotionTracking` are all marked. | Individual class pages |
| 3 | The **whole `PXR_EyeTracking` class is legacy**. The first sentence of its docs says: "For the latest ones, refer to the `PXR_MotionTracking` class". New code uses the eye interfaces of `PXR_MotionTracking`. | [PXR_EyeTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_EyeTracking/) |
| 4 | `PXR_MotionTracking` methods return **`int` (`TrackingStateCode`), not `bool`**: `PXR_MT_SUCCESS = 0`, `PXR_MT_FAILURE = -1`, `PXR_MT_DEVICE_NOT_SUPPORT = -3`, `PXR_MT_SERVICE_NEED_START = -4`, `PXR_MT_ET_PERMISSION_DENIED = -5`. `if (GetEyeTrackingData(...))` does not compile. | [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |
| 5 | `PXR_MixedReality` methods return the **`PxrResult`** enum. `SUCCESS = 0`, and failures are negative (`ERROR_PERMISSION_INSUFFICIENT = -1000710000`, `ERROR_SPATIAL_SENSING_SERVICE_UNAVAILABLE = -1005`, `ERROR_HANDLE_INVALID = -12`, etc.). Async interfaces return `Task<...>` tuples and must be `await`ed. | [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 6 | `QuerySpatialAnchorAsync` allows **only one call in flight at a time**. Wait for the previous call to finish before calling again. | [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 7 | `PXR_Enterprise` is **available only on enterprise devices** (PICO Neo2 / Neo2 Eye / Neo3 Pro / Neo3 Pro Eye / G2 4K, 4K E, 4K Plus on system 4.0.3+ / PICO 4 Enterprise). The docs state explicitly "Do not use them on consumer devices". Do not call it on consumer devices. | [PXR_Enterprise](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/) |
| 8 | Enterprise APIs have a required call order: first `InitEnterpriseService(bool isCamera)` ("Must be called before calling other enterprise APIs"), then `BindEnterpriseService(Action<bool> callback)` ("Must be called before calling other system related functions"). On exit, call `UnBindEnterpriseService()`. | [PXR_Enterprise](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/) |
| 9 | `PXR_FoveationRendering.SetFoveationLevel` takes **two parameters** `(FoveationLevel level, bool isETFR)`, not one. `FoveationLevel.None = -1` (not 0). `SetFoveationParameters` is Deprecated. | [PXR_FoveationRendering](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_FoveationRendering/) |
| 10 | `PXR_System.SetSystemDisplayFrequency(float rate)` accepts only **72 / 90 / 120**. The docs say "Other values are invalid". Do not pass 60 or 144. | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 11 | `PXR_System.GetSensorStatus()` returns **0 = null / 1 = 3DoF / 3 = 6DoF** (there is **no 2**). Do not check for 0/1/2. | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 12 | `PXR_System.SetExtraLatencyMode` is already Deprecated, and it is also limited to "Call this function once only". | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 13 | `PXR_Boundary.GetDimensions(BoundaryType)` **accepts only `PlayArea`**. With a stationary boundary it returns `(0,1,0)`. `GetGeometry(PlayArea)` returns empty for a stationary boundary. | [PXR_Boundary](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Boundary/) |
| 14 | `PXR_Boundary.EnableSeeThroughManual(bool)` has two hard prerequisites: camera clear flags must be solid color, and the background color alpha must be 0. The feature stops when the app pauses, so it **must be called again after resume**. | [PXR_Boundary](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Boundary/) |
| 15 | Camera images from `PXR_CameraImage` must be released in pairs: `AcquireCameraImage` then `GetCameraImageData` then `ReleaseCameraImage`. When no new image is available, the runtime returns `XR_CAMERA_IMAGE_NO_UPDATE_PICO`. This is a normal state, not an error. | [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 16 | Only `CreateCameraDeviceAsync` and `CreateCameraCaptureSessionAsync` in `PXR_CameraImage` are `async Task<PxrResult>`. All others are synchronous `PxrResult`. Do not add `await` to synchronous methods. | [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 17 | `PXR_Input.GetControllerStatus(Controller)` returns the **`ControllerStatus` enum** (`Static` / `SixDof` / `ThreeDof` / `Sleep` / `CollidedIn3Dof` / `CollidedIn6Dof`), not `bool`. To check connection, use `IsControllerConnected(Controller)`. | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 18 | In `PXR_Input`, `VibrateController` and `VibrateType` are **two different enums**. The old (Deprecated) vibration APIs use `VibrateController`. The current Haptic APIs use `VibrateType` (including `LeftPICO4U=4` / `RightPICO4U=8` / `BothPICO4U=12`). Do not mix them. | [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/) |
| 19 | The method name of `PXR_Boundary.SetGuardianSystemDisable(bool value)` is opposite to the parameter description in the docs (the docs say `true: enable / false: disable`). Follow the docs' parameter table and confirm by testing. Do not infer from the method name. | [PXR_Boundary](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Boundary/) |
| 20 | Face-tracking APIs are **Deprecated in both places** in v3.4.0: `EnableFaceTracking` / `EnableLipSync` / `GetFaceTrackingData` / `SetFaceTrackingStatus` on `PXR_System`, and `WantFaceTrackingService` / `StartFaceTracking` / `StopFaceTracking` / `GetFaceTrackingState` / `GetFaceTrackingData` on `PXR_MotionTracking`. The corpus gives no replacement class for this group. Do not invent a replacement API. | [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) / [PXR_MotionTracking](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MotionTracking/) |

## Workflows

### 1. Check whether a PICO API exists (anti-hallucination lookup)

1. First locate the class. Controller and haptics: `PXR_Input`. Headset system, volume, brightness, refresh rate: `PXR_System`. Boundary: `PXR_Boundary`. Foveated rendering: `PXR_FoveationRendering`. Gestures: `PXR_HandTracking`. Eye, face, body tracking and motion trackers: `PXR_MotionTracking`. Spatial anchors, scene and VST effects: `PXR_MixedReality`. Camera images: `PXR_CameraImage`. Device management: `PXR_Enterprise`.
2. Look up the method name in the method table of the matching section in this file. **If it is not in the table, the corpus does not contain it, so treat it as nonexistent.** Do not fill it in from memory.
3. Check the Deprecated column. `Yes` means use the current interface (see "Legacy API disposition"). `No` means copy the parameter order and the `ref` / `out` modifiers exactly from the signature column.
4. If still unsure, open the class's source URL to verify. Do not guess.

### 2. Common call sequences (this file gives signatures only. See the area docs for full steps)

- Controller haptic vibration: `SendHapticImpulse` / `SendHapticBuffer` (three overloads: AudioClip / PCM / PHF), with `sourceId` controlling `Start/Pause/Resume/Stop/UpdateHapticBuffer`. The whole old group, including `SetControllerVibration` / `StartVibrateBySharem` / `CreateHapticStream`, is Deprecated (see "Legacy API disposition"). Full steps are in INTERACTION.md, "Haptic feedback".
- Spatial anchors: `StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor)` then `GetSenseDataProviderState` (expect `Running`) then `CreateSpatialAnchorAsync` then `PersistSpatialAnchorAsync` then `QuerySpatialAnchorAsync` (one request at a time) then `LocateAnchor` then `UnPersistSpatialAnchorAsync` then `DestroyAnchor` then `StopSenseDataProvider`. Full steps are in MR.md, "Spatial anchor lifecycle".
- Eye tracking: `GetEyeTrackingSupported` then `StartEyeTracking` then `GetEyeTrackingState` / `GetEyeTrackingData` (`flags` comes from `EyeTrackingDataGetFlags`) then `StopEyeTracking`. Check errors by the `TrackingStateCode` return value (`-5` = eye tracking permission denied). Full steps are in INTERACTION.md, "Eye and face tracking integration".

### 3. Enterprise device management calls (enterprise devices only)

1. `PXR_Enterprise.InitEnterpriseService(bool isCamera)` (`isCamera` controls whether video see-through is enabled. Default `false`).
2. `PXR_Enterprise.BindEnterpriseService(Action<bool> callback)`. Other system-related interfaces may be called only after the callback returns `true`.
3. Call the target interfaces (see the category list below).
4. When the app exits or no longer needs them, call `PXR_Enterprise.UnBindEnterpriseService()`.

## Core API Anchors

> Signature columns are taken from the code blocks on each class page. The Deprecated column is taken from the `(Deprecated)` prefix in the "Member functions / Functions" table of the docs.

### PXR_Boundary

**Responsibility**: visibility of the safety boundary, configuration state, geometry data and trigger detection.

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| SetVisible | `void SetVisible(bool value)` | Set the boundary visible (can be overridden by system or user settings) | No |
| GetVisible | `bool GetVisible()` | Whether the boundary is visible | No |
| GetConfigured | `bool GetConfigured()` | Whether the boundary is configured (boundary interfaces are unavailable when not configured) | No |
| GetEnabled | `bool GetEnabled()` | Whether the boundary is enabled | No |
| TestNode | `PxrBoundaryTriggerInfo TestNode(BoundaryTrackingNode node, BoundaryType boundaryType)` | Test whether a tracking node triggers the boundary | No |
| TestPoint | `PxrBoundaryTriggerInfo TestPoint(PxrVector3f point, BoundaryType boundaryType)` | Test whether a point triggers the boundary | No |
| GetGeometry | `Vector3[] GetGeometry(BoundaryType boundaryType)` | Get the boundary point set | No |
| GetDimensions | `Vector3 GetDimensions(BoundaryType boundaryType)` | Get the PlayArea size (pass PlayArea only) | No |
| EnableSeeThroughManual | `void EnableSeeThroughManual(bool value)` | Use the camera image as the environment background | No |
| GetSeeThroughTrackingState | `PxrTrackingState GetSeeThroughTrackingState()` | Get the see-through tracking state | **Yes** |
| SetGuardianSystemDisable | `void SetGuardianSystemDisable(bool value)` | Turn the boundary system on or off (see rule 19) | No |
| UseGlobalPose | `void UseGlobalPose(bool value)` | Use the global pose | **Yes** |

Enums: `BoundaryType{OuterBoundary, PlayArea}`, `BoundaryTrackingNode{HandLeft, HandRight, Head}`, `PxrTrackingState{LostNoReason, LostCamera, LostHighLight, LostLowLight, LostLowFeatureCount, LostReLocation, LostInitialization, LostNoCamera, LostNoIMU, LostIMUJitter, LostUnknown}`.
Struct: `PxrBoundaryTriggerInfo{isTriggering, closestDistance, closestPoint, closestPointNormal, valid}`.

### PXR_FoveationRendering

**Responsibility**: foveated rendering level and parameter settings.

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| SetFoveationLevel | `bool SetFoveationLevel(FoveationLevel level, bool isETFR)` | Set the foveated rendering level. `isETFR` selects eye-tracked foveated rendering | No |
| GetFoveationLevel | `FoveationLevel GetFoveationLevel()` | Get the current level | No |
| SetFoveationParameters | `void SetFoveationParameters(float foveationGainX, float foveationGainY, float foveationArea, float foveationMinimum)` | Set foveated rendering detail parameters | **Yes** |

Enum: `FoveationLevel{None = -1, Low, Med, High, TopHigh}`.

### PXR_HandTracking

**Responsibility**: hand-tracking input source, ray and pinch state, joint positions and hand scale.

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| GetSettingState | `bool GetSettingState()` | Whether hand tracking is enabled | **Yes** |
| GetActiveInputDevice | `ActiveInputDevice GetActiveInputDevice()` | The currently active input device | No |
| GetAimState | `bool GetAimState(HandType hand, ref HandAimState aimState)` | Get the ray and finger pinch state and strength | No |
| GetJointLocations | `bool GetJointLocations(HandType hand, ref HandJointLocations jointLocations)` | Get the joint positions of the given hand | No |
| GetHandScale | `public static bool GetHandScale(HandType hand, ref float scale)` | Get the hand model scale | No |

Enums: `HandType{HandLeft=0, HandRight=1}`, `ActiveInputDevice{HeadActive=0, ControllerActive=1, HandTrackingActive=2}`, `HandAimStatus{AimComputed, AimRayValid, AimIndexPinching, AimMiddlePinching, AimRingPinching, AimLittlePinching, AimRayTouched}` (bit flags), `HandLocationStatus{OrientationValid, PositionValid, OrientationTracked, PositionTracked}`, `HandFinger{Thumb=0, Index, Middle, Ring, Pinky}`.
Structs: `HandAimState`, `HandJointLocation`, `HandJointLocations` (`jointCount` currently returns **26**), `Posef`, `Quatf`, `Vector3f`.

### PXR_System

**Responsibility**: SDK version, sensor status, refresh rate, performance levels, tracking origin, battery and volume services.

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| GetSDKVersion | `string GetSDKVersion()` | Get the SDK version | No |
| GetPredictedDisplayTime | `double GetPredictedDisplayTime()` | Get the predicted display time (milliseconds) | No |
| SetExtraLatencyMode | `bool SetExtraLatencyMode(int mode)` | Extra latency mode (0/1/2). Can be called only once | **Yes** |
| GetSensorStatus | `int GetSensorStatus()` | Sensor status: 0 null / 1 3DoF / 3 6DoF | No |
| SetSystemDisplayFrequency | `void SetSystemDisplayFrequency(float rate)` | Set refresh rate. 72/90/120 only | No |
| GetSystemDisplayFrequency | `float GetSystemDisplayFrequency()` | Get the current refresh rate | No |
| GetPredictedMainSensorStateNew | `int GetPredictedMainSensorStateNew(ref PxrSensorState2 sensorState, ref int sensorFrameIndex)` | Get the predicted state of the main sensor | No |
| ContentProtect | `int ContentProtect(int data)` | Turn content protection on or off (0 off / 1 on). **Note: the docs have a details section for this, but it is not in the method table** | No |
| EnableFaceTracking | `void EnableFaceTracking(bool enable)` | Turn face tracking on or off | **Yes** |
| EnableLipSync | `void EnableLipSync(bool enable)` | Turn lip sync on or off | **Yes** |
| GetFaceTrackingData | `void GetFaceTrackingData(Int64 ts, GetDataType flags, ref PxrFaceTrackingInfo faceTrackingInfo)` | Get face tracking data | **Yes** |
| SetPerformanceLevels | `int SetPerformanceLevels(PxrPerfSettings which, PxrSettingsLevel level)` | Set CPU/GPU performance level | No |
| GetPerformanceLevels | `PxrSettingsLevel GetPerformanceLevels(PxrPerfSettings which)` | Get CPU/GPU performance level | No |
| SetEyeFOV | `int SetEyeFOV(EyeType eye, float fovLeft, float fovRight, float fovUp, float fovDown)` | Set the four-direction FOV for one eye | **Yes** |
| SetFaceTrackingStatus | `int SetFaceTrackingStatus(PxrFtLipsyncValue value)` | Switch face tracking mode | **Yes** |
| SetTrackingOrigin | `void SetTrackingOrigin(PxrTrackingOrigin originMode)` | Set the tracking origin mode (Device / Floor) | No |
| GetTrackingOrigin | `void GetTrackingOrigin(out PxrTrackingOrigin originMode)` | Get the tracking origin mode | No |
| StartBatteryReceiver | `bool StartBatteryReceiver(string objName)` | Start the battery service for the named object | No |
| StopBatteryReceiver | `bool StopBatteryReceiver()` | Stop the battery service | No |
| SetCommonBrightness | `bool SetCommonBrightness(int brightness)` | Set headset brightness [0,255] | **Yes** |
| GetCommonBrightness | `int GetCommonBrightness()` | Get headset brightness | **Yes** |
| GetScreenBrightnessLevel | `int[] GetScreenBrightnessLevel()` | Get the screen brightness level array | **Yes** |
| SetScreenBrightnessLevel | `void SetScreenBrightnessLevel(int brightness, int level)` | Set a screen brightness level | **Yes** |
| StartAudioReceiver | `bool StartAudioReceiver(string objName)` | Start the volume service for the named object | No |
| StopAudioReceiver | `bool StopAudioReceiver()` | Stop the volume service | No |
| GetMaxVolumeNumber | `int GetMaxVolumeNumber()` | Get the maximum volume | No |
| GetCurrentVolumeNumber | `int GetCurrentVolumeNumber()` | Get the current volume [0,15] | No |
| VolumeUp | `bool VolumeUp()` | Volume up | No |
| VolumeDown | `bool VolumeDown()` | Volume down | No |
| SetVolumeNum | `bool SetVolumeNum(int volume)` | Set the volume [0,15] | No |
| GetDisplayFrequenciesAvailable | Not given in the docs. Only the implementation `PXR_Plugin.System.UPxr_GetDisplayFrequenciesAvailable()` is shown | Get the list of available refresh rates (Hz) | No |

Enums and parameter types (as mentioned in the docs): `PxrPerfSettings{CPU, GPU}`, `PxrSettingsLevel{POWER_SAVINGS, SUSTAINED_LOW, SUSTAINED_HIGH, BOOST}`, `PxrTrackingOrigin` (Device / Floor), `EyeType{LeftEye, RightEye, BothEye}`, `GetDataType{PXR_GET_FACE_DATA_DEFAULT, PXR_GET_FACE_DATA, PXR_GET_LIP_DATA, PXR_GET_FACELIP_DATA}`, `PxrFtLipsyncValue{STOP_FT, STOP_LIPSYNC, START_FT, START_LIPSYNC}`. Structs: `PxrSensorState2`, `PxrFaceTrackingInfo`.

### PXR_CameraImage

**Responsibility**: query camera capabilities, create devices and capture sessions, and get raw image buffers and intrinsic and extrinsic parameters. All methods return `PxrResult`.

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| GetAvailableCameras | `public static PxrResult GetAvailableCameras(out XrCameraIdPICO[] cameraIds)` | Get available camera IDs | No |
| GetCameraPropertyTypesAvailable | `public static PxrResult GetCameraPropertyTypesAvailable(XrCameraIdPICO cameraId, out XrCameraPropertyTypePICO[] propertyTypes)` | Get the supported property types | No |
| GetCameraFacingProperties | `public static PxrResult GetCameraFacingProperties(XrCameraIdPICO cameraId, out XrCameraFacingPICO propertyValue)` | Get the facing property | No |
| GetCameraPositionProperties | `public static PxrResult GetCameraPositionProperties(XrCameraIdPICO cameraId, out XrCameraPositionPICO propertyValue)` | Get the position property | No |
| GetCameraCameraTypeProperties | `public static PxrResult GetCameraCameraTypeProperties(XrCameraIdPICO cameraId, out XrCameraTypePICO propertyValue)` | Get the camera type property | No |
| GetCameraCapabilityAvailable | `public static PxrResult GetCameraCapabilityAvailable(XrCameraIdPICO cameraId, out XrCameraCapabilityTypePICO[] capabilitys)` | Get the supported capability categories | No |
| GetCameraImageFpsCapability | `public static PxrResult GetCameraImageFpsCapability(XrCameraIdPICO cameraId, out XrCameraImageFpsPICO[] imageFps)` | Get supported frame rates | No |
| GetCameraCameraModelCapability | `public static PxrResult GetCameraCameraModelCapability(XrCameraIdPICO cameraId, out XrCameraModelPICO[] cameraModels)` | Get supported camera models | No |
| GetCameraDataTransferTypeCapability | `public static PxrResult GetCameraDataTransferTypeCapability(XrCameraIdPICO cameraId, out XrCameraDataTransferTypePICO[] dataTransferTypes)` | Get supported transfer types | No |
| GetCameraImageFormatCapability | `public static PxrResult GetCameraImageFormatCapability(XrCameraIdPICO cameraId, out XrCameraImageFormatPICO[] formats)` | Get supported image formats | No |
| GetCameraImageResolutionCapability | `public static PxrResult GetCameraImageResolutionCapability(XrCameraIdPICO cameraId, out PxrExtent2Di[] resolutions)` | Get supported resolutions | No |
| CreateCameraDeviceAsync | `public static async Task<PxrResult> CreateCameraDeviceAsync(XrCameraIdPICO cameraId, CancellationToken token = default)` | Asynchronously create a camera device handle | No |
| CreateCameraCaptureSessionAsync | `public static async Task<PxrResult> CreateCameraCaptureSessionAsync(XrCameraIdPICO cameraId, int width, int height, XrCameraImageFpsPICO fps, XrCameraImageFormatPICO format, XrCameraDataTransferTypePICO transferType, XrCameraModelPICO model, CancellationToken token = default)` | Asynchronously create a capture session | No |
| DestroyCameraDevice | `public static PxrResult DestroyCameraDevice(XrCameraIdPICO cameraId)` | Destroy the camera device | No |
| DestroyCameraCaptureSession | `public static PxrResult DestroyCameraCaptureSession(XrCameraIdPICO cameraId)` | Destroy the capture session | No |
| GetCameraIntrinsics | `public static PxrResult GetCameraIntrinsics(XrCameraIdPICO cameraId, out XrCameraIntrinsics intrinsics)` | Get camera intrinsics | No |
| GetCameraExtrinsics | `public static PxrResult GetCameraExtrinsics(XrCameraIdPICO cameraId, out XrCameraExtrinsics extrinsics)` | Get camera extrinsics | No |
| BeginCameraCapture | `public static PxrResult BeginCameraCapture(XrCameraIdPICO cameraId)` | Start capture | No |
| EndCameraCapture | `public static PxrResult EndCameraCapture(XrCameraIdPICO cameraId)` | End capture | No |
| AcquireCameraImage | `public static PxrResult AcquireCameraImage(XrCameraIdPICO deviceId, Int64 lastCaptureTime, out ulong imageId, out Int64 captureTime)` | Get the newest frame after `lastCaptureTime` (pass 0 to get the earliest available frame) | No |
| ReleaseCameraImage | `public static PxrResult ReleaseCameraImage(XrCameraIdPICO deviceId, ulong imageId)` | Release an acquired image | No |
| GetCameraImageData | `public static PxrResult GetCameraImageData(XrCameraIdPICO deviceId, ulong imageId, out XrCameraImageDataRawBuffer rawBufferData)` | Get the raw buffer data | No |

Enums: `XrCameraIdPICO{XR_CAMERA_ID_RGB_LEFT_PICO=1, XR_CAMERA_ID_RGB_RIGHT_PICO=2}`, `XrCameraPropertyTypePICO`, `XrCameraFacingPICO{XR_CAMERA_FACING_WORLD_PICO=1}`, `XrCameraPositionPICO{UNSPECIFIED=1, LEFT=2, RIGHT=3}`, `XrCameraTypePICO{XR_CAMERA_TYPE_PASSTHROUGH_COLOR_PICO=1}`, `XrCameraCapabilityTypePICO`, `XrCameraDataTransferTypePICO{XR_CAMERA_DATA_TRANSFER_TYPE_RAW_BUFFER_PICO=1}`, `XrCameraImageFormatPICO{XR_CAMERA_IMAGE_FORMAT_RGBA_8888_PICO=1}`, `XrCameraModelPICO{XR_CAMERA_MODEL_PINHOLE_PICO=1}`, `XrCameraImageFpsPICO{XR_CAMERA_IMAGE_FPS_30_PICO=1, XR_CAMERA_IMAGE_FPS_60_PICO=2}`.

### PXR_Input

**Responsibility**: controller state, pose prediction and connection detection, plus haptic vibration (the current Haptic group and many deprecated legacy vibration groups).

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| GetControllerStatus | `public static ControllerStatus GetControllerStatus(Controller controller)` | Get the controller status enum | No |
| InputDeviceChanged | `public static Action<ActiveInputDevice> InputDeviceChanged;` | Input source switch callback (hand / controller) | No |
| GetDominantHand | `Controller GetDominantHand()` | Get the dominant-hand controller | **Yes** |
| SetDominantHand | `void SetDominantHand(Controller controller)` | Set the dominant-hand controller | **Yes** |
| SetControllerVibration | `void SetControllerVibration(float strength, int time, Controller controller)` | Legacy vibration interface | **Yes** |
| GetControllerDeviceType | `ControllerDevice GetControllerDeviceType()` | Get the device model | No |
| IsControllerConnected | `bool IsControllerConnected(Controller controller)` | Whether the controller is connected | No |
| SetControllerOriginOffset | `void SetControllerOriginOffset(Controller controller, Vector3 offset)` | Set the controller display position offset | No |
| GetControllerPredictRotation | `Quaternion GetControllerPredictRotation(Controller controller, double predictTime)` | Predict controller rotation | No |
| GetControllerPredictPosition | `Vector3 GetControllerPredictPosition(Controller controller, double predictTime)` | Predict controller position | No |
| SetControllerVibrationEvent | `int SetControllerVibrationEvent(UInt32 hand, int frequency, float strength, int time)` | Event-style vibration | **Yes** |
| StopControllerVCMotor | `int StopControllerVCMotor(int sourceId)` | Stop audio-driven vibration | **Yes** |
| StartControllerVCMotor | `int StartControllerVCMotor(string file, VibrateController vibrateController)` | Vibration driven by an audio file | **Yes** |
| SetControllerAmp | `int SetControllerAmp(float mode)` | Set the audio vibration amplitude | **Yes** |
| StartVibrateBySharem | `int StartVibrateBySharem(AudioClip audioClip, VibrateController vibrateController, ChannelFlip channelFlip, ref int sourceId)` | Vibration driven by an AudioClip | **Yes** |
| StartVibrateBySharem | `int StartVibrateBySharem(float[] data, VibrateController vibrateController, int buffersize, int frequency, int channelMask, ChannelFlip channelFlip, ref int sourceId)` | Vibration driven by PCM data (overload) | **Yes** |
| SaveVibrateByCache | `int SaveVibrateByCache(AudioClip audioClip, VibrateController vibrateController, ChannelFlip channelFlip, CacheConfig cacheConfig, ref int sourceId)` | Cache vibration data | **Yes** |
| SaveVibrateByCache | `int SaveVibrateByCache(float[] data, VibrateController vibrateController, int buffersize, int frequency, int channelMask, ChannelFlip channelFlip, CacheConfig cacheConfig, ref int sourceId)` | Cache vibration data (overload) | **Yes** |
| StartVibrateByCache | `int StartVibrateByCache(int sourceId)` | Play cached vibration | **Yes** |
| ClearVibrateByCache | `int ClearVibrateByCache(int sourceId)` | Clear cached vibration | **Yes** |
| StartVibrateByPHF | `int StartVibrateByPHF(TextAsset phfText, ref int sourceId, VibrateController vibrateController, ChannelFlip channelFlip, float amp)` | Vibration driven by a PHF file | **Yes** |
| PauseVibrate | `int PauseVibrate(int sourceId)` | Pause PHF vibration | **Yes** |
| ResumeVibrate | `int ResumeVibrate(int sourceId)` | Resume PHF vibration | **Yes** |
| UpdateVibrateParams | `int UpdateVibrateParams(int sourceId, VibrateController vibrateController, ChannelFlip channelFlip, float amp)` | Update vibration parameters at runtime | **Yes** |
| GetBodyTrackingPose | `int GetBodyTrackingPose(double predictTime, ref BodyTrackerResult bodyTrackerResult)` | Get full-body skeleton pose | **Yes** |
| GetMotionTrackerConnectStateWithID | `public static int GetMotionTrackerConnectStateWithID(ref PxrMotionTracker1ConnectState state)` | Get tracker connection count and IDs | **Yes** |
| GetMotionTrackerBattery | `public static int GetMotionTrackerBattery(int trackerId, ref int battery)` | Get tracker battery level | **Yes** |
| GetMotionTrackerCalibState | `public static int GetMotionTrackerCalibState(ref int calibrated)` | Whether the tracker has finished calibration | **Yes** |
| SetBodyTrackingMode | `public static int SetBodyTrackingMode(BodyTrackingMode mode)` | Set body tracking mode | **Yes** |
| SetBodyTrackingBoneLength | `public static int SetBodyTrackingBoneLength(BodyTrackingBoneLength boneLength)` | Set bone length | **Yes** |
| SendHapticImpulse | `void SendHapticImpulse(VibrateType vibrateType, float amplitude, int duration, int frequency)` | Send a haptic impulse (current) | No |
| SendHapticBuffer | `int SendHapticBuffer(VibrateType vibrateType, AudioClip audioClip, ChannelFlip channelFlip, ref int sourceId, CacheType cacheType)` | AudioClip haptic buffer (current) | No |
| SendHapticBuffer | `int SendHapticBuffer(VibrateType vibrateType, float[] pcmData, int buffersize, int frequency, int channelMask, ChannelFlip channelFlip, ref int sourceId, CacheType cacheType)` | PCM haptic buffer (overload) | No |
| SendHapticBuffer | `int SendHapticBuffer(VibrateType vibrateType, TextAsset phfText, ChannelFlip channelFlip, float amplitudeScale, ref int sourceId)` | PHF haptic buffer (overload) | No |
| StopHapticBuffer | `int StopHapticBuffer(int sourceId, bool clearCache)` | Stop a specific haptic buffer | No |
| PauseHapticBuffer | `int PauseHapticBuffer(int sourceId)` | Pause | No |
| ResumeHapticBuffer | `int ResumeHapticBuffer(int sourceId)` | Resume | No |
| StartHapticBuffer | `int StartHapticBuffer(int sourceId)` | Start playback | No |
| UpdateHapticBuffer | `int UpdateHapticBuffer(int sourceId, VibrateType vibrateType, ChannelFlip channelFlip, float amplitudeScale)` | Update parameters | No |
| CreateHapticStream | `int CreateHapticStream(string phfVersion, UInt32 frameDurationMs, ref VibrateInfo hapticInfo, float speed, ref int id)` | Create a haptic stream | **Yes** |
| WriteHapticStream | `int WriteHapticStream(int id, ref PxrPhfParamsNum frames, UInt32 numFrames)` | Write to a haptic stream | **Yes** |
| SetHapticStreamSpeed | `int SetHapticStreamSpeed(int id, float speed)` | Set stream speed | **Yes** |
| GetHapticStreamSpeed | `int GetHapticStreamSpeed(int id, ref float speed)` | Get stream speed | **Yes** |
| GetHapticStreamCurrentFrameSequence | `int GetHapticStreamCurrentFrameSequence(int id, ref UInt64 frameSequence)` | Get the current playback frame sequence number | **Yes** |
| StartHapticStream | `int StartHapticStream(int source_id)` | Start transmission | **Yes** |
| StopHapticStream | `int StopHapticStream(int source_id)` | Stop transmission | **Yes** |
| RemoveHapticStream | `int RemoveHapticStream(int source_id)` | Remove the stream | **Yes** |
| AnalysisHapticStreamPHF | `PxrPhfFile AnalysisHapticStreamPHF(TextAsset phfText)` | Parse a PHF file | **Yes** |
| ResetController | Listed in the docs method table, but the page has **no details section and no signature** | Reset the controller on PICO G3 | **Yes** |
| SetArmModelParameters | Listed in the docs method table, but the page has **no details section and no signature** | Set arm model parameters on PICO G3 | **Yes** |
| GetControllerHandness | Listed in the docs method table, but the page has **no details section and no signature** | Get the system dominant hand on PICO G3 | No |

Enums: `ControllerDevice{G2=3, Neo2, Neo3, PICO_4, G3, PICO_4U, NewController=10}`, `Controller{LeftController, RightController}`, `VibrateController{No=0, Left=1, Right=2, LeftAndRight=3}`, `VibrateType{None=0, LeftController=1, RightController=2, BothController=3, LeftPICO4U=4, RightPICO4U=8, BothPICO4U=12}`, `CacheType{DontCache=0, CacheAndVibrate=1, CacheNoVibrate=2}`, `ChannelFlip{No, Yes}`, `CacheConfig{CacheAndVibrate=1, CacheNoVibrate=2}`, `ControllerStatus{Static=0, SixDof, ThreeDof, Sleep, CollidedIn3Dof, CollidedIn6Dof}`.

### PXR_MixedReality

**Responsibility**: Sense Data Provider lifecycle, spatial anchors (local and shared), scene anchor data, and VST effects.

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| StartSenseDataProvider | `async Task<PxrResult> StartSenseDataProvider(PxrSenseDataProviderType type, CancellationToken token)` | Start the SpatialAnchor or SceneCapture provider | No |
| GetSenseDataProviderState | `PxrResult GetSenseDataProviderState(PxrSenseDataProviderType type, out PxrSenseDataProviderState state)` | Get the provider state | No |
| StopSenseDataProvider | `PxrResult StopSenseDataProvider(PxrSenseDataProviderType type)` | Stop the provider | No |
| CreateSpatialAnchorAsync | `async Task<(PxrResult result, ulong anchorHandle, Guid uuid)> CreateSpatialAnchorAsync(Vector3 position, Quaternion rotation, CancellationToken token)` | Create a spatial anchor in memory | No |
| PersistSpatialAnchorAsync | `async Task<PxrResult> PersistSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)` | Persist to local disk | No |
| UnPersistSpatialAnchorAsync | `async Task<PxrResult> UnPersistSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)` | Remove persistence | No |
| DestroyAnchor | `PxrResult DestroyAnchor(ulong anchorHandle)` | Destroy the in-memory anchor | No |
| GetAnchorUuid | `PxrResult GetAnchorUuid(ulong anchorHandle, out Guid uuid)` | Get the anchor UUID | No |
| LocateAnchor | `PxrResult LocateAnchor(ulong anchorHandle, out Vector3 position, out Quaternion rotation)` | Get the real-time anchor pose | No |
| QuerySpatialAnchorAsync | `async Task<(PxrResult result, List<ulong> anchorHandleList)> QuerySpatialAnchorAsync(Guid[] uuids, CancellationToken token)` | Load spatial anchors (one at a time) | No |
| QuerySpatialAnchorObjectsAsync | `public static async Task<(PxrResult result, List<GameObject> spatialAnchorObjects)> QuerySpatialAnchorObjectsAsync(Guid[] uuids = null, CancellationToken token = default)` | Query anchor objects that have the PXR_Spatial Anchor (Script) component. **The docs method table writes it as `QuerySpatialAnchorObjectsAsyn` (missing the final c). Use the signature in the code block** | No |
| StartSceneCaptureAsync | `async Task<PxrResult> StartSceneCaptureAsync(CancellationToken token)` | Launch the Room Capture app | No |
| QuerySceneAnchorAsync | `async Task<(PxrResult result, List<ulong> anchorHandleList)> QuerySceneAnchorAsync(PxrSemanticLabel[] labels, CancellationToken token)` | Load scene anchors by semantic label | No |
| QuerySceneAnchorAsync | `async Task<(PxrResult result, Dictionary<ulong, Guid> anchorDictionary)> QuerySceneAnchorAsync(CancellationToken token)` | Load all scene anchors (overload) | No |
| GetSceneAnchorComponentTypes | `PxrResult GetSceneAnchorComponentTypes(ulong anchorHandle, out PxrSceneComponentType[] types)` | Get scene anchor component types | No |
| GetSceneSemanticLabel | `PxrResult GetSceneSemanticLabel(ulong anchorHandle, out PxrSemanticLabel label)` | Get the semantic label | No |
| GetSceneBox3DData | `PxrResult GetSceneBox3DData(ulong anchorHandle, out Vector3 position, out Quaternion rotation, out Vector3 extent)` | Get 3D box information | No |
| GetSceneBox2DData | `PxrResult GetSceneBox2DData(ulong anchorHandle, out Vector2 offset, out Vector2 extent)` | Get 2D box information | No |
| GetScenePolygonData | `PxrResult GetScenePolygonData(ulong anchorHandle, out Vector2[] vertices)` | Get polygon information | No |
| UploadSpatialAnchorAsync | `async Task<(PxrResult result, Guid uuid)> UploadSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)` | Upload as a shared spatial anchor | No |
| DownloadSharedSpatialAnchorAsync | `async Task<PxrResult> DownloadSharedSpatialAnchorAsync(Guid uuid, CancellationToken token)` | Download a shared spatial anchor | No |
| CreateAnchorEntity | `PxrResult CreateAnchorEntity(Vector3 position, Quaternion rotation, out ulong taskId)` | Legacy: create an anchor entity | **Yes** |
| DestroyAnchorEntity | `PxrResult DestroyAnchorEntity(ulong handle)` | Legacy: destroy an anchor entity | **Yes** |
| GetAnchorPose | `PxrResult GetAnchorPose(ulong handle, out Quaternion orientation, out Vector3 position)` | Legacy: get anchor pose | **Yes** |
| GetAnchorEntityUuid | `PxrResult GetAnchorEntityUuid(ulong handle, out Guid uuid)` | Legacy: get UUID | **Yes** |
| PersistAnchorEntity | `PxrResult PersistAnchorEntity(ulong[] anchorHandles, PxrPersistLocation location, out ulong taskId)` | Legacy: persist | **Yes** |
| UnPersistAnchorEntity | `PxrResult UnPersistAnchorEntity(ulong[] anchorHandles, PxrPersistLocation location, out ulong taskId)` | Legacy: remove persistence | **Yes** |
| ClearPersistedAnchorEntity | `PxrResult ClearPersistedAnchorEntity(PxrPersistLocation location, out ulong taskId)` | Legacy: clear persisted data | **Yes** |
| GetAnchorComponentFlags | `PxrResult GetAnchorComponentFlags(ulong anchorHandle, out PxrAnchorComponentTypeFlags[] flags)` | Legacy: get component flags | **Yes** |
| LoadAnchorEntityByUuidFilter | `PxrResult LoadAnchorEntityByUuidFilter(out ulong taskId, Guid[] uuids)` | Legacy: load by UUID | **Yes** |
| LoadAnchorEntityBySceneFilter | `PxrResult LoadAnchorEntityBySceneFilter(PxrSpatialSceneDataTypeFlags[] flags, out ulong taskId)` | Legacy: load by scene data type | **Yes** |
| GetAnchorEntityLoadResults | `PxrResult GetAnchorEntityLoadResults(ulong taskId, uint count, out Dictionary<ulong, Guid> loadedAnchors)` | Legacy: get load results | **Yes** |
| StartSpatialSceneCapture | `PxrResult StartSpatialSceneCapture(out ulong taskId)` | Legacy: launch room calibration | **Yes** |
| GetAnchorVolumeInfo | `PxrResult GetAnchorVolumeInfo(ulong anchorHandle, out Vector3 center, out Vector3 extent)` | Legacy: get volume information | **Yes** |
| GetAnchorPlanePolygonInfo | `PxrResult GetAnchorPlanePolygonInfo(ulong anchorHandle, out Vector3[] vertices)` | Legacy: get polygon information | **Yes** |
| GetAnchorPlaneBoundaryInfo | `PxrResult GetAnchorPlaneBoundaryInfo(ulong anchorHandle, out Vector3 center, out Vector2 extent)` | Legacy: get rectangular boundary | **Yes** |
| GetAnchorSceneLabel | `PxrResult GetAnchorSceneLabel(ulong anchorHandle, out PxrSceneLabel label)` | Legacy: get scene label | **Yes** |
| EnableVideoSeeThrough | `int EnableVideoSeeThrough(bool state)` | Turn video see-through on or off | **Yes** |
| EnableVideoSeeThroughEffect | `int EnableVideoSeeThroughEffect(bool value)` | Turn the VST effect on or off | No |
| SetVideoSeeThroughEffect | `int SetVideoSeeThroughEffect(PxrLayerEffect type, float value, float duration)` | Set VST effect parameters | No |
| SetVideoSeeThroughLut | `int SetVideoSeeThroughLut(Texture2D texture, int row, int col)` | Set the VST LUT texture | No |

Enums: `PxrSenseDataProviderType{SpatialAnchor, SceneCapture}` (taken from the parameter description), `PxrSenseDataProviderState{Initialized, Running, Stopped}`, `PxrSceneComponentType{Location=0, Semantic, Box2D, Polygon, Box3D, TriangleMesh=5}`, `PxrSemanticLabel{Unknown=0, Floor, Ceiling, Wall, Door, Window, Opening, Table, Sofa, Chair, Human=10, VirtualWall=18}`, `PxrResult` (see key rule 5).

### PXR_MotionTracking

**Responsibility**: eye, face, full-body motion capture, PICO Motion Tracker and external devices. Return values are uniformly `int` (`TrackingStateCode`).

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| WantEyeTrackingService | `int WantEyeTrackingService()` | Request the eye tracking service | **Yes** |
| GetEyeTrackingSupported | `int GetEyeTrackingSupported(ref bool supported, ref int supportedModesCount, ref EyeTrackingMode[] supportedModes)` | Whether the device supports eye tracking | No |
| StartEyeTracking | `int StartEyeTracking(ref EyeTrackingStartInfo startInfo)` | Start eye tracking | No |
| StopEyeTracking | `int StopEyeTracking(ref EyeTrackingStopInfo stopInfo)` | Stop eye tracking | No |
| GetEyeTrackingState | `int GetEyeTrackingState(ref bool isTracking, ref EyeTrackingState state)` | Get eye tracking state | No |
| GetEyeTrackingData | `int GetEyeTrackingData(ref EyeTrackingDataGetInfo getInfo, ref EyeTrackingData data)` | Get eye tracking data | No |
| GetEyeOpenness | `int GetEyeOpenness(ref float leftEyeOpenness, ref float rightEyeOpenness)` | Get openness of both eyes | No |
| GetEyePupilInfo | `int GetEyePupilInfo(ref EyePupilInfo eyePupilPosition)` | Get pupil information | No |
| GetPerEyePose | `int GetPerEyePose(ref long timestamp, ref Posef leftEyePose, ref Posef rightPose)` | Get the pose of each eye | No |
| GetEyeBlink | `int GetEyeBlink(ref long timestamp, ref bool isLeftBlink, ref bool isRightBlink)` | Get blink state | No |
| WantFaceTrackingService | `int WantFaceTrackingService()` | Request the face tracking service | **Yes** |
| GetFaceTrackingSupported | `unsafe int GetFaceTrackingSupported(ref bool supported, ref int supportedModesCount, ref FaceTrackingMode[] supportedModes)` | Whether face tracking is supported | **Yes** |
| StartFaceTracking | `int StartFaceTracking(ref FaceTrackingStartInfo startInfo)` | Start face tracking | **Yes** |
| StopFaceTracking | `int StopFaceTracking(ref FaceTrackingStopInfo stopInfo)` | Stop face tracking | **Yes** |
| GetFaceTrackingState | `int GetFaceTrackingState(ref bool isTracking, ref FaceTrackingState state)` | Get face tracking state | **Yes** |
| GetFaceTrackingData | `int GetFaceTrackingData(ref FaceTrackingDataGetInfo getInfo, ref FaceTrackingData data)` | Get face tracking data | **Yes** |
| BodyTrackingAbnormalCalibrationData | `public static Action<int, int> BodyTrackingAbnormalCalibrationData;` | Calibration anomaly callback | **Yes** |
| BodyTrackingStateError | `public static Action<BodyTrackingStatusCode, BodyTrackingErrorCode> BodyTrackingStateError;` | Body tracking status and error code callback | **Yes** |
| BodyTrackingAction | `public static Action<int, BodyActionList> BodyTrackingAction;` | Bone node action change callback | **Yes** |
| StartMotionTrackerCalibApp | `int StartMotionTrackerCalibApp()` | Launch the PICO Motion Tracker calibration app | No |
| GetBodyTrackingSupported | `int GetBodyTrackingSupported(ref bool supported)` | Whether body tracking is supported | No |
| StartBodyTracking | `int StartBodyTracking(BodyTrackingMode mode, BodyTrackingBoneLength boneLength)` | Start body tracking | No |
| StopBodyTracking | `int StopBodyTracking()` | Stop body tracking | No |
| GetBodyTrackingState | `int GetBodyTrackingState(ref bool isTracking, ref BodyTrackingState state)` | Get body tracking state and abnormal reason | No |
| GetBodyTrackingData | `int GetBodyTrackingData(ref BodyTrackingGetDataInfo getInfo, ref BodyTrackingData data)` | Get body tracking data | No |
| MotionTrackerNumberOfConnections | `public static Action<int, int> MotionTrackerNumberOfConnections;` | Connection count change callback | **Yes** |
| MotionTrackerBatteryLevel | `public static Action<int, int> MotionTrackerBatteryLevel;` | Battery level change callback | **Yes** |
| MotionTrackerKeyAction | `public static Action<MotionTrackerEventData> MotionTrackerKeyAction;` | Key callback | **Yes** |
| MotionTrackingModeChangedAction | `public static Action<MotionTrackerMode> MotionTrackingModeChangedAction;` | Tracking mode change callback | **Yes** |
| GetMotionTrackerConnectStateWithSN | `int GetMotionTrackerConnectStateWithSN(ref MotionTrackerConnectState connectState)` | Get connection count and SNs | **Yes** |
| GetMotionTrackerDeviceType | `MotionTrackerType GetMotionTrackerDeviceType()` | Get the tracker version | **Yes** |
| CheckMotionTrackerModeAndNumber | `int CheckMotionTrackerModeAndNumber(MotionTrackerMode mode, MotionTrackerNum number)` | Validate mode and count | **Yes** |
| GetMotionTrackerMode | `MotionTrackerMode GetMotionTrackerMode()` | Get the current tracking mode | **Yes** |
| GetMotionTrackerLocations | `int GetMotionTrackerLocations(TrackerSN trackerSN, ref MotionTrackerLocations locations, ref MotionTrackerConfidence confidence, double predictTime)` | Get tracker pose (old) | **Yes** |
| ExtDevConnectAction | `public static Action<ExtDevConnectEventData> ExtDevConnectAction;` | External device connection callback | **Yes** |
| ExtDevBatteryAction | `public static Action<ExtDevBatteryEventData> ExtDevBatteryAction;` | External device battery callback | **Yes** |
| ExtDevPassDataAction | `public static Action<int> ExtDevPassDataAction;` | Pass-through data event. Listen to it before calling `GetExtDevTrackerByPassData` | No |
| GetExtDevTrackerConnectState | `int GetExtDevTrackerConnectState(ref ExtDevTrackerConnectState connectState)` | Get external device connection state | **Yes** |
| SetExtDevTrackerMotorVibrate | `int SetExtDevTrackerMotorVibrate(ref ExtDevTrackerMotorVibrate motorVibrate)` | Vibrate an external device | **Yes** |
| SetExtDevTrackerPassDataState | `int SetExtDevTrackerPassDataState(bool state)` | Set the pass-through interface state | No |
| SetExtDevTrackerByPassData | `int SetExtDevTrackerByPassData(ref ExtDevTrackerPassData passData)` | Send pass-through data to an external device | **Yes** |
| GetExtDevTrackerByPassData | `int GetExtDevTrackerByPassData(ref ExtDevTrackerPassDataArray passData, ref int realLength)` | Get pass-through data | **Yes** |
| GetExtDevTrackerBattery | `int GetExtDevTrackerBattery(ref TrackerSN trackerSN, ref int battery, ref int charger)` | Get external device battery | **Yes** |
| GetExtDevTrackerKeyData | `int GetExtDevTrackerKeyData(ref TrackerSN trackerSN, ref ExtDevTrackerKeyData keyData)` | Get external device key values | **Yes** |
| RequestMotionTrackerCompleteAction | Callback. The docs **do not give the generic parameter signature** | Whether tracker connection succeeded | No |
| MotionTrackerConnectionAction | Callback. The docs **do not give the generic parameter signature** | Tracker connection state change | No |
| MotionTrackerPowerKeyAction | Callback. The docs **do not give the generic parameter signature** | Tracker power key event | No |
| CheckMotionTrackerNumber | The docs give only the implementation `UPxr_CheckMotionTrackerNumber((int)number)`. Parameter `number` (expected count [0,3]) | Validate the number of connected trackers | No |
| GetMotionTrackerLocation | The docs give only the implementation `UPxr_GetMotionTrackerLocation(trackerid, ref location, ref isValidPose)` | Get the pose of one tracker (current) | No |
| GetMotionTrackerBattery | The docs give only the implementation `UPxr_GetMotionTrackerBatteryState(trackerid, ref battery, ref charger)` | Get tracker battery level (current) | No |
| ExpandDeviceConnectionAction | Callback. The docs **do not give the generic parameter signature**. Returns the tracker SN and connection state (0 disconnected / 1 connected) | External device connection state change | No |
| ExpandDeviceBatteryAction | Callback. The docs **do not give the generic parameter signature** | External device battery and charging state change | No |
| SetExpandDeviceVibrate | The docs give only the implementation `UPxr_SetExpandDeviceVibrate(deviceid, motorVibrate)` | Vibrate an external device (current) | No |
| GetExpandDevice | The docs give only the implementation `UPxr_GetExpandDevice(out deviceArray)` | Get the SN array of external devices | No |
| SetExpandDeviceCustomData | The docs give only the implementation `UPxr_SetExpandDeviceCustomData(ref dataArray)` | Send custom data to an external device | No |
| GetExpandDeviceCustomData | The docs give only the implementation `UPxr_GetExpandDeviceCustomData(out dataArray)` | Get custom data from an external device | No |
| GetExpandDeviceBattery | The docs give only the implementation `UPxr_GetExpandDeviceBattery(deviceid, ref battery, ref charger)` | Get external device battery level | No |

Enums: `TrackingStateCode{PXR_MT_SUCCESS=0, PXR_MT_FAILURE=-1, PXR_MT_MODE_NONE=-2, PXR_MT_DEVICE_NOT_SUPPORT=-3, PXR_MT_SERVICE_NEED_START=-4, PXR_MT_ET_PERMISSION_DENIED=-5, PXR_MT_FT_PERMISSION_DENIED=-6, PXR_MT_MIC_PERMISSION_DENIED=-7, PXR_MT_SYSTEM_DENIED=-8, PXR_MT_UNKNOW_ERROR=-9}`, `EyeTrackingMode{PXR_ETM_NONE=-1, PXR_ETM_BOTH=0, PXR_ETM_COUNT=1}`, `PerEyeUsage{LeftEye=0, RightEye=1, Combined=2, EyeCount=3}`, `EyeTrackingDataGetFlags{PXR_EYE_DEFAULT=0, PXR_EYE_POSITION=1<<0, PXR_EYE_ORIENTATION=1<<1}`, `FaceTrackingMode{PXR_FTM_NONE=-1, PXR_FTM_FACE=0, PXR_FTM_LIPS=1, PXR_FTM_FACE_LIPS_VIS=2, PXR_FTM_FACE_LIPS_BS=3}`, `BodyTrackingMode{BTM_FULL_BODY_LOW=0, BTM_FULL_BODY_HIGH=1}`, `BodyTrackingGetDataFlags{PXR_BODY_NONE=0, PXR_BODY_POSE=1<<0, PXR_BODY_ACTION=1<<1, PXR_BODY_VELO_ACC=1<<2}`, `MotionTrackerType{MT_1=1 (Beta), MT_2 (Official)}`, `MotionTrackerMode{BodyTracking, MotionTracking}`, `MotionTrackerNum{NONE=0, ONE, TWO, THREE}`, `MotionTrackerConfidence{PXR_STATIC_ACCURATE=0, PXR_6DOF_ACCURATE, PXR_3DOF_NOT_ACCURATE, PXR_6DOF_NOT_ACCURATE}`, `BlendShapeIndex` (face blendshape index table, see the source page).

### PXR_EyeTracking (legacy class)

**Responsibility**: reading legacy eye tracking data. The first sentence of the docs declares it legacy, so new code should use `PXR_MotionTracking`. All methods return `bool` (true = success).

| Method | Signature | Notes | Deprecated |
|---|---|---|---|
| GetHeadPosMatrix | `bool GetHeadPosMatrix(out Matrix4x4 matrix)` | Get the head PosMatrix | Class-level legacy |
| GetCombineEyeGazePoint | `bool GetCombineEyeGazePoint(out Vector3 point)` | Get the center point of both eyes (Unity camera coordinates. The return value is already divided by 1000) | Class-level legacy |
| GetCombineEyeGazeVector | `bool GetCombineEyeGazeVector(out Vector3 vector)` | Get the combined gaze direction of both eyes | Class-level legacy |
| GetLeftEyeGazeOpenness | `bool GetLeftEyeGazeOpenness(out float openness)` | Left eye openness [0.0, 1.0] | Class-level legacy |
| GetRightEyeGazeOpenness | `bool GetRightEyeGazeOpenness(out float openness)` | Right eye openness [0.0, 1.0] | Class-level legacy |
| GetLeftEyePoseStatus | `bool GetLeftEyePoseStatus(out uint status)` | Whether left eye data is available (bit flags `EyePoseStatus`) | Class-level legacy |
| GetRightEyePoseStatus | `bool GetRightEyePoseStatus(out uint status)` | Whether right eye data is available | Class-level legacy |
| GetCombinedEyePoseStatus | `bool GetCombinedEyePoseStatus(out uint status)` | Whether combined eye data is available (0 no / 1 yes) | Class-level legacy |
| GetLeftEyePositionGuide | `bool GetLeftEyePositionGuide(out Vector3 position)` | Left eye inner corner image coordinates (top-left (0,0), bottom-right (1,1)) | Class-level legacy |
| GetRightEyePositionGuide | `bool GetRightEyePositionGuide(out Vector3 position)` | Right eye inner corner image coordinates | Class-level legacy |
| GetFoveatedGazeDirection | `bool GetFoveatedGazeDirection(out Vector3 direction)` | Gaze direction in the View coordinate system (OpenXR right-handed) | Class-level legacy |
| GetFoveatedGazeTrackingState | `bool GetFoveatedGazeTrackingState(out uint state)` | Whether foveated gaze data is available (0 no / 1 yes) | Class-level legacy |

`EyePoseStatus` bit flags (from the parameter description): `GazePointValid=1<<0, GazeVectorValid=1<<1, EyeOpennessValid=1<<2, EyePupilDilationValid=1<<3, EyePositionGuideValid=1<<4, EyePupilPositionValid=1<<5, EyeConvergenceDistanceValid=1<<6, EyeGazePointValid=1<<7, EyeGazeVectorValid=1<<8, PupilDistanceValid=1<<9, ConvergenceDistanceValid=1<<10, PupilDiameterValid=1<<11`.

### PXR_Enterprise (enterprise devices only. Not available on consumer devices)

**Responsibility**: the system management surface for enterprise devices (power, keys, network, screen casting, large space, camera, dialogs and more), **257 methods** in total. The docs state that only PICO Neo2 / Neo2 Eye / Neo3 Pro / Neo3 Pro Eye / G2 4K, 4K E, 4K Plus (system 4.0.3+) and PICO 4 Enterprise support it. **Do not call it on consumer devices.** This file does **not expand the signatures** of this class. For signatures, see the [PXR_Enterprise source page](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Enterprise/). **No method in the docs method table carries a `(Deprecated)` marker.**

- **Service lifecycle (3)**: `InitEnterpriseService`, `BindEnterpriseService`, `UnBindEnterpriseService`
- **Device info and status (11)**: `StateGetDeviceInfo`, `GetCpuUsages`, `GetDeviceTemperatures`, `GetHeadTrackingConfidence`, `GetHeadTrackingStatus`, `GetRunningAppProcesses`, `GetFocusedApp`, `GetCurrentLauncher`, `GetDeviceOwner`, `SetDeviceOwner`, `IsInitSettingComplete`
- **Power, scheduled on/off, sleep (24)**: `ControlSetDeviceAction`, `ScreenOn`, `ScreenOff`, `AcquireWakeLock`, `ReleaseWakeLock`, `PropertySetScreenOffDelay`, `PropertySetSleepDelay`, `GetScreenOffDelay`, `GetSleepDelay`, `SetSystemAutoSleepTime`, `TimingStartup`, `TimingShutdown`, `OpenTimingStartup`, `CloseTimingStartup`, `OpenTimingShutdown`, `CloseTimingShutdown`, `GetTimingStartupStatus`, `GetTimingShutdownStatus`, `GetTimingStartupStatusTwo`, `GetTimingShutDownStatusTwo`, `SetPowerManageMode`, `GetPowerManageMode`, `SetPowerOffWithUSBCable`, `GetPowerOffWithUSBCable`
- **Key control (23)**: `PropertySetHomeKey`, `PropertySetHomeKeyAll`, `RemoveControllerHomeKey`, `GetHomeKeyStatus`, `PropertyDisablePowerKey`, `GetPowerKeyStatus`, `EnableEnterKey`, `DisableEnterKey`, `GetEnterKeyStatus`, `EnableVolumeKey`, `DisableVolumeKey`, `GetVolumeKeyStatus`, `EnableBackKey`, `DisableBackKey`, `GetBackKeyStatus`, `ResetAllKeyToDefault`, `SetSystemKeyUsability`, `SwitchVolumeToHomeAndEnter`, `IsVolumeChangeToHomeAndEnter`, `SetHMDVolumeKeyFunc`, `GetHMDVolumeKeyFunc`, `GetControllerKeyState`, `SetControllerKeyState`
- **Controller pairing, vibration, battery (12)**: `SetControllerPairTime`, `GetControllerPairTime`, `StartLeftControllerPair`, `MakeLeftControllerUnPair`, `StartRightControllerPair`, `MakeRightControllerUnPair`, `StopControllerPair`, `SetControllerPreferHand`, `SetControllerVibrateAmplitude`, `GetControllerVibrateAmplitude`, `GetControllerBattery`, `GetControllerConnectState`
- **Apps and system updates (22)**: `ControlAPPManager`, `SetAPPAsHome`, `SetLauncher`, `KillAppsByPidOrPackageName`, `KillBackgroundAppsWithWhiteList`, `AppKeepAlive`, `KeepAliveBackground`, `StartActivity`, `StartApp`, `StartService`, `StartForegroundService`, `SendBroadcast`, `SendOrderedBroadcast`, `InjectEvent`, `CustomizeAppLibrary`, `GetAppLibraryHideList`, `AppCopyrightVerify`, `InstallOTAPackage`, `OnlineSystemUpdate`, `OfflineSystemUpdate`, `SetBrowserHomePage`, `GetBrowserHomePage`
- **Network, Wi-Fi, USB (16)**: `ControlSetAutoConnectWIFI`, `ControlClearAutoConnectWIFI`, `ControlSetAutoConnectWIFIWithErrorCodeCallback`, `GetAutoConnectWiFiConfig`, `ConfigWifi`, `GetConfiguredWifi`, `ConnectWifi`, `SetStaticIpConfigurationtoConnectWifi`, `AllowWifiAutoJoin`, `SetWifiP2PDeviceName`, `GetWifiP2PDeviceName`, `SetUsbTetheringStaticIP`, `GetUsbTetheringStaticIPLocal`, `GetUsbTetheringStaticIPClient`, `SwitchSetUsbConfigurationOption`, `GetUsbConfigurationOption`
- **Screen casting: Miracast / PICOCast (28)**: `OpenMiracast`, `IsMiracastOn`, `CloseMiracast`, `StartScan`, `StopScan`, `ConnectWifiDisplay`, `DisConnectWifiDisplay`, `ForgetWifiDisplay`, `RenameWifiDisplay`, `SetWDModelsCallback`, `SetWDJsonCallback`, `UpdateWifiDisplays`, `GetConnectedWD`, `GetAutoMiracastConfig`, `PICOCastInit`, `PICOCastSetShowAuthorization`, `PICOCastGetShowAuthorization`, `PICOCastGetUrl`, `PICOCastStopCast`, `PICOCastSetOption`, `PICOCastGetOptionOrStatus`, `SetPicoCastMediaFormat`, `SetScreenCastAudioOutput`, `GetScreenCastAudioOutput`, `SetAcceptCastMode`, `GetAcceptCastMode`, `SetScreenCastMode`, `GetScreenCastMode`
- **Screenshot and recording (8)**: `Capture`, `Record`, `SetScreenRecordShotRatio`, `GetScreenRecordShotRatio`, `SetScreenResolution`, `GetScreenResolution`, `SetScreenRecordFrameRate`, `GetScreenRecordFrameRate`
- **Large space and maps (15)**: `SwitchLargeSpaceScene`, `GetSwitchLargeSpaceStatus`, `SaveLargeSpaceMaps`, `ExportMaps`, `ImportMaps`, `ImportMapByPath`, `IsMapInEffect`, `SetLargeSpaceMapScale`, `GetLargeSpaceBoundsInfo`, `GetLargeSpaceBoundsInfoWithType`, `OpenLargeSpaceQuickMode`, `CloseLargeSpaceQuickMode`, `SetOriginOfLargeSpaceQuickMode`, `SetBoundaryOfLargeSpaceQuickMode`, `GetLargeSpaceQuickModeInfo`
- **Boundary, floor and room calibration (18)**: `StartSetSecureBorder`, `SetDistanceSensitivity`, `GetDistanceSensitivity`, `SetSpeedSensitivity`, `GetSpeedSensitivity`, `SetMRCollisionAlertSensitivity`, `GetMRCollisionAlertSensitivity`, `SetFenceColor`, `GetFenceColor`, `SetFloorHeight`, `GetFloorHeight`, `GotoSeeThroughFloorSetting`, `GotoEnvironmentTextureCheck`, `StartRoomMark`, `ClearRoomMark`, `FreezeScreen`, `Recenter`, `ResetTracking`
- **Camera, VST, 4U camera group (16)**: `OpenVSTCamera`, `CloseVSTCamera`, `GetCameraParameters`, `AcquireVSTCameraFrame`, `AcquireVSTCameraFrameAntiDistortion`, `SetMarkerInfoCallback`, `ScanQRCode`, `OpenCameraAsyncfor4U`, `Configurefor4U`, `StartPreviewfor4U`, `SetCameraFrameBufferfor4U`, `StartGetImageDatafor4U`, `CloseCamerafor4U`, `GetCameraIntrinsicsfor4U`, `GetCameraExtrinsicsfor4U`, `GetCameraParametersNewfor4U`
- **Pose and IMU (12)**: `GetPredictedDisplayTime`, `GetPredictedMainSensorState`, `GetPredictedMainSensorState2`, `GetHeadPose`, `GetControllerPose`, `GetSwiftPose`, `GetSwiftTrackerDevices`, `GetHeadIMUData`, `GetControllerIMUData`, `GetSwiftIMUData`, `UseGlobalPose`, `ConvertPoseCoordinate`
- **Motion trackers (6)**: `StartSwiftTrackerPairing`, `UnBondSwiftTracker`, `SetMotionTrackerPredictionCoefficient`, `GetMotionTrackerPredictionCoefficient`, `StartMotionTrackerApp`, `SetMotionTrackerAutoStart`
- **Eye tracking, IPD, display (15)**: `ClearEyeTrackData`, `SetEyeTrackRate`, `GetEyeTrackRate`, `SetTrackFrequency`, `GetTrackFrequency`, `SetIPD`, `OpenIPDDetectionPage`, `SetSingleEyeSource`, `GetSingleEyeSource`, `SetViewVisual`, `GetViewVisual`, `SetScreenBrightness`, `SetPowerOnOffLogo`, `SetVirtualEnvironment`, `GetVirtualEnvironment`
- **Virtual display (3)**: `CreateVirtualDisplay`, `ReleaseVirtualDisplay`, `SetVirtualDisplaySurface`
- **System settings, localization, files (15)**: `SetSystemLanguage`, `GetSystemLanguage`, `SetSystemCountryCode`, `GetSystemCountryCode`, `SetTimeZone`, `SetSystemDate`, `SetSystemTime`, `SetSkipInitSettingPage`, `GetSkipInitSettingPage`, `StartVrSettingsItem`, `UPxr_CustomizeSettingsTabStatus`, `UPxr_GetCustomizeSettingsTabStatus`, `SwitchSystemFunction`, `GetSwitchSystemFunctionStatus`, `FileCopy`
- **Global dialogs (10)**: `ShowGlobalMessageDialog`, `HideGlobalMessageDialog`, `ShowGlobalTipsDialog`, `HideGlobalTipsDialog`, `ShowGlobalBigStatusDialog`, `HideGlobalBigStatusDialog`, `ShowGlobalSmallStatusDialog`, `HideGlobalSmallStatusDialog`, `ShowGlobalDialogByType`, `HideGlobalDialogByType`

> Note: `UPxr_CustomizeSettingsTabStatus` and `UPxr_GetCustomizeSettingsTabStatus` carry the `UPxr_` prefix in the docs method table. Do not drop the prefix.

## DO NOT

| Wrong | Right / notes |
|---|---|
| `if (PXR_MotionTracking.GetEyeTrackingData(ref a, ref b)) { }` | Returns `int`. Write `if (PXR_MotionTracking.GetEyeTrackingData(ref a, ref b) == 0)` (`PXR_MT_SUCCESS`). |
| `if (PXR_Input.GetControllerStatus(Controller.LeftController)) { }` | Returns the `ControllerStatus` enum. To check connection, use `PXR_Input.IsControllerConnected(...)`. |
| `PXR_FoveationRendering.SetFoveationLevel(FoveationLevel.High)` | Two arguments are required: `SetFoveationLevel(FoveationLevel.High, isETFR)`. |
| `PXR_Input.SetControllerVibration(0.5f, 100, Controller.LeftController)` | Deprecated. Use `PXR_Input.SendHapticImpulse(VibrateType.LeftController, amplitude, duration, frequency)`. |
| `PXR_MixedReality.CreateAnchorEntity(pos, rot, out taskId)` | The whole AnchorEntity API is Deprecated. Use `StartSenseDataProvider` + `CreateSpatialAnchorAsync`. |
| `PXR_System.EnableFaceTracking(true)` / `PXR_MotionTracking.StartFaceTracking(...)` | Face-tracking APIs on both classes are Deprecated in v3.4.0. The corpus gives no replacement class. Do not invent a replacement API. Check the INTERACTION area doc to confirm. |
| Using `PXR_EyeTracking.GetCombineEyeGazeVector(out v)` as a current interface | The whole class is legacy. The current path is `PXR_MotionTracking.GetEyeTrackingData` / `GetPerEyePose`. |
| `PXR_Boundary.GetDimensions(BoundaryType.OuterBoundary)` | This method accepts only `PlayArea`. |
| `PXR_System.SetSystemDisplayFrequency(60f)` | Only 72 / 90 / 120 are supported. Other values are invalid. |
| Calling `PXR_Enterprise.StateGetDeviceInfo(...)` directly | Call `InitEnterpriseService`, then wait for the `BindEnterpriseService` callback to succeed. Enterprise devices only. |
| Calling `PXR_CameraImage.AcquireCameraImage(...)` without a matching `ReleaseCameraImage` | Every Acquire must be paired with a Release, or the image resource is not freed. |
| `await PXR_CameraImage.GetCameraIntrinsics(...)` | Only `CreateCameraDeviceAsync` and `CreateCameraCaptureSessionAsync` are async. The rest are synchronous `PxrResult`. |
| Calling `QuerySpatialAnchorAsync` twice concurrently | Only one call at a time. Wait for the previous call to finish. |
| `PXR_HandTracking.GetHandScale()` with no arguments, or attached to a hand component | It is static and takes arguments: `PXR_HandTracking.GetHandScale(HandType hand, ref float scale)`. |
| Calling the current Haptic API with `VibrateController` | The current `SendHapticImpulse` / `SendHapticBuffer` use `VibrateType`. The two enums differ in both names and values. |
| Writing code that treats `SetGuardianSystemDisable(true)` as "turn the boundary off" | The method name conflicts with the docs' parameter description (`true: enable`). Confirm by testing before fixing the behavior in code. |

## Legacy API Disposition

**Criteria**: a method in the docs method table with a `(Deprecated)` prefix is deprecated. `PXR_EyeTracking` is a **whole-class legacy** (no per-method marker, but the docs declare it in the first sentence).

| Legacy surface | Methods involved | Disposition |
|---|---|---|
| `PXR_EyeTracking` whole class | 12 `Get*` eye methods | When reading an old project, use as reference only. New code uses the eye interfaces of `PXR_MotionTracking`. The first sentence of the source page gives this guidance. |
| `PXR_Input` legacy vibration group | `SetControllerVibration`, `SetControllerVibrationEvent`, `Start/StopControllerVCMotor`, `SetControllerAmp`, `StartVibrateBySharem` x2, `SaveVibrateByCache` x2, `StartVibrateByCache`, `ClearVibrateByCache`, `StartVibrateByPHF`, `PauseVibrate`, `ResumeVibrate`, `UpdateVibrateParams` | Migrate all to `SendHapticImpulse` / `SendHapticBuffer` + `Start/Stop/Pause/Resume/UpdateHapticBuffer`. |
| `PXR_Input` haptic stream group | `CreateHapticStream`, `WriteHapticStream`, `Set/GetHapticStreamSpeed`, `GetHapticStreamCurrentFrameSequence`, `Start/Stop/RemoveHapticStream`, `AnalysisHapticStreamPHF` | The whole group is Deprecated. Do not introduce it in new projects. |
| `PXR_Input` body and tracker group | `GetBodyTrackingPose`, `GetMotionTrackerConnectStateWithID`, `GetMotionTrackerBattery`, `GetMotionTrackerCalibState`, `SetBodyTrackingMode`, `SetBodyTrackingBoneLength` | Migrate to the body and motion tracker interfaces of `PXR_MotionTracking` (`StartBodyTracking`, `GetBodyTrackingData`, `GetMotionTrackerLocation`, `GetMotionTrackerBattery`, etc.). |
| `PXR_Input` G3 only | `ResetController`, `SetArmModelParameters` | Deprecated. The docs page has no signature details, so do not write calls to these from the method name. |
| `PXR_MixedReality` AnchorEntity group | `CreateAnchorEntity`, `DestroyAnchorEntity`, `GetAnchorPose`, `GetAnchorEntityUuid`, `Persist/UnPersist/ClearPersistedAnchorEntity`, `GetAnchorComponentFlags`, `LoadAnchorEntityByUuidFilter`, `LoadAnchorEntityBySceneFilter`, `GetAnchorEntityLoadResults`, `StartSpatialSceneCapture`, `GetAnchorVolumeInfo`, `GetAnchorPlanePolygonInfo`, `GetAnchorPlaneBoundaryInfo`, `GetAnchorSceneLabel` | The whole group is Deprecated. Use the Sense Data Provider and the `*Async` group instead (see Workflow 2 and MR.md, "Spatial anchor lifecycle"). |
| `PXR_MixedReality` VST | `EnableVideoSeeThrough` | Deprecated. The same page still has `EnableVideoSeeThroughEffect` / `SetVideoSeeThroughEffect` / `SetVideoSeeThroughLut`, but the docs **do not state** that they replace the former. For the correct way to enable VST, check the MR area doc. |
| `PXR_System` face-tracking group | `EnableFaceTracking`, `EnableLipSync`, `GetFaceTrackingData`, `SetFaceTrackingStatus` | Whole group Deprecated. |
| `PXR_MotionTracking` face-tracking group | `WantFaceTrackingService`, `GetFaceTrackingSupported`, `StartFaceTracking`, `StopFaceTracking`, `GetFaceTrackingState`, `GetFaceTrackingData` | Also a whole group Deprecated. In v3.4.0 face tracking is deprecated on **both classes**, and the corpus gives no replacement class. |
| `PXR_MotionTracking` old tracker and external device group | `GetMotionTrackerConnectStateWithSN`, `GetMotionTrackerDeviceType`, `CheckMotionTrackerModeAndNumber`, `GetMotionTrackerMode`, `GetMotionTrackerLocations`, the `ExtDev*` family (`ExtDevConnectAction`, `ExtDevBatteryAction`, `GetExtDevTrackerConnectState`, `SetExtDevTrackerMotorVibrate`, `SetExtDevTrackerByPassData`, `GetExtDevTrackerByPassData`, `GetExtDevTrackerBattery`, `GetExtDevTrackerKeyData`), and the 4 `MotionTracker*` callbacks | Migrate to the current group: `CheckMotionTrackerNumber`, `GetMotionTrackerLocation` (no s), `GetMotionTrackerBattery`, `MotionTrackerConnectionAction`, `MotionTrackerPowerKeyAction`, `RequestMotionTrackerCompleteAction`, and the `ExpandDevice*` family. Old and new names differ only by singular or plural, or by `ExtDev` versus `ExpandDevice`, so they are easy to mix up. |
| `PXR_System` other | `SetExtraLatencyMode`, `SetEyeFOV`, `SetCommonBrightness`, `GetCommonBrightness`, `GetScreenBrightnessLevel`, `SetScreenBrightnessLevel` | Deprecated. For brightness needs on enterprise devices, `PXR_Enterprise.SetScreenBrightness` may be considered (enterprise devices only). |
| `PXR_Boundary` | `GetSeeThroughTrackingState`, `UseGlobalPose` | Deprecated. `PXR_Enterprise` has another `UseGlobalPose` (enterprise devices only, not marked Deprecated). The two are not the same API. |
| `PXR_FoveationRendering` | `SetFoveationParameters` | Deprecated. Use `SetFoveationLevel(level, isETFR)` for level control. |
| `PXR_HandTracking` | `GetSettingState` | Deprecated. |
