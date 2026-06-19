---
name: unity-addressables-loading
description: "Addressables asset loading and instantiation (1.22.3 & 2.9.1) — LoadAssetAsync<T>, LoadAssetsAsync, InstantiateAsync, and key/label/IResourceLocation overloads. Addressables 资源加载与实例化(1.22.3 与 2.9.1:LoadAssetAsync、LoadAssetsAsync、InstantiateAsync,以及 key/label/IResourceLocation 重载)。"
type: reference
---

# Addressables - Loading & Instantiation

All rules here come from `Runtime/Addressables.cs` — versions **1.22.3** (Unity 2022) and **2.9.1** (Unity 6). All anchors cite the version they apply to.

## `LoadAssetAsync<T>`

| Signature | [1.22.3] | [2.9.1] |
|-----------|:--------:|:-------:|
| `LoadAssetAsync<T>(IResourceLocation location)` | `:1039` | `:1108` |
| `LoadAssetAsync<T>(object key)` | `:1050` | `:1127` |
| `LoadAsset<T>(IResourceLocation)` / `LoadAsset<T>(object key)` (non-Async) | `:993, 1007` — `[Obsolete]` | **Removed** |

**Key form**: `object key` accepts addressable name, label, AssetReference's RuntimeKey, or an IResourceLocation obtained from `LoadResourceLocationsAsync`. When multiple entries share a label, only the first match is loaded — use `LoadAssetsAsync` (plural) instead.

```csharp
var handle = Addressables.LoadAssetAsync<GameObject>("Player");
await handle.Task;
Instantiate(handle.Result);      // Prefab is cached in Addressables; DO NOT Destroy the prefab
Addressables.Release(handle);
```

## `LoadAssetsAsync<T>`

All plural-key overloads. Pay close attention to version differences.

| Signature | [1.22.3] | [2.9.1] | Notes |
|-----------|:--------:|:-------:|-------|
| `LoadAssetsAsync<T>(IList<IResourceLocation> locations, Action<T> callback)` | `:1185` | `:1197` | |
| `LoadAssetsAsync<T>(IList<IResourceLocation> locations, Action<T> callback, bool releaseDependenciesOnFailure)` | `:1227` | `:1239` | |
| `LoadAssetsAsync<T>(IList<object> keys, Action<T> callback, MergeMode mode)` | `:1276` — **present** | **Removed** | 2.9.1 replaces with `IEnumerable` overload |
| `LoadAssetsAsync<T>(IEnumerable keys, Action<T> callback, MergeMode mode)` | `:1309` | `:1272` | Both versions have this. |
| `LoadAssetsAsync<T>(IList<object> keys, Action<T> callback, MergeMode mode, bool releaseDependenciesOnFailure)` | `:1336` — **present** | **Removed** | 2.9.1 replaces with `IEnumerable` overload |
| `LoadAssetsAsync<T>(IEnumerable keys, Action<T> callback, MergeMode mode, bool releaseDependenciesOnFailure)` | `:1383` | `:1351` | |
| `LoadAssetsAsync<T>(string key, Action<T> callback = null)` | **Does not exist** | `:1304` | Convenient single-key-multi-asset load for labels |
| `LoadAssetsAsync<T>(string key, bool releaseDependenciesOnFailure, Action<T> callback = null)` | **Does not exist** | `:1396` | |
| `LoadAssetsAsync<T>(object key, Action<T> callback)` | `:1429` | `:1427` | |
| `LoadAssetsAsync<T>(object key, Action<T> callback, bool releaseDependenciesOnFailure)` | `:1471` | `:1469` | |
| `LoadAssets<T>(...)` (3 overloads, no Async) | `:1156, 1242, 1398` — `[Obsolete]` | **Removed** | |

The per-asset `callback` fires as each asset completes — useful for progress UI. The `handle.Result` is the final `IList<T>`.

## `MergeMode`

Controls how results from multiple keys combine. Defined inline on `Addressables.cs:2.9.1:687` / `1.22.3:697`:

```csharp
public enum MergeMode
{
    None = 0,          // First-key-wins: ignore subsequent keys
    UseFirst = 0,      // Alias for None
    Union,             // Combine unique results from every key
    Intersection,      // Keep only assets matching ALL keys (label intersection)
}
```

Classic label use: `LoadAssetsAsync<Sprite>(new[] {"UI", "Icon"}, null, MergeMode.Intersection)` loads sprites that carry BOTH labels.

## `InstantiateAsync`

| Signature | [1.22.3] | [2.9.1] |
|-----------|:--------:|:-------:|
| `InstantiateAsync(IResourceLocation, Transform parent = null, bool instantiateInWorldSpace = false, bool trackHandle = true)` | `:2009` | `:1828` |
| `InstantiateAsync(IResourceLocation, Vector3 position, Quaternion rotation, Transform parent = null, bool trackHandle = true)` | `:2023` | `:1842` |
| `InstantiateAsync(object key, Transform parent = null, ...)` | `:2036` | `:1855` |
| `InstantiateAsync(object key, Vector3 position, Quaternion rotation, ...)` | `:2050` | `:1869` |
| `InstantiateAsync(object key, InstantiationParameters, bool trackHandle = true)` | `:2062` | `:1881` |
| `InstantiateAsync(IResourceLocation, InstantiationParameters, bool trackHandle = true)` | `:2074` | `:1893` |
| `Instantiate(...)` (6 overloads, no Async) | `:1892-1972` — `[Obsolete]` | **Removed** |

Handle result is `GameObject`. The returned instance is already in the scene — you don't `Instantiate()` the result.

### `trackHandle` (default `true`)

- `true` — Addressables remembers which handle produced which instance. You can later call `Addressables.ReleaseInstance(gameObject)` and it finds the handle automatically.
- `false` — You must keep the handle yourself and call `Addressables.Release(handle)` explicitly. `ReleaseInstance(gameObject)` returns `false` without doing anything.

## `Release` overloads

| Signature | [1.22.3] | [2.9.1] |
|-----------|:--------:|:-------:|
| `Release(AsyncOperationHandle handle)` | `:1500` | `:1498` |
| `Release<TObject>(AsyncOperationHandle<TObject> handle)` | `:1491` | `:1489` |
| `Release<TObject>(TObject obj)` — release by asset object | **Does not exist** | `:1479` |

The `Release<TObject>(TObject obj)` on 2.9.1 is a convenience: pass the prefab/asset you got from `.Result`, Addressables looks up the cached handle and decrements. Only works if `trackHandle: true` was used (or for top-level `LoadAssetAsync` calls).

## `ReleaseInstance`

| Signature | [1.22.3] | [2.9.1] |
|-----------|:--------:|:-------:|
| `ReleaseInstance(AsyncOperationHandle handle)` | `:1520` | `:1518` |
| `ReleaseInstance(AsyncOperationHandle<GameObject> handle)` | `:1531` | `:1529` |
| `ReleaseInstance(GameObject instance)` | **Does not exist** | `:1508` |

Use `ReleaseInstance` — NOT `Destroy` — on anything produced by `InstantiateAsync`:

```csharp
// ✅ Correct
var handle = Addressables.InstantiateAsync("Enemy");
var go = await handle.Task;
// ... gameplay ...
Addressables.ReleaseInstance(go);         // 2.9.1 — direct GameObject release
// or on 1.22.3:
Addressables.ReleaseInstance(handle);
```

`GameObject.Destroy(go)` does NOT decrement the Addressables handle. The bundle stays loaded.

## `LoadResourceLocationsAsync`

Resolves keys to `IResourceLocation` objects without loading the asset. Useful for pre-flight checks or custom loading pipelines.

| Signature | [1.22.3] | [2.9.1] |
|-----------|:--------:|:-------:|
| `LoadResourceLocationsAsync(object key, Type type = null)` | `:1142` | `:1168` |
| `LoadResourceLocationsAsync(IList<object> keys, MergeMode, Type type = null)` | `:1087` | **Removed** |
| `LoadResourceLocationsAsync(IEnumerable keys, MergeMode, Type type = null)` | `:1108` | `:1148` |
| `LoadResourceLocations(...)` (no Async) | `:1065, 1122` — `[Obsolete]` | **Removed** |

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Passing `IList<object>` on 2.9.1

```csharp
// 1.22.3 — compiles, works
IList<object> keys = new List<object> { "A", "B" };
var h = Addressables.LoadAssetsAsync<GameObject>(keys, null, MergeMode.Union);

// 2.9.1 — this overload no longer exists. Use IEnumerable instead:
IEnumerable<string> keys = new[] { "A", "B" };
var h = Addressables.LoadAssetsAsync<GameObject>(keys, null, MergeMode.Union);   // :1272
```

Note that `List<object>` still implements `IEnumerable`, so `new List<object>{"A","B"}` keeps compiling — what changes is that the COMPILER picks the `IEnumerable` overload now. No runtime difference for the call itself.

### 2. Using `Destroy` on an Instantiated instance

```csharp
// ❌ WRONG — bundle refcount never drops
var go = await Addressables.InstantiateAsync("Enemy").Task;
GameObject.Destroy(go);

// ✅ CORRECT
var go = await Addressables.InstantiateAsync("Enemy").Task;
Addressables.ReleaseInstance(go);   // handles both destroy and refcount decrement
```

### 3. Releasing the handle AND the instance

```csharp
// ❌ WRONG — double release
var handle = Addressables.InstantiateAsync("Enemy");
var go = await handle.Task;
// ...
Addressables.ReleaseInstance(go);
Addressables.Release(handle);   // second decrement, handle invalid
```

With `trackHandle: true` (default), `ReleaseInstance` already releases the handle. Do one or the other, not both.

### 4. Calling `LoadAssetAsync` on a label that points to multiple assets

```csharp
// ❌ WRONG — only loads the FIRST asset, silently ignores the rest
var h = Addressables.LoadAssetAsync<Sprite>("IconLabel");

// ✅ CORRECT
var h = Addressables.LoadAssetsAsync<Sprite>("IconLabel", null);   // 2.9.1 string overload
// or on 1.22.3:
var h = Addressables.LoadAssetsAsync<Sprite>((object)"IconLabel", null);
```

### 5. Instantiating a prefab returned by `LoadAssetAsync`

```csharp
var prefab = await Addressables.LoadAssetAsync<GameObject>("Enemy").Task;

// ✅ Allowed — it's a normal prefab reference
var a = GameObject.Instantiate(prefab);   // no handle tracking — must Destroy manually
var b = GameObject.Instantiate(prefab);

// Later, when done with ALL instances:
GameObject.Destroy(a); GameObject.Destroy(b);
Addressables.Release(prefab);             // 2.9.1; or retain handle and Release that on 1.22.3
```

`LoadAssetAsync` + `Instantiate` is perfectly fine for pooling scenarios where you want a single bundle load and many cheap `Instantiate` calls. `InstantiateAsync` shines when each instance should have its own handle lifetime (e.g. one enemy per bundle + bundle unloads with enemy).

## Canonical template

```csharp
public class EnemySpawner : MonoBehaviour
{
    [SerializeField] AssetReferenceGameObject enemyRef;
    GameObject m_Prefab;
    AsyncOperationHandle<GameObject> m_PrefabHandle;

    async void OnEnable()
    {
        m_PrefabHandle = Addressables.LoadAssetAsync<GameObject>(enemyRef.RuntimeKey);
        m_Prefab = await m_PrefabHandle.Task;
    }

    public GameObject Spawn(Vector3 pos)
    {
        if (m_Prefab == null) return null;
        return Instantiate(m_Prefab, pos, Quaternion.identity);   // light clone — bundle already loaded
    }

    void OnDisable()
    {
        // Destroy your spawned clones normally — they are not tracked by Addressables
        if (m_PrefabHandle.IsValid())
            Addressables.Release(m_PrefabHandle);
    }
}
```

For a use-and-forget single enemy:

```csharp
async void SpawnOnce(string key, Vector3 pos)
{
    var go = await Addressables.InstantiateAsync(key, pos, Quaternion.identity).Task;
    await UniTask.Delay(5000);
    Addressables.ReleaseInstance(go);   // decrements refcount and destroys the GameObject
}
```
