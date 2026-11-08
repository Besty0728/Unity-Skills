# PICO Unity SDK — Version Differences and Migration (v3.4.0)

> Anchor: PICO official documentation v3.4.0 (fetched 2026-08-13). Every rule can be verified against the source URLs.
> Method-level additions and removals come from comparing the API reference pages across versions. To verify: load the same page with `?v=<version>`, e.g. `https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/?v=3.1.0` vs `?v=3.2.0`.

## When to Load This Document

- Migrating a legacy PICO Unity Integration SDK project from 2.x to 3.x, or upgrading across versions within 3.x.
- A PICO API fails to compile or the IDE strikes it through, and you need to confirm whether it was **removed**, marked **(Deprecated)**, or listed as "not supported" in the changelog.
- Determining "the earliest SDK version that has feature X", to avoid writing new features into a project on an old SDK.
- Integrating the Unity OpenXR plugin, or migrating from the PICO Unity OpenXR SDK to the PICO Unity Integration SDK.
- Checking the matching of SDK version, device system version (PUI), Unity version and device model.
- Writing code while you "vaguely remember" an API name but are not sure of it. The removal/deprecation/rename lists in this document exist to correct wrong memories.

## Key Rules

| # | Rule | Source |
|---|---|---|
| 1 | The current anchor version is **v3.4.0, released 27 February 2026**. Official downloads only offer the latest SDK; older versions require contacting the PICO enterprise support team. Consumer store development must use the latest SDK. | [v3.4.0](https://developer-cn.picoxr.com/document/updates-unity/3i0euapg/) · [Older versions](https://developer-cn.picoxr.com/document/unity/where-to-download-an-older-version-of-sdk/) |
| 2 | **3.0.0 is the first breaking point**: the spatial anchor and scene capture interfaces were fully rewritten. All `*AnchorEntity*` APIs from 2.5.0 and earlier became Deprecated, and the event-driven flow was replaced by an async flow. | [v3.0.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-300/) · [MR migration](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/) |
| 3 | **3.2.0 is the second breaking point**: 30 members of `PXR_Input`, 27 members of `PXR_MotionTracking`, plus 2 of `PXR_Boundary`, 1 of `PXR_FoveationRendering` and 1 of `PXR_HandTracking` were marked deprecated in one go. Most vibration and motion tracker code written from AI memory falls into this batch. | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 4 | Methods that were truly **removed** from the documentation are very few: only 3.0.0 removed 6 Fitness/Swift methods from `PXR_Input` and `WriteConfigFileToDataLocal` from `PXR_Enterprise`. The vast majority are "kept but marked (Deprecated)". | [PXR_Input?v=2.5.0](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/?v=2.5.0) vs [?v=3.0.0](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/?v=3.0.0) |
| 5 | 64-bit: **platform services have supported only 64-bit apps since 2.3.0**; **the entire SDK has supported only 64-bit app development since 3.1.0** (Target Architectures must be ARM64 and Scripting Backend must be IL2CPP; enforced by Project Validation from 3.1.0). | [v2.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-230/) · [v3.1.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-310/) |
| 6 | **From 3.1.0 the top menu `PXR_SDK` was renamed to `PICO`**. Any `PXR_SDK > ...` menu path is wrong in 3.1.0 and later. | [v3.1.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-310/) |
| 7 | **From 3.3.0 the composition layer component `PXR_Over Lay` was renamed `PXR_Composition Layer`**; however the script file is still `PXR_OverLay.cs`, the editor class is still `PXR_OverLayEditor`, and the v3.4.0 documentation still contains leftover `PXR_Over Lay (Script)` references. | [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) · [Composition layer parameters](https://developer-cn.picoxr.com/document/unity/about-the-pxr-overlay-component/) |
| 8 | **The Unity OpenXR plugin is supported from 3.3.0**. Once it is enabled, consult the *PICO Unity OpenXR SDK Development Guide* instead of the Integration SDK feature docs. 3.3.0 also made PICO Building Blocks compatible with this plugin. | [OpenXR plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) · [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| 9 | Unity engine-side support was only completed in **3.1.0**: Unity 6, XR Interaction Toolkit 3.x, AR Foundation 6.0, and macOS development environment. Do not assume these are available in 3.0.x or earlier. | [v3.1.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-310/) |
| 10 | A batch of `PXR_System` APIs were marked **"not supported"** in the 3.2.0 changelog: `SetExtraLatencyMode`, `EnableFaceTracking`, `EnableLipSync`, `GetFaceTrackingData`, `SetEyeFOV`, `SetFaceTrackingStatus`, `SetCommonBrightness`, `GetCommonBrightness`, `GetScreenBrightnessLevel`, `SetScreenBrightnessLevel`. **They carry no (Deprecated) marker in the v3.4.0 API reference and are still searchable**, making them the easiest class to misuse. | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) · [PXR_System](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_System/) |
| 11 | `GetDisplayFrequenciesAvailable` (`PXR_System`, gets all refresh rates supported by the device) was **added in 3.2.0** and does not exist in 3.1.0 or earlier. | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 12 | From 3.2.0, `PXR_Boundary.GetDimensions` **may only be called at StageLevel**; in the same version `SessionStateChanged` changed from `Action<int>` to `Action<XrSessionState>`. | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 13 | **Same-named APIs move between classes**: `UseGlobalPose` was deprecated on `PXR_Boundary` in 3.2.0 and reappeared as a new API on `PXR_Enterprise` in 3.3.0; `GetMotionTrackerBattery` was deprecated on `PXR_Input` in 3.2.0 and in the same version appeared as a new API on `PXR_MotionTracking`. Remembering only the method name without the class name will certainly produce errors. | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) · [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| 14 | **The official documentation contradicts itself**: the v3.4.0 *Face Tracking* guide still teaches `PXR_MotionTracking.StartFaceTracking` / `GetFaceTrackingData` and similar, while the API reference marks them (Deprecated) and the 3.2.0 changelog lists them as deprecated; the older `PXR_System` set (2.1.4+) is marked "not supported". Before shipping, treat the actual SDK source as authoritative. | [Face tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/) · [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) |
| 15 | The device system version (PUI) requirement increases monotonically with the SDK. **Installing a new SDK on an older device system means the features simply do not work.** 3.3.0 requires PUI 5.13.0 for the PICO 4 series and 5.14.0 or later for the PICO 4 Ultra series. | [v3.3.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-330/) |
| 16 | The 3.2.0 device declaration is the **PICO 4 Ultra series (PICO Neo3 and the PICO 4 series "coming soon")**; only in **3.4.0** are spatial anchors, shared spatial anchors, spatial mesh, scene capture and MR safety guard explicitly supported on the PICO 4 series. When building MR for PICO 4, check this timeline. | [v3.2.0](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-320/) · [v3.4.0](https://developer-cn.picoxr.com/document/updates-unity/3i0euapg/) |
| 17 | The only new API page in 3.4.0 is **`PXR_CameraImage`** (user device camera image data management); no XR class pages were added or removed between 2.5.0 and 3.3.2. | [v3.4.0](https://developer-cn.picoxr.com/document/updates-unity/3i0euapg/) · [PXR_CameraImage](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_CameraImage/) |
| 18 | MR version compatibility matrix: **MR APIs in 2.5.0 and earlier are supported on PICO 4 but not PICO 4 Ultra**; **3.0.0 supports only spatial anchors (not shared) and scene capture on PICO 4**, while PICO 4 Ultra supports all of them. Video see-through is outside the scope of this rewrite. | [MR migration](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/) |
| 19 | In 2.1.3 the **haptic feedback API was renamed as a whole** (involving both additions and deprecations); in 2.0.5 the **"payment" and "achievement" features were removed** and provided by platform services instead. Calls of these two kinds in legacy 2.x code must be rewritten. | [v2.1.x](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-211-to-215/) · [v2.0.x](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-201-to-207/) |
| 20 | **2.0.6 has no public release note** (the official record jumps directly from 2.0.5 to 2.0.7); do not fabricate changes for that version. | [v2.0.x](https://developer-cn.picoxr.com/document/updates-unity/release-notes-for-version-201-to-207/) |

## Workflow

### 1. Locate the Version Generation (Timeline Overview)

Step 0 of any migration task: confirm the project's current version and the target version, and list every breaking point between them.

| Version | Release date | Device system version requirement | Key changes |
|---|---|---|---|
| 2.0.1 | 2021-10-12 | — | Major iteration; UnityXR SDK (Legacy) / UnitySDK (Deprecated) functionality merged in; added Vulkan, Metrics Tool, `GetSensorStatus` |
| 2.0.2 | 2021-12-09 | — | Large space map sharing APIs `SaveLargeSpaceMaps` / `ExportMaps` / `ImportMaps`; `KillBackgroundAppsWithWhiteList` |
| 2.0.3 | 2022-01-28 | — | Spatial audio renderer, 120Hz refresh rate, mixed reality capture (MRC) |
| 2.0.4 | 2022-04-01 | — | Preview Tool, focus awareness; input system upgraded to Unity Input System |
| 2.0.5 | 2022-05-30 | — | **Removed "payment" and "achievement" features** (moved to platform services); account & friends / voice chat / rooms & matchmaking; DRM protection |
| 2.0.7 | 2022-09-09 | — | Platform services completed (social interaction / leaderboards / in-app purchase / achievements / motion data authorization); composition layers support Cubemap and Equirect |
| 2.1.1 | 2022-09-27 | — | PICO 4 series support; hand tracking, haptic feedback, DLC; **UI copy Pico → PICO** |
| 2.1.2 | 2022-11-07 | PUI 5.1.0+ (PICO 4 series) / 4.9.0+ (PICO Neo3 series) | Cloud save, challenges, PC-side debugging tool; MRC option moved into the `PXR_Manager` panel |
| 2.1.3 | 2023-01-12 | PUI 5.3.0+ | **Haptic feedback API renamed as a whole** (additions and deprecations); Optimize Buffer Discards (Vulkan); `SetEyeFOV` |
| 2.1.4 | 2023-03-02 | PUI 5.4.0+ | Application space warp (AppSW), Late Latching, eye tracking, face tracking, Metrics HUD, PDC |
| 2.1.5 | 2023-05-17 | PUI 5.5.0+ | Splash screen, EAC composition layer, FFR downsampling; hand pose editor (`PXR_Hand Pose Generator` / `PXR_Hand Pose`); body tracking; `SetFaceTrackingStatus` |
| 2.2.0 | 2023-06-29 | PUI 5.6.0+ | Render Viewport Scale; Late Latching Debug; OIDC authorized login |
| 2.3.0 | 2023-08-17 | PUI 5.7.0+ | Spatial anchors, scene capture, ETFR, adaptive resolution; **eye and face tracking interfaces refactored**; **platform services 64-bit only** |
| 2.4.0 | 2023-11-16 | PUI 5.8.0+ | Super resolution, sharpening, EAC; RGB camera series interfaces (enterprise); `GetPerEyePose` / `GetEyeBlink` |
| 2.5.0 | 2024-01-24 | 5.9.0+ | HDR video on the External Surface layer; XR Interaction Toolkit hand and 3D object interaction |
| 3.0.0 | 2024-09-02 | 5.11.0+ | **MR interface major rewrite** (spatial anchors / scene capture); shared spatial anchors, spatial mesh, MR safety guard, spatial data permissions; independent tracking; project configuration validation |
| 3.0.5 | 2024-11-05 | 5.12.0+ | PICO Building Blocks; Blurred Quad layer; `GetControllerStatus` / `InputDeviceChanged`; scene capture adds 10 semantic labels |
| 3.1.0 | 2025-02-18 | 5.12.0+ | Unity 6 / XRIT 3.x / AR Foundation 6.0 / macOS support; **64-bit only**; **menu `PXR_SDK` → `PICO`**; `PXR_Spatial Anchor (Script)`, `PXR_Scene Capture Manager (Script)`; enterprise camera `*for4U` series |
| 3.2.0 | 2025-05-29 | 5.13.0+ (device: PICO 4 Ultra series) | **Large-scale deprecation** (`PXR_Input` 30 items, `PXR_MotionTracking` 27 items, etc.); PICO XR Portal; SecureMR; `UPxr_SetSuperResolutionOrSharpening`; shared anchor interfaces with progress |
| 3.3.0 | 2025-09-18 | PICO 4 series 5.13.0 / PICO 4 Ultra series 5.14.0+ | **Unity OpenXR plugin supported**; **`PXR_Over Lay` → `PXR_Composition Layer`**; PICO Debugger; 20 new enterprise interfaces |
| 3.3.2 | 2025-11-11 | — | Eye buffer upper-limit control; SecureMR adds SVD / NORM / SWAP_HWC_CHW operators and more operators |
| 3.4.0 | 2026-02-27 | — | **Plane detection**; spatial anchors / shared anchors / spatial mesh / scene capture / MR safety guard support PICO 4 series; `PXR_CameraImage`; SecureMR JavaScript Operator / Dynamic Texture / Readback Tensor |

### 2. 2.5.0 and Earlier → 3.0.0: MR Interface Migration

Source: [MR feature compatibility and migration notes](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/)

1. First check the compatibility matrix (key rule 18) to confirm which MR capabilities the target device supports on the target SDK.
2. Panel toggle rename: on `PXR_Manager (Script)`, the original **Anchor** checkbox → **Spatial Anchor**; scene capture adds a **Scene Capture** checkbox (not present in the 2.5.0 era).
3. The lifecycle changed: from 3.0.0 on, **every anchor or scene capture operation must first call `StartSenseDataProvider` and end with `StopSenseDataProvider`**; 2.5.0 had no such step.
4. Event-driven → async: the old three-step pattern "call the API + listen for `AnchorEntity*` events + then call `Get*Results`" is consolidated in 3.0.0 into a single `*Async` call.
5. Replace the interfaces one by one (the full mapping is in "Core API anchors → Rename / migration mapping").
6. Motion tracking follows the [motion tracker API compatibility notes](https://developer-cn.picoxr.com/document/unity/motion-tracker-api-compatibility/); note that the PICO motion tracker Beta and the official release apply different interfaces.

### 3. 3.1.0 → 3.2.0 and Later: The Deprecation Wave for Input / Motion Tracking / Haptics

1. Work through the code against "Core API anchors → 3.2.0 newly deprecated list" item by item. The batch is large (`PXR_Input` 30 + `PXR_MotionTracking` 27); search globally for method names rather than relying on compiler warnings.
2. Handle first the ones where the changelog explicitly gives a replacement:
   - `CheckMotionTrackerModeAndNumber` → `CheckMotionTrackerNumber`
   - `GetMotionTrackerLocations` → `GetMotionTrackerLocation`
   - `SetExtDevTrackerMotorVibrate` → `SetExpandDeviceVibrate`
   - `SetExtDevTrackerByPassData` → `SetExpandDeviceCustomData`
   - `GetExtDevTrackerByPassData` → `GetExpandDeviceCustomData`
   - `GetExtDevTrackerBattery` → `GetExpandDeviceBattery`
   - `ExtDevConnectAction` → `ExpandDeviceConnectionAction`
   - `MotionTrackerNumberOfConnections` / `GetExtDevTrackerConnectState` / `GetMotionTrackerConnectStateWithSN` → use the `MotionTrackerConnectionAction` event instead
   - `MotionTrackerKeyAction` → `MotionTrackerPowerKeyAction`
   - `MotionTrackerBatteryLevel` → `GetMotionTrackerBattery` (note: this is the new API on `PXR_MotionTracking`)
   - `BodyTrackingAbnormalCalibrationData` / `BodyTrackingStateError` → use `GetBodyTrackingState` instead
3. For APIs whose changelog gives no replacement (vibration/haptic streams `*HapticStream*`, `*Vibrate*`, `GetDominantHand` / `SetDominantHand`, etc.), do not invent a replacement API. Check the current SDK source or the current method list of [PXR_Input](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_Input/).
4. Note that in the 3.2.0 changelog, the `ExtDevBatteryAction` line reads "deprecated. Replaced by callback event `ExtDevBatteryAction`" — **this is a typo in the official documentation** (it replaces itself). From context it should be `ExpandDeviceBatteryAction`. The source code is authoritative.

### 4. Integrating the Unity OpenXR Plugin / Migrating from the PICO Unity OpenXR SDK (3.3.0+)

Source: [Support for the Unity OpenXR plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/)

1. Import PICO Unity Integration SDK **3.3.0 or later** and enable the plugin following the "Enable the Unity OpenXR plugin" section of *Complete project configuration*.
2. For subsequent feature development, consult the [PICO Unity OpenXR SDK Development Guide](https://developer-cn.picoxr.com/document/unity-openxr/); do not copy the Integration SDK component workflow over.
3. If the original project uses **PICO Unity OpenXR SDK 1.4.0 or earlier**, adapt it as follows:

| Feature | Old (PICO Unity OpenXR SDK ≤1.4.0) | New (PICO Unity Integration SDK 3.3.0+) |
|---|---|---|
| Scene fade | `PICOScreenFade` | `PXR_ScreenFade` |
| Screen refresh rate | `TryGetSupportedDisplayRefreshRates`, `GetDisplayRefreshRateCount` (both deprecated) | `GetDisplayFrequenciesAvailable` |
| See-through | Attaching the Passthrough Layer Feature requires the `PICO Manager (Script)` component | This component is not required |
| See-through toggle | `EnableSeeThroughManual` (deprecated) | `EnableVideoSeeThrough` |
| Composition layers | All interfaces deprecated | Use directly per the *Composition layers* documentation |
| Namespaces | `SecureContentFlag`, `BodyJointSet`, `BodyTrackingData`, `BodyTrackingDataInfo`, `BodyTrackingBoneLength`, `GeometryInstanceTransform`, `PassThroughStyle` each standalone | Unified into the `pxr` namespace, moved to `PXR_Type.cs`; `using` directives must be changed |

> The body text of this document says "after using PICO Unity Integration SDK **3.0.0** and later you need to adapt", while the table header says **3.3.0** and later. This is an inconsistency in the original: when citing it, follow the table header (3.3.0) and note the discrepancy as uncertain.

### 5. Sequence for "Does This API Still Exist?"

1. Open the API reference page for the class in the current version, `https://developer-cn.picoxr.com/reference/unity/client-api/<ClassName>/`, and search for the method name.
2. Not found → it may have been removed (check the "truly removed" list in this document) or it never belonged to this class (check "moved between classes").
3. Found but the title carries **(Deprecated)** → deprecated; check the changelog for the corresponding version to get the replacement.
4. Found without (Deprecated) → still cross-check the 3.2.0 changelog "not supported" list (these carry no marker; see key rule 10).
5. To confirm the introducing version → check each version with `?v=`, e.g. present in `?v=3.1.0` but absent in `?v=3.0.5` means it was introduced in 3.1.0.
6. When the guide and the API reference conflict (e.g. face tracking), **the SDK source is authoritative**; state the conclusion with both sides noted.

## Core API Anchors

### Truly Removed Methods (Gone From the Documentation; Calls Fail to Compile)

| Version | Class | Removed |
|---|---|---|
| 3.0.0 | `PXR_Input` | `GetFitnessBandBattery`, `GetFitnessBandCalibState`, `GetFitnessBandConnectState`, `OpenFitnessBandCalibrationAPP`, `SetControllerEnableKey`, `SetSwiftMode` |
| 3.0.0 | `PXR_Enterprise` | `WriteConfigFileToDataLocal` |

> Five of the Fitness/Swift methods did not vanish outright; they were **renamed and moved to new names under `PXR_MotionTracking` / `PXR_Input`**, see the rename table below. `SetControllerEnableKey` and `WriteConfigFileToDataLocal` have no counterpart in the v3.4.0 documentation.

### Newly Deprecated List

**3.0.0 (`PXR_MixedReality`, 17 items, MR rewrite)**
`ClearPersistedAnchorEntity`, `CreateAnchorEntity`, `DestroyAnchorEntity`, `EnableVideoSeeThrough`, `GetAnchorComponentFlags`, `GetAnchorEntityLoadResults`, `GetAnchorEntityUuid`, `GetAnchorPlaneBoundaryInfo`, `GetAnchorPlanePolygonInfo`, `GetAnchorPose`, `GetAnchorSceneLabel`, `GetAnchorVolumeInfo`, `LoadAnchorEntityBySceneFilter`, `LoadAnchorEntityByUuidFilter`, `PersistAnchorEntity`, `StartSpatialSceneCapture`, `UnPersistAnchorEntity`

**3.2.0 (`PXR_Input`, 30 items)**
Vibration / haptics: `SetControllerVibration`, `SetControllerVibrationEvent`, `StartControllerVCMotor`, `StopControllerVCMotor`, `SetControllerAmp`, `StartVibrateBySharem`, `SaveVibrateByCache`, `StartVibrateByCache`, `ClearVibrateByCache`, `StartVibrateByPHF`, `PauseVibrate`, `ResumeVibrate`, `UpdateVibrateParams`, `CreateHapticStream`, `WriteHapticStream`, `SetHapticStreamSpeed`, `GetHapticStreamSpeed`, `GetHapticStreamCurrentFrameSequence`, `StartHapticStream`, `StopHapticStream`, `RemoveHapticStream`, `AnalysisHapticStreamPHF`
Other: `GetDominantHand`, `SetDominantHand`, `GetBodyTrackingPose`, `GetMotionTrackerConnectStateWithID`, `GetMotionTrackerBattery`, `GetMotionTrackerCalibState`, `SetBodyTrackingMode`, `SetBodyTrackingBoneLength`
(The changelog separately lists `ResetController` and `SetArmModelParameters` as deprecated.)

**3.2.0 (`PXR_MotionTracking`, 27 items)**
`WantEyeTrackingService`, `WantFaceTrackingService`, `GetFaceTrackingSupported`, `StartFaceTracking`, `StopFaceTracking`, `GetFaceTrackingState`, `GetFaceTrackingData`, `BodyTrackingAbnormalCalibrationData`, `BodyTrackingStateError`, `BodyTrackingAction`, `MotionTrackerNumberOfConnections`, `MotionTrackerBatteryLevel`, `MotionTrackerKeyAction`, `MotionTrackingModeChangedAction`, `GetMotionTrackerConnectStateWithSN`, `GetMotionTrackerDeviceType`, `CheckMotionTrackerModeAndNumber`, `GetMotionTrackerMode`, `GetMotionTrackerLocations`, `ExtDevConnectAction`, `ExtDevBatteryAction`, `GetExtDevTrackerConnectState`, `GetExtDevTrackerKeyData`, `SetExtDevTrackerMotorVibrate`, `SetExtDevTrackerByPassData`, `GetExtDevTrackerByPassData`, `GetExtDevTrackerBattery`

**3.2.0 (other classes)**
`PXR_Boundary.GetSeeThroughTrackingState`, `PXR_Boundary.UseGlobalPose`, `PXR_FoveationRendering.SetFoveationParameters`, `PXR_HandTracking.GetSettingState`

### Additions Per Version (Highest-Value Items; Full Lists in the Corresponding Changelogs)

| Version | Class | Added |
|---|---|---|
| 3.0.0 | `PXR_MixedReality` | `StartSenseDataProvider`, `StopSenseDataProvider`, `GetSenseDataProviderState`, `CreateSpatialAnchorAsync`, `DestroyAnchor`, `PersistSpatialAnchorAsync`, `UnPersistSpatialAnchorAsync`, `QuerySpatialAnchorAsync`, `GetAnchorUuid`, `LocateAnchor`, `UploadSpatialAnchorAsync`, `DownloadSharedSpatialAnchorAsync`, `StartSceneCaptureAsync`, `QuerySceneAnchorAsync`, `GetSceneSemanticLabel`, `GetSceneAnchorComponentTypes`, `GetSceneBox2DData`, `GetSceneBox3DData`, `GetScenePolygonData`, `PxrResult`, `PxrSemanticLabel`, `PxrSenseDataProviderState` |
| 3.0.0 | `PXR_MotionTracking` | `StartBodyTracking`, `StopBodyTracking`, `GetBodyTrackingState`, `GetBodyTrackingData`, `GetBodyTrackingSupported`, `StartMotionTrackerCalibApp`, `BodyTrackingMode`, `EyeTrackingData`, `FaceTrackingData`, `MotionTrackerLocations`, and 65 items in total |
| 3.0.0 | `PXR_HandTracking` | `GetHandScale`, `HandAimState`, `HandJointLocations`, `HandFinger`, `HandType`, `ActiveInputDevice`, `Posef`, `Quatf`, `Vector3f`, etc. |
| 3.0.5 | `PXR_Input` | `GetControllerStatus`, `InputDeviceChanged` |
| 3.1.0 | `PXR_MixedReality` | `QuerySpatialAnchorObjectsAsync` (**note**: the API reference page title is truncated to `QuerySpatialAnchorObjectsAsyn`; the full name is in the signature) |
| 3.1.0 | `PXR_Enterprise` | `OpenCameraAsyncfor4U`, `Configurefor4U`, `StartPreviewfor4U`, `SetCameraFrameBufferfor4U`, `StartGetImageDatafor4U`, `CloseCamerafor4U`, `GetCameraIntrinsicsfor4U`, `GetCameraExtrinsicsfor4U`, `GetCameraParametersNewfor4U`, `RequestUserPermission` |
| 3.2.0 | `PXR_System` | `GetDisplayFrequenciesAvailable` |
| 3.2.0 | `PXR_MotionTracking` | `CheckMotionTrackerNumber`, `GetMotionTrackerLocation`, `GetMotionTrackerBattery`, `MotionTrackerConnectionAction`, `MotionTrackerPowerKeyAction`, `RequestMotionTrackerCompleteAction`, `ExpandDeviceConnectionAction`, `ExpandDeviceBatteryAction`, `GetExpandDevice`, `GetExpandDeviceBattery`, `SetExpandDeviceVibrate`, `SetExpandDeviceCustomData`, `GetExpandDeviceCustomData` |
| 3.2.0 | `PXR_Enterprise` | `SetDeviceOwner`, `GetDeviceOwner`, `SetBrowserHomePage`, `GetBrowserHomePage`, `SetMotionTrackerAutoStart`, `AllowWifiAutoJoin`, `GetLargeSpaceBoundsInfoWithType` |
| 3.2.0 | `PXR_MixedReality` | `UploadSpatialAnchorWithProgressAsync`, `DownloadSharedSpatialAnchorWithProgressAsync` |
| 3.3.0 | `PXR_Enterprise` | `GetHeadTrackingStatus`, `GetHeadPose`, `GetControllerPose`, `GetSwiftPose`, `GetSwiftTrackerDevices`, `GetHeadIMUData`, `GetControllerIMUData`, `GetSwiftIMUData`, `StartSwiftTrackerPairing`, `UnBondSwiftTracker`, `ResetTracking`, `SetFenceColor`, `GetFenceColor`, `SetUsbTetheringStaticIP`, `GetUsbTetheringStaticIPLocal`, `GetUsbTetheringStaticIPClient`, `SetLargeSpaceMapScale`, `GetPredictedMainSensorState2`, `UseGlobalPose`, `ConvertPoseCoordinate` |
| 3.4.0 | `PXR_CameraImage` | Entire class new: `GetAvailableCameras`, `CreateCameraDeviceAsync`, `CreateCameraCaptureSessionAsync`, `BeginCameraCapture`, `AcquireCameraImage`, `GetCameraImageData`, `ReleaseCameraImage`, `EndCameraCapture`, `DestroyCameraCaptureSession`, `DestroyCameraDevice`, `GetCameraIntrinsics`, `GetCameraExtrinsics`, etc. |

### Renames / Migration Mapping (Old Name → Current Name)

**MR (2.5.0 and earlier → 3.0.0)**
`CreateAnchorEntity` → `CreateSpatialAnchorAsync` | `DestroyAnchorEntity` → `DestroyAnchor` | `PersistAnchorEntity` → `PersistSpatialAnchorAsync` | `UnPersistAnchorEntity` → `UnPersistSpatialAnchorAsync` | `LoadAnchorEntityByUuidFilter` → `QuerySpatialAnchorAsync` | `GetAnchorEntityUuid` → `GetAnchorUuid` | `GetAnchorPose` → `LocateAnchor` | `StartSpatialSceneCapture` → `StartSceneCaptureAsync` | `LoadAnchorEntityBySceneFilter` → `QuerySceneAnchorAsync` | `GetAnchorSceneLabel` → `GetSceneSemanticLabel` | `GetAnchorComponentFlags` → `GetSceneAnchorComponentTypes` | `GetAnchorVolumeInfo` → `GetSceneBox3DData` | `GetAnchorPlaneBoundaryInfo` → `GetSceneBox2DData` | `GetAnchorPlanePolygonInfo` → `GetScenePolygonData`

**Motion tracking / motion trackers (old names as annotated in the official "old name" notes)**
`GetFitnessBandConnectState` → `GetMotionTrackerConnectStateWithID` | `GetFitnessBandBattery` → `GetMotionTrackerBattery` | `GetFitnessBandCalibState` → `GetMotionTrackerCalibState` | `SetSwiftMode` → `SetBodyTrackingMode` | `FitnessBandNumberOfConnections` → `MotionTrackerNumberOfConnections` | `FitnessBandElectricQuantity` → `MotionTrackerBatteryLevel` | `FitnessBandAbnormalCalibrationData` → `BodyTrackingAbnormalCalibrationData` | `OpenFitnessBandCalibrationAPP` / `StartBodyTrackingCalibApp` → `StartMotionTrackerCalibApp` | `GetMotionTrackerConnectState` → `GetMotionTrackerConnectStateWithSN` | `GetMotionTrackerType` → `GetMotionTrackerDeviceType`
(Among the "current names" above, apart from `StartMotionTrackerCalibApp`, `GetBodyTrackingState` and similar, most were deprecated again in 3.2.0, so a second hop through the 3.2.0 mapping is needed.)

**Components / menu / panel**
`PXR_Over Lay` (component display name; the script file is still `PXR_OverLay.cs`) → `PXR_Composition Layer` (3.3.0) | menu `PXR_SDK` → `PICO` (3.1.0) | **Anchor** checkbox on `PXR_Manager (Script)` → **Spatial Anchor** (3.0.0)

### Version-Sensitive Class / Component Names (Avoid Mix-Ups)

- XR classes stably present from 2.5.0 to 3.4.0: `PXR_Boundary`, `PXR_Enterprise`, `PXR_EyeTracking`, `PXR_FoveationRendering`, `PXR_HandTracking`, `PXR_Input`, `PXR_MixedReality`, `PXR_MotionTracking`, `PXR_System`.
- Present only from 3.4.0: `PXR_CameraImage`.
- Components only in 3.1.0 and later: `PXR_Spatial Anchor (Script)`, `PXR_Scene Capture Manager (Script)`, the `SpatialAnchor` prefab.
- Hand pose editor scripts introduced in 2.1.5: `PXR_Hand Pose Generator`, `PXR_Hand Pose`; the `HandPoseGenerator` prefab introduced in 2.3.0.
- 2.4.0 spatial audio: the `PXR_Audio_Spatializer_Audio Source` script **gained** Reflection Gain DB / Directivity Alpha / Directivity Order parameters (the script itself existed earlier); the same version **added** the `PXR_Audio_Spatializer_Audio Listener` script.
- PICO Building Blocks introduced in 3.0.5; PICO XR Portal in 3.2.0; in 3.3.0 PICO Debugger and five Building Blocks submodules: `PICO Spatial Anchor Sample` / `PICO Spatial Mesh` / `PICO Scene Capture` / `PICO Composition Layer Overlay` / `PICO Composition Layer Underlay`.

## DO NOT

```csharp
// ❌ Using the 2.5.0-era anchor pattern (deprecated since 3.0.0, and lacks the Provider start/stop)
PXR_MixedReality.CreateAnchorEntity(pos, rot, out var taskId);
// Listening for the AnchorEntityCreated event ...
// ✅ 3.x: start the Provider first, then use the async interfaces
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);
var (result, handle, uuid) = await PXR_MixedReality.CreateSpatialAnchorAsync(pos, rot);
```

```csharp
// ❌ Remembered motion tracker interfaces (2.5.0 era; removed from the documentation in 3.0.0)
PXR_Input.GetFitnessBandBattery(id, ref battery);
PXR_Input.SetSwiftMode(mode);
// ✅ Battery: rename first (3.0.0 → PXR_Input.GetMotionTrackerBattery), then move the class (3.2.0 → PXR_MotionTracking)
PXR_MotionTracking.GetMotionTrackerBattery(/* ... */);   // The same-named version on PXR_Input is already (Deprecated)
// ⚠️ Mode setting: SetBodyTrackingMode exists only on PXR_Input and was deprecated in 3.2.0;
//    the v3.4.0 PXR_MotionTracking has no method of the same name. Look up the replacement in the current SDK source; do not invent one
```

```csharp
// ❌ Remembering only the method name and not the class: UseGlobalPose is deprecated on PXR_Boundary
PXR_Boundary.UseGlobalPose(true);
// ✅ From 3.3.0 this capability is on PXR_Enterprise
PXR_Enterprise.UseGlobalPose(true);
```

```csharp
// ❌ Calling a 3.2.0-only interface from a project on 3.1.0 or earlier
var rates = PXR_System.GetDisplayFrequenciesAvailable();   // Introduced only in 3.2.0
// ✅ First confirm the SDK version is ≥ 3.2.0; otherwise the method does not exist at all
```

```csharp
// ❌ Using it because it is searchable in the API reference and not marked Deprecated
PXR_System.SetEyeFOV(/* ... */);
PXR_System.EnableFaceTracking(true);
// ✅ This batch was explicitly marked "not supported" in the 3.2.0 changelog; it is an unmarked trap, so switch to a currently supported approach
```

- ❌ Writing the menu path `PXR_SDK > ...` in a project on 3.1.0 or later → ✅ From 3.1.0 it is `PICO > ...`.
- ❌ Telling users in a 3.3.0+ project to look for the `PXR_Over Lay (Script)` component → ✅ The panel name is `PXR_Composition Layer`; but when pointing to the source file it is still `PXR_OverLay.cs` (both are correct; do not mix them).
- ❌ Directly producing `PXR_MotionTracking.StartFaceTracking` from the *Face Tracking* guide as the recommended v3.4.0 approach → ✅ This series was marked deprecated in 3.2.0; the guide and the API reference contradict each other, so check the SDK source before concluding and note the conflict. See [Face tracking](https://developer-cn.picoxr.com/document/unity/face-tracking/).
- ❌ Downloading "some historical SDK package" for a legacy project → ✅ Official downloads only offer the latest version; older versions require contacting the PICO enterprise support team, and consumer store apps must be developed with the latest SDK. See [Older versions](https://developer-cn.picoxr.com/document/unity/where-to-download-an-older-version-of-sdk/).
- ❌ Assuming the full MR suite runs on PICO 4 from 3.2.0 → ✅ 3.2.0 declares the device as the PICO 4 Ultra series (PICO Neo3 / PICO 4 "coming soon"); spatial anchor support for PICO 4 was only stated in 3.4.0.
- ❌ Presenting Unity 6 / XRIT 3.x / AR Foundation 6.0 / macOS development to a 2.x project → ✅ These were supported only from 3.1.0.
- ❌ Fabricating changes for 2.0.6 → ✅ The official release notes jump from 2.0.5 directly to 2.0.7; there is no public record for that version.
