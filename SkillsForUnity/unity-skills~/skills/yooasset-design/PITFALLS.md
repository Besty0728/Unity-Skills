---
name: unity-yooasset-pitfalls
description: "YooAsset pitfalls checklist (v2.3.18) — the 30 mistakes AI agents most often introduce, plus a legacy-API migration list of pre-2.3.18 calls to avoid. YooAsset 陷阱清单(v2.3.18:AI 最易引入的 30 个错误,以及 2.3.18 之前不可再用的旧 API 迁移清单)。"
type: reference
---

# YooAsset - Pitfalls Checklist

A checklist of the 30 mistakes AI agents most often introduce when writing YooAsset code against **v2.3.18**. Every item cites a source anchor. Scan before writing or reviewing YooAsset code.

A separate `## Legacy API migration` section at the bottom lists pre-2.3.18 calls you must NOT emit.

---

## Startup & lifecycle

### ❌ P1. Calling `CreatePackage` / `InitializeAsync` before `YooAssets.Initialize()`
- Symptom: `Exception: YooAssets not initialize !`
- Source: `Runtime/YooAssets.cs:223-229` — every public method guards with `CheckException`
- ✅ Fix: call `YooAssets.Initialize()` at process start and gate boot code with `if (!YooAssets.Initialized) YooAssets.Initialize();`. A repeated `Initialize()` call only warns and returns, but relying on that hides ownership of the driver object.

### ❌ P2. Calling `CreatePackage` twice with the same name
- Symptom: `Exception: Package DefaultPackage already existed !`
- Source: `Runtime/YooAssets.cs:118-120`
- ✅ Fix: `YooAssets.TryGetPackage(name) ?? YooAssets.CreatePackage(name)`

### ❌ P3. Calling any `Load*` / `GetPackageVersion` / `UpdatePackageManifestAsync` on a package whose `InitializeStatus != Succeed`
- Symptom: `DebugCheckInitialize` throws in DEBUG; NRE in release builds
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:1157-1170`
- ✅ Fix: gate on `package.InitializeStatus == EOperationStatus.Succeed` or `package.PackageValid`

### ❌ P4. Destroying the `[YooAssets]` driver GameObject manually
- Symptom: `OperationSystem.Update` stops pumping; every future async op hangs at `Status == Processing`
- Source: `Runtime/YooAssets.cs:52-63`
- ✅ Fix: never `GameObject.Destroy` the driver. Use `YooAssets.Destroy()` for full teardown.

### ❌ P5. Using `YooAssets.Destroy()` to clean up between scenes
- Symptom: every `AssetBundle` Unity holds is unloaded, including unrelated systems; next scene loses bundles
- Source: `Runtime/YooAssets.cs:69-87` — step 4 calls `AssetBundle.UnloadAllAssetBundles(true)`
- ✅ Fix: destroy packages individually via `pkg.DestroyAsync` + `YooAssets.RemovePackage(pkg)`. Reserve `YooAssets.Destroy()` for full process shutdown.

### ❌ P6. `YooAssets.RemovePackage` while `InitializeStatus != None`
- Symptom: logs `The resource package X has not been destroyed …` and returns false
- Source: `Runtime/YooAssets.cs:177-190`
- ✅ Fix: `yield return package.DestroyAsync();` first

---

## PlayMode mismatches

### ❌ P7. Mismatched `InitializeParameters` subclass for the intended `EPlayMode`
- Symptom: `InitializeAsync` infers a different play mode than you intended; an unknown custom subclass throws `NotImplementedException`
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:107-135, 170-181`
- ✅ Fix: the five modes map 1:1 — `EditorSimulateMode ↔ EditorSimulateModeParameters`, `OfflinePlayMode ↔ OfflinePlayModeParameters`, `HostPlayMode ↔ HostPlayModeParameters`, `WebPlayMode ↔ WebPlayModeParameters`, `CustomPlayMode ↔ CustomPlayModeParameters`

### ❌ P8. `EditorSimulateMode` reaching a player build
- Symptom: `Exception: Editor simulate mode only support unity editor.`
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:160-163`
- ✅ Fix: guard with `#if UNITY_EDITOR` or runtime check on `Application.isEditor`

### ❌ P9. Using `HostPlayMode` / `OfflinePlayMode` / `EditorSimulateMode` on WebGL
- Symptom: `Exception: <Mode> can not support WebGL plateform !`
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:186-196`
- ✅ Fix: on WebGL only `WebPlayMode` is legal; branch on `UNITY_WEBGL`

### ❌ P10. `CustomPlayMode.FileSystemParameterList` with the main FS not at the end
- Symptom: the earlier FS is used as the main; lookups fall through
- Source: `Runtime/InitializeParameters.cs:107-109` docstring ("列表最后一个元素作为主文件系统")
- ✅ Fix: append the main FS last

---

## Handle lifecycle

### ❌ P11. Forgetting `Release()` on a load handle
- Symptom: bundle refcount never drops; `UnloadUnusedAssetsAsync` does nothing; `AutoUnloadBundleWhenUnused = true` is useless
- Source: `Runtime/ResourceManager/Handle/HandleBase.cs:21-32`
- ✅ Fix: pair every `LoadAsset*/LoadScene*/LoadRawFile*` with `handle.Release()` (or `Dispose()`)

### ❌ P12. Using a handle after `Release()`
- Symptom: `AssetObject` returns null; callbacks never fire; warnings on every access
- Source: `Runtime/ResourceManager/Handle/HandleBase.cs:128-146` (`IsValidWithWarning`)
- ✅ Fix: set the field to null right after release

### ❌ P13. Creating a `NetworkVariable`-style subscription per frame
- Symptom: doesn't exist — YooAsset has no `OnValueChanged` event; users copy it over from Netcode
- Source: nothing in YooAsset; confirmed by grepping the Runtime tree
- ✅ Fix: use `handle.Completed += OnLoaded`, `await handle.Task`, or `yield return handle`

### ❌ P14. Unloading a `SceneHandle` via `UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync`
- Symptom: the matching bundle's refcount desyncs; subsequent loads behave unpredictably
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:576-622` (scene flow is fully YooAsset-managed)
- ✅ Fix: `sceneHandle.Release()` (or `handle.UnloadAsync()`) — do not mix managers

---

## Loading type mistakes

### ❌ P15. `LoadAssetSync<T>()` with `T : UnityEngine.Behaviour`
- Symptom: in DEBUG, `Exception: Load asset type is invalid : ...`; in non-DEBUG builds the guard is compiled out, so treat this as a design-time rule
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:1178-1181`
- ✅ Fix: load the `GameObject` prefab, then `GetComponent<T>()`

### ❌ P16. Loading a plain C# type
- Symptom: in DEBUG, `Exception: Load asset type is invalid : ...`; in non-DEBUG builds the guard is compiled out, so do not rely on runtime validation
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:1183-1186`
- ✅ Fix: wrap your data in a `ScriptableObject` or load raw bytes via `LoadRawFileAsync`

### ❌ P17. Calling default-package `YooAssets.LoadAssetAsync(...)` before `SetDefaultPackage`
- Symptom: runtime exception: `Default package is null. Please use SetDefaultPackage !`
- Source: `Runtime/YooAssetsExtension.cs:16, 260-295, 612-616`
- ✅ Fix: prefer `var pkg = YooAssets.GetPackage("…"); pkg.LoadAssetAsync<T>(location)` or call `YooAssets.SetDefaultPackage(pkg)` before using static shortcuts

### ❌ P18. Loading without specifying a generic type, then casting
- Symptom: `GetAssetObject<Sprite>()` returns null because the provider's `AssetObject` was resolved as `UnityEngine.Object`
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:675-681, 726-732` — default type is `typeof(UnityEngine.Object)`
- ✅ Fix: always pass the concrete type: `LoadAssetAsync<Sprite>(...)` or `LoadAssetAsync(location, typeof(Sprite))`

---

## Update-flow mistakes

### ❌ P19. Hallucinating a class named `UpdatePackageVersionOperation`
- Symptom: compile error
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:225-231` — the real return type is `RequestPackageVersionOperation`
- ✅ Fix: `RequestPackageVersionOperation op = pkg.RequestPackageVersionAsync(); yield return op; var v = op.PackageVersion;`

### ❌ P20. Calling `UpdatePackageManifestAsync` without draining loaders first
- Symptom: YooAsset logs warning `Found loaded bundle before update manifest !`; subsequent behavior is undefined when a bundle is replaced mid-flight
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:242-247`
- ✅ Fix: release handles, then `yield return pkg.UnloadAllAssetsAsync();` before the manifest update

### ❌ P21. Skipping `RequestPackageVersionAsync` and going straight to `UpdatePackageManifestAsync("someVersion")`
- Symptom: stale or wrong version; manifest loads but assets are missing
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:225, 238` — the ordering is documented by `Samples~/Space Shooter/.../FsmNode`
- ✅ Fix: always request first, use `op.PackageVersion`

### ❌ P22. Calling `CreateResourceDownloader` before `UpdatePackageManifestAsync` finishes
- Symptom: the downloader operates against the old manifest; newly-added bundles are missed
- Source: `FsmCreateDownloader.cs:27-49` — the sample state machine sequences them strictly
- ✅ Fix: `yield return manifestOp;` → `CreateResourceDownloader(...)`

### ❌ P23. Reading `GetPackageVersion()` immediately after `RequestPackageVersionAsync` returns
- Symptom: `GetPackageVersion()` returns the **currently-active** manifest version, not the freshly-requested one
- Source: `Runtime/ResourcePackage/ResourcePackage.cs:303-307` — reads `_playModeImpl.ActiveManifest.PackageVersion`
- ✅ Fix: read `.PackageVersion` on the `RequestPackageVersionOperation` itself

---

## Downloader mistakes

### ❌ P24. Subscribing callbacks with `+=`
- Symptom: `DownloadFinishCallback` / `DownloadUpdateCallback` / … are **plain delegate fields**, not events. `+=` can silently multiply subscribers across editor reloads
- Source: `Runtime/ResourcePackage/Operation/DownloaderOperation.cs:86-101`
- ✅ Fix: assign once: `downloader.DownloadUpdateCallback = OnUpdate;`

### ❌ P25. Assigning callbacks after `BeginDownload()`
- Symptom: early progress events fire against null callbacks; the wiring is too late
- Source: `DownloaderOperation.cs:330-336`
- ✅ Fix: wire every callback first, then `BeginDownload()`

### ❌ P26. Combining downloaders from different packages
- Symptom: YooAsset logs `The downloaders have different resource packages !` and aborts the combine
- Source: `DownloaderOperation.cs:289-295`
- ✅ Fix: run the two downloaders in parallel instead of combining them

---

## FileSystem & services mistakes

### ❌ P27. Assigning the wrong value type to a `FileSystemParametersDefine` key
- Symptom: runtime cast / format error deep inside the file system
- Source: `Runtime/FileSystem/FileSystemParametersDefine.cs` + the consumers listed in FILESYSTEM.md
- ✅ Fix: match the value type (e.g. `DOWNLOAD_WATCH_DOG_TIME` → `int` seconds, `REMOTE_SERVICES` → `IRemoteServices`, `DECRYPTION_SERVICES` → `IDecryptionServices`)

### ❌ P28. Returning null from `IRemoteServices.GetRemoteFallbackURL`
- Symptom: downloader attempts null URL; NRE
- Source: `Runtime/Services/IRemoteServices.cs:4-17` (interface contract is "return a URL")
- ✅ Fix: return the main URL when you have no real fallback

---

## Build / Collector mistakes

### ❌ P29. `IFilterRule` missing the `FindAssetType` property (2.3.16 breaking change)
- Symptom: `IFilterRule` implementation fails to compile
- Source: `Editor/AssetBundleCollector/CollectRules/IFilterRule.cs:29` + `CHANGELOG.md:179-192`
- ✅ Fix: add `public string FindAssetType => "t:Prefab";` (or the appropriate filter)

### ❌ P30. Enabling `ScriptableBuildParameters.ReplaceAssetPathWithAddress` while still loading by asset path
- Symptom: runtime `Load*` returns nothing — the manifest no longer stores paths
- Source: `Editor/AssetBundleBuilder/BuildPipeline/ScriptableBuildPipeline/ScriptableBuildParameters.cs:32-36`
- ✅ Fix: pick one — either keep it off, or commit to loading by address

---

## Legacy API migration (AI must not emit these against 2.3.18)

These are frequent training-data hallucinations. If you catch yourself typing one, stop.

| Do NOT emit | Why it's wrong | Instead |
|-------------|----------------|---------|
| `package.CreateResourceDownloader(10, 3, timeout: 60)` | The `timeout` parameter was removed in 2.3.16 (`CHANGELOG.md:173-177`). No such overload exists in 2.3.18. | Set `DOWNLOAD_WATCH_DOG_TIME` on `CacheFileSystemParameters` via `AddParameter` |
| `ResourceDownloaderOperation.timeout = 60` | Same as above — removed. | Same as above |
| `UpdatePackageVersionOperation` / `pkg.UpdatePackageVersionAsync()` | The class/method does not exist. | `RequestPackageVersionOperation op = pkg.RequestPackageVersionAsync(); var v = op.PackageVersion;` |
| `YooAssets.LoadAsset(...)` | Bare `LoadAsset` does not exist; YooAsset exposes `LoadAssetSync` / `LoadAssetAsync`. | `var pkg = YooAssets.GetPackage("…"); pkg.LoadAssetAsync<T>(location);` |
| Treating `YooAssets.LoadAssetAsync(...)` as nonexistent | Outdated rule. 2.3.18 has default-package static shortcuts in `YooAssetsExtension.cs`. | Prefer explicit `ResourcePackage`; if using the shortcut, call `YooAssets.SetDefaultPackage(pkg)` first. |
| `pkg.UnloadUnusedAssets()` (synchronous) | Does not exist. The sync `TryUnloadUnusedAsset(location)` only targets one asset. | `pkg.UnloadUnusedAssetsAsync(loopCount: 10)` — returns an `UnloadUnusedAssetsOperation` |
| `class MyFilterRule : IFilterRule { bool IsCollectAsset(...) … }` (no `FindAssetType`) | `FindAssetType` is required since 2.3.16. | Add `public string FindAssetType => "t:Prefab";` |
| `handle.OnValueChanged += ...` | YooAsset has no `OnValueChanged` hook; this is Netcode vocabulary. | `handle.Completed += OnLoaded;` or `await handle.Task;` |
| `downloader.DownloadUpdateCallback += OnUpdate` | Plain-field assignment; `+=` can accumulate subscribers across editor reloads. | `downloader.DownloadUpdateCallback = OnUpdate;` |
| `package.DestroyAsync().Wait()` | `DestroyAsync` returns a YooAsset `DestroyOperation`, not `Task` — `.Wait()` is not the API shape. | `yield return pkg.DestroyAsync();` or `await pkg.DestroyAsync().Task` |
| `YooAssets.Initialize(OperationSystemUpdateMode.Driver)` | `Initialize` only takes an optional `ILogger`. | `YooAssets.Initialize();` or `YooAssets.Initialize(myLogger)` |
| `ResourcePackage pkg = new ResourcePackage("DefaultPackage");` | Constructor is `internal`. | `YooAssets.CreatePackage("DefaultPackage")` |
| `package.LoadSceneAsync(location, LoadSceneMode.Additive, addToBuild: true)` | There is no `addToBuild` parameter; build inclusion is decided by the bundle layout. | `package.LoadSceneAsync(location, LoadSceneMode.Additive, LocalPhysicsMode.None, suspendLoad: false)` |
| `initParams.UnpackingPath = "…"` | Field does not exist on `InitializeParameters`. | Set `FileSystemParametersDefine.UNPACK_FILE_SYSTEM_ROOT` (added 2.3.18) on the buildin FS parameters |

Source for every "why it's wrong" claim: the files listed in the main [SKILL.md](./SKILL.md) rule table plus `CHANGELOG.md`. When in doubt, grep the Runtime or Editor directory before emitting code.
