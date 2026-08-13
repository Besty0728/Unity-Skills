# PICO Unity SDK — 混合现实与环境感知（v3.4.0）

> 锚定：PICO 官方文档 v3.4.0（2026-08-13 抓取）。规则均可通过来源 URL 复核。

## 何时加载本文档

- 要在 PICO 上做 MR/AR 效果：开启视频透视（Video Seethrough）、把虚拟物体叠加到现实画面上。
- 要用空间锚点（Spatial Anchor）把虚拟物体钉在现实位置，或做多人共享空间锚点（Shared Spatial Anchor）。
- 要读取现实环境几何：场景标定（Scene Capture / 房间标定）、空间网格（Spatial Mesh）、平面检测（Plane Detection）。
- 要拿到头显摄像头的原始图像（用户设备 `PXR_CameraImage` 或企业设备 `PXR_Enterprise` 两条完全不同的路线）。
- 要配置 MR 安全防护（MR Safeguard）、混合现实捕捉（MRC），或排查半透明物体在 MR 下的渲染异常。
- 要把 2.5.0 及更早版本的 MR 接口迁移到 3.x。

## 关键规则

| # | 规则 | 来源 |
|---|---|---|
| 1 | 环境感知套件（Sense Pack）**仅支持开发 64 位应用**。 | [sense-pack-overview](https://developer-cn.picoxr.com/document/unity/sense-pack-overview/) |
| 2 | 视频透视要求**禁用场景内所有后处理**；若使用 Vulkan + 渲染管线（URP 或 Built-in），还必须**禁用 HDR**。任一条件不满足透视都不生效。 | [seethrough](https://developer-cn.picoxr.com/document/unity/seethrough/) |
| 3 | 视频透视要求主相机 **Clear Flags = Solid Color**，且 **Background 的 R/G/B/A 全设为 0**（Hexadecimal `000000`）。 | [seethrough](https://developer-cn.picoxr.com/document/unity/seethrough/) |
| 4 | `PXR_Manager.EnableVideoSeeThrough` 赋值后生效/失效**有延迟**；需要精确状态必须监听 `PXR_Manager.VstDisplayStatusChanged`（`PxrVstStatus`：`Disabled`/`Enabling`/`Enabled`/`Disabling`）。 | [seethrough](https://developer-cn.picoxr.com/document/unity/seethrough/) |
| 5 | 空间锚点、场景标定、平面检测在调用任何其它接口前必须先 `StartSenseDataProvider(PxrSenseDataProviderType.…)`，用完调 `StopSenseDataProvider`。状态可用 `GetSenseDataProviderState` 复查。 | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) · [plane-detection](https://developer-cn.picoxr.com/document/unity/plane-detection/) |
| 6 | **Handle 不是永久的，应用重启后就会变更**。跨会话唯一标识只能用 UUID（`GetAnchorUuid`）。 | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 7 | 删除锚点的顺序是**先 `UnPersistSpatialAnchorAsync`，再 `DestroyAnchor`**。反过来会先删掉 handle，导致系统再也找不到磁盘上的那个锚点。 | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 8 | `QuerySpatialAnchorAsync` 和 `UnPersistSpatialAnchorAsync` **都不支持并发调用**，必须等上一次结束。且 `QuerySpatialAnchorAsync` **仅能加载本应用创建的锚点**。 | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 9 | 锚点建议放在用户头戴 **3 米范围内**；找回范围与放置后的观察范围相关，**半径最大不超过 5 米**，超出且附近无其它锚点则可能找不回。 | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 10 | `LocateAnchor` 对**空间锚点**是实时的（建议约 1 秒一次，最高每帧）；但对**场景锚点**，只有再次调用 `QuerySceneAnchorAsync` 后返回的位姿才会更新，否则永远是旧值。 | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 11 | 共享空间锚点前置条件缺一不可：项目内配置 AppID、初始化平台服务、调用 `GetLoggedInUser` 或 `GetAccessToken` 触发登录、用户已登录 PICO 帐号。缺失会返回 `ERROR_ANCHOR_SHARING_AUTHENTICATION_FAILURE (-602)`。 | [shared-spatial-anchors](https://developer-cn.picoxr.com/document/unity/shared-spatial-anchors/) · [troubleshooting](https://developer-cn.picoxr.com/document/unity/mixed-reality-troubleshooting/) |
| 12 | 下载方**无法持久化或去持久化**共享锚点；上传方也无法主动去持久化云端锚点。PICO 云端会自动去持久化**最近一次活跃起 7 天内不活跃**的锚点。 | [shared-spatial-anchors](https://developer-cn.picoxr.com/document/unity/shared-spatial-anchors/) |
| 13 | 场景标定前提：**XR Origin 对象和 Camera Offset 对象的 Position、Rotation 都必须为 (0,0,0)**，并已开启视频透视。 | [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 14 | 场景锚点属于 PICO 系统、**只读**：仅“房间标定”应用可修改，三方应用只能读；应用**无法创建空间**，只能在“房间标定”创建的空间内添加空间锚点。对场景锚点 handle 调 `DestroyAnchor` 会提示 “Invalid handle”。 | [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) · [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 15 | `QuerySceneAnchorAsync` 只返回 handle 列表，**不会为每个锚点匹配语义标签**；必须逐个 `GetSceneSemanticLabel`，再按语义分派到 `GetSceneBox2DData` / `GetSceneBox3DData` / `GetScenePolygonData`。 | [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 16 | 在 `PXR_Manager (Script)` 上勾选 **Spatial Anchor / Shared Spatial Anchor / Scene Capture / Spatial Mesh** 任一项，SDK 会自动写入 `com.picovr.permission.SPATIAL_DATA` 权限。用户可拒绝或事后撤销，应用**必须提供降级方案**（淡出依赖内容或改用头戴/手柄输入），否则遇到 `ERROR_PERMISSION_INSUFFICIENT (-1000710000)`。 | [spatial-data-permission-control](https://developer-cn.picoxr.com/document/unity/spatial-data-permission-control/) · [troubleshooting](https://developer-cn.picoxr.com/document/unity/mixed-reality-troubleshooting/) |
| 17 | 空间网格实时读取时**只加载以头戴为中心半径约 5 米内**的网格；需要更大范围必须应用侧自行存储。LOD 对应密度：High 约 250 三角/㎡、Medium 约 125、Low 约 80。 | [spatial-mesh](https://developer-cn.picoxr.com/document/unity/spatial-mesh/) |
| 18 | MR 安全防护**依赖空间网格**（必须先启用 Spatial Mesh）。勾选 **MR Safeguard** 后 SDK 写入 `<meta-data android:name="enable_mr_safeguard" android:value="1" />`。碰撞球半径：头戴 20 cm（整体透出）、手柄 10 cm（圆形区域透出）。能否使用**以应用审核结果为准**。 | [mr-safeguard](https://developer-cn.picoxr.com/document/unity/mr-safeguard/) |
| 19 | `SpatialAnchorDataUpdated` / `SceneAnchorDataUpdated` **只在发现新数据时推送**；锚点减少（用户走远）时系统不推送事件，旧锚点也不会被自动删除。 | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 20 | 版本兼容矩阵：SDK 2.5.0 及更早版本的 MR 接口 **PICO 4 支持、PICO 4 Ultra 不支持**；SDK 3.0.0 在 PICO 4 上**仅支持空间锚点（不含共享空间锚点）与场景标定**，PICO 4 Ultra 全部支持。视频透视不在此次重构范围内。 | [compatibility-and-porting](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/) |

## 工作流程

### 1. 开启视频透视（几乎所有 MR 功能的前置）

设备要求：PICO 4 Ultra 系列 + 系统 5.14.0 以上（PICO 4 系列需改用 SDK 3.1.0 及更早版本）。

1. 场景添加 `XR Origin`，挂载 `PXR_Manager (Script)`。
2. `PXR_Manager (Script)` 面板勾选 **Video Seethrough**。
3. 选中 Main Camera：**Clear Flags** 设为 **Solid Color**，**Background** 的 R/G/B/A 全设 0。
4. 关闭场景内全部后处理；若用 Vulkan + URP/Built-in，关闭 HDR。
5. 代码（`using Unity.XR.PXR;`）：`PXR_Manager.EnableVideoSeeThrough = true;`，开启后应用生命周期内全局生效。
6. 需要精确状态就订阅 `PXR_Manager.VstDisplayStatusChanged`，按 `PxrVstStatus` 分支处理。
7. 可选调色：`PXR_MixedReality.EnableVideoSeeThroughEffect(true)` → `PXR_MixedReality.SetVideoSeeThroughEffect(PxrLayerEffect.Brightness, 40, 10)`（value 范围 [-50,50]，默认 0；duration=0 表示立即生效）。
8. 可选 LUT：纹理 ≤ 512×512，Inspector 里勾 **Read/Write** + **Override For Android** → **Format = RGBA 32 bit**，然后 `PXR_MixedReality.SetVideoSeeThroughLut(lutTex, row, col)`。
9. 也可用 **PICO Building Blocks** 一键完成上述配置。

分层结构（从底到顶）：透视层（VST，Runtime 生成，应用取不到数据） → Underlay 层 → 应用图层 → Overlay 层。UI 发虚时可提高 `eyebufferScale`，或把固定尺寸 UI 搬到 Underlay/Overlay 层（Underlay 需要在应用图层对应位置的 Alpha 通道“开洞”）。

### 2. 空间锚点生命周期（本地 + 共享）

设备要求：PICO 4 系列 / PICO 4 Ultra 系列 + 系统 5.15.0 以上；前提是 XR Origin + `PXR_Manager` + 已开视频透视。

```csharp
// 0) PXR_Manager 面板勾选 Spatial Anchor（共享还需勾 Shared Spatial Anchor）
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);

// 1) 创建（内存）
var (result, anchorHandle, uuid) =
    await PXR_MixedReality.CreateSpatialAnchorAsync(transform.position, transform.rotation);

// 2) 持久化到设备本地磁盘（不持久化则退出应用即丢失）
await PXR_MixedReality.PersistSpatialAnchorAsync(anchorHandle);

// 3) 找回：传 UUID 数组加载指定锚点；不传则加载全部
var query = await PXR_MixedReality.QuerySpatialAnchorAsync();      // 串行调用，勿并发
foreach (var handle in query.anchorHandleList)
{
    PXR_MixedReality.LocateAnchor(handle, out var pos, out var rot);   // 约 1s 一次
}

// 4) 删除：顺序不可颠倒
await PXR_MixedReality.UnPersistSpatialAnchorAsync(anchorHandle);
PXR_MixedReality.DestroyAnchor(anchorHandle);

PXR_MixedReality.StopSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);
```

也可以走组件路线：给 GameObject 加 `PXR_SpatialAnchor` 组件，SDK 会基于其 Transform 自动创建锚点并自动更新姿态；用 `anchor.Created` 判断完成，`anchor.uuid` / `anchor.handle` 取标识，`anchor.PersistAsync()` / `anchor.UnPersistAsync()` 管理持久化，`Destroy(gameObject)` 直接销毁锚点。SDK 在 `/Assets/Resources/Prefabs` 下提供 `SpatialAnchor.prefab`（已挂该组件并完成创建）。用 `QuerySpatialAnchorObjectsAsync` 可直接拿回挂了该组件的 GameObject 列表。

共享链路（设备 A → PICO 云端 → 设备 B）：
1. 设备 A：`CreateSpatialAnchorAsync` → `PersistSpatialAnchorAsync` → `UploadSpatialAnchorAsync`（要进度用 `UploadSpatialAnchorWithProgressAsync`）。
2. 用 `RoomService` / `MatchmakingService` / `NetworkingService`（或 Photon Unity Networking）把 UUID 传给设备 B。
3. 设备 B：`DownloadSharedSpatialAnchorAsync`（要进度用 `DownloadSharedSpatialAnchorWithProgressAsync`）→ `QuerySpatialAnchorAsync` 加载到场景。
4. 引导话术：创建者分享前应在锚点附近充分观察环境；接收方识别不到时，走到锚点附近或分享者观察过的位置多环顾。

### 3. 场景标定与平面检测

设备要求：PICO 4 系列 / PICO 4 Ultra 系列 + 系统 5.14.0 以上。

```csharp
// 前提：XR Origin 与 Camera Offset 的 Position/Rotation 均为 (0,0,0)，PXR_Manager 勾选 Scene Capture
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SceneCapture);

// 先查有没有现成数据；没有再拉起系统级“房间标定”应用
var scene = await PXR_MixedReality.QuerySceneAnchorAsync(null);
if (scene.result != PxrResult.SUCCESS || scene.anchorHandleList.Count == 0)
{
    var captured = await PXR_MixedReality.StartSceneCaptureAsync();   // SUCCESS 表示用户完成标定
    scene = await PXR_MixedReality.QuerySceneAnchorAsync(null);
}

foreach (var handle in scene.anchorHandleList)
{
    PXR_MixedReality.GetSceneSemanticLabel(handle, out var label);    // 必须单独查语义
    // Floor / Ceiling            -> GetScenePolygonData
    // Wall / Door / Window / Opening / VirtualWall / WallArt -> GetSceneBox2DData
    // Table / Sofa / Chair / …   -> GetSceneBox3DData
    PXR_MixedReality.LocateAnchor(handle, out var pos, out var rot);
}
// 监听 PXR_Manager.SceneAnchorDataUpdated，收到后重跑以上步骤
PXR_MixedReality.StopSenseDataProvider(PxrSenseDataProviderType.SceneCapture);
```

组件路线：给 GameObject 加 `PXR_Scene Capture Manager (Script)`，配置 **Box 2D Prefab** / **Box 3D Prefab**，组件会自动完成开 Provider、取数据、绘模型全流程。编辑器内预览：`adb pull /sdcard/SceneAnchorData.json`，把 JSON 拖到组件的 **Scene Capture Data** 字段（预览要求 PICO 4 Ultra 系列 + 系统 5.13.0 以上）。

平面检测：`StartSenseDataProvider(PxrSenseDataProviderType.PlaneDetection)` → 订阅 `PXR_Manager.PlaneDetectionDataUpdated`（回调参数 `List<PxrPlaneData>`）→ `StopSenseDataProvider`。`PxrPlaneData` 自带 `label`、`box2D`、`indices`、`vertices`、`state`、`orientationMode`。SDK 也支持通过 AR Foundation 做平面检测（水平/垂直/任意平面、边界顶点、`ARPlane.classifications` 分类）。

### 4. 空间网格与 MR 安全防护

1. `PXR_Manager (Script)` 勾选 **Spatial Mesh**，按需设置 **LOD**（High/Medium/Low）。
2. 可视化最省事的路线：把 `PXR_Spatial Mesh Manager (Script)` 挂到 GameObject 上，配置 **Mesh Prefab**（至少含 `Mesh Filter`；要显示还需 `Mesh Renderer`），在 **Custom Mesh Color** 里按语义配色，用 Unity Event **On Spatial Mesh Added / Updated / Removed (Guid, GameObject)** 或对应的 `MeshAdded` / `MeshUpdated` / `MeshRemoved` Action 接结果。
3. 要自己控制显隐：拿 `XRGeneralSettings.Instance.Manager.ActiveLoaderAs<PXR_Loader>().meshSubsystem`，用 `subsystem.Start()` / `subsystem.Stop()` 控制扫描，订阅 `SpatialMeshDataUpdated`（`Action<List<PxrSpatialMeshInfo>>`），按 `MeshChangeState.Added/Updated/Removed/Unchanged` 分支处理。
4. 性能建议：需要完整网格时先引导用户环视扫全空间；扫完若不需要实时更新，存下当前数据并关闭空间网格 Provider；优先用较低 LOD。
5. MR 安全防护：在空间网格已启用的前提下勾选 **MR Safeguard**。设计约束见规则 18——全程开透视、无长时间大范围虚拟遮挡、无跑动等激烈动作；特殊场景（替换真实表面、短暂 VR 界面、原地出拳踢腿）需额外安全提示，且用户移动超过约一米时建议把显示内容整体转半透明。

### 5. 相机图像数据（两条互斥路线，选错设备就跑不通）

**路线 A — 用户设备：`PXR_CameraImage`**（PICO 4 Ultra 系统 5.15.0 以上 / Project Swan PICO OS 6；底层为 OpenXR 扩展 `XR_PICO_camera_image`，依赖 `XR_EXT_future`）

1. AndroidManifest.xml 声明 `<uses-permission android:name="android.permission.CAMERA" />`，运行时申请相机权限。
2. `GetAvailableCameras` 拿 `XrCameraIdPICO[]`。
3. 查能力：`GetCameraImageResolutionCapability` / `GetCameraImageFormatCapability` / `GetCameraImageFpsCapability` / `GetCameraCameraModelCapability` / `GetCameraDataTransferTypeCapability` / `GetCameraCapabilityAvailable`。
4. `CreateCameraDeviceAsync(cameraId)` → `CreateCameraCaptureSessionAsync(cameraId, width, height, fps, format, transferType, model)`。
5. `BeginCameraCapture(cameraId)`，然后每帧 `AcquireCameraImage(cameraId, lastCaptureTime, out imageId, out captureTime)` → `GetCameraImageData(cameraId, imageId, out XrCameraImageDataRawBuffer)` → **必须** `ReleaseCameraImage(cameraId, imageId)`。
6. 收尾：`EndCameraCapture` → `DestroyCameraCaptureSession` → `DestroyCameraDevice`。
7. 相机标定：`GetCameraIntrinsics`（`focalLength` / `principalPoint` / `fov`）、`GetCameraExtrinsics`（`pose.Position` / `pose.Orientation`）。

**路线 B — 企业设备：`PXR_Enterprise`（仅 PICO 4 Ultra Enterprise）**

1. **Edit > Project Settings > Player > Build** 勾选 **Custom Main Manifest**，在 AndroidManifest.xml 加 `android.permission.CAMERA`；动态授权弹窗发生在调用 `OpenCameraAsync` 时。
2. `PXR_Enterprise.InitEnterpriseService()` → `PXR_Enterprise.BindEnterpriseService(callback)`。
3. 需要全局位姿时 `PXR_Enterprise.UseGlobalPose(true)`；`Configurefor4U` 传参配置（`PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA` 输出带畸变原生图像；`Configurefor4U(bool enableMvHevc, int videoFps)` 中 `enableMvHevc` 暂不支持须传 `false`，`videoFps` 建议 5–60、默认 60）。
4. `OpenCameraAsyncfor4U(callback, setting)` → `SetCameraFrameBufferfor4U(width, height, ref data, Action<Frame>)` → `StartGetImageDatafor4U(PXRCaptureRenderMode, width, height)`；或 `StartPreviewfor4U(surfaceObj, mode)` 直接渲到 Android Surface。
5. 内外参：`GetCameraParametersNewfor4U(width, height)` → `RGBCameraParamsNew`；`GetCameraIntrinsicsfor4U(width, height, h_fov, v_fov)`；`GetCameraExtrinsicsfor4U(out Matrix4x4 left, out Matrix4x4 right)`。
6. 坐标系：相机算法用右手系、Unity 用左手系，统一到 global 时用 `PXR_Enterprise.ConvertPoseCoordinate(ConvertCoordinateType.kLocal2Global, srcPose, ref destPose)`（转换后仍为左手系），并把 XR Origin 的 **Tracking Origin Mode** 设为 **Floor**。
7. **灭屏处理是硬要求**：相机不受 Unity 生命周期控制，灭屏后仍持续出数据直到休眠才下电。在 `OnApplicationPause(true)` 调 `CloseCamerafor4U()`，恢复时 `OpenCameraAsyncfor4U`，并在主线程 `Update` 里重新 `StartGetImageDatafor4U`。

## 核心 API 锚点

### 入口类与组件

| 名称 | 说明 |
|---|---|
| `PXR_MixedReality` | MR 静态接口类（锚点、场景标定、透视特效）。命名空间 `Unity.XR.PXR` |
| `PXR_Manager` | 挂在 XR Origin 上的总控组件，面板开关 + 静态属性/事件 |
| `PXR_CameraImage` | 用户设备相机图像静态工具类 |
| `PXR_Enterprise` / `PXR_EnterprisePlugin` | 企业设备接口（方法多带 `for4U` 后缀） |
| `PXR_Loader` | XR Loader，用于取 `meshSubsystem` |
| `PXR_SpatialAnchor`（面板名 `PXR_Spatial Anchor (Script)`） | 锚点自动化组件：`Created` / `uuid` / `handle` / `PersistAsync()` / `UnPersistAsync()` |
| `PXR_Spatial Mesh Manager (Script)` | 空间网格绘制组件（Mesh Prefab、Custom Mesh Color） |
| `PXR_Scene Capture Manager (Script)` | 场景标定一站式组件（Box 2D/3D Prefab、Scene Capture Data） |
| `PXR_Plugin.Render.UPxr_EnablePremultipliedAlpha(bool)` | 启用 PremultipliedAlpha，统一半透明渲染表现 |

### PXR_Manager 面板开关与成员

- 面板勾选项：`Video Seethrough`、`Spatial Anchor`、`Shared Spatial Anchor`、`Scene Capture`、`Spatial Mesh`（含 `LOD`）、`MR Safeguard`、`MRC`（含 `foreground Layer Masks` / `back Layer Masks`，字段 `openMRC`）。
- 静态成员：`PXR_Manager.EnableVideoSeeThrough`、`PXR_Manager.VstDisplayStatusChanged`、`PXR_Manager.SpatialAnchorDataUpdated`、`PXR_Manager.SceneAnchorDataUpdated`、`PXR_Manager.PlaneDetectionDataUpdated`。

### PXR_MixedReality 方法（3.x 现行）

```csharp
async Task<PxrResult> StartSenseDataProvider(PxrSenseDataProviderType type, CancellationToken token)
PxrResult GetSenseDataProviderState(PxrSenseDataProviderType type, out PxrSenseDataProviderState state)
PxrResult StopSenseDataProvider(PxrSenseDataProviderType type)

async Task<(PxrResult result, ulong anchorHandle, Guid uuid)> CreateSpatialAnchorAsync(Vector3 position, Quaternion rotation, CancellationToken token)
async Task<PxrResult> PersistSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)
async Task<PxrResult> UnPersistSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)
PxrResult DestroyAnchor(ulong anchorHandle)
PxrResult GetAnchorUuid(ulong anchorHandle, out Guid uuid)
PxrResult LocateAnchor(ulong anchorHandle, out Vector3 position, out Quaternion rotation)
async Task<(PxrResult result, List<ulong> anchorHandleList)> QuerySpatialAnchorAsync(Guid[] uuids, CancellationToken token)
async Task<(PxrResult result, List<GameObject> spatialAnchorObjects)> QuerySpatialAnchorObjectsAsync(Guid[] uuids = null, CancellationToken token = default)

async Task<(PxrResult result, Guid uuid)> UploadSpatialAnchorAsync(ulong anchorHandle, CancellationToken token)
async Task<(PxrResult result, Guid uuid)> UploadSpatialAnchorWithProgressAsync(ulong anchorHandle, Action<int> progressUpdated, CancellationToken token = default)
async Task<PxrResult> DownloadSharedSpatialAnchorAsync(Guid uuid, CancellationToken token)
async Task<PxrResult> DownloadSharedSpatialAnchorWithProgressAsync(Guid uuid, Action<int> progressUpdated, CancellationToken token = default)

async Task<PxrResult> StartSceneCaptureAsync(CancellationToken token)
async Task<(PxrResult result, List<ulong> anchorHandleList)> QuerySceneAnchorAsync(PxrSemanticLabel[] labels, CancellationToken token)
async Task<(PxrResult result, Dictionary<ulong, Guid> anchorDictionary)> QuerySceneAnchorAsync(CancellationToken token)
PxrResult GetSceneAnchorComponentTypes(ulong anchorHandle, out PxrSceneComponentType[] types)
PxrResult GetSceneSemanticLabel(ulong anchorHandle, out PxrSemanticLabel label)
PxrResult GetSceneBox3DData(ulong anchorHandle, out Vector3 position, out Quaternion rotation, out Vector3 extent)
PxrResult GetSceneBox2DData(ulong anchorHandle, out Vector2 offset, out Vector2 extent)
PxrResult GetScenePolygonData(ulong anchorHandle, out Vector2[] vertices)

int EnableVideoSeeThroughEffect(bool value)
int SetVideoSeeThroughEffect(PxrLayerEffect type, float value, float duration)   // value ∈ [-50,50]
int SetVideoSeeThroughLut(Texture2D texture, int row, int col)                   // 纹理 ≤ 512×512、RGBA32
```

### 枚举与结构体

- `PxrSenseDataProviderType`：官方文档中出现的成员为 `SpatialAnchor` / `SceneCapture` / `PlaneDetection`。空间网格**不通过** sense data provider 启停，用 `meshSubsystem`。
- `PxrSenseDataProviderState`：`Initialized` / `Running` / `Stopped`。
- `PxrSceneComponentType`：`Location`(0) / `Semantic` / `Box2D` / `Polygon` / `Box3D` / `TriangleMesh`(5)。
- `PxrSemanticLabel`：`Unknown`(0) / `Floor` / `Ceiling` / `Wall` / `Door` / `Window` / `Opening` / `Table` / `Sofa` / `Chair` / `Human`(10，暂不可用) / `VirtualWall`(18)；平面检测与场景标定文档另列出 `Curtain` / `Cabinet` / `Bed` / `Plant` / `Screen` / `Refrigerator` / `WashingMachine` / `AirConditioner` / `Lamp` / `WallArt`。
- `PxrPlaneOrientation`：`HorizontalUpward`(0) / `HorizontalDownward`(1) / `Vertical`(2) / `Arbitrary`(3)。
- `MeshChangeState`：`Added` / `Updated` / `Removed` / `Unchanged`。
- `PxrVstStatus`：`Disabled` / `Enabling` / `Enabled` / `Disabling`。
- `PxrLayerEffect`：`Colortemp` / `Brightness` / `Saturation` / `Contrast`。
- 结构体：`PxrSpatialMeshInfo`（`uuid`/`state`/`position`/`rotation`/`indices`/`vertices`/`labels`）、`PxrPlaneData`、`PxrSceneBox3D`、`PxrSceneBox2D`、`PxrScenePolygon`、`PxrSceneComponentData`。
- `PxrResult` 高频错误码：`SUCCESS`(0)、`ERROR_VALIDATION_FAILURE`(-1)、`ERROR_FUNCTION_UNSUPPORTED`(-7)、`ERROR_FEATURE_UNSUPPORTED`(-8)、`ERROR_EXTENSION_NOT_PRESENT`(-9)、`ERROR_SIZE_INSUFFICIENT`(-11，锚点数量超上限)、`ERROR_HANDLE_INVALID`(-12)、`ERROR_ANCHOR_SHARING_NETWORK_TIMEOUT`(-601)、`ERROR_ANCHOR_SHARING_AUTHENTICATION_FAILURE`(-602)、`ERROR_ANCHOR_SHARING_NETWORK_FAILURE`(-603)、`ERROR_ANCHOR_SHARING_LOCALIZATION_FAIL`(-604)、`ERROR_ANCHOR_SHARING_MAP_INSUFFICIENT`(-605)、`ERROR_SPATIAL_SENSING_SERVICE_UNAVAILABLE`(-1005)、`ERROR_PERMISSION_INSUFFICIENT`(-1000710000)。完整表见 [troubleshooting](https://developer-cn.picoxr.com/document/unity/mixed-reality-troubleshooting/)。

### 相机与企业接口

- `PXR_CameraImage`：`GetAvailableCameras` / `GetCameraPropertyTypesAvailable` / `GetCameraFacingProperties` / `GetCameraPositionProperties` / `GetCameraCameraTypeProperties` / `GetCameraCapabilityAvailable` / `GetCameraImageFpsCapability` / `GetCameraCameraModelCapability` / `GetCameraDataTransferTypeCapability` / `GetCameraImageFormatCapability` / `GetCameraImageResolutionCapability` / `CreateCameraDeviceAsync` / `CreateCameraCaptureSessionAsync` / `DestroyCameraCaptureSession` / `DestroyCameraDevice` / `GetCameraIntrinsics` / `GetCameraExtrinsics` / `BeginCameraCapture` / `EndCameraCapture` / `AcquireCameraImage` / `GetCameraImageData` / `ReleaseCameraImage`。
- 相机类型：`XrCameraIdPICO`（`XR_CAMERA_ID_RGB_LEFT_PICO` / `XR_CAMERA_ID_RGB_RIGHT_PICO`）、`XrCameraPropertyTypePICO`、`XrCameraFacingPICO`、`XrCameraPositionPICO`、`XrCameraTypePICO`、`XrCameraCapabilityTypePICO`、`XrCameraImageFpsPICO`、`XrCameraModelPICO`、`XrCameraDataTransferTypePICO`、`XrCameraImageFormatPICO`、`PxrExtent2Di`、`XrCameraIntrinsics`、`XrCameraExtrinsics`、`XrCameraImageDataRawBuffer`。
- `PXR_Enterprise`：`InitEnterpriseService` / `BindEnterpriseService` / `UseGlobalPose` / `Configurefor4U` / `OpenCameraAsyncfor4U` / `CloseCamerafor4U` / `StartPreviewfor4U` / `SetCameraFrameBufferfor4U` / `StartGetImageDatafor4U` / `ConvertPoseCoordinate` / `GetPredictedMainSensorState` / `GetCameraParametersNewfor4U` / `GetCameraIntrinsicsfor4U` / `GetCameraExtrinsicsfor4U`。
- 企业类型：`PXRCaptureRenderMode`（`PXRCapture_RenderMode_LEFT` / `_RIGHT` / `_3D` / `_Interlace`）、`PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA` / `KEY_MCTF` / `KEY_EIS` / `KEY_MFNR` / `VALUE_TRUE` / `VALUE_FALSE`、`Frame`（`width`/`height`/`timestamp`/`datasize`/`data`/`pose`/`status`）、`RGBCameraParamsNew`、`PXR_EnterprisePlugin.ConvertCoordinateType`（`kLocal2Global` / `kGlobal2Local`）。

### 已废弃（2.5.0 及更早）→ 3.x 映射

`CreateAnchorEntity` → `CreateSpatialAnchorAsync`｜`DestroyAnchorEntity` → `DestroyAnchor`｜`PersistAnchorEntity` → `PersistSpatialAnchorAsync`｜`UnPersistAnchorEntity` → `UnPersistSpatialAnchorAsync`｜`LoadAnchorEntityByUuidFilter` → `QuerySpatialAnchorAsync`｜`GetAnchorEntityUuid` → `GetAnchorUuid`｜`GetAnchorPose` → `LocateAnchor`｜`StartSpatialSceneCapture` → `StartSceneCaptureAsync`｜`LoadAnchorEntityBySceneFilter` → `QuerySceneAnchorAsync`｜`GetAnchorSceneLabel` → `GetSceneSemanticLabel`｜`GetAnchorComponentFlags` → `GetSceneAnchorComponentTypes`｜`GetAnchorVolumeInfo` → `GetSceneBox3DData`｜`GetAnchorPlaneBoundaryInfo` → `GetSceneBox2DData`｜`GetAnchorPlanePolygonInfo` → `GetScenePolygonData`｜`EnableVideoSeeThrough(bool)` → `PXR_Manager.EnableVideoSeeThrough`。另有 `ClearPersistedAnchorEntity`、`GetAnchorEntityLoadResults` 无 3.x 对应项，仅存在于旧版流程。

## DO NOT

```csharp
// ❌ 官方文档中空间网格不通过 sense data provider 启停
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialMesh);
// ✅ 空间网格走 meshSubsystem（或直接用 PXR_Spatial Mesh Manager 组件）
var subsystem = XRGeneralSettings.Instance.Manager.ActiveLoaderAs<PXR_Loader>().meshSubsystem;
subsystem.Start();
```

```csharp
// ❌ 已废弃的方法式透视开关（API 文档标注 Deprecated）
PXR_MixedReality.EnableVideoSeeThrough(true);
// ✅ 3.x 用 PXR_Manager 的静态属性
PXR_Manager.EnableVideoSeeThrough = true;
```

```csharp
// ❌ 先销毁再去持久化：handle 已删，系统找不到磁盘上的锚点
PXR_MixedReality.DestroyAnchor(anchorHandle);
await PXR_MixedReality.UnPersistSpatialAnchorAsync(anchorHandle);
// ✅ 顺序反过来
await PXR_MixedReality.UnPersistSpatialAnchorAsync(anchorHandle);
PXR_MixedReality.DestroyAnchor(anchorHandle);
```

```csharp
// ❌ 把 anchorHandle 存进存档，下次启动直接拿来 LocateAnchor
PlayerPrefs.SetString("anchor", anchorHandle.ToString());
// ✅ 只存 UUID，下次用 QuerySpatialAnchorAsync 换回新的 handle
PXR_MixedReality.GetAnchorUuid(anchorHandle, out var uuid);
PlayerPrefs.SetString("anchor", uuid.ToString());
```

```csharp
// ❌ 并发发起多次查询/去持久化（两个接口都不支持同时多次调用）
var t1 = PXR_MixedReality.QuerySpatialAnchorAsync();
var t2 = PXR_MixedReality.QuerySpatialAnchorAsync();
await Task.WhenAll(t1, t2);
// ✅ 串行 await
var r1 = await PXR_MixedReality.QuerySpatialAnchorAsync();
var r2 = await PXR_MixedReality.QuerySpatialAnchorAsync();
```

```csharp
// ❌ 以为场景锚点位姿会随头动实时刷新，于是每帧空转
void Update() => PXR_MixedReality.LocateAnchor(sceneAnchorHandle, out _, out _);
// ✅ 场景锚点必须重新 QuerySceneAnchorAsync 才会刷新位姿；平时缓存即可
await PXR_MixedReality.QuerySceneAnchorAsync(null);
PXR_MixedReality.LocateAnchor(sceneAnchorHandle, out var pos, out var rot);
```

```csharp
// ❌ 把场景锚点当自家锚点删
PXR_MixedReality.DestroyAnchor(sceneAnchorHandle);   // 返回 "Invalid handle"
// ✅ 场景锚点只读，只能读语义/几何数据，不能销毁、持久化或修改
```

```csharp
// ❌ 在普通用户设备上调企业相机接口（仅 PICO 4 Ultra Enterprise 可用）
PXR_Enterprise.OpenCameraAsyncfor4U(ok => { });
// ✅ 用户设备走 PXR_CameraImage
await PXR_CameraImage.CreateCameraDeviceAsync(XrCameraIdPICO.XR_CAMERA_ID_RGB_LEFT_PICO);
```

```csharp
// ❌ 取完图不释放，几帧后必然耗尽图像资源
PXR_CameraImage.AcquireCameraImage(cam, last, out var imageId, out var t);
PXR_CameraImage.GetCameraImageData(cam, imageId, out var data);
// ✅ Acquire / Release 必须成对
PXR_CameraImage.ReleaseCameraImage(cam, imageId);
```

- ❌ 试图从透视层（VST 层）读像素做算法：VST 数据由 Runtime 生成、应用取不到，视频透视画面也不向应用暴露任何物理环境信息 → ✅ 走 `PXR_CameraImage`（用户设备）或 `PXR_Enterprise`（企业设备）。
- ❌ 手写 `com.picovr.permission.SPATIAL_DATA` 却没在 `PXR_Manager` 勾选对应功能 → ✅ 勾选 Spatial Anchor / Shared Spatial Anchor / Scene Capture / Spatial Mesh，SDK 自动写入；并且必须为“用户拒绝授权”准备降级路径。
- ❌ MRC 场景里切换相机后忘记把新 XR 相机的 Tag 设为 `MainCamera` → ✅ MRC 运行期间主相机 Tag 必须是 `MainCamera`，否则 MRC 失效（录出第一视角）。Vulkan 项目还必须把 **Color Space** 设为 **Linear**，否则帧率大幅下降。见 [mixed-reality-capture](https://developer-cn.picoxr.com/document/unity/mixed-reality-capture/)。
- ❌ MR 场景里堆多层半透明物体并指望正确排序 → ✅ 目前不支持正确渲染多层半透明；相交时手动排 Render Queue（示例：墙面 1000 / 半透明圆柱 3000 / 半透明方块 4000），双面材质用 `ZWrite Off` 或拆正反两个 Pass，并用 `PXR_Plugin.Render.UPxr_EnablePremultipliedAlpha(true)` 统一表现。见 [semitransparent](https://developer-cn.picoxr.com/document/unity/tips-on-dealing-with-semitransparent-objects/)。
- ⚠️ 拼写歧义：API 参考中的枚举成员是 `PxrSemanticLabel.Unknown`，而场景标定指南的示例代码里写作 `PxrSemanticLabel.UnKnown`。使用前以 SDK 实际源码为准，不要凭记忆二选一。见 [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) 与 [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/)。
