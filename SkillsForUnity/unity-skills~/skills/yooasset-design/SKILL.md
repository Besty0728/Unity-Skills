---
name: unity-yooasset-design
description: "Source-anchored design rules for YooAsset v2.3.18. Load this before writing any ResourcePackage / InitializeAsync / AssetHandle / Downloader / FileSystem / AssetBundleBuilder code to avoid hallucinated APIs, PlayMode-parameter mismatch, and Handle leaks. Triggers: YooAsset, YooAssets, ResourcePackage, AssetHandle, SubAssetsHandle, AllAssetsHandle, RawFileHandle, SceneHandle, InitializeAsync, EPlayMode, EditorSimulateMode, OfflinePlayMode, HostPlayMode, WebPlayMode, CustomPlayMode, RequestPackageVersionAsync, UpdatePackageManifestAsync, ResourceDownloaderOperation, ResourceUnpackerOperation, FileSystemParameters, FileSystemParametersDefine, IDecryptionServices, IRemoteServices, IWebDecryptionServices, AssetBundleBuilder, AssetBundleCollector, BuildParameters, 资源管理, 热更新, 资源包, 资源更新, 资源下载, 资源加载, 资产管线, 资源打包, 资源构建, 包裹, 远端资源, 离线模式, 联机模式, 编辑器模拟模式, yooasset 设计, asset management, hot update, asset bundle update, asset loading, asset package, CDN resources, patch flow."
---

# YooAsset - Design Rules

Advisory module. Every rule is distilled from YooAsset **v2.3.18** (2025-12-04) source at `Assets/YooAsset/`. Each rule cites a concrete file/line so the reasoning is auditable and the AI does not improvise against stale memory.

> **Mode**: Documentation only — guidance for AI under all three operating modes; no REST skills to gate.

## When to Load This Module

Load before writing or reviewing any of:

- `YooAssets.Initialize()` / `CreatePackage()` / `Destroy()` bootstrap code
- default package shortcuts: `YooAssets.SetDefaultPackage(...)`, `YooAssets.LoadAssetAsync(...)`, `YooAssets.CreateResourceDownloader(...)`
- `ResourcePackage.InitializeAsync(...)` with any of the 5 `EPlayMode` variants
- `LoadAssetSync/Async`, `LoadSubAssetsAsync`, `LoadAllAssetsAsync`, `LoadRawFileAsync`, `LoadSceneAsync` and their matching `Handle` usage / release
- Patch flow: `RequestPackageVersionAsync → UpdatePackageManifestAsync → CreateResourceDownloader → BeginDownload`
- `FileSystemParameters` construction (Buildin / Cache / Editor / WebServer / WebRemote / custom)
- `IDecryptionServices` / `IRemoteServices` / `IWebDecryptionServices` implementations
- Editor-side `AssetBundleBuilder.Run(...)` and `AssetBundleCollector` configuration
- Any code branching on `EOperationStatus` / `EFileClearMode` / `PackageDetails`

## Critical Rule Summary (memorize even if you skip the sub-docs)

| # | Rule | Source anchor |
|---|------|---------------|
| 1 | Call `YooAssets.Initialize()` before any `CreatePackage()` and keep it as a process-level singleton. A second call only logs a warning and returns, but normal architecture should gate it with `YooAssets.Initialized`. The call creates a `[{nameof(YooAssets)}]` driver GameObject via `DontDestroyOnLoad` to pump `OperationSystem.Update()`. Do not `Destroy` this driver yourself. | `Runtime/YooAssets.cs:38-64` |
| 2 | `ResourcePackage.InitializeAsync(...)` has no separate `EPlayMode` argument: it infers play mode from the concrete `InitializeParameters` subclass. The five built-in subclasses map 1:1 to the five modes; an unknown subclass throws `NotImplementedException`, while a recognized but semantically wrong subclass wires the wrong file-system topology and fails later. | `Runtime/ResourcePackage/ResourcePackage.cs:107-135, 170-181`, `Runtime/InitializeParameters.cs:8-110` |
| 3 | `EditorSimulateMode` works only when `UNITY_EDITOR` is defined; `WebPlayMode` works only when `UNITY_WEBGL` is defined (and every other mode is rejected on WebGL). | `Runtime/ResourcePackage/ResourcePackage.cs:160-197` |
| 4 | Every `AssetHandle` / `SubAssetsHandle` / `AllAssetsHandle` / `RawFileHandle` / `SceneHandle` **must** be released via `Release()` or `Dispose()`. Without release, bundles never unload even with `AutoUnloadBundleWhenUnused = true`. | `Runtime/ResourceManager/Handle/HandleBase.cs:21-40`, `Runtime/InitializeParameters.cs:48-49` |
| 5 | `LoadAssetSync/Async` performs a DEBUG-only type guard that rejects any `Type` derived from `UnityEngine.Behaviour` and any type not derived from `UnityEngine.Object`. Treat the restriction as architectural even though the guard is compiled out of non-DEBUG builds. | `Runtime/ResourcePackage/ResourcePackage.cs:1172-1187` |
| 6 | YooAsset 2.3.18 includes default-package static shortcuts on `YooAssets` (`SetDefaultPackage`, `Load*`, `CreateResourceDownloader`, etc.). They are valid API, but architecture should prefer explicit `ResourcePackage` references in multi-package or library code. | `Runtime/YooAssetsExtension.cs:16, 217-295, 479-506` |
| 7 | `RequestPackageVersionAsync()` returns `RequestPackageVersionOperation` (exposes `.PackageVersion`). There is **no** `UpdatePackageVersionOperation` class in 2.3.18 — if you think you remember one, you are confusing it with the older name. | `Runtime/ResourcePackage/ResourcePackage.cs:225-231` |
| 8 | Before `UpdatePackageManifestAsync`, call `UnloadAllAssetsAsync()`; YooAsset logs a warning when loaders are still alive. | `Runtime/ResourcePackage/ResourcePackage.cs:242-247` |
| 9 | Downloader callbacks (`DownloadFinishCallback` / `DownloadUpdateCallback` / `DownloadErrorCallback` / `DownloadFileBeginCallback`) must be assigned **before** `BeginDownload()`. They are delegate fields, not events — only one subscriber per slot. | `Runtime/DownloadSystem` + `Runtime/ResourcePackage/Operation/DownloaderOperation.cs:86-101, 330-336` |
| 10 | `ResourcePackage.DestroyAsync()` must run before `YooAssets.RemovePackage()`. `RemovePackage` refuses when `InitializeStatus != EOperationStatus.None`. | `Runtime/YooAssets.cs:177-190`, `Runtime/ResourcePackage/ResourcePackage.cs:210-218` |
| 11 | The patch flow is strictly ordered: `InitializeAsync → RequestPackageVersionAsync → UpdatePackageManifestAsync(version) → CreateResourceDownloader → BeginDownload`. Jumping ahead (e.g. loading assets between version and manifest) is unsupported. | `Runtime/ResourcePackage/ResourcePackage.cs:225, 238, 972`, `Samples~/Space Shooter/.../PatchLogic/FsmNode/Fsm*.cs` |

## Sub-doc Routing

| Sub-doc | When to read |
|---------|--------------|
| [INIT.md](./INIT.md) | `Initialize` / `Destroy` / `CreatePackage` / `TryGetPackage` / `RemovePackage`; single driver GameObject; `OperationSystem` loop |
| [PLAYMODE.md](./PLAYMODE.md) | All 5 `EPlayMode` values, their matching `InitializeParameters` subclass, platform `#if` guards, and the runtime dispatch table |
| [HANDLES.md](./HANDLES.md) | `HandleBase` API (Release / IsDone / Progress / Status / LastError / Completed / Task / IEnumerator), `AssetHandle.Instantiate*`, reference counting, `AutoUnloadBundleWhenUnused` |
| [LOADING.md](./LOADING.md) | `LoadAsset`, `LoadSubAssets`, `LoadAllAssets`, `LoadRawFile`, `LoadScene` — all overloads + Behaviour/Type rejection + `LoadSceneParameters` |
| [UPDATE.md](./UPDATE.md) | Patch flow, `RequestPackageVersionOperation`, `UpdatePackageManifestOperation`, `PreDownloadContentOperation`, `ResourceDownloaderOperation` (4 callbacks, pause/resume/cancel, `Combine`) |
| [FILESYSTEM.md](./FILESYSTEM.md) | `FileSystemParameters` + 24 `FileSystemParametersDefine` constants + 5 factory helpers + `IDecryptionServices` / `IRemoteServices` / `IWebDecryptionServices` |
| [BUILD.md](./BUILD.md) | `AssetBundleBuilder.Run(...)`, `BuildParameters` (Scriptable / Raw / Simulate), `AssetBundleCollector`, `IFilterRule.FindAssetType`, `ScriptableBuildParameters.ReplaceAssetPathWithAddress` |
| [PITFALLS.md](./PITFALLS.md) | 30 concrete hallucination pitfalls + legacy API migration section |

## Routing to Other Modules

- Asmdef & assembly layout for YooAsset consumers → load [asmdef](../asmdef/SKILL.md)
- Async orchestration across multiple YooAsset operations → load [async](../async/SKILL.md)
- Architecture-level decisions (Addressables vs YooAsset, single-package vs multi-package) → load [architecture](../architecture/SKILL.md)
- Performance review of load/release hot paths → load [performance](../performance/SKILL.md)

## Version Scope

This document targets YooAsset **2.3.18** (2025-12-04). Key recent history:

- **2.3.18** — added `UNPACK_FILE_SYSTEM_ROOT` file-system parameter, `EFileClearMode.ClearBundleFilesByLocations`, `RawFileBuildParameters.IncludePathInHash`.
- **2.3.17** — **[CRITICAL]** fixed a CRC-validation bug that let corrupted downloads pass verification on 2.3.15/2.3.16. Also fixed a Package-destroy race where an in-flight `AssetBundle` load could block unload.
- **2.3.16** — removed the downloader `timeout` parameter (use `DOWNLOAD_WATCH_DOG_TIME` on the cache file system instead). `IFilterRule` gained a required `FindAssetType` property.
- **2.3.15** — bumped the manifest binary format; installers built with 2.3.15+ are **not readable** by 2.3.14 or earlier clients and vice versa. Added `FILE_VERIFY_MAX_CONCURRENCY`, `StripUnityVersion`, preview `UseWeakReferenceHandle` (under `YOOASSET_EXPERIMENTAL`).

Source: `Assets/YooAsset/CHANGELOG.md:5-275`.

## Migration Notes (hallucination shield)

| Legacy API (pre-2.3.18) | Status in 2.3.18 | Replacement | Source |
|-------------------------|------------------|-------------|--------|
| `CreateResourceDownloader(count, retry, timeout)` overload / `ResourceDownloaderOperation.timeout` property | **Removed** in 2.3.16 | Assign `DOWNLOAD_WATCH_DOG_TIME` on the `CacheFileSystemParameters` / `BuildinFileSystemParameters` | `CHANGELOG.md:173-177`, `FileSystemParametersDefine.cs:18` |
| `IFilterRule` without a `FindAssetType` property | **Breaking change** in 2.3.16 — compilation breaks | Implement `public string FindAssetType { get; }` that returns a Unity asset-type filter string | `CHANGELOG.md:179-192` |
| Old manifest binary (pre-2.3.15 client reading a 2.3.15+ manifest, or vice versa) | **Wire-incompatible** | Rebuild + re-ship installer; no runtime bridge exists | `CHANGELOG.md:196-198` |
| `YooAssets.LoadAsset(...)` | **Does not exist** — method names include `LoadAssetSync` / `LoadAssetAsync`, not bare `LoadAsset` | `package.LoadAssetAsync<T>(location)` or `YooAssets.LoadAssetAsync<T>(location)` after `SetDefaultPackage` | `Runtime/YooAssetsExtension.cs:217-295`, `Runtime/ResourcePackage/ResourcePackage.cs:641-732` |
| `YooAssets.LoadAssetAsync(...)` marked as hallucination | **Outdated rule** — 2.3.18 has this default-package shortcut | Prefer `package.LoadAssetAsync<T>(location)` for explicit ownership; static shortcut is acceptable only after `YooAssets.SetDefaultPackage(package)` | `Runtime/YooAssetsExtension.cs:16, 260-295` |
| `package.UnloadUnusedAssets()` (synchronous) | **Does not exist** | `package.UnloadUnusedAssetsAsync(int loopCount = 10)` returns an `UnloadUnusedAssetsOperation` | `Runtime/ResourcePackage/ResourcePackage.cs:355-361` |
| `UpdatePackageVersionOperation` class (often confused with the real type) | **Does not exist** | `RequestPackageVersionOperation`, with a `.PackageVersion` string property | `Runtime/ResourcePackage/ResourcePackage.cs:225-231` |
| `NetworkVariable<T>` / `OnValueChanged` style sync for assets (leaked in from Netcode) | **Does not exist in YooAsset** | Subscribe `AssetHandle.Completed` event or `await handle.Task` / `yield return handle` | `Runtime/ResourceManager/Handle/AssetHandle.cs:20-37`, `Runtime/ResourceManager/Handle/HandleBase.cs:152-173` |
| `ResourceDownloaderOperation.OnDownloadFinishCallback` (event-style) | Field is a plain delegate, not an event | Assign once: `downloader.DownloadFinishCallback = OnFinish;` — do not `+=` (single-slot field) | `Runtime/ResourcePackage/Operation/DownloaderOperation.cs:86-101` |

When in doubt, read the cited source — not your memory.
