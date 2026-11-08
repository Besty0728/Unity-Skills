# PICO Unity SDK — Mixed Reality and Environment Perception (v3.4.0)

> Anchor: PICO official documentation v3.4.0 (fetched 2026-08-13). Every rule can be verified against the source URLs.

## When to Load This Document

- Building MR/AR effects on PICO: enabling video see-through (Video Seethrough) and overlaying virtual objects on the real-world image.
- Pinning virtual objects to real-world positions with Spatial Anchors, or building multi-user Shared Spatial Anchors.
- Reading real-environment geometry: scene capture (Scene Capture / room capture), spatial mesh (Spatial Mesh), plane detection (Plane Detection).
- Getting the raw camera image from the headset (two completely different routes: the user-device `PXR_CameraImage` or the enterprise-device `PXR_Enterprise`).
- Configuring MR Safeguard, mixed reality capture (MRC), or troubleshooting rendering anomalies of semi-transparent objects under MR.
- Migrating MR interfaces from SDK 2.5.0 and earlier to 3.x.

## Key Rules

| # | Rule | Source |
|---|---|---|
| 1 | The Sense Pack (environment perception suite) **supports only 64-bit app development**. | [sense-pack-overview](https://developer-cn.picoxr.com/document/unity/sense-pack-overview/) |
| 2 | Video see-through requires **disabling all post-processing in the scene**; if using Vulkan + a render pipeline (URP or Built-in), you must also **disable HDR**. If any condition is unmet, see-through does not take effect. | [seethrough](https://developer-cn.picoxr.com/document/unity/seethrough/) |
| 3 | Video see-through requires the main camera's **Clear Flags = Solid Color**, and **R/G/B/A of Background all set to 0** (hexadecimal `000000`). | [seethrough](https://developer-cn.picoxr.com/document/unity/seethrough/) |
| 4 | Assigning `PXR_Manager.EnableVideoSeeThrough` takes effect/stops with **a delay**; for an exact state, subscribe to `PXR_Manager.VstDisplayStatusChanged` (`PxrVstStatus`: `Disabled` / `Enabling` / `Enabled` / `Disabling`). | [seethrough](https://developer-cn.picoxr.com/document/unity/seethrough/) |
| 5 | Spatial anchors, scene capture and plane detection must first call `StartSenseDataProvider(PxrSenseDataProviderType.…)` before calling any other interface, and call `StopSenseDataProvider` when done. The state can be re-checked with `GetSenseDataProviderState`. | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) · [plane-detection](https://developer-cn.picoxr.com/document/unity/plane-detection/) |
| 6 | **Handles are not permanent; they change after the app restarts.** The only cross-session identifier is the UUID (`GetAnchorUuid`). | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 7 | The order for deleting an anchor is **`UnPersistSpatialAnchorAsync` first, then `DestroyAnchor`**. The reverse removes the handle first, so the system can never find that anchor on disk again. | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 8 | Both `QuerySpatialAnchorAsync` and `UnPersistSpatialAnchorAsync` **do not support concurrent calls**; wait for the previous call to finish. Also, `QuerySpatialAnchorAsync` **can only load anchors created by this app**. | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 9 | Anchors should be placed **within 3 meters of the user's head**; the retrieval range depends on the observation range after placement, with **a maximum radius of 5 meters**; beyond that, if there are no other anchors nearby, the anchor may not be found again. | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) |
| 10 | `LocateAnchor` is real-time for **spatial anchors** (about once per second is recommended, at most every frame); but for **scene anchors**, the pose only updates when `QuerySceneAnchorAsync` is called again, otherwise it stays at the old value forever. | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 11 | Shared spatial anchors require all of the following preconditions: AppID configured in the project, platform services initialized, `GetLoggedInUser` or `GetAccessToken` called to trigger login, and the user signed in to a PICO account. Missing any of these returns `ERROR_ANCHOR_SHARING_AUTHENTICATION_FAILURE (-602)`. | [shared-spatial-anchors](https://developer-cn.picoxr.com/document/unity/shared-spatial-anchors/) · [troubleshooting](https://developer-cn.picoxr.com/document/unity/mixed-reality-troubleshooting/) |
| 12 | The downloading side **cannot persist or unpersist** a shared anchor; the uploading side also cannot actively unpersist the cloud anchor. PICO's cloud automatically unpersists anchors that have been **inactive for 7 days since their last activity**. | [shared-spatial-anchors](https://developer-cn.picoxr.com/document/unity/shared-spatial-anchors/) |
| 13 | Preconditions for scene capture: the **Position and Rotation of the XR Origin object and the Camera Offset object must both be (0,0,0)**, and video see-through must be enabled. | [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 14 | Scene anchors belong to the PICO system and are **read-only**: only the "room capture" app can modify them, and third-party apps can only read them; an app **cannot create spaces**, and can only add spatial anchors inside spaces created by "room capture". Calling `DestroyAnchor` on a scene anchor handle returns the message "Invalid handle". | [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) · [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) |
| 15 | `QuerySceneAnchorAsync` returns only a handle list and **does not match semantic labels for each anchor**; you must call `GetSceneSemanticLabel` for each one, then dispatch by semantic to `GetSceneBox2DData` / `GetSceneBox3DData` / `GetScenePolygonData`. | [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 16 | Ticking any of **Spatial Anchor / Shared Spatial Anchor / Scene Capture / Spatial Mesh** on `PXR_Manager (Script)` makes the SDK automatically write the `com.picovr.permission.SPATIAL_DATA` permission. The user may deny it or revoke it later, so the app **must provide a fallback** (fade out content that depends on it, or switch to headset/controller input); otherwise you encounter `ERROR_PERMISSION_INSUFFICIENT (-1000710000)`. | [spatial-data-permission-control](https://developer-cn.picoxr.com/document/unity/spatial-data-permission-control/) · [troubleshooting](https://developer-cn.picoxr.com/document/unity/mixed-reality-troubleshooting/) |
| 17 | When reading the spatial mesh in real time, **only meshes within about 5 meters of radius centered on the headset** are loaded; for a larger range the app must store them itself. LOD density: High about 250 triangles/m², Medium about 125, Low about 80. | [spatial-mesh](https://developer-cn.picoxr.com/document/unity/spatial-mesh/) |
| 18 | MR Safeguard **depends on the spatial mesh** (Spatial Mesh must be enabled first). After ticking **MR Safeguard**, the SDK writes `<meta-data android:name="enable_mr_safeguard" android:value="1" />`. Collision sphere radius: headset 20 cm (whole-body see-through), controller 10 cm (circular-area see-through). Whether it can be used **depends on the app review result**. | [mr-safeguard](https://developer-cn.picoxr.com/document/unity/mr-safeguard/) |
| 19 | `SpatialAnchorDataUpdated` / `SceneAnchorDataUpdated` **push only when new data is found**; when anchors decrease (the user walks away), the system does not push an event, and old anchors are not automatically deleted. | [spatial-anchors](https://developer-cn.picoxr.com/document/unity/spatial-anchors/) · [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/) |
| 20 | Version compatibility matrix: MR interfaces in SDK 2.5.0 and earlier are **supported on PICO 4 and not supported on PICO 4 Ultra**; SDK 3.0.0 on PICO 4 **supports only spatial anchors (not shared spatial anchors) and scene capture**, while PICO 4 Ultra supports all of them. Video see-through is outside the scope of this rewrite. | [compatibility-and-porting](https://developer-cn.picoxr.com/document/unity/compatibility-and-porting-guide-for-mr-features/) |

## Workflow

### 1. Enable Video See-Through (Prerequisite for Nearly All MR Features)

Device requirements: PICO 4 Ultra series + system 5.14.0 or later (PICO 4 series must use SDK 3.1.0 or earlier).

1. Add `XR Origin` to the scene and attach `PXR_Manager (Script)`.
2. On the `PXR_Manager (Script)` panel, tick **Video Seethrough**.
3. Select Main Camera: set **Clear Flags** to **Solid Color**, and set R/G/B/A of **Background** all to 0.
4. Disable all post-processing in the scene; if using Vulkan + URP/Built-in, disable HDR.
5. Code (`using Unity.XR.PXR;`): `PXR_Manager.EnableVideoSeeThrough = true;`; once enabled, it takes effect globally for the app's lifetime.
6. If you need an exact state, subscribe to `PXR_Manager.VstDisplayStatusChanged` and branch on `PxrVstStatus`.
7. Optional color grading: `PXR_MixedReality.EnableVideoSeeThroughEffect(true)` → `PXR_MixedReality.SetVideoSeeThroughEffect(PxrLayerEffect.Brightness, 40, 10)` (value range [-50,50], default 0; duration=0 takes effect immediately).
8. Optional LUT: texture ≤ 512×512; in the Inspector tick **Read/Write** + **Override For Android** → **Format = RGBA 32 bit**, then `PXR_MixedReality.SetVideoSeeThroughLut(lutTex, row, col)`.
9. You can also complete all of the above in one step with **PICO Building Blocks**.

Layer structure (bottom to top): see-through layer (VST, generated by the Runtime; the app cannot access its data) → Underlay layer → app layer → Overlay layer. If the UI looks blurry, you can increase `eyebufferScale`, or move fixed-size UI to the Underlay/Overlay layer (the Underlay requires "punching a hole" in the alpha channel of the app layer at the corresponding position).

### 2. Spatial Anchor Lifecycle (Local + Shared)

Device requirements: PICO 4 series / PICO 4 Ultra series + system 5.15.0 or later; prerequisites are XR Origin + `PXR_Manager` + video see-through enabled.

```csharp
// 0) Tick Spatial Anchor on the PXR_Manager panel (also tick Shared Spatial Anchor for sharing)
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);

// 1) Create (in memory)
var (result, anchorHandle, uuid) =
    await PXR_MixedReality.CreateSpatialAnchorAsync(transform.position, transform.rotation);

// 2) Persist to the device's local disk (without persisting, it is lost when the app exits)
await PXR_MixedReality.PersistSpatialAnchorAsync(anchorHandle);

// 3) Retrieve: pass a UUID array to load specific anchors; pass nothing to load all
var query = await PXR_MixedReality.QuerySpatialAnchorAsync();      // Serial calls only, do not run concurrently
foreach (var handle in query.anchorHandleList)
{
    PXR_MixedReality.LocateAnchor(handle, out var pos, out var rot);   // About once per second
}

// 4) Delete: the order must not be reversed
await PXR_MixedReality.UnPersistSpatialAnchorAsync(anchorHandle);
PXR_MixedReality.DestroyAnchor(anchorHandle);

PXR_MixedReality.StopSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);
```

A component route is also available: add the `PXR_SpatialAnchor` component to a GameObject, and the SDK creates the anchor based on its Transform and updates the pose automatically; use `anchor.Created` to detect completion, `anchor.uuid` / `anchor.handle` to get identifiers, `anchor.PersistAsync()` / `anchor.UnPersistAsync()` to manage persistence, and `Destroy(gameObject)` to destroy the anchor directly. The SDK provides `SpatialAnchor.prefab` under `/Assets/Resources/Prefabs` (the component is already attached and creation is completed). `QuerySpatialAnchorObjectsAsync` returns the list of GameObjects carrying this component directly.

Shared pipeline (device A → PICO cloud → device B):
1. Device A: `CreateSpatialAnchorAsync` → `PersistSpatialAnchorAsync` → `UploadSpatialAnchorAsync` (for progress, use `UploadSpatialAnchorWithProgressAsync`).
2. Pass the UUID to device B using `RoomService` / `MatchmakingService` / `NetworkingService` (or Photon Unity Networking).
3. Device B: `DownloadSharedSpatialAnchorAsync` (for progress, use `DownloadSharedSpatialAnchorWithProgressAsync`) → `QuerySpatialAnchorAsync` to load it into the scene.
4. Guidance wording: before sharing, the creator should observe the environment thoroughly near the anchor; if the receiver does not recognize it, have them walk near the anchor or to the position the sharer observed, and look around in circles.

### 3. Scene Capture and Plane Detection

Device requirements: PICO 4 series / PICO 4 Ultra series + system 5.14.0 or later.

```csharp
// Prerequisite: XR Origin and Camera Offset Position/Rotation are both (0,0,0); PXR_Manager has Scene Capture ticked
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SceneCapture);

// Check for existing data first; if none, launch the system-level "room capture" app
var scene = await PXR_MixedReality.QuerySceneAnchorAsync(null);
if (scene.result != PxrResult.SUCCESS || scene.anchorHandleList.Count == 0)
{
    var captured = await PXR_MixedReality.StartSceneCaptureAsync();   // SUCCESS means the user completed the capture
    scene = await PXR_MixedReality.QuerySceneAnchorAsync(null);
}

foreach (var handle in scene.anchorHandleList)
{
    PXR_MixedReality.GetSceneSemanticLabel(handle, out var label);    // Semantics must be queried separately
    // Floor / Ceiling            -> GetScenePolygonData
    // Wall / Door / Window / Opening / VirtualWall / WallArt -> GetSceneBox2DData
    // Table / Sofa / Chair / …   -> GetSceneBox3DData
    PXR_MixedReality.LocateAnchor(handle, out var pos, out var rot);
}
// Listen for PXR_Manager.SceneAnchorDataUpdated, and rerun the steps above when it fires
PXR_MixedReality.StopSenseDataProvider(PxrSenseDataProviderType.SceneCapture);
```

Component route: add `PXR_Scene Capture Manager (Script)` to a GameObject and configure **Box 2D Prefab** / **Box 3D Prefab**; the component automatically handles the whole flow of starting the Provider, fetching data and drawing models. Editor preview: `adb pull /sdcard/SceneAnchorData.json`, then drag the JSON into the component's **Scene Capture Data** field (preview requires PICO 4 Ultra series + system 5.13.0 or later).

Plane detection: `StartSenseDataProvider(PxrSenseDataProviderType.PlaneDetection)` → subscribe to `PXR_Manager.PlaneDetectionDataUpdated` (callback parameter `List<PxrPlaneData>`) → `StopSenseDataProvider`. `PxrPlaneData` carries `label`, `box2D`, `indices`, `vertices`, `state`, and `orientationMode`. The SDK also supports plane detection through AR Foundation (horizontal / vertical / arbitrary planes, boundary vertices, `ARPlane.classifications` classification).

### 4. Spatial Mesh and MR Safeguard

1. On `PXR_Manager (Script)`, tick **Spatial Mesh** and set **LOD** (High / Medium / Low) as needed.
2. The simplest visualization route: attach `PXR_Spatial Mesh Manager (Script)` to a GameObject, configure **Mesh Prefab** (must contain at least `Mesh Filter`; `Mesh Renderer` is also needed to display), color by semantic in **Custom Mesh Color**, and hook results via the Unity Event **On Spatial Mesh Added / Updated / Removed (Guid, GameObject)** or the corresponding `MeshAdded` / `MeshUpdated` / `MeshRemoved` actions.
3. To control visibility yourself: get `XRGeneralSettings.Instance.Manager.ActiveLoaderAs<PXR_Loader>().meshSubsystem`, control scanning with `subsystem.Start()` / `subsystem.Stop()`, subscribe to `SpatialMeshDataUpdated` (`Action<List<PxrSpatialMeshInfo>>`), and branch on `MeshChangeState.Added/Updated/Removed/Unchanged`.
4. Performance advice: when a complete mesh is needed, first guide the user to look around and scan the whole space; after scanning, if real-time updates are not needed, store the current data and stop the Spatial Mesh Provider; prefer a lower LOD.
5. MR Safeguard: tick **MR Safeguard** while Spatial Mesh is enabled. Design constraints are in rule 18: keep see-through on throughout, avoid long-duration large-area virtual occlusion, and avoid violent movement such as running; special scenarios (replacing real surfaces, brief VR interfaces, punching/kicking in place) require additional safety prompts, and when the user moves more than about one meter, it is recommended to make the displayed content semi-transparent as a whole.

### 5. Camera Image Data (Two Mutually Exclusive Routes; Choosing the Wrong Device Means It Will Not Run)

**Route A: user device, `PXR_CameraImage`** (PICO 4 Ultra system 5.15.0 or later / Project Swan PICO OS 6; the underlying layer is the OpenXR extension `XR_PICO_camera_image`, which depends on `XR_EXT_future`)

1. Declare `<uses-permission android:name="android.permission.CAMERA" />` in AndroidManifest.xml and request the camera permission at runtime.
2. `GetAvailableCameras` returns `XrCameraIdPICO[]`.
3. Query capabilities: `GetCameraImageResolutionCapability` / `GetCameraImageFormatCapability` / `GetCameraImageFpsCapability` / `GetCameraCameraModelCapability` / `GetCameraDataTransferTypeCapability` / `GetCameraCapabilityAvailable`.
4. `CreateCameraDeviceAsync(cameraId)` → `CreateCameraCaptureSessionAsync(cameraId, width, height, fps, format, transferType, model)`.
5. `BeginCameraCapture(cameraId)`, then each frame `AcquireCameraImage(cameraId, lastCaptureTime, out imageId, out captureTime)` → `GetCameraImageData(cameraId, imageId, out XrCameraImageDataRawBuffer)` → **must** call `ReleaseCameraImage(cameraId, imageId)`.
6. Teardown: `EndCameraCapture` → `DestroyCameraCaptureSession` → `DestroyCameraDevice`.
7. Camera calibration: `GetCameraIntrinsics` (`focalLength` / `principalPoint` / `fov`), `GetCameraExtrinsics` (`pose.Position` / `pose.Orientation`).

**Route B: enterprise device, `PXR_Enterprise` (PICO 4 Ultra Enterprise only)**

1. **Edit > Project Settings > Player > Build**: tick **Custom Main Manifest**, and add `android.permission.CAMERA` to AndroidManifest.xml; the dynamic permission prompt appears when `OpenCameraAsync` is called.
2. `PXR_Enterprise.InitEnterpriseService()` → `PXR_Enterprise.BindEnterpriseService(callback)`.
3. When a global pose is needed, `PXR_Enterprise.UseGlobalPose(true)`; configure via `Configurefor4U` (`PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA` outputs the native image with distortion; in `Configurefor4U(bool enableMvHevc, int videoFps)`, `enableMvHevc` is not yet supported and must be passed as `false`; `videoFps` is recommended at 5–60, default 60).
4. `OpenCameraAsyncfor4U(callback, setting)` → `SetCameraFrameBufferfor4U(width, height, ref data, Action<Frame>)` → `StartGetImageDatafor4U(PXRCaptureRenderMode, width, height)`; or `StartPreviewfor4U(surfaceObj, mode)` renders directly to an Android Surface.
5. Intrinsics and extrinsics: `GetCameraParametersNewfor4U(width, height)` → `RGBCameraParamsNew`; `GetCameraIntrinsicsfor4U(width, height, h_fov, v_fov)`; `GetCameraExtrinsicsfor4U(out Matrix4x4 left, out Matrix4x4 right)`.
6. Coordinate systems: camera algorithms use a right-handed system and Unity uses a left-handed system; to unify into global, use `PXR_Enterprise.ConvertPoseCoordinate(ConvertCoordinateType.kLocal2Global, srcPose, ref destPose)` (the result is still left-handed), and set the XR Origin's **Tracking Origin Mode** to **Floor**.
7. **Screen-off handling is a hard requirement**: the camera is not controlled by the Unity lifecycle, and keeps producing data after the screen turns off until the device sleeps and powers down. Call `CloseCamerafor4U()` in `OnApplicationPause(true)`, call `OpenCameraAsyncfor4U` on resume, and restart `StartGetImageDatafor4U` on the main thread in `Update`.

## Core API Anchors

### Entry Classes and Components

| Name | Description |
|---|---|
| `PXR_MixedReality` | MR static interface class (anchors, scene capture, see-through effects). Namespace `Unity.XR.PXR` |
| `PXR_Manager` | Master control component attached to XR Origin; panel toggles + static properties/events |
| `PXR_CameraImage` | Static utility class for user-device camera images |
| `PXR_Enterprise` / `PXR_EnterprisePlugin` | Enterprise-device interfaces (most methods carry a `for4U` suffix) |
| `PXR_Loader` | XR Loader, used to get `meshSubsystem` |
| `PXR_SpatialAnchor` (panel name `PXR_Spatial Anchor (Script)`) | Anchor automation component: `Created` / `uuid` / `handle` / `PersistAsync()` / `UnPersistAsync()` |
| `PXR_Spatial Mesh Manager (Script)` | Spatial mesh drawing component (Mesh Prefab, Custom Mesh Color) |
| `PXR_Scene Capture Manager (Script)` | All-in-one scene capture component (Box 2D/3D Prefab, Scene Capture Data) |
| `PXR_Plugin.Render.UPxr_EnablePremultipliedAlpha(bool)` | Enables PremultipliedAlpha to unify semi-transparent rendering appearance |

### PXR_Manager Panel Toggles and Members

- Panel checkboxes: `Video Seethrough`, `Spatial Anchor`, `Shared Spatial Anchor`, `Scene Capture`, `Spatial Mesh` (including `LOD`), `MR Safeguard`, `MRC` (including `foreground Layer Masks` / `back Layer Masks`, field `openMRC`).
- Static members: `PXR_Manager.EnableVideoSeeThrough`, `PXR_Manager.VstDisplayStatusChanged`, `PXR_Manager.SpatialAnchorDataUpdated`, `PXR_Manager.SceneAnchorDataUpdated`, `PXR_Manager.PlaneDetectionDataUpdated`.

### PXR_MixedReality Methods (Current in 3.x)

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
int SetVideoSeeThroughLut(Texture2D texture, int row, int col)                   // Texture ≤ 512×512, RGBA32
```

### Enums and Structs

- `PxrSenseDataProviderType`: the members that appear in the official documentation are `SpatialAnchor` / `SceneCapture` / `PlaneDetection`. The spatial mesh is **not** started or stopped through the sense data provider; use `meshSubsystem` instead.
- `PxrSenseDataProviderState`: `Initialized` / `Running` / `Stopped`.
- `PxrSceneComponentType`: `Location`(0) / `Semantic` / `Box2D` / `Polygon` / `Box3D` / `TriangleMesh`(5).
- `PxrSemanticLabel`: `Unknown`(0) / `Floor` / `Ceiling` / `Wall` / `Door` / `Window` / `Opening` / `Table` / `Sofa` / `Chair` / `Human`(10, currently unavailable) / `VirtualWall`(18); the plane detection and scene capture docs additionally list `Curtain` / `Cabinet` / `Bed` / `Plant` / `Screen` / `Refrigerator` / `WashingMachine` / `AirConditioner` / `Lamp` / `WallArt`.
- `PxrPlaneOrientation`: `HorizontalUpward`(0) / `HorizontalDownward`(1) / `Vertical`(2) / `Arbitrary`(3).
- `MeshChangeState`: `Added` / `Updated` / `Removed` / `Unchanged`.
- `PxrVstStatus`: `Disabled` / `Enabling` / `Enabled` / `Disabling`.
- `PxrLayerEffect`: `Colortemp` / `Brightness` / `Saturation` / `Contrast`.
- Structs: `PxrSpatialMeshInfo` (`uuid` / `state` / `position` / `rotation` / `indices` / `vertices` / `labels`), `PxrPlaneData`, `PxrSceneBox3D`, `PxrSceneBox2D`, `PxrScenePolygon`, `PxrSceneComponentData`.
- Frequently seen `PxrResult` error codes: `SUCCESS`(0), `ERROR_VALIDATION_FAILURE`(-1), `ERROR_FUNCTION_UNSUPPORTED`(-7), `ERROR_FEATURE_UNSUPPORTED`(-8), `ERROR_EXTENSION_NOT_PRESENT`(-9), `ERROR_SIZE_INSUFFICIENT`(-11, anchor count exceeds the limit), `ERROR_HANDLE_INVALID`(-12), `ERROR_ANCHOR_SHARING_NETWORK_TIMEOUT`(-601), `ERROR_ANCHOR_SHARING_AUTHENTICATION_FAILURE`(-602), `ERROR_ANCHOR_SHARING_NETWORK_FAILURE`(-603), `ERROR_ANCHOR_SHARING_LOCALIZATION_FAIL`(-604), `ERROR_ANCHOR_SHARING_MAP_INSUFFICIENT`(-605), `ERROR_SPATIAL_SENSING_SERVICE_UNAVAILABLE`(-1005), `ERROR_PERMISSION_INSUFFICIENT`(-1000710000). For the full table see [troubleshooting](https://developer-cn.picoxr.com/document/unity/mixed-reality-troubleshooting/).

### Camera and Enterprise Interfaces

- `PXR_CameraImage`: `GetAvailableCameras` / `GetCameraPropertyTypesAvailable` / `GetCameraFacingProperties` / `GetCameraPositionProperties` / `GetCameraCameraTypeProperties` / `GetCameraCapabilityAvailable` / `GetCameraImageFpsCapability` / `GetCameraCameraModelCapability` / `GetCameraDataTransferTypeCapability` / `GetCameraImageFormatCapability` / `GetCameraImageResolutionCapability` / `CreateCameraDeviceAsync` / `CreateCameraCaptureSessionAsync` / `DestroyCameraCaptureSession` / `DestroyCameraDevice` / `GetCameraIntrinsics` / `GetCameraExtrinsics` / `BeginCameraCapture` / `EndCameraCapture` / `AcquireCameraImage` / `GetCameraImageData` / `ReleaseCameraImage`.
- Camera types: `XrCameraIdPICO` (`XR_CAMERA_ID_RGB_LEFT_PICO` / `XR_CAMERA_ID_RGB_RIGHT_PICO`), `XrCameraPropertyTypePICO`, `XrCameraFacingPICO`, `XrCameraPositionPICO`, `XrCameraTypePICO`, `XrCameraCapabilityTypePICO`, `XrCameraImageFpsPICO`, `XrCameraModelPICO`, `XrCameraDataTransferTypePICO`, `XrCameraImageFormatPICO`, `PxrExtent2Di`, `XrCameraIntrinsics`, `XrCameraExtrinsics`, `XrCameraImageDataRawBuffer`.
- `PXR_Enterprise`: `InitEnterpriseService` / `BindEnterpriseService` / `UseGlobalPose` / `Configurefor4U` / `OpenCameraAsyncfor4U` / `CloseCamerafor4U` / `StartPreviewfor4U` / `SetCameraFrameBufferfor4U` / `StartGetImageDatafor4U` / `ConvertPoseCoordinate` / `GetPredictedMainSensorState` / `GetCameraParametersNewfor4U` / `GetCameraIntrinsicsfor4U` / `GetCameraExtrinsicsfor4U`.
- Enterprise types: `PXRCaptureRenderMode` (`PXRCapture_RenderMode_LEFT` / `_RIGHT` / `_3D` / `_Interlace`), `PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA` / `KEY_MCTF` / `KEY_EIS` / `KEY_MFNR` / `VALUE_TRUE` / `VALUE_FALSE`, `Frame` (`width` / `height` / `timestamp` / `datasize` / `data` / `pose` / `status`), `RGBCameraParamsNew`, `PXR_EnterprisePlugin.ConvertCoordinateType` (`kLocal2Global` / `kGlobal2Local`).

### Deprecated (2.5.0 and Earlier) → 3.x Mapping

`CreateAnchorEntity` → `CreateSpatialAnchorAsync` | `DestroyAnchorEntity` → `DestroyAnchor` | `PersistAnchorEntity` → `PersistSpatialAnchorAsync` | `UnPersistAnchorEntity` → `UnPersistSpatialAnchorAsync` | `LoadAnchorEntityByUuidFilter` → `QuerySpatialAnchorAsync` | `GetAnchorEntityUuid` → `GetAnchorUuid` | `GetAnchorPose` → `LocateAnchor` | `StartSpatialSceneCapture` → `StartSceneCaptureAsync` | `LoadAnchorEntityBySceneFilter` → `QuerySceneAnchorAsync` | `GetAnchorSceneLabel` → `GetSceneSemanticLabel` | `GetAnchorComponentFlags` → `GetSceneAnchorComponentTypes` | `GetAnchorVolumeInfo` → `GetSceneBox3DData` | `GetAnchorPlaneBoundaryInfo` → `GetSceneBox2DData` | `GetAnchorPlanePolygonInfo` → `GetScenePolygonData` | `EnableVideoSeeThrough(bool)` → `PXR_Manager.EnableVideoSeeThrough`. `ClearPersistedAnchorEntity` and `GetAnchorEntityLoadResults` have no 3.x counterpart and exist only in the old flow.

## DO NOT

```csharp
// ❌ The official docs do not start or stop the spatial mesh through the sense data provider
await PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialMesh);
// ✅ The spatial mesh goes through meshSubsystem (or directly use the PXR_Spatial Mesh Manager component)
var subsystem = XRGeneralSettings.Instance.Manager.ActiveLoaderAs<PXR_Loader>().meshSubsystem;
subsystem.Start();
```

```csharp
// ❌ Deprecated method-style see-through toggle (API docs mark it Deprecated)
PXR_MixedReality.EnableVideoSeeThrough(true);
// ✅ In 3.x use the static property on PXR_Manager
PXR_Manager.EnableVideoSeeThrough = true;
```

```csharp
// ❌ Destroying first, then unpersisting: the handle is already gone, so the system cannot find the anchor on disk
PXR_MixedReality.DestroyAnchor(anchorHandle);
await PXR_MixedReality.UnPersistSpatialAnchorAsync(anchorHandle);
// ✅ Reverse the order
await PXR_MixedReality.UnPersistSpatialAnchorAsync(anchorHandle);
PXR_MixedReality.DestroyAnchor(anchorHandle);
```

```csharp
// ❌ Storing anchorHandle in a save file and passing it straight to LocateAnchor on the next launch
PlayerPrefs.SetString("anchor", anchorHandle.ToString());
// ✅ Store only the UUID, and exchange it for a new handle with QuerySpatialAnchorAsync next time
PXR_MixedReality.GetAnchorUuid(anchorHandle, out var uuid);
PlayerPrefs.SetString("anchor", uuid.ToString());
```

```csharp
// ❌ Issuing multiple queries/unpersists concurrently (neither interface supports simultaneous multiple calls)
var t1 = PXR_MixedReality.QuerySpatialAnchorAsync();
var t2 = PXR_MixedReality.QuerySpatialAnchorAsync();
await Task.WhenAll(t1, t2);
// ✅ Await serially
var r1 = await PXR_MixedReality.QuerySpatialAnchorAsync();
var r2 = await PXR_MixedReality.QuerySpatialAnchorAsync();
```

```csharp
// ❌ Assuming the scene anchor pose refreshes in real time as the head moves, and spinning every frame
void Update() => PXR_MixedReality.LocateAnchor(sceneAnchorHandle, out _, out _);
// ✅ A scene anchor pose refreshes only when QuerySceneAnchorAsync is called again; otherwise cache it
await PXR_MixedReality.QuerySceneAnchorAsync(null);
PXR_MixedReality.LocateAnchor(sceneAnchorHandle, out var pos, out var rot);
```

```csharp
// ❌ Deleting a scene anchor as if it were your own
PXR_MixedReality.DestroyAnchor(sceneAnchorHandle);   // Returns "Invalid handle"
// ✅ Scene anchors are read-only: you can only read semantic/geometry data; you cannot destroy, persist or modify them
```

```csharp
// ❌ Calling enterprise camera interfaces on a regular user device (available only on PICO 4 Ultra Enterprise)
PXR_Enterprise.OpenCameraAsyncfor4U(ok => { });
// ✅ User devices use PXR_CameraImage
await PXR_CameraImage.CreateCameraDeviceAsync(XrCameraIdPICO.XR_CAMERA_ID_RGB_LEFT_PICO);
```

```csharp
// ❌ Acquiring images without releasing them; image resources are inevitably exhausted within a few frames
PXR_CameraImage.AcquireCameraImage(cam, last, out var imageId, out var t);
PXR_CameraImage.GetCameraImageData(cam, imageId, out var data);
// ✅ Acquire / Release must be paired
PXR_CameraImage.ReleaseCameraImage(cam, imageId);
```

- ❌ Trying to read pixels from the see-through layer (VST layer) for algorithms: VST data is generated by the Runtime and inaccessible to the app, and the video see-through image does not expose any physical environment information to the app → ✅ Use `PXR_CameraImage` (user device) or `PXR_Enterprise` (enterprise device).
- ❌ Handwriting `com.picovr.permission.SPATIAL_DATA` without ticking the corresponding feature on `PXR_Manager` → ✅ Tick Spatial Anchor / Shared Spatial Anchor / Scene Capture / Spatial Mesh and let the SDK write it automatically; and you must prepare a fallback path for "user denied authorization".
- ❌ In an MRC scene, forgetting to set the new XR camera's tag to `MainCamera` after switching cameras → ✅ While MRC is running the main camera's tag must be `MainCamera`, otherwise MRC fails (recording the first-person view). Vulkan projects must also set **Color Space** to **Linear**, otherwise the frame rate drops significantly. See [mixed-reality-capture](https://developer-cn.picoxr.com/document/unity/mixed-reality-capture/).
- ❌ Stacking multiple layers of semi-transparent objects in an MR scene and expecting correct sorting → ✅ Correct rendering of multiple semi-transparent layers is currently not supported; where they intersect, manually set the Render Queue (example: wall 1000 / semi-transparent cylinder 3000 / semi-transparent cube 4000), use `ZWrite Off` for double-sided materials or split them into front and back Passes, and use `PXR_Plugin.Render.UPxr_EnablePremultipliedAlpha(true)` to unify the appearance. See [semitransparent](https://developer-cn.picoxr.com/document/unity/tips-on-dealing-with-semitransparent-objects/).
- ⚠️ Spelling ambiguity: in the API reference the enum member is `PxrSemanticLabel.Unknown`, while the example code in the scene capture guide writes `PxrSemanticLabel.UnKnown`. Before using it, treat the actual SDK source as authoritative; do not pick one from memory. See [PXR_MixedReality](https://developer-cn.picoxr.com/reference/unity/client-api/PXR_MixedReality/) and [scene-capture](https://developer-cn.picoxr.com/document/unity/scene-capture/).
