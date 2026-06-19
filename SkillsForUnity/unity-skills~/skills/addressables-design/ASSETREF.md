---
name: unity-addressables-assetref
description: "AssetReference and AssetReferenceT<T> serialized field rules (Addressables 1.22.3 & 2.9.1) — type hierarchy, Inspector wiring, and cross-version differences. AssetReference 与 AssetReferenceT 序列化字段规则(Addressables 1.22.3 与 2.9.1:类型层级、Inspector 连接、跨版本差异)。"
type: reference
---

# Addressables - AssetReference & AssetReferenceT<T>

All rules here come from `Runtime/AssetReference.cs` — versions **1.22.3** (Unity 2022) and **2.9.1** (Unity 6).

`AssetReference` is the **serialized Inspector field** type — the canonical way to point at an Addressable asset from a MonoBehaviour / ScriptableObject without hard-coding a key string. This file is the most feature-parity across the two versions, but still has important differences.

## The type hierarchy (2.9.1 line numbers)

```
AssetReference                                                   // :310 — base, non-generic
 └─ AssetReferenceT<TObject>                                     // :22  — typed base
      ├─ AssetReferenceGameObject                                // :94
      ├─ AssetReferenceTexture / Texture2D / Texture3D           // :109, :124, :139
      ├─ AssetReferenceSprite                                    // :154
      └─ AssetReferenceAtlasedSprite                             // :238
```

Layout in 1.22.3 is the same; line numbers are ~8-12 lower due to removed obsolete wrappers.

## Critical fields & properties (2.9.1)

| Member | Line | Purpose |
|--------|:----:|---------|
| `AsyncOperationHandle OperationHandle { get; }` | `:338` | The cached handle after `LoadAssetAsync` / `LoadSceneAsync`. Read-only. Invalid until load starts. |
| `object RuntimeKey { get; }` | `:354` | GUID as an object. Pass to `Addressables.LoadAssetAsync<T>(key)` to sidestep AssetReference handle caching. |
| `string AssetGUID { get; }` | `:369` | Serialized GUID string. |
| `string SubObjectName { get; }` | `:382` | Sub-asset name (e.g. a specific sprite in a sheet). |
| `bool IsValid()` | `:419` | The cached `OperationHandle` is a valid, live handle. |
| `bool IsDone { get; }` | `:428` | The cached operation has completed. `false` if no op has been started. |
| `Object Asset { get; }` | `:488` | `OperationHandle.Result` unboxed to `Object`. Valid only after `LoadAssetAsync` succeeds. |

## Load / scene / instantiate

### Typed `AssetReferenceT<TObject>` path (preferred)

```csharp
// :46 [2.9.1] — corresponding 1.22.3 line :46 (same)
public virtual AsyncOperationHandle<TObject> LoadAssetAsync()
```

### Non-generic `AssetReference.LoadAssetAsync<TObject>()` (generic method on base)

```csharp
// :555 [2.9.1]
public virtual AsyncOperationHandle<TObject> LoadAssetAsync<TObject>()
```

### Scene and Instantiate

```csharp
// :581
public virtual AsyncOperationHandle<SceneInstance> LoadSceneAsync(
    LoadSceneMode loadMode = LoadSceneMode.Single, bool activateOnLoad = true, int priority = 100)

// :599
public virtual AsyncOperationHandle<SceneInstance> UnLoadScene()       // Note capital U-L!

// :616
public virtual AsyncOperationHandle<GameObject> InstantiateAsync(
    Vector3 position, Quaternion rotation, Transform parent = null)

// :632
public virtual AsyncOperationHandle<GameObject> InstantiateAsync(
    Transform parent = null, bool instantiateInWorldSpace = false)
```

### Release

```csharp
// :651
public virtual void ReleaseAsset()                         // matches LoadAssetAsync / LoadSceneAsync

// :667
public virtual void ReleaseInstance(GameObject obj)        // matches InstantiateAsync
```

## `[Obsolete]` wrappers on 1.22.3 (removed on 2.9.1)

Every non-Async method on `AssetReference` / `AssetReferenceT<T>` that existed on 1.22.3 is removed on 2.9.1:

| [1.22.3] | [2.9.1] |
|----------|:-------:|
| `AssetReferenceT<TObject>.LoadAsset()` at `:44-46` | **Removed** |
| `AssetReference.LoadAsset<TObject>()` at `:520-531` | **Removed** |
| `AssetReference.LoadScene()` at `:534-535` | **Removed** |
| `AssetReference.Instantiate(Vector3, Quaternion, Transform)` at `:551-553` | **Removed** |
| `AssetReference.Instantiate(Transform, bool)` at `:567-568` | **Removed** |

If your old code calls `myRef.LoadAsset()` — on 2.9.1 it does not compile.

## UI restrictions (unchanged in both versions)

`[AssetReferenceUIRestriction]`-derived attributes control the Inspector picker. Common built-ins:

- `[AssetReferenceUILabelRestriction(new[] { "ui", "icon" })]` — restricts the picker to assets bearing these labels.

Defined in `Runtime/AssetReferenceUIRestriction.cs` — identical in both versions.

## `RuntimeKey` vs direct `LoadAssetAsync`

You have two load paths from an `AssetReference`:

```csharp
[SerializeField] AssetReferenceGameObject ref_;

// Path A (recommended): the AssetReference owns the handle
AsyncOperationHandle<GameObject> a = ref_.LoadAssetAsync();
await a.Task;
var prefab = a.Result;
// ...later...
ref_.ReleaseAsset();                                    // ✅ uses cached OperationHandle

// Path B: use RuntimeKey to load independently — AssetReference does NOT cache the handle
var b = Addressables.LoadAssetAsync<GameObject>(ref_.RuntimeKey);
await b.Task;
var prefab2 = b.Result;
// ...later...
Addressables.Release(b);                                // ✅ release the handle YOU started
```

**Path A** sets `ref_.OperationHandle` — calling `LoadAssetAsync` a second time on the same `AssetReference` without releasing first throws `InvalidOperationException: Attempting to load an AssetReference that already has an active handle`.

**Path B** keeps `ref_.OperationHandle` clean. Useful when the same AssetReference is loaded multiple times for different lifetimes (e.g. pooling).

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Calling `Addressables.Release(handle)` on an `AssetReference`-loaded handle

```csharp
// ❌ WRONG — double release path
var h = ref_.LoadAssetAsync();
await h.Task;
Addressables.Release(h);          // decrements refcount once
ref_.ReleaseAsset();              // tries to release the SAME handle again → invalid op exception
```

```csharp
// ✅ CORRECT — one or the other. For AssetReference-cached handles, use ReleaseAsset.
var h = ref_.LoadAssetAsync();
await h.Task;
ref_.ReleaseAsset();
```

### 2. Loading the same AssetReference twice without releasing

```csharp
// ❌ WRONG [2.9.1 and 1.22.3] — throws "already has an active handle"
var a = ref_.LoadAssetAsync();
var b = ref_.LoadAssetAsync();   // Exception

// ✅ CORRECT — use RuntimeKey for second concurrent load
var a = ref_.LoadAssetAsync();
var b = Addressables.LoadAssetAsync<GameObject>(ref_.RuntimeKey);
```

### 3. Using `Instantiate` (no Async) — 1.22.3 Obsolete, 2.9.1 removed

```csharp
// ❌ WRONG
var go = ref_.Instantiate(Vector3.zero, Quaternion.identity);

// ✅ CORRECT
var handle = ref_.InstantiateAsync(Vector3.zero, Quaternion.identity);
var go = await handle.Task;
// ...
ref_.ReleaseInstance(go);
```

### 4. Destroying an instance returned by `InstantiateAsync` via `GameObject.Destroy`

```csharp
// ❌ WRONG — refcount stays, bundle leaks
var go = await ref_.InstantiateAsync().Task;
GameObject.Destroy(go);

// ✅ CORRECT
ref_.ReleaseInstance(go);       // method on AssetReference, not Addressables
```

### 5. Assuming `Asset` property returns something synchronously

```csharp
// ❌ WRONG — Asset is null until LoadAssetAsync has completed
var a = ref_.Asset;                                      // null
ref_.LoadAssetAsync();
var b = ref_.Asset;                                      // still null (op not done)

// ✅ CORRECT
await ref_.LoadAssetAsync().Task;
var a = ref_.Asset;                                      // now valid
```

### 6. Serializing an `AssetReference<TObject>` — `<TObject>` is not serializable

```csharp
// ❌ WRONG — Unity serializer cannot open a closed generic
[SerializeField] AssetReferenceT<GameObject> weapon_;

// ✅ CORRECT — use a concrete subclass
[SerializeField] AssetReferenceGameObject weapon_;
// or define your own closed subclass:
public class WeaponReference : AssetReferenceT<WeaponDefinition> {
    public WeaponReference(string guid) : base(guid) {}
}
```

The base `AssetReferenceT<T>` is generic and Unity cannot render its drawer. Every concrete type in the class hierarchy above (`AssetReferenceGameObject` etc.) is a closed generic and therefore serializable.

### 7. Ignoring `RuntimeKeyIsValid()` before loading

```csharp
// ❌ RISKY — LoadAssetAsync will fail if the AssetReference is empty (designer never assigned one)
await ref_.LoadAssetAsync().Task;

// ✅ CORRECT — guard in Awake / OnEnable
if (!ref_.RuntimeKeyIsValid()) { Debug.LogError("AssetReference unassigned"); return; }
await ref_.LoadAssetAsync().Task;
```

## Canonical ScriptableObject template

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;

[CreateAssetMenu]
public class EnemyDefinition : ScriptableObject
{
    [SerializeField] AssetReferenceGameObject prefab;

    public async Task<GameObject> SpawnAsync(Vector3 pos, Quaternion rot)
    {
        if (!prefab.RuntimeKeyIsValid())
            throw new System.InvalidOperationException($"EnemyDefinition '{name}' has no prefab");

        // Use RuntimeKey when multiple concurrent spawns are possible
        var handle = Addressables.InstantiateAsync(prefab.RuntimeKey, pos, rot);
        var go = await handle.Task;
        return go;
    }

    // For a single unique boss:
    GameObject m_Boss;
    public async Task EnsureBossAsync(Transform parent)
    {
        if (m_Boss == null && !prefab.IsValid())
        {
            var h = prefab.InstantiateAsync(parent, instantiateInWorldSpace: false);
            m_Boss = await h.Task;
        }
    }
    public void ReleaseBoss()
    {
        if (m_Boss != null) { prefab.ReleaseInstance(m_Boss); m_Boss = null; }
    }
}
```

## `AssetReferenceAtlasedSprite`

Special subclass at `:238`. Unique because Unity sprites bundled into a sprite atlas cannot be loaded via `LoadAssetAsync<Sprite>` — you must load the atlas first. The class embeds that logic. Use it for any sprite pointing INTO an atlas; use `AssetReferenceSprite` for standalone sprites.

## Summary: when to use what

| You want | Use |
|----------|-----|
| A reusable shared prefab across many spawns | `LoadAssetAsync<GameObject>` + `Instantiate` (see LOADING.md) |
| One-off instance with automatic bundle release | `AssetReference.InstantiateAsync` + `AssetReference.ReleaseInstance` |
| A designer-facing field in Inspector | `AssetReferenceT<T>` subclass (e.g. `AssetReferenceGameObject`) |
| Multiple concurrent loads of the same serialized reference | Use `ref_.RuntimeKey` + `Addressables.LoadAssetAsync<T>(key)` |
