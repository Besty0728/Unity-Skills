---
name: unity-addressables-pitfalls
description: "30 concrete Addressables pitfalls verified against 1.22.3 and 2.9.1 — 1.x-vs-2.x surface mismatches, version-tagged hallucinations, and source-cited fixes. Addressables 的 30 个真实陷阱(对照 1.22.3 与 2.9.1 验证:1.x 与 2.x 接口错配、按版本标注的幻觉点、附源码出处的修正)。"
type: reference
---

# Addressables - Pitfalls (30 concrete hallucinations)

All pitfalls below are verified against `com.unity.addressables@1.22.3` and `com.unity.addressables@8460f1c9c927` (v2.9.1). Each entry tags the affected version(s) and cites the source anchor.

The #1 meta-rule: **training-data snapshots mix 1.x and 2.x**. If you emit code using the 1.x-only surface while targeting Unity 6, compilation fails. When in doubt, read the cited source.

---

## Section A — Removed / Obsolete API hallucinations

### 1. [2.9.1] Calling `Addressables.LoadAsset` (no Async)

**Symptom**: `error CS0117: 'Addressables' does not contain a definition for 'LoadAsset'`

**Source**: `Addressables.cs:1.22.3:992-1007` (`[Obsolete]`); `Addressables.cs:2.9.1` has no `LoadAsset` member.

**Fix**: Use `Addressables.LoadAssetAsync<T>(key)` — `Addressables.cs:2.9.1:1127`.

---

### 2. [2.9.1] Calling `Addressables.Instantiate` (no Async)

**Symptom**: `error CS0117: 'Addressables' does not contain a definition for 'Instantiate'`

**Source**: `Addressables.cs:1.22.3:1892-1972` (all 6 overloads `[Obsolete]`); removed on 2.9.1.

**Fix**: `Addressables.InstantiateAsync(key, ...)` — `Addressables.cs:2.9.1:1855`.

---

### 3. [2.9.1] Calling `Addressables.LoadScene` / `UnloadScene` (no Async)

**Symptom**: Compile error — both methods are `[Obsolete]` on 1.22.3 (`Addressables.cs:2089, 2105, 2180-2225`) and removed on 2.9.1.

**Fix**: `LoadSceneAsync` / `UnloadSceneAsync`. On 2.9.1, `LoadSceneAsync` has a `SceneReleaseMode` parameter — see [SCENE.md](./SCENE.md).

---

### 4. [2.9.1] Awaiting `Addressables.InitializationOperation`

**Symptom**: Compile error — property removed. On 1.22.3 the property returns `default(AsyncOperationHandle<IResourceLocator>)` which is equivalent to yielding on nothing.

**Source**: `Addressables.cs:1.22.3:981-982` — `[Obsolete]`, body is `=> default`.

**Fix**: `await Addressables.InitializeAsync().Task` — see [INIT.md](./INIT.md).

---

### 5. [2.9.1] `IList<object>` keys overload

**Symptom**: `The call is ambiguous` OR `CS1503: cannot convert from 'List<object>' to '<some other type>'`.

**Source**: 1.22.3 had both `IList<object>` and `IEnumerable` overloads. 2.9.1 removed the `IList<object>` variant of: `LoadResourceLocationsAsync` (`1.22.3:1087`), `LoadAssetsAsync` (`1.22.3:1275, 1336`), `GetDownloadSizeAsync` (`1.22.3:1584`), `DownloadDependenciesAsync` (`1.22.3:1662`), `ClearDependencyCacheAsync` (`1.22.3:1732, 1814`).

**Fix**: Pass `IEnumerable` — `List<T>`, `T[]`, `IEnumerable<T>` all work. The runtime behavior is identical.

---

### 6. [Both] Using `ResourceManager.RegisterDiagnosticCallback`

**Symptom**: 1.22.3 — warning. 2.9.1 — compile error.

**Source**: `ResourceManager.cs:1.22.3:353` — `[Obsolete]`.

**Fix**: Addressables Profiler module (`Window → Analysis → Profiler → Addressables`). No public API replacement.

---

### 7. [2.9.1] Referencing `LegacyResourcesLocator` or `LegacyResourcesProvider`

**Symptom**: `The type or namespace name 'LegacyResourcesLocator' could not be found`.

**Source**: Removed types. 1.22.3 had them in `Runtime/ResourceLocators/` and `Runtime/ResourceManager/ResourceProviders/`. Absent on 2.9.1.

**Fix**: Migrate Resources-folder assets into Addressables groups. If you still need Resources, call `UnityEngine.Resources.Load` directly — it's a separate subsystem.

---

### 8. [2.9.1] Subscribing to `DiagnosticEvent` / `DiagnosticEventCollector`

**Symptom**: `The type or namespace name 'DiagnosticEvent' could not be found`.

**Source**: Removed types. Diagnostics now flow through `Runtime/ResourceManager/Diagnostics/Profiling/ProfilerRuntime.cs`.

**Fix**: Implement `IProfilerEmitter` and register through `Profiler.BeginSample` blocks, or consume the Profiler's Addressables frame data.

---

## Section B — Handle lifecycle hallucinations

### 9. [Both] Forgetting to `Release` a loaded asset

**Symptom**: Memory keeps growing. AssetBundle unload never happens. `Resources.UnloadUnusedAssets()` has no effect on bundles held by Addressables handles.

**Source**: `AsyncOperationHandle.cs:2.9.1:264-268, 536-540` — `Release()` decrements refcount; only at 0 does the bundle unload.

**Fix**: Every `Load*Async` has a matching `Release` / `ReleaseInstance`. For instances created via `InstantiateAsync`, use `ReleaseInstance` (it handles both the GameObject destroy and the handle release).

---

### 10. [Both] Double-releasing a handle

**Symptom**: `Exception: Attempting to use an invalid operation handle`.

**Source**: `AsyncOperationHandle.cs:2.9.1:210` — invalid-handle guard throws.

**Fix**: One `Release` per handle. After `Release`, the handle's internal operation is nulled; subsequent reads of `Status`, `Result`, `IsDone`, or a second `Release` all throw.

---

### 11. [Both] Mixing `Addressables.Release(handle)` with `AssetReference.ReleaseAsset()`

**Symptom**: Same `Attempting to use an invalid operation handle` exception, but stems from the AssetReference's cached `OperationHandle`.

**Source**: `AssetReference.cs:2.9.1:651` — `ReleaseAsset` calls `Addressables.Release` on the cached handle. Calling `Addressables.Release(handle)` first leaves the AssetReference pointing at a now-invalid handle.

**Fix**: Use ONE release path per load call. AssetReference load → AssetReference release. Direct `Addressables.LoadAssetAsync(key)` → `Addressables.Release(handle)`.

---

### 12. [Both] Using `GameObject.Destroy` on an `InstantiateAsync` instance

**Symptom**: No exception, but the bundle never unloads. Memory leak.

**Source**: `Addressables.cs` (both versions) — `ReleaseInstance` is the documented contract for instances produced by `InstantiateAsync`.

**Fix**: `Addressables.ReleaseInstance(go)` — or, on 2.9.1, `Addressables.Release(prefab)` works for the backing prefab handle.

---

### 13. [Both] `WaitForCompletion()` on WebGL

**Symptom**: Exception on WebGL player (sometimes a hang in Editor).

**Source**: `AsyncOperationHandle.cs:2.9.1:178-203, 591-613` — pumps the ResourceManager synchronously, unsupported on WebGL's JS backend.

**Fix**: `await handle.Task` or subscribe `handle.Completed`. Use `WaitForCompletion` only in Editor tests / hidden loading screens with known-local assets, guarded by `#if !UNITY_WEBGL || UNITY_EDITOR`.

---

### 14. [Both] Reading `handle.Result` without checking `Status`

**Symptom**: `NullReferenceException` when the load failed — `Result` is `default(T)`, not an exception.

**Source**: `AsyncOperationHandle.cs:2.9.1:273-276` — `Result` just returns `InternalOp.Result`, which is `default` on failure.

**Fix**: `if (handle.Status == AsyncOperationStatus.Succeeded) use(handle.Result); else log(handle.OperationException);`.

---

### 15. [Both] Awaiting the handle struct directly

**Symptom**: `CS1061: 'AsyncOperationHandle<TObject>' does not contain a definition for 'GetAwaiter'` — unless your project pulls in an extension from UniTask or a custom helper.

**Source**: `AsyncOperationHandle.cs:2.9.1` — no `GetAwaiter` method.

**Fix**: `await handle.Task` (`:289`) — always portable.

---

## Section C — Scene-API hallucinations

### 16. [2.9.1] Loading additively, then using `SceneManager.UnloadSceneAsync` with `OnlyReleaseSceneOnHandleRelease`

**Symptom**: Scene unloads but bundle remains resident — your scene memory does not go down.

**Source**: `ISceneProvider.cs:2.9.1:14-26` — `OnlyReleaseSceneOnHandleRelease` requires manual `Release`.

**Fix**: Either pass `SceneReleaseMode.ReleaseSceneWhenSceneUnloaded` (default) OR call `Addressables.Release(handle)` after `SceneManager.UnloadSceneAsync` completes.

---

### 17. [Both] Forgetting `activateOnLoad: false` for a scene transition

**Symptom**: The target scene activates mid-fade, causing a visible pop.

**Fix**: `LoadSceneAsync(key, LoadSceneMode.Single, activateOnLoad: false)`, await the handle, do your fade, then `yield return handle.Result.ActivateAsync()`.

---

### 18. [2.9.1] Copy-pasting 1.22.3 `LoadSceneAsync` expecting same behavior

**Symptom**: Code compiles on both. But on 2.9.1 the method now takes an extra optional `SceneReleaseMode` param — a new default (`ReleaseSceneWhenSceneUnloaded`) that matches 1.22.3 behavior, so no behavior change unless you change the value.

**Fix**: No action needed unless you want to opt into manual release. Understand the new parameter exists before relying on "old" behavior.

---

## Section D — Loading / overload hallucinations

### 19. [Both] Using `LoadAssetAsync` on a label matching multiple assets

**Symptom**: Only the FIRST asset loads; other assets with the same label are silently ignored.

**Source**: `Addressables.cs:2.9.1:1127` — `LoadAssetAsync<T>(object key)` picks first match; `LoadAssetsAsync<T>(...)` returns the full list.

**Fix**: Use `LoadAssetsAsync<T>(label, callback)` when multiple assets share a label.

---

### 20. [Both] Instantiating a prefab from `LoadAssetAsync` and then expecting `ReleaseInstance` to work

**Symptom**: `Addressables.ReleaseInstance(go)` returns `false` and the handle is not released. Memory leak when you `Destroy` the GameObject.

**Source**: `Addressables.cs` (both) — `ReleaseInstance` only works for instances produced by `InstantiateAsync` with `trackHandle: true`.

**Fix**: Either use `InstantiateAsync` in the first place, or retain the `LoadAssetAsync` handle yourself and `Addressables.Release(handle)` at the end of the lifetime.

---

### 21. [Both] Passing `LoadAssetAsync<SomeMonoBehaviour>(key)` for a prefab

**Symptom**: Load succeeds but `Result` is `null`. You cannot load a `MonoBehaviour` reference through Addressables; you must load the `GameObject` and `GetComponent` manually.

**Fix**: `LoadAssetAsync<GameObject>` + `Instantiate` + `GetComponent<SomeMonoBehaviour>`.

---

### 22. [2.9.1] Expecting `Addressables.Release<TObject>(TObject obj)` to work on 1.22.3

**Symptom**: `error CS1061: 'Addressables' does not contain a definition for 'Release' that takes a 'GameObject'`.

**Source**: `Addressables.cs:2.9.1:1479` — new overload, not present on 1.22.3.

**Fix**: On 1.22.3, retain the handle and call `Addressables.Release(handle)`.

---

## Section E — Update / catalog flow hallucinations

### 23. [Both] Skipping `CheckForCatalogUpdates` and always calling `UpdateCatalogs`

**Symptom**: Every launch re-downloads every registered catalog regardless of whether anything changed. Meaningful bandwidth cost for live-ops-heavy games.

**Fix**: See [UPDATE.md](./UPDATE.md). `CheckForCatalogUpdates` → `UpdateCatalogs(staleList)`.

---

### 24. [Both] Calling `UpdateCatalogs` with open asset handles

**Symptom**: Existing handles point at now-invalid provider state; subsequent operations on those handles behave unpredictably.

**Fix**: Release all load handles before updating catalogs. Bootstrap / update flow must run before gameplay load.

---

### 25. [Both] Calling `CleanBundleCache` while handles are alive

**Symptom**: Bundles get deleted while open; the OS reports stale file handles or subsequent loads fail.

**Fix**: Run `CleanBundleCache` during the loading screen, AFTER releasing all handles. Or use `UpdateCatalogs(autoCleanBundleCache: true)` which manages the timing internally.

---

### 26. [1.22.3] Expecting `autoCleanBundleCache` to be a 1.22.3 feature

The overload exists in both (`Addressables.cs:1.22.3:2356` and `:2.9.1:2147`), but on 1.22.3 it uses the older text-based `ContentCatalogData` format. On 2.9.1 it understands the new `BinaryCatalogInitializationData` format too. Behavior is similar but not byte-identical. No action needed for most projects.

---

## Section F — AssetReference hallucinations

### 27. [Both] Loading the same `AssetReference` twice without releasing

**Symptom**: `InvalidOperationException: Attempting to load an AssetReference that already has an active handle`.

**Source**: `AssetReference.cs:2.9.1:488` (Asset getter) and `:555` (`LoadAssetAsync<TObject>`) — the class caches the handle on `OperationHandle`.

**Fix**: Either `ReleaseAsset()` between loads, or use `Addressables.LoadAssetAsync<T>(ref_.RuntimeKey)` for independent concurrent loads.

---

### 28. [Both] Serializing `AssetReferenceT<GameObject>` directly

**Symptom**: Unity Inspector shows no drawer for the field; serialization ignores it.

**Source**: `AssetReferenceT<TObject>` is an open generic at `AssetReference.cs:2.9.1:22`. Unity's serializer cannot serialize open generics.

**Fix**: Use a closed subclass. `AssetReferenceGameObject` at `:94` is the canonical choice; define your own for custom types: `public class WeaponReference : AssetReferenceT<WeaponDefinition> { }`.

---

### 29. [2.9.1] Calling `AssetReference.LoadAsset<T>()` (no Async)

**Symptom**: Compile error on 2.9.1. Warning on 1.22.3.

**Source**: `AssetReference.cs:1.22.3:520-531` — `[Obsolete]`. Removed on 2.9.1.

**Fix**: `AssetReference.LoadAssetAsync<T>()`.

---

### 30. [Both] `assetRef.Asset` is null before load completes

**Symptom**: Reading `assetRef.Asset` before `LoadAssetAsync` has completed returns `null`. Not an exception — silently null.

**Source**: `AssetReference.cs:2.9.1:488` — `Asset => OperationHandle.IsValid() ? OperationHandle.Result : null`.

**Fix**: Always `await assetRef.LoadAssetAsync().Task` before reading `Asset`, or check `assetRef.IsDone`.

---

## Legacy API migration — quick-reference

When porting 1.22.3 code to 2.9.1, search for and fix ALL of these patterns:

| Pattern in 1.22.3 code | Replacement for 2.9.1 |
|------------------------|------------------------|
| `Addressables.Initialize(` | `Addressables.InitializeAsync(` |
| `Addressables.InitializationOperation` | `await Addressables.InitializeAsync().Task` |
| `Addressables.LoadAsset<` | `Addressables.LoadAssetAsync<` |
| `Addressables.LoadAssets<` | `Addressables.LoadAssetsAsync<` |
| `Addressables.LoadContentCatalog(` | `Addressables.LoadContentCatalogAsync(` |
| `Addressables.LoadResourceLocations(` | `Addressables.LoadResourceLocationsAsync(` |
| `Addressables.GetDownloadSize(` | `Addressables.GetDownloadSizeAsync(` |
| `Addressables.DownloadDependencies(` | `Addressables.DownloadDependenciesAsync(` |
| `Addressables.Instantiate(` | `Addressables.InstantiateAsync(` |
| `Addressables.LoadScene(` | `Addressables.LoadSceneAsync(` |
| `Addressables.UnloadScene(` | `Addressables.UnloadSceneAsync(` |
| `IList<object> keys` → multi-key API | `IEnumerable keys` |
| `AssetReference.LoadAsset(` | `AssetReference.LoadAssetAsync(` |
| `AssetReference.Instantiate(` | `AssetReference.InstantiateAsync(` |
| `AssetReference.LoadScene(` | `AssetReference.LoadSceneAsync(` |
| `LegacyResourcesLocator` / `LegacyResourcesProvider` | Remove; use Addressables groups |
| `DiagnosticEvent` / `DiagnosticEventCollector` | Remove; use Addressables Profiler |
| `ResourceManager.RegisterDiagnosticCallback` | Remove; use Addressables Profiler |

If a single method name appears as both `Foo` and `FooAsync` in your code — you are mixing versions. Always pick the `Async` name.
