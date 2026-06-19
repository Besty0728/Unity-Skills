---
name: unity-addressables-handles
description: "AsyncOperationHandle and AsyncOperationHandle<T> lifecycle (Addressables 1.22.3 & 2.9.1) — the handle structs, completion, status, release, and ref-counting. AsyncOperationHandle 与 AsyncOperationHandle 泛型句柄的生命周期(1.22.3 与 2.9.1:句柄结构体、完成、状态、释放、引用计数)。"
type: reference
---

# Addressables - AsyncOperationHandle lifecycle

All rules here come from `Runtime/ResourceManager/AsyncOperations/AsyncOperationHandle.cs` and `Runtime/ResourceManager/AsyncOperations/AsyncOperationBase.cs` — versions **1.22.3** and **2.9.1**. The handle struct is effectively identical across both versions; differences noted inline.

## The struct(s)

Addressables exposes two handle structs:

```csharp
// Typed
public struct AsyncOperationHandle<TObject> : IEnumerator, IEquatable<AsyncOperationHandle<TObject>>
{
    public event Action<AsyncOperationHandle<TObject>> Completed;       // :101 [2.9.1]
    public event Action<AsyncOperationHandle> CompletedTypeless;        // :118 [2.9.1]
    public event Action<AsyncOperationHandle> Destroyed;                // :149 [2.9.1]
    public TObject Result { get; }                                      // :273 [2.9.1]
    public AsyncOperationStatus Status { get; }                         // :281 [2.9.1]
    public bool IsDone { get; }                                         // :218 [2.9.1]
    public bool IsValid();                                              // :227 [2.9.1]
    public Exception OperationException { get; }                        // :235 [2.9.1]
    public float PercentComplete { get; }                               // :248 [2.9.1]
    public Task<TObject> Task { get; }                                  // :289 [2.9.1]
    public string DebugName { get; }                                    // :127 [2.9.1]
    public DownloadStatus GetDownloadStatus();                          // :47  [2.9.1]
    public TObject WaitForCompletion();                                 // :178 [2.9.1]
    public void Release();                                              // :264 [2.9.1]
    public void ReleaseHandleOnCompletion();                            // :110 [2.9.1]
    public void GetDependencies(List<AsyncOperationHandle> deps);       // :141 [2.9.1]
}

// Non-typed (implicit conversion from typed; does not increment refcount)
public struct AsyncOperationHandle : IEnumerator
{
    public event Action<AsyncOperationHandle> Completed;                // :385 [2.9.1]
    public object Result { get; }                                       // :545 [2.9.1]
    public AsyncOperationHandle<T> Convert<T>();                        // :405 [2.9.1]
    public object WaitForCompletion();                                  // :591 [2.9.1]
    public void Release();                                              // :536 [2.9.1]
    public void ReleaseHandleOnCompletion();                            // :394 [2.9.1]
}
```

Source anchors use 2.9.1 line numbers; 1.22.3 layout differs by a handful of lines but the surface is the same.

## Reference counting

- Every Load/Instantiate/Download call returns a handle with refcount **1**.
- Implicit conversion (typed → non-typed) does **not** increment. The two structs share the same internal operation.
- Calling `handle.Release()` decrements by 1. When refcount hits 0 the operation is destroyed, the bundle unloaded, and the handle becomes invalid.
- Using an invalid handle (`IsValid() == false`) throws: `Attempting to use an invalid operation handle` (`AsyncOperationHandle.cs:2.9.1:210, 465`).

## `Completed` event

```csharp
// ✅ Fires once. If the handle has already completed when you subscribe,
//    the callback is deferred to the end of the current frame (LateUpdate).
var handle = Addressables.LoadAssetAsync<GameObject>("Enemy");
handle.Completed += op =>
{
    if (op.Status == AsyncOperationStatus.Succeeded)
        Instantiate(op.Result);
    // op == handle (same struct) — no need to retain the lambda closure over handle.
};
```

Implementation: `AsyncOperationHandle.cs:2.9.1:101-105` forwards to `InternalOp.Completed`. The deferred-dispatch promise in the xmldoc lives in `AsyncOperationBase.cs`.

## `Task` / `await`

```csharp
GameObject prefab = await Addressables.LoadAssetAsync<GameObject>("Enemy").Task;
```

- `handle.Task` (`AsyncOperationHandle.cs:2.9.1:289`) caches a `Task<TObject>` wrapper. Reading `.Task` multiple times returns the same Task.
- Awaiting the Task does NOT release the handle. You still need to call `Release` (unless `autoReleaseHandle` was set on the originating call).

## `IEnumerator` / coroutine

```csharp
IEnumerator Load() {
    var handle = Addressables.LoadAssetAsync<GameObject>("Enemy");
    yield return handle;                         // the handle IS an IEnumerator
    if (handle.Status == AsyncOperationStatus.Succeeded)
        Instantiate(handle.Result);
    Addressables.Release(handle);
}
```

`AsyncOperationHandle<T>.MoveNext()` returns `!IsDone` (`AsyncOperationHandle.cs:2.9.1:305`).

## `WaitForCompletion`

Blocking, synchronous. Pumps the ResourceManager on the calling thread until `IsDone`. DO NOT use on the main thread in shipped gameplay code.

```csharp
// 2.9.1 source :178-203
public TObject WaitForCompletion()
{
#if !UNITY_2021_1_OR_NEWER
    AsyncOperationHandle.IsWaitingForCompletion = true;
    try { ... }
    finally { ... m_InternalOp?.m_RM?.Update(Time.unscaledDeltaTime); }
#else
    if (IsValid() && !InternalOp.IsDone)
        InternalOp.WaitForCompletion();
    m_InternalOp?.m_RM?.Update(Time.unscaledDeltaTime);
    ...
#endif
}
```

**WebGL**: Unity's JS backend does not support a synchronous wait on async operations. On 2.9.1 WebGL builds, `WaitForCompletion` throws. 1.22.3 behavior depended on Unity version but was never reliable.

**Acceptable uses**:
- Edit-mode Editor scripts / tests (not gameplay).
- Hidden loading screens where you have already proved the data is locally cached and the operation is purely synchronous.
- Warm-up phases guarded by `#if !UNITY_WEBGL`.

## `ReleaseHandleOnCompletion`

Convenience — registers `op => op.Release()` on the `Completed` event.

```csharp
// :110 [2.9.1]
public void ReleaseHandleOnCompletion()
{
    Completed += op => op.Release();
}
```

Use when you don't need `Result` after completion (fire-and-forget warmups):

```csharp
var warm = Addressables.DownloadDependenciesAsync("BossAssets");
warm.ReleaseHandleOnCompletion();   // refcount auto-drops once done
```

## `Status`

`AsyncOperationStatus.None / InProgress / Succeeded / Failed` (enum defined in `AsyncOperationStatus.cs`). Only read `Result` when `Status == Succeeded`. Check `OperationException` when `Status == Failed`.

```csharp
if (handle.Status == AsyncOperationStatus.Failed)
{
    Debug.LogError(handle.OperationException);
    Addressables.Release(handle);
    return;
}
```

## `GetDownloadStatus`

```csharp
// :47 [2.9.1]
public DownloadStatus GetDownloadStatus()
```

Returns a `DownloadStatus { DownloadedBytes, TotalBytes, IsDone }` struct (`Runtime/ResourceManager/AsyncOperations/DownloadStatus.cs`). Reflects THIS operation and its transitive dependencies. Use for progress UI on `DownloadDependenciesAsync` handles.

`PercentComplete` mixes init / download / load / instantiate — it is NOT a pure download percentage. For download progress UIs, compute `(float)status.DownloadedBytes / status.TotalBytes`.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Forgetting to release

```csharp
// ❌ WRONG — bundle stays in memory until domain reload
var handle = Addressables.LoadAssetAsync<GameObject>("Enemy");
Instantiate(await handle.Task);

// ✅ CORRECT
var handle = Addressables.LoadAssetAsync<GameObject>("Enemy");
var prefab = await handle.Task;
var instance = Instantiate(prefab);
// Option A: keep the handle, release when done with the asset
// Option B: use InstantiateAsync + ReleaseInstance (see LOADING.md) — the instance carries its own handle
```

### 2. Double release

```csharp
// ❌ WRONG
var handle = Addressables.LoadAssetAsync<GameObject>("Enemy");
await handle.Task;
Addressables.Release(handle);
handle.Release();   // throws "Attempting to use an invalid operation handle"
```

One `Release` per `Acquire` (every Load/Instantiate implicitly acquires once). The handle becomes invalid after the first release.

### 3. Using `.Result` without checking `Status`

```csharp
// ❌ WRONG — Result is default(T) if the op failed
var handle = Addressables.LoadAssetAsync<GameObject>("Enemy");
await handle.Task;
Instantiate(handle.Result);   // NullReferenceException if download failed

// ✅ CORRECT
var handle = Addressables.LoadAssetAsync<GameObject>("Enemy");
await handle.Task;
if (handle.Status == AsyncOperationStatus.Succeeded)
    Instantiate(handle.Result);
else
    Debug.LogError(handle.OperationException);
Addressables.Release(handle);
```

### 4. Awaiting `handle` (the struct) instead of `handle.Task`

```csharp
// ❌ DOES NOT COMPILE — AsyncOperationHandle<T> has no GetAwaiter extension in Addressables itself
await Addressables.LoadAssetAsync<GameObject>("Enemy");

// ✅ CORRECT — either .Task or a coroutine yield
await Addressables.LoadAssetAsync<GameObject>("Enemy").Task;
```

(UniTask and some utility packages add a `.GetAwaiter()` extension; Addressables itself does not. If your code relies on one, that's a package-specific extension, not the Addressables API surface.)

### 5. Using `WaitForCompletion` on WebGL

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
// ❌ throws on WebGL player
var prefab = Addressables.LoadAssetAsync<GameObject>("Enemy").WaitForCompletion();
#else
// Fine in Editor / Standalone — but still prefer async elsewhere
var prefab = Addressables.LoadAssetAsync<GameObject>("Enemy").WaitForCompletion();
#endif
```

## Canonical load-release template

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class AssetLoader
{
    AsyncOperationHandle<GameObject> m_Handle;

    public async Task<GameObject> LoadAsync(string key)
    {
        if (m_Handle.IsValid())
            Addressables.Release(m_Handle);            // ditch the previous one

        m_Handle = Addressables.LoadAssetAsync<GameObject>(key);
        await m_Handle.Task;

        if (m_Handle.Status != AsyncOperationStatus.Succeeded)
        {
            var ex = m_Handle.OperationException;
            Addressables.Release(m_Handle);
            m_Handle = default;
            throw new System.Exception($"Failed to load {key}", ex);
        }
        return m_Handle.Result;
    }

    public void Unload()
    {
        if (m_Handle.IsValid())
            Addressables.Release(m_Handle);
        m_Handle = default;
    }
}
```

## Version differences

The handle struct surface is identical across 1.22.3 and 2.9.1 — Addressables treats `AsyncOperationHandle` as a stable public contract. The only meaningful difference:

- **2.9.1** removed `Addressables.Release<TObject>(AsyncOperationHandle<TObject>)` ambiguity by adding `Addressables.Release<TObject>(TObject obj)` (`Addressables.cs:2.9.1:1479`). You can now release by the asset object too. On 1.22.3 only handle-based release exists.

```csharp
// 2.9.1 only
GameObject prefab = await Addressables.LoadAssetAsync<GameObject>("Enemy").Task;
// ...use prefab...
Addressables.Release(prefab);   // looks up the handle internally
```

On 1.22.3 the equivalent is impossible — you must retain the handle and call `Addressables.Release(handle)`.
