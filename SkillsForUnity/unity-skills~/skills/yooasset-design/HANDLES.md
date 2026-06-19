---
name: unity-yooasset-handles
description: "YooAsset handles, release, and reference counting (v2.3.18) — the five handle classes, HandleBase, AssetHandle, and AutoUnloadBundleWhenUnused. YooAsset 句柄、释放与引用计数(v2.3.18:五种句柄类、HandleBase、AssetHandle、AutoUnloadBundleWhenUnused)。"
type: reference
---

# YooAsset - Handles, Release, Reference Counting

All rules come from `Runtime/ResourceManager/Handle/HandleBase.cs`, `Runtime/ResourceManager/Handle/AssetHandle.cs`, and the `AutoUnloadBundleWhenUnused` field on `Runtime/InitializeParameters.cs:48-49`.

## The five handle classes

```
HandleBase (abstract, IEnumerator, IDisposable)  — Runtime/ResourceManager/Handle/HandleBase.cs:6
├── AssetHandle        — AssetHandle.cs:5          single UnityEngine.Object + Instantiate helpers
├── SubAssetsHandle    — sub-objects inside one asset (e.g. sprite-sheet sub-sprites)
├── AllAssetsHandle    — every asset in one bundle
├── RawFileHandle      — plain file content (text / bytes) from a raw-file bundle
└── SceneHandle        — scene load; owns a LoadSceneParameters
```

Every handle is returned by a `ResourcePackage.Load*Sync/Async` call (see LOADING.md) and **must** be released.

## `HandleBase` — the shared API (verbatim, keep these signatures)

```csharp
public abstract class HandleBase : IEnumerator, IDisposable                // :6
{
    public void Release();                                                 // :21
    public void Dispose();                                                 // :37  => Release()

    public AssetInfo GetAssetInfo();                                       // :45
    public DownloadStatus GetDownloadStatus();                             // :53

    public EOperationStatus Status { get; }                                // :63
    public string LastError { get; }                                       // :76
    public float Progress { get; }                                         // :89
    public bool IsDone { get; }                                            // :102
    public bool IsValid { get; }                                           // :115

    public System.Threading.Tasks.Task Task { get; }                       // :152

    // IEnumerator — makes `yield return handle;` work in a coroutine
    bool IEnumerator.MoveNext();                                           // :163  returns !IsDone
    void IEnumerator.Reset();                                              // :167
    object IEnumerator.Current { get; }                                    // :170
}
```

### What each member really means

- `Release()` / `Dispose()` — drops the handle's contribution to the bundle refcount. Once refcount hits zero, the bundle becomes a candidate for unload (either on `UnloadUnusedAssetsAsync` or immediately when `AutoUnloadBundleWhenUnused = true`).
- `Status` — `None` before start, `Processing` during load, `Succeed` / `Failed` once `IsDone`.
- `LastError` — non-empty only when `Status == Failed`. Surface it in UI for release builds.
- `IsValid` — checks `Provider != null && !Provider.IsDestroyed`. Internal use emits warnings; prefer the plain `IsValid` in user code.
- `Task` — a `System.Threading.Tasks.Task` you can `await`. Internally bridges to `Provider.Task`.
- IEnumerator — lets `yield return handle;` work in coroutines without extra wrapping.

Source: `Runtime/ResourceManager/Handle/HandleBase.cs:21-173`.

## `AssetHandle` — extras on top of `HandleBase`

```csharp
public sealed class AssetHandle : HandleBase                               // AssetHandle.cs:5
{
    public event System.Action<AssetHandle> Completed;                     // :20
    public void WaitForAsyncComplete();                                    // :42

    public UnityEngine.Object AssetObject { get; }                         // :53
    public TAsset GetAssetObject<TAsset>() where TAsset : UnityEngine.Object;  // :67

    public GameObject InstantiateSync();                                   // :77
    public GameObject InstantiateSync(Transform parent);                   // :81
    public GameObject InstantiateSync(Transform parent, bool worldPositionStays);  // :85
    public GameObject InstantiateSync(Vector3 position, Quaternion rotation);      // :89
    public GameObject InstantiateSync(Vector3 position, Quaternion rotation, Transform parent);  // :93

    public InstantiateOperation InstantiateAsync(bool actived = true);     // :101
    public InstantiateOperation InstantiateAsync(Transform parent, bool actived = true);                 // :105
    public InstantiateOperation InstantiateAsync(Transform parent, bool worldPositionStays, bool actived = true);  // :109
    public InstantiateOperation InstantiateAsync(Vector3 position, Quaternion rotation, bool actived = true);       // :113
    public InstantiateOperation InstantiateAsync(Vector3 position, Quaternion rotation, Transform parent, bool actived = true);  // :117
}
```

### `Completed` event semantics

```csharp
public event System.Action<AssetHandle> Completed
{
    add
    {
        if (IsValidWithWarning == false)
            throw new System.Exception($"{nameof(AssetHandle)} is invalid");
        if (Provider.IsDone)
            value.Invoke(this);   // ← already done: fires synchronously in `+=`
        else
            _callback += value;
    }
    // remove is symmetric
}
```

Source: `Runtime/ResourceManager/Handle/AssetHandle.cs:20-37`. Two practical consequences:

1. Subscribing **after** the load already finished still fires the callback (on the same frame).
2. Subscribing on an invalid handle throws — guard with `if (handle.IsValid) handle.Completed += ...;`.

### `WaitForAsyncComplete()` — synchronous wait

Only use when you truly need to block the frame (e.g. a boot-time critical dependency). It marches the provider to completion inside the current call. On WebGL, this is effectively a no-op for many asset types because WebGL has no true sync file I/O; use `WebGLForceSyncLoadAsset = true` on your `InitializeParameters` when you need fully synchronous loads on WebGL.

Source: `Runtime/ResourceManager/Handle/AssetHandle.cs:42-47`, `Runtime/InitializeParameters.cs:54`.

### `AssetObject` vs `GetAssetObject<T>()`

Both resolve to the same `Provider.AssetObject`. Prefer the generic version because it returns the concrete type without an explicit cast; both return `null` when `IsValid == false`.

## Three ways to wait for one handle

```csharp
// 1. Coroutine
IEnumerator LoadSomething() {
    var h = package.LoadAssetAsync<GameObject>("player");
    yield return h;                   // IEnumerator on HandleBase
    if (h.Status == EOperationStatus.Succeed) {
        h.InstantiateSync();
    }
    h.Release();
}

// 2. async / await
async Task LoadSomethingAsync() {
    var h = package.LoadAssetAsync<GameObject>("player");
    await h.Task;
    if (h.Status == EOperationStatus.Succeed) {
        h.InstantiateSync();
    }
    h.Release();
}

// 3. Completed event
var h = package.LoadAssetAsync<GameObject>("player");
h.Completed += OnLoaded;
void OnLoaded(AssetHandle h) {
    if (h.Status == EOperationStatus.Succeed) h.InstantiateSync();
    h.Release();
}
```

All three are first-class. Do not mix "`yield return handle` + `await handle.Task`" on the same handle in the same scope — it works, but it's harder to reason about ownership.

## Reference counting and `AutoUnloadBundleWhenUnused`

```csharp
// Runtime/InitializeParameters.cs:48-49
public bool AutoUnloadBundleWhenUnused = false;
```

- Every successful load increments the refcount on the underlying bundle.
- `Release()` decrements the refcount.
- With `AutoUnloadBundleWhenUnused = false` (default): the bundle stays resident until you call `UnloadUnusedAssetsAsync()` or `UnloadAllAssetsAsync()`.
- With `AutoUnloadBundleWhenUnused = true`: the moment the last handle is released, `TryUnloadBundle()` fires immediately.

Source: `Runtime/ResourceManager/Handle/HandleBase.cs:27-30`:

```csharp
public void Release()
{
    if (IsValidWithWarning == false) return;
    Provider.ReleaseHandle(this);
    // Actively unload bundles whose refcount just hit zero
    if (Provider.RefCount == 0)
        Provider.TryUnloadBundle();
    Provider = null;
}
```

**Mandatory rule**: `AutoUnloadBundleWhenUnused` is **not** "I don't need to release handles anymore". It still requires `Release()` to drop refcount.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Forgetting to release

```csharp
// ❌ WRONG — handle leaks; bundle refcount never drops; UnloadUnusedAssetsAsync is now useless
var h = package.LoadAssetAsync<GameObject>("player");
yield return h;
h.InstantiateSync();
// no h.Release()

// ✅ CORRECT — release the moment you've pulled what you needed
var h = package.LoadAssetAsync<GameObject>("player");
yield return h;
var instance = h.InstantiateSync();
h.Release();   // instance is still alive; the handle is not
```

### 2. Using the handle after `Release()`

```csharp
// ❌ WRONG — IsValidWithWarning logs a warning; subsequent accesses return null/default
h.Release();
var obj = h.AssetObject;    // null

// ✅ CORRECT — treat Release() as terminal for the handle
```

### 3. Subscribing `Completed` on an invalid handle

```csharp
// ❌ WRONG — throws from the add accessor
if (h != null) h.Completed += OnLoaded;  // h may be valid-ref but Provider already destroyed

// ✅ CORRECT
if (h != null && h.IsValid) h.Completed += OnLoaded;
```

### 4. Using `InstantiateSync` on a failed handle

```csharp
// ❌ WRONG — Provider.AssetObject is null; InstantiateSync returns null silently
var go = h.InstantiateSync();

// ✅ CORRECT
if (h.Status == EOperationStatus.Succeed) {
    var go = h.InstantiateSync();
} else {
    Debug.LogWarning(h.LastError);
}
```

### 5. Casting `AssetObject` directly instead of `GetAssetObject<T>`

```csharp
// ❌ Works but drops compile-time type info
var prefab = (GameObject)h.AssetObject;

// ✅ Generic accessor preserves the type and handles IsValid for you
var prefab = h.GetAssetObject<GameObject>();
```

### 6. `IsValid` misuse when `Release()` already ran

```csharp
// ❌ WRONG — after Release(), IsValid is false; but the class-level field still points at the old instance
h.Release();
if (h.IsValid) h.Completed += ...;   // never fires

// ✅ CORRECT — set your reference to null right after Release
h.Release(); h = null;
```

### 7. Holding an `AssetHandle` across a `package.UpdatePackageManifestAsync` call

```csharp
// ❌ WRONG — UpdatePackageManifestAsync warns and may misbehave if loaders are alive
h = package.LoadAssetAsync<GameObject>("player");
yield return h;
yield return package.UpdatePackageManifestAsync(newVersion);   // warning in console

// ✅ CORRECT — drain before patch
h.Release(); h = null;
yield return package.UnloadAllAssetsAsync();
yield return package.UpdatePackageManifestAsync(newVersion);
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:242-247` — the warning is emitted by `ResourcePackage.UpdatePackageManifestAsync` when `_resourceManager.HasAnyLoader()` is true.

## Scene handles are special

`SceneHandle` (see LOADING.md) is returned by `LoadSceneAsync`. Releasing it triggers `UnloadSceneAsync` automatically. Do **not** call `SceneManager.UnloadSceneAsync(scene)` on a YooAsset-loaded scene — use `sceneHandle.UnloadAsync()` / `Release()`.

## Canonical handle template

```csharp
using System.Collections;
using UnityEngine;
using YooAsset;

public class OneShotPrefab : MonoBehaviour
{
    [SerializeField] private string _location = "player";
    private AssetHandle _handle;

    IEnumerator Start()
    {
        var package = YooAssets.GetPackage("DefaultPackage");

        _handle = package.LoadAssetAsync<GameObject>(_location);
        yield return _handle;

        if (_handle.Status == EOperationStatus.Succeed)
            _handle.InstantiateSync();
        else
            Debug.LogError($"Load {_location} failed: {_handle.LastError}");
    }

    void OnDestroy()
    {
        // Release is idempotent-ish: guards with IsValidWithWarning internally
        _handle?.Release();
        _handle = null;
    }
}
```
