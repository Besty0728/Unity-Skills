---
name: unity-addressables-design
description: Source-anchored design rules for Unity Addressables across two versions — 1.22.3 on Unity 2022 and 2.9.1 on Unity 6 — covering initialization, async operation handles, asset/scene loading, catalog updates, content download, AssetReference, and version migration. Use when writing or reviewing Addressables code, loading assets/scenes asynchronously, setting up hot-update or catalog refresh, or migrating between versions, even if the user just says "可寻址" or "热更". 为 Unity Addressables 提供源码锚定的设计规则(覆盖两个版本,Unity 2022 用 1.22.3、Unity 6 用 2.9.1;含初始化、异步操作句柄、资源/场景加载、目录更新、资源下载、AssetReference、版本迁移);当用户要编写或审查 Addressables 代码、异步加载资源/场景、配置热更新或目录刷新时使用。
---

# Addressables - Design Rules

Advisory module. Every rule is distilled from Unity Addressables source at two versions:
- **1.22.3** — `com.unity.addressables@1.22.3` (Unity 2022, min 2019.4)
- **2.9.1** — `com.unity.addressables@8460f1c9c927` (Unity 6, min 2023.1)

Each rule cites a concrete file/line so the reasoning is auditable and the AI does not improvise against stale memory.

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

## When to Load This Module

Load before writing or reviewing any of:

- `Addressables.InitializeAsync()` / `LoadContentCatalogAsync()` bootstrap code
- `LoadAssetAsync<T>` / `LoadAssetsAsync<T>` / `InstantiateAsync` and their handle release
- `LoadSceneAsync` / `UnloadSceneAsync` — especially with `SceneReleaseMode` (2.9.1)
- `CheckForCatalogUpdates` → `UpdateCatalogs` → `CleanBundleCache` patch flow
- `GetDownloadSizeAsync` / `DownloadDependenciesAsync` / `ClearDependencyCacheAsync`
- `AssetReference` / `AssetReferenceT<T>` field declarations and load/release
- Any code that calls `WaitForCompletion()` or uses `AsyncOperationHandle` directly
- Migration from 1.22.3 to 2.9.1 — removed APIs, changed overload signatures

## Version Difference Matrix

| Area | 1.22.3 (Unity 2022) | 2.9.1 (Unity 6) |
|------|---------------------|-----------------|
| Non-Async variants (`LoadAsset`, `Instantiate`, `LoadScene`, etc.) | `[Obsolete]` — compile warning | **Removed** — compile error |
| `IList<object>` multi-key overloads | Present | Replaced by `IEnumerable` |
| `SceneReleaseMode` enum | Does not exist | **New** — controls bundle lifetime on scene unload |
| `LoadSceneAsync` `releaseMode` param | Absent | `SceneReleaseMode.ReleaseSceneWhenSceneUnloaded` default |
| `LoadAssetsAsync<T>(string key, ...)` | Does not exist | **New** string-key overload |
| `UpdateCatalogs(bool autoCleanBundleCache, ...)` | Does not exist | **New** overload |
| `LegacyResourcesLocator` / `LegacyResourcesProvider` | Present | **Removed** |
| `DiagnosticEvent` / `DiagnosticEventCollector` | Present | **Removed** |
| `ResourceManagerEventCollector` | Present | **Removed** |
| `ResourceManager.RegisterDiagnosticCallback()` | `[Obsolete]` | **Removed** |
| `InitializationOperation` property | `[Obsolete]`, returns `default` | **Removed** |
| `BinaryCatalogInitializationData` | Does not exist | **New** |
| `CachedFileProvider` | Does not exist | **New** |

## Critical Rule Summary

| # | Rule | Version | Source anchor |
|---|------|---------|---------------|
| 1 | All non-Async variants (`LoadAsset`, `Instantiate`, `LoadScene`, `UnloadScene`, `GetDownloadSize`, `DownloadDependencies`, `Initialize`, `LoadContentCatalog`) are `[Obsolete]` in 1.22.3 and **removed** in 2.9.1. Always use the `*Async` form. | Both | `Addressables.cs:1.22.3:862-2226`, `Addressables.cs:2.9.1` (absent) |
| 2 | Every `AsyncOperationHandle` returned by a Load/Instantiate call MUST be released via `Addressables.Release(handle)`. Forgetting leaks the AssetBundle in memory indefinitely — even after the scene unloads. | Both | `AsyncOperationHandle.cs:2.9.1:178-203` |
| 3 | `WaitForCompletion()` blocks the calling thread synchronously. On WebGL it is **unsupported** and throws. Never call it on the main thread in production; use `await handle.Task` or the `Completed` event instead. | Both | `AsyncOperationHandle.cs:2.9.1:178-203` |
| 4 | `LoadSceneAsync` in 2.9.1 adds `SceneReleaseMode releaseMode` (default `ReleaseSceneWhenSceneUnloaded`). If a Single-mode load unloads your additive scene and you need the bundle to stay alive, pass `OnlyReleaseSceneOnHandleRelease` and release the handle manually. | 2.9.1 | `ISceneProvider.cs:2.9.1:14-26`, `Addressables.cs:2.9.1:1914` |
| 5 | Multi-key overloads changed from `IList<object>` to `IEnumerable` in 2.9.1. The old `IList<object>` overloads no longer exist — pass `IEnumerable` or `string[]`. | 2.9.1 | `Addressables.cs:2.9.1:1148,1566,1636` |
| 6 | `LegacyResourcesLocator` and `LegacyResourcesProvider` were removed in 2.9.1. Do not reference them in code targeting Unity 6. | 2.9.1 | `Runtime/ResourceLocators/` (absent in 2.9.1) |
| 7 | `ResourceManager.RegisterDiagnosticCallback()` was `[Obsolete]` in 1.22.3 and removed in 2.9.1. Use the Addressables Profiler window instead. | 2.9.1 | `ResourceManager.cs:1.22.3:353` (absent in 2.9.1) |
| 8 | Catalog update flow is strictly ordered: `CheckForCatalogUpdates → UpdateCatalogs`. In 2.9.1, `UpdateCatalogs(bool autoCleanBundleCache, ...)` can auto-clean stale bundles in one call. | Both | `Addressables.cs:2.9.1:2092-2147` |
| 9 | `AssetReference.LoadAssetAsync<T>()` returns a handle that must be released via `assetRef.ReleaseAsset()`, NOT `Addressables.Release(handle)`. Mixing the two causes double-release exceptions. | Both | `AssetReference.cs:1.22.3:44-46` |
| 10 | `InitializationOperation` property (1.22.3) is `[Obsolete]` and returns `default`. Do not await it. Use `await Addressables.InitializeAsync()` instead. | 1.22.3 | `Addressables.cs:1.22.3:981-982` |

## Sub-doc Routing

| Sub-doc | When to read |
|---------|--------------|
| [INIT.md](./INIT.md) | `InitializeAsync` / `LoadContentCatalogAsync` / catalog loading order / `autoReleaseHandle` semantics |
| [HANDLES.md](./HANDLES.md) | `AsyncOperationHandle<T>` lifecycle — `Completed`, `WaitForCompletion`, `Release`, `IsDone`, `Status`, `OperationException`, ref-counting |
| [LOADING.md](./LOADING.md) | `LoadAssetAsync`, `LoadAssetsAsync` (all overloads + version diff), `MergeMode`, `InstantiateAsync`, `ReleaseInstance` |
| [SCENE.md](./SCENE.md) | `LoadSceneAsync` / `UnloadSceneAsync` / `SceneInstance.ActivateAsync` / `SceneReleaseMode` (2.9.1) / `activateOnLoad=false` pattern |
| [UPDATE.md](./UPDATE.md) | `CheckForCatalogUpdates` → `UpdateCatalogs` flow / `autoCleanBundleCache` (2.9.1) / `CleanBundleCache` / `ResourceLocatorInfo` |
| [DOWNLOAD.md](./DOWNLOAD.md) | `GetDownloadSizeAsync` / `DownloadDependenciesAsync` / `ClearDependencyCacheAsync` / `DownloadStatus` struct |
| [ASSETREF.md](./ASSETREF.md) | `AssetReference` / `AssetReferenceT<T>` / `LoadAssetAsync` / `ReleaseAsset` / `OperationHandle` property / `IsDone` guard |
| [PITFALLS.md](./PITFALLS.md) | 30 concrete hallucination pitfalls with version tags + legacy API migration section |

## Routing to Other Modules

- Asmdef layout for Addressables consumers → load [asmdef](../asmdef/SKILL.md)
- Async orchestration across multiple Addressables operations → load [async](../async/SKILL.md)
- Architecture-level decisions (Addressables vs YooAsset, group strategy) → load [architecture](../architecture/SKILL.md)
- Performance review of load/release hot paths → load [performance](../performance/SKILL.md)

## Version Scope

This document targets two versions:
- **1.22.3** — shipped with Unity 2022 LTS. Contains `[Obsolete]` non-Async variants still present for migration.
- **2.9.1** — shipped with Unity 6 (2023.1+). All `[Obsolete]` APIs removed. New `SceneReleaseMode`, binary catalog format, `AutoGroupGenerator`.

When a rule applies to only one version it is tagged `[1.22.3]` or `[2.9.1]`. Untagged rules apply to both.

## Migration Notes (hallucination shield)

| Legacy API | Status | Replacement | Source |
|------------|--------|-------------|--------|
| `Addressables.Initialize()` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.InitializeAsync()` | `Addressables.cs:1.22.3:862-864` |
| `Addressables.LoadAsset<T>(key)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.LoadAssetAsync<T>(key)` | `Addressables.cs:1.22.3:992-1007` |
| `Addressables.LoadAssets<T>(keys, cb, mode)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.LoadAssetsAsync<T>(keys, cb, mode)` | `Addressables.cs:1.22.3:1242-1276` |
| `Addressables.Instantiate(key, ...)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.InstantiateAsync(key, ...)` | `Addressables.cs:1.22.3:1892-1972` |
| `Addressables.LoadScene(key, ...)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.LoadSceneAsync(key, ...)` | `Addressables.cs:1.22.3:2090-2106` |
| `Addressables.UnloadScene(handle, ...)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.UnloadSceneAsync(handle, ...)` | `Addressables.cs:1.22.3:2180-2226` |
| `Addressables.GetDownloadSize(key)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.GetDownloadSizeAsync(key)` | `Addressables.cs:1.22.3:1547` |
| `Addressables.DownloadDependencies(key)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `Addressables.DownloadDependenciesAsync(key)` | `Addressables.cs:1.22.3:1608` |
| `Addressables.InitializationOperation` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | `await Addressables.InitializeAsync()` | `Addressables.cs:1.22.3:981-982` |
| `LoadResourceLocationsAsync(IList<object> keys, ...)` | Present in 1.22.3, **removed** in 2.9.1 | `LoadResourceLocationsAsync(IEnumerable keys, ...)` | `Addressables.cs:2.9.1:1148` |
| `GetDownloadSizeAsync(IList<object> keys)` | Present in 1.22.3, **removed** in 2.9.1 | `GetDownloadSizeAsync(IEnumerable keys)` | `Addressables.cs:2.9.1:1566` |
| `DownloadDependenciesAsync(IList<object> keys, mode, ...)` | Present in 1.22.3, **removed** in 2.9.1 | `DownloadDependenciesAsync(IEnumerable keys, mode, ...)` | `Addressables.cs:2.9.1:1636` |
| `LegacyResourcesLocator` / `LegacyResourcesProvider` | Present in 1.22.3, **removed** in 2.9.1 | Use Addressables groups for all assets | `Runtime/ResourceLocators/LegacyResourcesLocator.cs:1.22.3` |
| `ResourceManager.RegisterDiagnosticCallback(...)` | `[Obsolete]` in 1.22.3, **removed** in 2.9.1 | Addressables Profiler window | `ResourceManager.cs:1.22.3:353` |
| `DiagnosticEvent` / `DiagnosticEventCollector` | Present in 1.22.3, **removed** in 2.9.1 | Addressables Profiler / custom IProfilerEmitter | `Runtime/ResourceManager/Diagnostics/:1.22.3` |

When in doubt, read the cited source — not your memory.
