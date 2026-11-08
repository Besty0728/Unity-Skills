# PICO Unity SDK — Installation, Project Configuration and Quick Start (v3.4.0)

> Anchor: PICO official documentation v3.4.0 (fetched 2026-08-13). Every rule can be verified against the source URLs.

## When to Load This Document

- Building a PICO project from scratch: installing the editor, importing the PICO Unity Integration SDK, creating the first XR scene, and producing an APK that runs on the headset.
- Choosing between the **PICO XR (PXR_) traditional route** and the **Unity OpenXR plugin route**, or migrating from the legacy PICO Unity OpenXR SDK.
- Configuring Player Settings / XR Plug-in Management / Project Validation, or troubleshooting "build fails, black screen after install, abnormal Activity display".
- Handling AndroidManifest metadata and permissions (which the SDK writes automatically and which must be written by hand).
- Using PICO Building Blocks to configure features in one step, or integrating AR Foundation (Session / Camera / Anchors / Meshing / Face / Body).
- Checking the capability matrix of device model, system version, Unity version and SDK version.

## Key Rules

| # | Rule | Source |
|---|---|---|
| 1 | **The two XR routes are mutually exclusive**: when both the PICO XR plugin and the Unity OpenXR plugin are enabled, **only the PICO XR plugin takes effect**; to use the OpenXR route you must enable *only* the Unity OpenXR plugin. Do not tick other vendors' plugins either, or the app will not run properly on PICO devices. | [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) |
| 2 | The Unity OpenXR route is supported only from **SDK 3.3.0**; once enabled, feature integration must follow **a separate set of docs**, the *PICO Unity OpenXR SDK Development Guide* (`/document/unity-openxr/`), and the PXR_ guides in this set must not be copied over. | [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) |
| 3 | Four renames when migrating from PICO Unity OpenXR SDK 1.4.0 or earlier to Integration SDK 3.3.0+: `PICOScreenFade` → `PXR_ScreenFade`; `EnableSeeThroughManual` (deprecated) → `EnableVideoSeeThrough`; `TryGetSupportedDisplayRefreshRates` / `GetDisplayRefreshRateCount` (both deprecated) → `GetDisplayFrequenciesAvailable`; see-through no longer needs the `PICO Manager (Script)` component. | [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) |
| 4 | When migrating, the enum/struct namespaces are **unified to `pxr`** and moved into `PXR_Type.cs`: `SecureContentFlag`, `BodyJointSet`, `BodyTrackingData`, `BodyTrackingDataInfo`, `BodyTrackingBoneLength`, `GeometryInstanceTransform`, `PassThroughStyle`. | [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) |
| 5 | **Every scene must have `PXR_Manager` attached, including loading/splash scenes.** Project Validation lists "the currently open scene has PXR_Manager (Script) added" as Required, and reports an error if it is not met. | [about-pxr-manager](https://developer-cn.picoxr.com/document/unity/about-pxr-manager/) · [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 6 | From **3.1.0** the SDK **supports only 64-bit app development**: `Scripting Backend` must be `IL2CPP`, `Target Architectures` must tick `ARM64` and **untick `ARMv7`**. | [about-pico-unity-integration-sdk](https://developer-cn.picoxr.com/document/unity/about-pico-unity-integration-sdk/) · [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) |
| 7 | `Minimum API Level` must be ≥ **Android 10.0 (API Level 29)**; lower values cause a build error. Use `Automatic (highest installed)` for `Target API Level` (the Recommended rule also requires targetSdkVersion=Auto). | [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) · [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 8 | The Unity editor **minimum is 2021.3.26, maximum is Unity 6.3**; an LTS version is recommended. Version-linked minimums: Unity 2022 requires SDK ≥ 2.1.5 and OS ≥ 5.5.0; Unity 2023 requires SDK ≥ 3.0.0 and OS ≥ 5.11.0; Unity 6 requires SDK ≥ 3.1.0 and OS ≥ 5.12.0. | [hardware-and-software-requirements](https://developer-cn.picoxr.com/document/unity/hardware-and-software-requirements/) |
| 9 | `Graphics API` must be **one of Vulkan or OpenGLES3.0**; when using ETFR (eye-tracked foveated rendering) the **first entry in the list must be OpenGLES3.0**. | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 10 | **Unity 2022 + Vulkan + ticking `Development Build` crashes** (the Validation Required rule reports it directly); **Unity 2022.1.14+ combined with URP + Linear + 4xMSAA + OpenGL also crashes**. | [hardware-and-software-requirements](https://developer-cn.picoxr.com/document/unity/hardware-and-software-requirements/) · [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 11 | The package name format is fixed as `com.companyName.productName`, and the PICO SDK **does not support** `com.epicgames.xxx`, `com.unity.xxxx`, `com.test.xxx`, `com.DefaultCompany.xxx`, `com.Unity-Technologies.xxx`, `com.vr.xxx`, `com.xr.xxx`, `com.UnityTechnologies.xxx`; using them makes store submission fail. | [naming-conventions-for-app-package-name](https://developer-cn.picoxr.com/document/unity/naming-conventions-for-app-package-name/) |
| 12 | Two metadata entries in AndroidManifest **must be added manually**, otherwise the Activity displays abnormally: `pvr.app.type = vr`, `pvr.display.orientation = 180`. | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) |
| 13 | The eye/face tracking metadata and permissions (`picovr.software.eye_tracking`, `com.picovr.permission.EYE_TRACKING`, `picovr.software.face_tracking`, `com.picovr.permission.FACE_TRACKING`, `android.permission.RECORD_AUDIO`) are **written automatically by the SDK after the feature is enabled**; the docs say "do not modify unless necessary". However, **when using a custom Manifest you must add them yourself per feature**. | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) |
| 14 | Standard Android permissions (read external storage, Bluetooth, internet, vibrate, write settings, change configuration) **must be handwritten** into AndroidManifest.xml; the SDK does not write them for you. | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) |
| 15 | The custom manifest path is unique: after ticking **`Custom Main Manifest`** under `Edit > Project Settings > Player > Publishing Settings > Build`, the file is generated at **`Assets/Plugins/Android/AndroidManifest.xml`**. | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) · [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) |
| 16 | Project Validation requires **SDK ≥ 3.0.0**; its entry point is `Edit > Project Settings > XR Plug-in Management > Project Validation`; keep `Selected Profiles` at the default `Turn Off`. Unfixed Required items affect the build; to let it through anyway you can tick `Ignore build errors`, but the related XR features may then be unavailable. | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 17 | Scene uniqueness Required trio: the scene may contain **only one `MainCamera` tag, one `Audio Listener`, and one `XR Origin`**. In addition, the Keystore `keystoreName`/`keystorePass` and Key `keyaliasName`/`keyaliasPass` must not be empty, `Default Orientation` must be `LandscapeLeft`, and `Application Entry Point` must be `Activity`. | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 18 | Composition layer count: **maximum 7** (exceeding it is a Required error), **recommended no more than 4** (Recommended). | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 19 | AR Foundation requires you first to tick **`AR Foundation`** under `Edit > Project Settings > XR Plug-in Management > PICO > Android Settings`, then tick `Body Tracking` / `Face Tracking` / `Anchor` / `Meshing` as needed to enable permissions. Environment pairing: SDK 3.0.0 & 3.0.5 → Unity 2022.3 + AR Foundation 5.1; SDK 3.1.0 → Unity 6 + AR Foundation 6.0. **Image Tracking is not supported for now.** | [ar-foundation](https://developer-cn.picoxr.com/document/unity/ar-foundation-for-pico-unity-integration-sdk/) |
| 20 | **Enabling Minify-related options in the publishing settings crashes the app**; the PICO 4 series and "Unity 6 + Vulkan" have compatibility issues, so Unity 2022 LTS or lower is recommended. | [known-issues](https://developer-cn.picoxr.com/document/unity/known-issues/) |

### Internal Inconsistencies in the Documentation (Confirm Before Copying)

- **XR Interaction Toolkit version**: *Create an XR Scene* says "PICO Unity Integration SDK does not yet support XR Interaction Toolkit 3.x.x" (its manual configuration steps are based on 2.x `XR Controller (Action-Based)` plus presets); *PICO Building Blocks* says "XR Interaction Toolkit version: 3.x.x", and Building Blocks in SDK 3.1.0+ were developed on XRI 3.x.x. Branch according to the XRI version the project actually has installed before writing code. Sources: [create-an-xr-scene](https://developer-cn.picoxr.com/document/unity/create-an-xr-scene/) vs [pico-building-blocks](https://developer-cn.picoxr.com/document/unity/pico-building-blocks/)
- **Minimum Unity version**: *Hardware and Software Requirements* says 2021.3.26; the Information panel of PICO XR Portal says "Unity 2020.3.21 and above". Follow the former. Sources: [hardware-and-software-requirements](https://developer-cn.picoxr.com/document/unity/hardware-and-software-requirements/) vs [pico-xr-portal](https://developer-cn.picoxr.com/document/unity/pico-xr-portal/)

## Workflow

### A. Six-Step Quick Start (PXR_ Traditional Route, From Zero to APK)

1. **Create an account and app**: register a PICO developer account → create an organization (the organization name becomes the store publisher name) → create an app, choosing the **6 DOF platform (recommended)** as the publishing platform. You may skip qualification certification when only trying out the development flow; it must be completed to distribute. [create-a-developer-account-organization-and-app](https://developer-cn.picoxr.com/document/unity/create-a-developer-account-organization-and-app/)
2. **Set up the environment**: on the device, go to `"设置 > 通用 > 关于本机"` (on PICO 4 Ultra, `"控制中心 > 设置 > 关于本机"`), then tap the **software version number** repeatedly to unlock the "Developer" option → turn on the **USB debugging switch**; non-mainland-China devices must sign in with a PICO account on the headset. When installing Unity, be sure to tick **all options under Android Build Support** (including `Android SDK & NDK Tools` and `OpenJDK`). The quoted Chinese strings are the device's on-screen menu labels. [set-up-the-development-environment](https://developer-cn.picoxr.com/document/unity/set-up-the-development-environment/)
3. **Import the SDK**: in Unity Hub create a **Core template > 3D** project (**the project name and path must not contain Chinese characters**) → `Window > Package Manager > + > Add package from disk` and select `package.json`, or `Add package from git URL` and enter the repository HTTPS address. After importing, a **PXR SDK Setting** window pops up; close it. [import-the-sdk](https://developer-cn.picoxr.com/document/unity/import-the-sdk/)
4. **Project configuration**: `Edit > Project Settings > XR Plug-in Management > Android tab`, tick **PICO** → under `Player` set `Company Name` / `Product Name` / `Version` → on the Android tab's `Other Settings`: `Minimum API Level = Android 10.0 (API Level 29)`, `Target API Level = Automatic (highest installed)`, `Scripting Backend = IL2CPP`, `Target Architectures = ARM64` (untick ARMv7) → get the **APP ID** from the **API test** page of the developer platform app and enter it into `PICO > Platform Settings`, then click apply. [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/)
5. **Build the XR scene**: in Package Manager, import the `Starter Assets` and `XR Device Simulator` samples of **XR Interaction Toolkit** → delete the default `Main Camera`, then `+ > XR > XR Origin (VR)` → on `XR Origin`, `Add Component` and add **`PXR_Manager`** → add a `Plane` as the ground (Scale 10,1,10) → controllers: XRI 2.x uses the `XR Controller (Action-Based)` preset `XRI Default Left Controller` with `Model Prefab` pointing to `LeftControllerModel` / `RightControllerModel` under `Packages > PICO Integration > Assets > Resources > Prefabs`; XRI 3.x directly uses `PICO Controller Tracking` from PICO Building Blocks → add **`Input Action Manager`** to `XR Origin`, and set `Action Assets > Element 0` to `XRI Default Input Actions`. [create-an-xr-scene](https://developer-cn.picoxr.com/document/unity/create-an-xr-scene/)
6. **Build and run**: `File > Build Settings > Platform = Android > Switch Platform` → sign in on the device and connect it to the PC via USB → `Add Open Scenes` → under `Run Device` choose `All compatible devices` or a specific model → `Build And Run`. [build-and-run-the-scene](https://developer-cn.picoxr.com/document/unity/build-and-run-the-scene/)

### B. Switching to the Unity OpenXR Plugin Route

1. First import **PICO Unity Integration SDK 3.3.0 or later** into the project.
2. `Window > Package Manager > + > Add package by name`, enter **`com.unity.xr.openxr`** → `Add`; once complete, **OpenXR** appears under `Project Settings > XR Plug-in Management`.
3. **Untick the PICO XR plugin**, keeping only the Unity OpenXR plugin (otherwise the PICO XR plugin takes precedence).
4. Adapt the interfaces and namespaces per rules 3/4 in the table above, then consult the *PICO Unity OpenXR SDK Development Guide* for feature integration.
   Sources: [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) · [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/)

### C. Project Validation Fix Loop (First Stop When a Build Fails)

1. `Edit > Project Settings > XR Plug-in Management > Project Validation` lists only misconfigured items by default; tick `Show all` to see every item.
2. Read the icon levels: **green check** = configured or not applicable; **yellow exclamation** = Recommended, does not affect the build, may be ignored depending on the project; **red cross** = Required, affects the build, must be fixed.
3. Click `Fix All` to fix everything at once, or `Fix` items one by one.
4. You can also go through `PICO > Portal > Configs`: on the panel, use `To Apply` / `To Apply All` to fix items, and jump to **`PXR_ProjectSetting.asset`** to edit project settings directly (the Portal requires SDK ≥ 3.2.0).
   Sources: [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) · [pico-xr-portal](https://developer-cn.picoxr.com/document/unity/pico-xr-portal/)

### D. One-Step Configuration With PICO Building Blocks

- **Three entry points**: `··· > Overlay Menu > XR Building Blocks` in the top-right of the Scene/Game view (Unity 2022/2023); `Hierarchy > + > PICO Building Blocks`; `GameObject > PICO Building Blocks` (the last two are for Unity 2020–2023).
- **Shared side effects**: almost every Block checks whether the scene has an `XR Origin`; if not, it automatically generates `[Building Block] XR Origin (XR Rig)`, attaches **`PXR_Manager (Script)`** to it, and ticks the corresponding checkboxes per feature (such as `Hand Tracking`, `Video Seethrough`, `Body Tracking`, `Spatial Anchor`, `Scene Capture`, `Spatial Mesh`).
- **Four changes brought by XRI 3.x.x**: ① `XR Origin` no longer contains the left and right controllers; replace the old `XR Origin` with **`XR Origin (XR Rig).prefab`** under `/Assets/XR Interaction Toolkit/{version}/prefabs`; ② the Action Maps in `XRI DefaultInputActions.inputactions` dropped the word "Hand" (`XRI LeftHand` → `XRI Left`, `XRI LeftHand Interaction` → `XRI Left Interaction`, `XRI LeftHand Locomotion` → `XRI Left Locomotion`; the right hand is the same); ③ for gesture-related setups, the main camera's `XR Interaction Hands Setup` is replaced with **`XR Origin Hands (XR Rig)`**; ④ when interacting with a Canvas using controllers, you **must hide `EventSystem`**.
  Source: [pico-building-blocks](https://developer-cn.picoxr.com/document/unity/pico-building-blocks/)

### E. Integrating AR Foundation

1. `Edit > Project Settings > XR Plug-in Management > PICO > Android Settings`: tick `AR Foundation` and the required feature permissions.
2. In the scene use **`+ > XR > XR Origin (AR)`** (the added object is named `XR Origin (XR Rig)`); add `+ > XR > AR Session` only when a session is needed.
3. Once enabled, AR Foundation automatically sets `camera.backgroundColor` to `new Color(0, 0, 0, 0)` and attaches **`PXR_AR Camera Effect Manager (Script)`** to the `Main Camera` under `XR Origin (XR Rig)`.
4. Extra hardware thresholds per feature: camera = PICO 4 series / 4 Ultra series + OS ≥ 5.12.0; full-body motion capture = Neo3 / 4 / 4 Ultra series + OS ≥ 5.12.0 + PICO motion tracker (official release); face tracking = PICO 4 Enterprise with a face-tracking camera + OS ≥ 5.12.0; anchors = PICO 4 series + OS ≥ 5.12.0; mesh = PICO 4 Ultra series + OS ≥ 5.12.0.
5. Component replacements when adapting the official `arfoundation-samples`: `Bone Controller (Script)` → **`PXR_Bone Controller (Script)`**; `AR Kit Blend Shape Visualizer (Script)` → **`PXR_Blend Shape Visualizer (Script)`**; components on the `Human Body Tracking` object → **`PXR_Human Body Tracker (Script)`** (and on `AR Human Body Manager (Script)`, **untick `Pose 2D`**).
   Source: [ar-foundation](https://developer-cn.picoxr.com/document/unity/ar-foundation-for-pico-unity-integration-sdk/)

## Core API Anchors

### SDK Directory and Acquisition

- After import it sits at **`Packages/PICO Integration`**, with five subdirectories: `Assets` (controller/hand assets, prefabs, shaders), `Editor` (containing `PXR_BuildProcessor`, `PXR_MetaData`, `PXR_OverLayEditor`), `Platform`, `Runtime`, `SpatialAudio`.
- Acquisition: the resources page of the PICO developer website; GitHub repository **`https://github.com/Pico-Developer/PICO-Unity-XR-SDK`** (the text calls it the "PICO-Unity-Integration-SDK repository", but the repository name is `PICO-Unity-XR-SDK`).
- AR Foundation official adaptation sample repository: **`https://github.com/Pico-Developer/PICOARFoundationSamples-Unity`** (adapted scenes are under `Assets/Scenes/PICO`).

### Components / Scripts (PICO Side)

| Name | Description |
|---|---|
| `PXR_Manager` / `PXR_Manager (Script)` | The master component every scene must have; the panel centrally manages Home key calibration, `Open Screen Fade`, ETFR, FFR, Eye/Face/Hand/Body Tracking, `Use Content Protect`, `MRC`, Late Latching, `Use Recommended MSAA`, Adaptive Resolution, Video Seethrough, Spatial Anchor |
| `PXR_ScreenFade` | After ticking `Open Screen Fade`, it is automatically attached by `PXR_Manager` to a GameObject |
| `PXR_ProjectSetting.asset` | Project settings asset; can be reached from `Open PICO XR Project Setting` in PICO XR Portal |
| `PXR_Composition Layer (Script)` | Composition layer component attached when Building Blocks configure Overlay / Underlay |
| `PXR_Scene Capture Manager (Script)` | Attached by the scene capture Block; configure `Box 2D Prefab` / `Box 3D Prefab` |
| `PXR_BodyTrackingBlock.cs` / `PXR_ObjectTrackingBlock.cs` | Implementation scripts for the full-body motion capture / independent tracking Blocks |
| `PXR_AR Camera Effect Manager (Script)` | Automatically attached to `Main Camera` once AR Foundation is enabled; configure see-through effects and LUT |
| `PXR_Human Body Tracker (Script)` | AR Foundation full-body motion capture component; configure `Skeleton Prefab` and `Human Body Manager` |
| `PXR_Bone Controller (Script)` | Drives avatar bones; enum value names must match the model's node names |
| `PXR_Blend Shape Visualizer (Script)` | BlendShape visualization component for AR Foundation face tracking |
| Script files | `PXR_BoneController.cs`, `PXR_HumanBodyTracker.cs`, `PXR_BlendShapeVisualizer.cs` |
| Shader | `PXR_SDK/PXR_UnderlayHole` (Underlay hole punching) |
| Prefabs | `LeftControllerModel`, `RightControllerModel`, `BodyTracking.prefab`, `BodyTrackingDebug.prefab` |

### Interfaces / Enums (Verbatim Forms Used in This Domain)

```csharp
PXR_Manager.EnableVideoSeeThrough = true;                                 // Enable video see-through
PXR_MixedReality.EnableVideoSeeThroughEffect(true);                       // Enable video see-through effects
PXR_MixedReality.SetVideoSeeThroughEffect(PxrLayerEffect.Colortemp, x, 0);// Color temperature / brightness / saturation / contrast
PXR_MixedReality.SetVideoSeeThroughLut(lutTex, row, col);                 // LUT texture row and column count
```

- `PxrLayerEffect`: `Colortemp`, `Brightness`, `Saturation`, `Contrast`
- `BodyTrackerRole` (24 values; the AR Foundation page gives the full definition): `Root`(0), `LeftUpLeg`, `RightUpLeg`, `Spine3`, `LeftLeg`, `RightLeg`, `Spine6`, `LeftFoot`, `RightFoot`, `Spine7`, `LeftToes`, `RightToes`, `Neck1`, `LeftShoulder1`, `RightShoulder1`, `Neck4`, `LeftArm`, `RightArm`, `LeftForearm`, `RightForearm`, `LeftHand`, `RightHand`, `LeftHandMid1`, `RightHandMid1`(23)
- `BlendShapeIndex`: enum of indices for the 52 face BlendShapes (such as `EyeLookDown_L`, `JawOpen`, `TongueOut`)
- `GetDisplayFrequenciesAvailable`: queries all screen refresh rates the device supports (replaces two deprecated interfaces)

### Unity-Side Objects / Components (Original Names Referenced in PICO Docs)

`XR Origin (VR)`, `XR Origin (AR)`, `XR Origin (XR Rig)`, `XR Origin Hands (XR Rig)`, `Camera Offset`, `Left Controller` / `Right Controller`, `XR Controller (Action-Based)`, `Input Action Manager`, `XR Interaction Manager`, `Tracker Device Graphic Raycast` (as written on the PICO Building Blocks page; the actual Unity component name is `Tracked Device Graphic Raycaster`, see the *Create Interactive UI* page), `AR Session`, `AR Human Body Manager (Script)`, `AR Face Manager (Script)`, `AR Session (Script)`, `AR Anchor Manager`, `AR Mesh Manager (Script)`, `Starter Assets`, `XR Device Simulator`, `XRI Default Input Actions`, `XRI Default Left Controller`

### Menu Paths

`Edit > Project Settings > XR Plug-in Management` | `… > Project Validation` | `… > PICO > Android Settings` | `Edit > Project Settings > Player > Publishing Settings > Build` | `Window > Package Manager` | `File > Build Settings` | `PICO > Platform Settings` | `PICO > Portal` | `GameObject > PICO Building Blocks` | `Hierarchy > + > XR > XR Origin (VR) / XR Origin (AR) / AR Session`

## DO NOT

| ❌ Wrong | ✅ Correct |
|---|---|
| Ticking both `PICO` and `OpenXR` under XR Plug-in Management, believing they run side by side | Pick one; for OpenXR tick **only** the Unity OpenXR plugin (when both are ticked, only the PICO XR plugin takes effect) |
| Carrying `PICOScreenFade` / `EnableSeeThroughManual` / `PICO Manager (Script)` over to the OpenXR route | `PXR_ScreenFade` / `EnableVideoSeeThrough`; see-through no longer needs the Manager component |
| `TryGetSupportedDisplayRefreshRates()`, `GetDisplayRefreshRateCount()` | `GetDisplayFrequenciesAvailable()` (the first two are deprecated) |
| Attaching `PXR_Manager` only to the main scene | Attach it to every scene, **including loading/splash scenes** |
| `Scripting Backend = Mono` or keeping `ARMv7` | `IL2CPP` + `ARM64` only (64-bit only since 3.1.0) |
| Leaving `Minimum API Level` at a default low version | ≥ `Android 10.0 (API Level 29)` |
| Package name `com.DefaultCompany.MyApp`, `com.vr.demo` | `com.<real company>.<product>`, avoiding the 8 banned prefixes |
| Handwriting `picovr.software.eye_tracking` / `com.picovr.permission.FACE_TRACKING` and the like | Tick the feature in the editor and let the SDK write them; only `pvr.app.type`, `pvr.display.orientation` and standard Android permissions need handwriting |
| Assuming a custom Manifest lives in `Assets/Android/` or the project root | After ticking `Custom Main Manifest`, it is always generated at **`Assets/Plugins/Android/AndroidManifest.xml`** |
| Looking for SDK resources under `Assets/PICO/…` | Look under **`Packages/PICO Integration/…`** (controller prefabs are in `Assets/Resources/Prefabs`) |
| Adding `XR Origin (VR)` to an AR Foundation scene | Add **`XR Origin (AR)`**, and add `AR Session` as needed |
| Copying `Bone Controller` / `AR Kit Blend Shape Visualizer` from `arfoundation-samples` verbatim | Replace them with `PXR_Bone Controller (Script)` / `PXR_Blend Shape Visualizer (Script)`, and replace `Human Body Tracking` with `PXR_Human Body Tracker (Script)` |
| Planning to use AR Foundation Image Tracking | **Not supported for now**; use another approach |
| Ticking Minify in the release build for size optimization | Do not tick it; it crashes the app |
| Ticking `Development Build` with Vulkan for debugging under Unity 2022 | Crashes; switch to OpenGLES or remove Development Build |
| Leaving multiple `Audio Listener`s / multiple `MainCamera` tags / multiple `XR Origin`s in the scene | Keep one of each, otherwise Project Validation Required reports an error |

## Developer Tools Index

| Tool | One-line purpose | URL |
|---|---|---|
| Developer tools overview | Overview of all developer tools PICO provides or supports (debugging, performance monitoring, interaction editing) | https://developer-cn.picoxr.com/document/unity/developer-tools-overview/ |
| PICO Developer Center quick start | Quick-start entry for the PDC tool | https://developer-cn.picoxr.com/document/unity/pdc-basic-info/ |
| Create and debug adb commands | Debug system default commands or create custom adb commands with PDC | https://developer-cn.picoxr.com/document/unity/create-and-debug-adb-commands/ |
| Preview scenes in real time | Preview in-app scenes on the headset in real time via streaming | https://developer-cn.picoxr.com/document/unity/preview-app-scenes/ |
| Monitor device performance | Monitor device performance metrics in real time on the PC and configure threshold alerts | https://developer-cn.picoxr.com/document/unity/monitor-device-performance/ |
| Screenshots, recording and casting | Screenshot / recording / casting quick tools in PDC | https://developer-cn.picoxr.com/document/unity/quick-tools/ |
| Push a URL to a PICO device | Push a specified URL to the device and open it in the PICO browser | https://developer-cn.picoxr.com/document/unity/push-url-to-pico-device/ |
| Download development resources | Get developer tools, SDKs and samples from the PDC download center | https://developer-cn.picoxr.com/document/unity/download-developer-tools/ |
| Install the streaming service | Install the streaming service to monitor performance data; on Windows you can configure the PICO Unity Live Preview Plugin | https://developer-cn.picoxr.com/document/unity/download-streaming-service/ |
| Manage files | View files on the PICO device and in PDC | https://developer-cn.picoxr.com/document/unity/manage-files/ |
| PDC troubleshooting guide | Common problems and fixes when using PDC | https://developer-cn.picoxr.com/document/unity/pdc-troubleshooting/ |
| SpatialMLCapture user guide | Record stereo RGB + depth + real-time pose multimodal data and save it as SpatialMP4 | https://developer-cn.picoxr.com/document/unity/spatialmlcapture/ |
| SpatialMP4 white paper | Specification for the MP4-based spatial video container format | https://developer-cn.picoxr.com/document/unity/spatialmp4-whitepaper/ |
| SpatialMLCapture service agreement (mainland China) | Terms of service for SpatialMLCapture | https://developer-cn.picoxr.com/document/unity/spatialmlcapture-terms-of-service/ |
| SpatialMLCapture privacy statement | Privacy statement for SpatialMLCapture | https://developer-cn.picoxr.com/document/unity/spatialmlcapture-privacy-policy/ |
| XR Profiling Toolkit | Unity package for automated graphics performance analysis, supporting feature toggles, data export and reports | https://developer-cn.picoxr.com/document/unity/xr-profiling-toolkit/ |
| Headset performance monitoring tool (Metrics HUD) | Monitor device performance metrics in real time on the headset | https://developer-cn.picoxr.com/document/unity/metrics-hud/ |
| RenderDoc for PICO | Graphics analysis and debugging tool | https://developer-cn.picoxr.com/document/unity/renderdoc-for-pico/ |
| CLI command-line tool | Manage files on the PICO developer platform from the command line | https://developer-cn.picoxr.com/document/unity/command-line-utility/ |
| PICO Haptic Editor | Edit wideband, multi-channel haptic feedback (supports .phf/.wav/.mp4 import) | https://developer-cn.picoxr.com/document/unity/pico-haptic-editor/ |
| PICO graphics analysis and debugging tool | Graphics-level analysis and debugging of app performance | https://developer-cn.picoxr.com/document/unity/pico-graphics-probe-tool/ |
| Snapdragon Profiler | Snapdragon Profiler; analyzes CPU/GPU/DSP/memory/power/network bottlenecks | https://developer-cn.picoxr.com/document/unity/242767/ |
| PICO Debugger | View logs and scene information, and use built-in tools for targeted optimization | https://developer-cn.picoxr.com/document/unity/pico-debugger/ |

## Official Sample Index

| Sample | One-line purpose | URL |
|---|---|---|
| Get Started demo | GetStartedDemo: indoor/outdoor scenes; experience view rotation, continuous movement, teleportation and scene switching | https://developer-cn.picoxr.com/document/unity/get-started-demo/ |
| Motion tracking sample | PICOMotionTrackerSample: full-body motion capture and independent tracking with motion trackers | https://developer-cn.picoxr.com/document/unity/pico-motion-tracking-sample/ |
| Interaction sample | InteractionSample: controller/gesture interaction, haptic feedback, keyboard input, dynamic movement | https://developer-cn.picoxr.com/document/unity/pico-interaction-sample/ |
| Spatial audio sample | PICOSpatialAudioSample: configure spatial audio parameters, move within the scene and play sound sources | https://developer-cn.picoxr.com/document/unity/spatial-audio-sample/ |
| Mixed reality sample | Video see-through, scene capture, spatial mesh, spatial anchors and shared spatial anchors | https://developer-cn.picoxr.com/document/unity/mixed-reality-sample/ |
| Toon World | Toon World: showcase of performance optimization with multi-view rendering + AppSW + late latching | https://developer-cn.picoxr.com/document/unity/toon-world/ |
| Space Arena | Space Arena Party: multiplayer social demo with friends, social interaction, and rooms & matchmaking | https://developer-cn.picoxr.com/document/unity/space-arena-party/ |
| MicroWar | MicroWar: comprehensive game sample combining the interaction kit and core platform services features | https://developer-cn.picoxr.com/document/unity/micro-war/ |
| Body tracking sample | Pico-Body-Tracking-Demo: sample project for body tracking features | https://developer-cn.picoxr.com/document/unity/body-tracking-demo/ |
| Adaptive resolution sample | AdaptiveResolutionSample: multi-room scene that automatically adjusts resolution based on GPU load | https://developer-cn.picoxr.com/document/unity/adaptive-resolution-demo/ |
| Platform services simple sample | SimpleDemo: initialize platform services, request user permissions, get signed-in user info | https://developer-cn.picoxr.com/document/unity/simple-demo/ |
| Account & friends sample | UserDemo: debug the "account & friends" service | https://developer-cn.picoxr.com/document/unity/user-demo/ |
| Real-time voice sample | RtcDemo: debug most functions of the RTC real-time voice service | https://developer-cn.picoxr.com/document/unity/rtc-demo/ |
| Rooms & matchmaking sample | RoomAndMatchmakingEntry / GameAPITest: debug the rooms and matchmaking services | https://developer-cn.picoxr.com/document/unity/room-and-matchmaking-demo/ |
| Leaderboard sample | GameAPITest: debug panel for all leaderboard service interfaces | https://developer-cn.picoxr.com/document/unity/leaderboard-demo/ |
| Achievement sample | GameAPITest: debug panel for all achievement service interfaces | https://developer-cn.picoxr.com/document/unity/achievement-demo/ |
| Challenge sample | Challenges Demo: debug the challenge service interfaces | https://developer-cn.picoxr.com/document/unity/challenge-demo/ |
| In-app purchase (IAP) sample | IAP Demo: all in-app purchase service interfaces and basic implementation principles | https://developer-cn.picoxr.com/document/unity/iap-demo/ |
| Downloadable content (DLC) sample | DLC Demo: basic usage of the DLC service | https://developer-cn.picoxr.com/document/unity/dlc-demo/ |
| Subscription sample | IAPDemo.cs: debug subscription features (requires ticking UseV2) | https://developer-cn.picoxr.com/document/unity/subscription-demo/ |
| Sports data authorization sample | SportCenter: debug the sports data authorization interfaces | https://developer-cn.picoxr.com/document/unity/sports-demo/ |
| Highlights sample | HighlightsDemo: screenshots, recording, cross-device sharing | https://developer-cn.picoxr.com/document/unity/highlights-demo/ |
| Speech-to-text sample | SpeechToTextDemo: initialize the ASR engine, start/stop speech recognition | https://developer-cn.picoxr.com/document/unity/speech-to-text-demo/ |
