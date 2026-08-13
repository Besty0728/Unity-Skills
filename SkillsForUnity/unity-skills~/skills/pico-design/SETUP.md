# PICO Unity SDK — 安装、项目配置与快速入门（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。

## 何时加载本文档

- 从零搭建 PICO 项目：装编辑器、导入 PICO Unity Integration SDK、建第一个 XR 场景、打出能在头显上跑的 APK。
- 在 **PICO XR（PXR_）传统路线** 与 **Unity OpenXR 插件路线** 之间做技术选型，或从旧的 PICO Unity OpenXR SDK 迁移。
- 配 Player Settings / XR Plug-in Management / Project Validation，或排查"构建失败、装上去黑屏、Activity 显示异常"。
- 处理 AndroidManifest 的 metadata 与权限（哪些 SDK 自动写、哪些必须手写）。
- 用 PICO Building Blocks 一键配置功能，或接入 AR Foundation（Session / Camera / Anchors / Meshing / Face / Body）。
- 核对设备型号、系统版本、Unity 版本与 SDK 版本的能力矩阵。

## 关键规则

| # | 规则 | 来源 |
|---|---|---|
| 1 | **两条 XR 路线互斥**：同时启用 PICO XR 插件和 Unity OpenXR 插件时，**只有 PICO XR 插件生效**；要走 OpenXR 路线必须"仅启用 Unity OpenXR 插件"。也不要勾选其他厂商的插件，否则应用无法在 PICO 设备上正常运行。 | [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) |
| 2 | Unity OpenXR 路线自 **SDK 3.3.0** 起才支持；启用后功能接入要查**另一套文档**《PICO Unity OpenXR SDK 开发指南》（`/document/unity-openxr/`），不能照搬本套 PXR_ 指南。 | [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) |
| 3 | 从 PICO Unity OpenXR SDK 1.4.0 及更低版本迁到 Integration SDK 3.3.0+ 的四处改名：`PICOScreenFade`→`PXR_ScreenFade`；`EnableSeeThroughManual`（已废弃）→`EnableVideoSeeThrough`；`TryGetSupportedDisplayRefreshRates` / `GetDisplayRefreshRateCount`（均已废弃）→`GetDisplayFrequenciesAvailable`；透视不再需要 `PICO Manager (Script)` 组件。 | [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) |
| 4 | 迁移时枚举/结构体命名空间**统一改为 `pxr`** 并迁到 `PXR_Type.cs`：`SecureContentFlag`、`BodyJointSet`、`BodyTrackingData`、`BodyTrackingDataInfo`、`BodyTrackingBoneLength`、`GeometryInstanceTransform`、`PassThroughStyle`。 | [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/) |
| 5 | **每个场景都要挂 `PXR_Manager`，包括画面加载场景**。Project Validation 把"当前打开的场景是否添加了 PXR_Manager (Script)"列为 Required，不满足直接报错。 | [about-pxr-manager](https://developer-cn.picoxr.com/document/unity/about-pxr-manager/) · [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 6 | 自 **3.1.0** 起 SDK **仅支持开发 64 位应用**：`Scripting Backend` 必须 `IL2CPP`、`Target Architectures` 必须勾 `ARM64` 并**取消勾选 `ARMv7`**。 | [about-pico-unity-integration-sdk](https://developer-cn.picoxr.com/document/unity/about-pico-unity-integration-sdk/) · [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) |
| 7 | `Minimum API Level` 必须 ≥ **Android 10.0 (API Level 29)**，低于会构建报错；`Target API Level` 用 `Automatic (highest installed)`（Recommended 规则也要求 targetSdkVersion=Auto）。 | [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) · [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 8 | Unity 编辑器**最低 2021.3.26，最高 Unity 6.3**，推荐 LTS。版本联动下限：Unity 2022 需 SDK ≥ 2.1.5 且 OS ≥ 5.5.0；Unity 2023 需 SDK ≥ 3.0.0 且 OS ≥ 5.11.0；Unity 6 需 SDK ≥ 3.1.0 且 OS ≥ 5.12.0。 | [hardware-and-software-requirements](https://developer-cn.picoxr.com/document/unity/hardware-and-software-requirements/) |
| 9 | `Graphics API` 必须是 **Vulkan 或 OpenGLES3.0 之一**；用 ETFR（眼动追踪注视点渲染）时列表**第一个必须是 OpenGLES3.0**。 | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 10 | **Unity 2022 + Vulkan + 勾选 `Development Build` 会崩溃**（Validation 的 Required 规则会直接报错）；**Unity 2022.1.14+ 同时用 URP + Linear + 4xMSAA + OpenGL 也会崩溃**。 | [hardware-and-software-requirements](https://developer-cn.picoxr.com/document/unity/hardware-and-software-requirements/) · [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 11 | 包名格式固定 `com.companyName.productName`，且 PICO SDK **不支持** `com.epicgames.xxx`、`com.unity.xxxx`、`com.test.xxx`、`com.DefaultCompany.xxx`、`com.Unity-Technologies.xxx`、`com.vr.xxx`、`com.xr.xxx`、`com.UnityTechnologies.xxx`——用了无法通过商店上架申请。 | [naming-conventions-for-app-package-name](https://developer-cn.picoxr.com/document/unity/naming-conventions-for-app-package-name/) |
| 12 | AndroidManifest 里有两条 metadata **必须手动添加**，否则 Activity 显示异常：`pvr.app.type = vr`、`pvr.display.orientation = 180`。 | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) |
| 13 | 眼动/面部追踪的 metadata 与权限（`picovr.software.eye_tracking`、`com.picovr.permission.EYE_TRACKING`、`picovr.software.face_tracking`、`com.picovr.permission.FACE_TRACKING`、`android.permission.RECORD_AUDIO`）由 SDK **在启用功能后自动写入**，"若非必要请不要修改"；但**用自定义 Manifest 时要按功能自行补齐**。 | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) |
| 14 | 安卓标准权限（读外部存储、蓝牙、互联网、震动、写入配置、更改配置）**必须手写**进 AndroidManifest.xml，SDK 不代写。 | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) |
| 15 | 自定义清单路径唯一：`Edit > Project Settings > Player > Publishing Settings > Build` 勾 **`Custom Main Manifest`** 后，文件生成在 **`Assets/Plugins/Android/AndroidManifest.xml`**。 | [android-manifest](https://developer-cn.picoxr.com/document/unity/android-manifest/) · [complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) |
| 16 | Project Validation 需 **SDK ≥ 3.0.0**，入口 `Edit > Project Settings > XR Plug-in Management > Project Validation`；`Selected Profiles` 保持默认 `Turn Off`。Required 未修复会影响构建；实在要放行可勾 `Ignore build errors`，但相关 XR 功能可能不可用。 | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 17 | 场景唯一性 Required 三连：场景中**只能有一个 `MainCamera` Tag、一个 `Audio Listener`、一个 `XR Origin`**。另外 Keystore 的 `keystoreName`/`keystorePass` 与 Key 的 `keyaliasName`/`keyaliasPass` 不能为空，`Default Orientation` 必须 `LandscapeLeft`，`Application Entry Point` 必须 `Activity`。 | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 18 | 合成层数量：**最大 7 个**（超过 Required 报错），**建议不超过 4 个**（Recommended）。 | [project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) |
| 19 | AR Foundation 必须先在 `Edit > Project Settings > XR Plug-in Management > PICO > Android Settings` 勾 **`AR Foundation`**，再按需勾 `Body Tracking` / `Face Tracking` / `Anchor` / `Meshing` 开权限。环境绑定：SDK 3.0.0 & 3.0.5 → Unity 2022.3 + AR Foundation 5.1；SDK 3.1.0 → Unity 6 + AR Foundation 6.0。**暂不支持图片追踪（Image Tracking）**。 | [ar-foundation](https://developer-cn.picoxr.com/document/unity/ar-foundation-for-pico-unity-integration-sdk/) |
| 20 | 发布设置里**启用 Minify 相关选项会导致应用崩溃**；PICO 4 系列与"Unity 6 + Vulkan"存在兼容性问题，推荐 Unity 2022 LTS 或更低。 | [known-issues](https://developer-cn.picoxr.com/document/unity/known-issues/) |

### 文档内部口径冲突（照抄前先确认）

- **XR Interaction Toolkit 版本**：《创建一个 XR 场景》写"PICO Unity Integration SDK 暂未支持 3.x.x 版本的 XR Interaction Toolkit"（其手动配置步骤基于 2.x 的 `XR Controller (Action-Based)` + 预设）；而《PICO Building Blocks》写"XR Interaction Toolkit 版本：3.x.x"，且 SDK 3.1.0+ 的 Building Blocks 就是基于 XRI 3.x.x 开发的。写代码前按项目实际装的 XRI 版本分支处理。来源：[create-an-xr-scene](https://developer-cn.picoxr.com/document/unity/create-an-xr-scene/) vs [pico-building-blocks](https://developer-cn.picoxr.com/document/unity/pico-building-blocks/)
- **最低 Unity 版本**：《硬件和软件要求》写 2021.3.26；PICO XR Portal 的 Information 面板写"Unity 2020.3.21 及以上"。以前者为准。来源：[hardware-and-software-requirements](https://developer-cn.picoxr.com/document/unity/hardware-and-software-requirements/) vs [pico-xr-portal](https://developer-cn.picoxr.com/document/unity/pico-xr-portal/)

## 工作流程

### A. 六步快速入门（PXR_ 传统路线，从零到 APK）

1. **建帐号与应用**：注册 PICO 开发者帐号 → 创建组织（组织名即商店发行商）→ 创建应用，发布平台选 **6 DOF 平台（推荐）**。仅体验开发流程可跳过资质认证；要走分发必须完成。[create-a-developer-account-organization-and-app](https://developer-cn.picoxr.com/document/unity/create-a-developer-account-organization-and-app/)
2. **配环境**：设备 `设置 > 通用 > 关于本机`（PICO 4 Ultra 为 `控制中心 > 设置 > 关于本机`）连点 **软件版本号** 开出"开发者"选项 → 打开 **USB 调试开关**；非中国大陆版设备需在头戴上登录 PICO 帐号。装 Unity 时务必勾 **Android Build Support 下所有选项**（含 `Android SDK & NDK Tools`、`OpenJDK`）。[set-up-the-development-environment](https://developer-cn.picoxr.com/document/unity/set-up-the-development-environment/)
3. **导入 SDK**：Unity Hub 建 **核心模板 > 3D** 项目（**项目名和路径不能含中文**）→ `Window > Package Manager > + > Add package from disk` 选 `package.json`，或 `Add package from git URL` 填仓库 HTTPS 地址。导入后会弹 **PXR SDK Setting** 窗口，关闭即可。[import-the-sdk](https://developer-cn.picoxr.com/document/unity/import-the-sdk/)
4. **项目配置**：`Edit > Project Settings > XR Plug-in Management > 安卓页签` 勾 **PICO** → `Player` 设 `Company Name`/`Product Name`/`Version` → Android 页签 `Other Settings`：`Minimum API Level = Android 10.0 (API Level 29)`、`Target API Level = Automatic (highest installed)`、`Scripting Backend = IL2CPP`、`Target Architectures = ARM64`（去掉 ARMv7）→ 从开发者平台应用的 **API 测试** 页拿 **APP ID**，填进 `PICO > Platform Settings` 并点应用。[complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/)
5. **搭 XR 场景**：Package Manager 里给 **XR Interaction Toolkit** 导入 `Starter Assets` 和 `XR Device Simulator` 两个 Sample → 删掉默认 `Main Camera`，`+ > XR > XR Origin (VR)` → 给 `XR Origin` `Add Component` 加 **`PXR_Manager`** → 加 `Plane` 当地面（Scale 10,1,10）→ 手柄：XRI 2.x 走 `XR Controller (Action-Based)` 预设 `XRI Default Left Controller` + `Model Prefab` 指向 `Packages > PICO Integration > Assets > Resources > Prefabs` 下的 `LeftControllerModel`/`RightControllerModel`；XRI 3.x 直接用 PICO Building Blocks 的 `PICO Controller Tracking` → 给 `XR Origin` 加 **`Input Action Manager`**，`Action Assets > Element 0` 设为 `XRI Default Input Actions`。[create-an-xr-scene](https://developer-cn.picoxr.com/document/unity/create-an-xr-scene/)
6. **打包运行**：`File > Build Settings > Platform = Android > Switch Platform` → 设备登录帐号并 USB 连 PC → `Add Open Scenes` → `Run Device` 选 `All compatible devices` 或具体机型 → `Build And Run`。[build-and-run-the-scene](https://developer-cn.picoxr.com/document/unity/build-and-run-the-scene/)

### B. 切换到 Unity OpenXR 插件路线

1. 项目内先导入 **PICO Unity Integration SDK 3.3.0 及更高版本**。
2. `Window > Package Manager > + > Add package by name`，填 **`com.unity.xr.openxr`** → `Add`；完成后 `Project Settings > XR Plug-in Management` 下出现 **OpenXR**。
3. **取消勾选 PICO XR 插件**，只保留 Unity OpenXR 插件（否则 PICO XR 插件会抢先生效）。
4. 按上表规则 3/4 做接口与命名空间适配，随后功能接入改查《PICO Unity OpenXR SDK 开发指南》。
   来源：[complete-project-settings](https://developer-cn.picoxr.com/document/unity/complete-project-settings/) · [support-for-the-unity-openxr-plugin](https://developer-cn.picoxr.com/document/unity/support-for-the-unity-openxr-plugin/)

### C. Project Validation 修复回路（构建失败第一站）

1. `Edit > Project Settings > XR Plug-in Management > Project Validation`，默认只列未正确配置项；勾 `Show all` 可看全部。
2. 看图标分级：**绿勾**=已配置或不适用；**黄叹号**=Recommended，不影响构建，可按项目实际忽略；**红叉**=Required，会影响构建，必须修。
3. 点 `Fix All` 一键修，或逐条 `Fix`。
4. 也可以走 `PICO > Portal > Configs`：面板上用 `To Apply` / `To Apply All` 修复，并可跳转到 **`PXR_ProjectSetting.asset`** 直接改项目设置（Portal 需 SDK ≥ 3.2.0）。
   来源：[project-validation](https://developer-cn.picoxr.com/document/unity/project-validation/) · [pico-xr-portal](https://developer-cn.picoxr.com/document/unity/pico-xr-portal/)

### D. 用 PICO Building Blocks 一键配置

- **入口三选一**：Scene/Game 视图右上角 `··· > Overlay Menu > XR Building Blocks`（Unity 2022/2023）；`Hierarchy > + > PICO Building Blocks`；`GameObject > PICO Building Blocks`（后两者 Unity 2020–2023）。
- **共同副作用**：几乎每个 Block 都会检查场景内有无 `XR Origin`，没有就自动生成 `[Building Block] XR Origin (XR Rig)`，并给它挂 **`PXR_Manager (Script)`**、按功能勾上对应选框（如 `Hand Tracking`、`Video Seethrough`、`Body Tracking`、`Spatial Anchor`、`Scene Capture`、`Spatial Mesh`）。
- **XRI 3.x.x 带来的四个变更**：① `XR Origin` 不再包含左右手柄，需用 `/Assets/XR Interaction Toolkit/{version}/prefabs` 下的 **`XR Origin (XR Rig).prefab`** 替换旧 `XR Origin`；② `XRI DefaultInputActions.inputactions` 的 Action Map 去掉了 "Hand" 字样（`XRI LeftHand`→`XRI Left`，`XRI LeftHand Interaction`→`XRI Left Interaction`，`XRI LeftHand Locomotion`→`XRI Left Locomotion`，右手同理）；③ 手势相关主相机 `XR Interaction Hands Setup` 换成 **`XR Origin Hands (XR Rig)`**；④ 用 Canvas 与手柄交互时**需隐藏 `EventSystem`**。
  来源：[pico-building-blocks](https://developer-cn.picoxr.com/document/unity/pico-building-blocks/)

### E. 接入 AR Foundation

1. `Edit > Project Settings > XR Plug-in Management > PICO > Android Settings` 勾 `AR Foundation` 及所需功能权限。
2. 场景里用 **`+ > XR > XR Origin (AR)`**（加进来的对象名为 `XR Origin (XR Rig)`），需要会话时再加 `+ > XR > AR Session`。
3. 启用后 AR Foundation 会自动把 `camera.backgroundColor` 设为 `new Color(0, 0, 0, 0)`，并给 `XR Origin (XR Rig)` 下的 `Main Camera` 挂 **`PXR_AR Camera Effect Manager (Script)`**。
4. 各功能的额外硬件门槛：相机 = PICO 4 系列/4 Ultra 系列 + OS ≥ 5.12.0；全身动捕 = Neo3/4/4 Ultra 系列 + OS ≥ 5.12.0 + PICO 体感追踪器（正式版）；面部追踪 = 搭载面部追踪相机的 PICO 4 Enterprise + OS ≥ 5.12.0；锚点 = PICO 4 系列 + OS ≥ 5.12.0；网格 = PICO 4 Ultra 系列 + OS ≥ 5.12.0。
5. 适配官方 `arfoundation-samples` 时的组件替换：`Bone Controller (Script)` → **`PXR_Bone Controller (Script)`**；`AR Kit Blend Shape Visualizer (Script)` → **`PXR_Blend Shape Visualizer (Script)`**；`Human Body Tracking` 对象上的组件 → **`PXR_Human Body Tracker (Script)`**（并在 `AR Human Body Manager (Script)` 上**取消勾选 `Pose 2D`**）。
   来源：[ar-foundation](https://developer-cn.picoxr.com/document/unity/ar-foundation-for-pico-unity-integration-sdk/)

## 核心 API 锚点

### SDK 目录与获取

- 导入后位于 **`Packages/PICO Integration`**，五个子目录：`Assets`（手柄/手部资产、预制体、Shader）、`Editor`（含 `PXR_BuildProcessor`、`PXR_MetaData`、`PXR_OverLayEditor`）、`Platform`、`Runtime`、`SpatialAudio`。
- 获取途径：PICO 开发者官网资源页；GitHub 仓库 **`https://github.com/Pico-Developer/PICO-Unity-XR-SDK`**（文中称"PICO-Unity-Integration-SDK 仓库"，但仓库名是 `PICO-Unity-XR-SDK`）。
- AR Foundation 官方适配示例仓库：**`https://github.com/Pico-Developer/PICOARFoundationSamples-Unity`**（适配场景在 `Assets/Scenes/PICO` 下）。

### 组件 / 脚本（PICO 侧）

| 名称 | 说明 |
|---|---|
| `PXR_Manager` / `PXR_Manager (Script)` | 每个场景必挂的总管组件；面板集中管理 Home 键校准、`Open Screen Fade`、ETFR、FFR、Eye/Face/Hand/Body Tracking、`Use Content Protect`、`MRC`、Late Latching、`Use Recommended MSAA`、Adaptive Resolution、Video Seethrough、Spatial Anchor |
| `PXR_ScreenFade` | 勾选 `Open Screen Fade` 后由 `PXR_Manager` 自动挂到 GameObject 上 |
| `PXR_ProjectSetting.asset` | 项目设置资产，可从 PICO XR Portal 的 `Open PICO XR Project Setting` 跳转 |
| `PXR_Composition Layer (Script)` | Building Blocks 配置 Overlay / Underlay 时挂载的合成层组件 |
| `PXR_Scene Capture Manager (Script)` | 场景标定 Block 挂载，配 `Box 2D Prefab` / `Box 3D Prefab` |
| `PXR_BodyTrackingBlock.cs` / `PXR_ObjectTrackingBlock.cs` | 全身动捕 / 独立追踪 Block 的实现脚本 |
| `PXR_AR Camera Effect Manager (Script)` | AR Foundation 启用后自动挂到 `Main Camera`，配透视特效与 LUT |
| `PXR_Human Body Tracker (Script)` | AR Foundation 全身动捕组件，配 `Skeleton Prefab` 与 `Human Body Manager` |
| `PXR_Bone Controller (Script)` | 驱动 Avatar 骨骼，枚举值名需与模型节点名匹配 |
| `PXR_Blend Shape Visualizer (Script)` | AR Foundation 面部追踪的 BlendShape 可视化组件 |
| 脚本文件 | `PXR_BoneController.cs`、`PXR_HumanBodyTracker.cs`、`PXR_BlendShapeVisualizer.cs` |
| Shader | `PXR_SDK/PXR_UnderlayHole`（Underlay 打洞） |
| 预制体 | `LeftControllerModel`、`RightControllerModel`、`BodyTracking.prefab`、`BodyTrackingDebug.prefab` |

### 接口 / 枚举（本域出现的原样写法）

```csharp
PXR_Manager.EnableVideoSeeThrough = true;                                 // 开启视频透视
PXR_MixedReality.EnableVideoSeeThroughEffect(true);                       // 开启视频透视特效
PXR_MixedReality.SetVideoSeeThroughEffect(PxrLayerEffect.Colortemp, x, 0);// 色温/亮度/饱和度/对比度
PXR_MixedReality.SetVideoSeeThroughLut(lutTex, row, col);                 // LUT 纹理行列数
```

- `PxrLayerEffect`：`Colortemp`、`Brightness`、`Saturation`、`Contrast`
- `BodyTrackerRole`（24 值，AR Foundation 页给出完整定义）：`Root`(0)、`LeftUpLeg`、`RightUpLeg`、`Spine3`、`LeftLeg`、`RightLeg`、`Spine6`、`LeftFoot`、`RightFoot`、`Spine7`、`LeftToes`、`RightToes`、`Neck1`、`LeftShoulder1`、`RightShoulder1`、`Neck4`、`LeftArm`、`RightArm`、`LeftForearm`、`RightForearm`、`LeftHand`、`RightHand`、`LeftHandMid1`、`RightHandMid1`(23)
- `BlendShapeIndex`：面部 52 个 BlendShape 的索引枚举（如 `EyeLookDown_L`、`JawOpen`、`TongueOut`）
- `GetDisplayFrequenciesAvailable`：查询设备支持的所有屏幕刷新率（替代两个已废弃接口）

### Unity 侧对象 / 组件（PICO 文档中引用的原名）

`XR Origin (VR)`、`XR Origin (AR)`、`XR Origin (XR Rig)`、`XR Origin Hands (XR Rig)`、`Camera Offset`、`Left Controller` / `Right Controller`、`XR Controller (Action-Based)`、`Input Action Manager`、`XR Interaction Manager`、`Tracker Device Graphic Raycast`（PICO Building Blocks 页的写法；Unity 实际组件名见《创建可交互 UI》页的 `Tracked Device Graphic Raycaster`）、`AR Session`、`AR Human Body Manager (Script)`、`AR Face Manager (Script)`、`AR Session (Script)`、`AR Anchor Manager`、`AR Mesh Manager (Script)`、`Starter Assets`、`XR Device Simulator`、`XRI Default Input Actions`、`XRI Default Left Controller`

### 菜单路径

`Edit > Project Settings > XR Plug-in Management`｜`… > Project Validation`｜`… > PICO > Android Settings`｜`Edit > Project Settings > Player > Publishing Settings > Build`｜`Window > Package Manager`｜`File > Build Settings`｜`PICO > Platform Settings`｜`PICO > Portal`｜`GameObject > PICO Building Blocks`｜`Hierarchy > + > XR > XR Origin (VR) / XR Origin (AR) / AR Session`

## DO NOT

| ❌ 错误写法 | ✅ 正确写法 |
|---|---|
| XR Plug-in Management 里同时勾 `PICO` 和 `OpenXR`（以为能双跑） | 二选一；走 OpenXR 就**只**勾 Unity OpenXR 插件（同时勾只有 PICO XR 插件生效） |
| OpenXR 路线沿用 `PICOScreenFade` / `EnableSeeThroughManual` / `PICO Manager (Script)` | `PXR_ScreenFade` / `EnableVideoSeeThrough`；透视不再需要 Manager 组件 |
| `TryGetSupportedDisplayRefreshRates()`、`GetDisplayRefreshRateCount()` | `GetDisplayFrequenciesAvailable()`（前两者已废弃） |
| 只在主场景挂 `PXR_Manager` | 每个场景都挂，**包括画面加载场景** |
| `Scripting Backend = Mono` 或保留 `ARMv7` | `IL2CPP` + 仅 `ARM64`（3.1.0 起只支持 64 位） |
| `Minimum API Level` 用默认低版本 | ≥ `Android 10.0 (API Level 29)` |
| 包名写 `com.DefaultCompany.MyApp`、`com.vr.demo` | `com.<真实公司>.<产品>`，避开 8 个被禁前缀 |
| 手写 `picovr.software.eye_tracking` / `com.picovr.permission.FACE_TRACKING` 之类 | 在编辑器里勾功能让 SDK 自动写；只有 `pvr.app.type`、`pvr.display.orientation` 和安卓标准权限需手写 |
| 以为自定义 Manifest 在 `Assets/Android/` 或项目根 | 勾 `Custom Main Manifest` 后固定生成在 **`Assets/Plugins/Android/AndroidManifest.xml`** |
| 找 SDK 资源时去 `Assets/PICO/…` | 在 **`Packages/PICO Integration/…`**（手柄预制体在 `Assets/Resources/Prefabs`） |
| AR Foundation 场景里加 `XR Origin (VR)` | 加 **`XR Origin (AR)`**，并按需加 `AR Session` |
| 直接照抄 `arfoundation-samples` 的 `Bone Controller` / `AR Kit Blend Shape Visualizer` | 换成 `PXR_Bone Controller (Script)` / `PXR_Blend Shape Visualizer (Script)`，`Human Body Tracking` 换 `PXR_Human Body Tracker (Script)` |
| 计划用 AR Foundation 的 Image Tracking | **暂不支持**，换其它方案 |
| 发布包勾 Minify 做体积优化 | 不要勾，会导致应用崩溃 |
| Unity 2022 下勾 `Development Build` 配 Vulkan 调试 | 会崩溃；换 OpenGLES 或去掉 Development Build |
| 场景里留多个 `Audio Listener` / 多个 `MainCamera` Tag / 多个 `XR Origin` | 各保留一个，否则 Project Validation Required 报错 |

## 开发者工具索引

| 工具 | 一句话用途 | URL |
|---|---|---|
| 开发者工具概览 | PICO 提供或支持的全部开发者工具总览（调试、性能监测、交互编辑） | https://developer-cn.picoxr.com/document/unity/developer-tools-overview/ |
| PICO 开发者中心快速开始 | PDC 工具的快速上手入口 | https://developer-cn.picoxr.com/document/unity/pdc-basic-info/ |
| 创建和调试 adb 命令 | 用 PDC 调试系统默认命令或创建自定义 adb 命令 | https://developer-cn.picoxr.com/document/unity/create-and-debug-adb-commands/ |
| 实时预览场景 | 基于串流在头戴上实时预览应用内场景 | https://developer-cn.picoxr.com/document/unity/preview-app-scenes/ |
| 监测设备性能 | PC 端实时监测设备性能指标并配置阈值预警 | https://developer-cn.picoxr.com/document/unity/monitor-device-performance/ |
| 截屏、录屏与投屏 | PDC 的截屏/录屏/投屏快捷工具 | https://developer-cn.picoxr.com/document/unity/quick-tools/ |
| 推送 URL 到 PICO 设备 | 把指定 URL 推到设备并用 PICO 浏览器打开 | https://developer-cn.picoxr.com/document/unity/push-url-to-pico-device/ |
| 下载开发资源 | 在 PDC 下载中心获取开发者工具、SDK 和示例 | https://developer-cn.picoxr.com/document/unity/download-developer-tools/ |
| 安装串流服务 | 装串流服务以监控性能数据，Windows 可配 PICO Unity Live Preview Plugin | https://developer-cn.picoxr.com/document/unity/download-streaming-service/ |
| 管理文件 | 查看 PICO 设备与 PDC 中的文件 | https://developer-cn.picoxr.com/document/unity/manage-files/ |
| PDC 问题排查指南 | PDC 使用中常见问题及解决方法 | https://developer-cn.picoxr.com/document/unity/pdc-troubleshooting/ |
| SpatialMLCapture 使用指南 | 录制立体 RGB+深度+实时位姿的多模态数据并存为 SpatialMP4 | https://developer-cn.picoxr.com/document/unity/spatialmlcapture/ |
| SpatialMP4 白皮书 | 基于 MP4 的空间视频容器格式规范 | https://developer-cn.picoxr.com/document/unity/spatialmp4-whitepaper/ |
| SpatialMLCapture 服务协议（中国大陆） | SpatialMLCapture 的服务条款 | https://developer-cn.picoxr.com/document/unity/spatialmlcapture-terms-of-service/ |
| SpatialMLCapture 隐私声明 | SpatialMLCapture 的隐私声明 | https://developer-cn.picoxr.com/document/unity/spatialmlcapture-privacy-policy/ |
| XR Profiling Toolkit | 自动化图形性能分析 Unity 包，支持特性切换、数据导出与报告 | https://developer-cn.picoxr.com/document/unity/xr-profiling-toolkit/ |
| 头戴端性能监测工具 (Metrics HUD) | 在头戴端实时监测设备性能指标 | https://developer-cn.picoxr.com/document/unity/metrics-hud/ |
| RenderDoc for PICO | 图像分析和调试工具 | https://developer-cn.picoxr.com/document/unity/renderdoc-for-pico/ |
| CLI 命令行工具 | 命令行管理 PICO 开发者平台的文件 | https://developer-cn.picoxr.com/document/unity/command-line-utility/ |
| PICO 触觉编辑器 | 编辑宽频、多通道触觉反馈（支持 .phf/.wav/.mp4 导入） | https://developer-cn.picoxr.com/document/unity/pico-haptic-editor/ |
| PICO 图像分析和调试工具 | 对应用性能进行图形层面的分析和调试 | https://developer-cn.picoxr.com/document/unity/pico-graphics-probe-tool/ |
| 骁龙分析器 | Snapdragon Profiler，分析 CPU/GPU/DSP/内存/功耗/网络瓶颈 | https://developer-cn.picoxr.com/document/unity/242767/ |
| PICO Debugger | 查看日志与场景信息，并用内置工具做针对性优化 | https://developer-cn.picoxr.com/document/unity/pico-debugger/ |

## 官方示例索引

| 示例 | 一句话用途 | URL |
|---|---|---|
| 入门示例 | GetStartedDemo：室内/室外场景，体验视角转动、连续运动、传送与场景切换 | https://developer-cn.picoxr.com/document/unity/get-started-demo/ |
| 运动追踪示例 | PICOMotionTrackerSample：体感追踪器的全身动捕与独立追踪 | https://developer-cn.picoxr.com/document/unity/pico-motion-tracking-sample/ |
| 交互示例 | InteractionSample：手柄/手势交互、触觉反馈、键盘输入、动态运动 | https://developer-cn.picoxr.com/document/unity/pico-interaction-sample/ |
| 空间音频示例 | PICOSpatialAudioSample：配置空间音频参数、场景内移动与播放声源 | https://developer-cn.picoxr.com/document/unity/spatial-audio-sample/ |
| 混合现实示例 | 视频透视、场景标定、空间网格、空间锚点与共享空间锚点 | https://developer-cn.picoxr.com/document/unity/mixed-reality-sample/ |
| 卡通世界 | Toon World：多视图渲染 + AppSW + 延迟锁定的性能优化展示 | https://developer-cn.picoxr.com/document/unity/toon-world/ |
| 太空竞技场 | Space Arena Party：好友、社交互动、房间&匹配的多人社交 Demo | https://developer-cn.picoxr.com/document/unity/space-arena-party/ |
| 迷你战争 | MicroWar：交互套件 + 平台服务核心功能的综合游戏示例 | https://developer-cn.picoxr.com/document/unity/micro-war/ |
| 人体追踪示例 | Pico-Body-Tracking-Demo：人体追踪功能示例项目 | https://developer-cn.picoxr.com/document/unity/body-tracking-demo/ |
| 自适应分辨率示例 | AdaptiveResolutionSample：按 GPU 负载自动调分辨率的多房间场景 | https://developer-cn.picoxr.com/document/unity/adaptive-resolution-demo/ |
| 平台服务简单示例 | SimpleDemo：初始化平台服务、申请用户权限、获取登录用户信息 | https://developer-cn.picoxr.com/document/unity/simple-demo/ |
| 帐号&好友示例 | UserDemo：调试"帐号&好友"服务 | https://developer-cn.picoxr.com/document/unity/user-demo/ |
| 实时语音示例 | RtcDemo：调试 RTC 实时语音服务的大部分功能 | https://developer-cn.picoxr.com/document/unity/rtc-demo/ |
| 房间&匹配示例 | RoomAndMatchmakingEntry / GameAPITest：房间与匹配服务调试 | https://developer-cn.picoxr.com/document/unity/room-and-matchmaking-demo/ |
| 排行榜示例 | GameAPITest：排行榜服务全部接口的调试面板 | https://developer-cn.picoxr.com/document/unity/leaderboard-demo/ |
| 成就示例 | GameAPITest：成就服务全部接口的调试面板 | https://developer-cn.picoxr.com/document/unity/achievement-demo/ |
| 挑战示例 | Challenges Demo：调试"挑战"服务接口 | https://developer-cn.picoxr.com/document/unity/challenge-demo/ |
| 应用内购（IAP）示例 | IAP Demo：内购服务全部接口与基本实现原理 | https://developer-cn.picoxr.com/document/unity/iap-demo/ |
| 可下载内容（DLC）示例 | DLC Demo：DLC 服务的基本用法 | https://developer-cn.picoxr.com/document/unity/dlc-demo/ |
| 订阅示例 | IAPDemo.cs：订阅功能调试（需勾选 UseV2） | https://developer-cn.picoxr.com/document/unity/subscription-demo/ |
| 运动数据授权示例 | SportCenter：调试运动数据授权接口 | https://developer-cn.picoxr.com/document/unity/sports-demo/ |
| 高光时刻示例 | HighlightsDemo：截屏、录屏、跨端分享 | https://developer-cn.picoxr.com/document/unity/highlights-demo/ |
| 语音转文字示例 | SpeechToTextDemo：初始化 ASR 引擎、开始/停止语音识别 | https://developer-cn.picoxr.com/document/unity/speech-to-text-demo/ |
