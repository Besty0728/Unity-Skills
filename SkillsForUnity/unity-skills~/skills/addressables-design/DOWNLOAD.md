---
name: unity-addressables-download
description: "Addressables download, size, and cache APIs (1.22.3 & 2.9.1) — GetDownloadSizeAsync, DownloadDependenciesAsync, cache clearing, and the version-divergent download surface. Addressables 下载、大小与缓存 API(1.22.3 与 2.9.1:GetDownloadSizeAsync、DownloadDependenciesAsync、清缓存、跨版本差异较大的下载接口)。"
type: reference
---

# Addressables - Download, Size & Cache

All rules here come from `Runtime/Addressables.cs`, `Runtime/ResourceManager/AsyncOperations/DownloadStatus.cs`, and `Runtime/ResourceManager/AsyncOperations/GetDownloadSizeOperation.cs` (2.9.1 only) — versions **1.22.3** (Unity 2022) and **2.9.1** (Unity 6).

The download API set is one of the most version-divergent areas of the public surface because 2.9.1 cleaned up `IList<object>` into `IEnumerable` and added `string`-key conveniences.

## `GetDownloadSizeAsync`

Returns the **sum** of bytes that would be downloaded to satisfy the keys. Reads the catalog; does not actually download anything.

| Signature | [1.22.3] | [2.9.1] | Notes |
|-----------|:--------:|:-------:|-------|
| `GetDownloadSizeAsync(object key)` | `:1559` | `:1542` | |
| `GetDownloadSizeAsync(string key)` | **Does not exist** | `:1554` | String-key convenience |
| `GetDownloadSizeAsync(IList<object> keys)` | `:1584` | **Removed** | Use `IEnumerable` overload |
| `GetDownloadSizeAsync(IEnumerable keys)` | `:1596` | `:1566` | |
| `GetDownloadSize(object key)` (no Async) | `:1547` — `[Obsolete]` | **Removed** | |

Result is `long` — bytes remaining, NOT total bundle size. Assets already in the disk cache report 0.

```csharp
var sizeHandle = Addressables.GetDownloadSizeAsync("PreloadLabel");
long bytes = await sizeHandle.Task;
Debug.Log($"Need to download {bytes / 1024f / 1024f:F2} MB");
Addressables.Release(sizeHandle);
```

## `DownloadDependenciesAsync`

Downloads every bundle required by the keys, but does NOT load any asset into memory. Useful for preloading a level's assets during a loading screen.

| Signature | [1.22.3] | [2.9.1] | Notes |
|-----------|:--------:|:-------:|-------|
| `DownloadDependenciesAsync(object key, bool autoReleaseHandle = false)` | `:1628` | `:1588` | |
| `DownloadDependenciesAsync(IList<IResourceLocation> locations, bool autoReleaseHandle = false)` | `:1648` | `:1610` | |
| `DownloadDependenciesAsync(IList<object> keys, MergeMode, bool autoReleaseHandle = false)` | `:1662` | **Removed** | Use `IEnumerable` overload |
| `DownloadDependenciesAsync(IEnumerable keys, MergeMode, bool autoReleaseHandle = false)` | `:1686` | `:1636` | |
| `DownloadDependencies(object key)` (no Async) | `:1608` — `[Obsolete]` | **Removed** | |

`autoReleaseHandle` defaults to **`false`** (unlike Load APIs where it's usually true). You typically want to inspect `Status` and `GetDownloadStatus()` while the download runs, so keep the handle.

## `DownloadStatus` struct

```csharp
// Runtime/ResourceManager/AsyncOperations/DownloadStatus.cs — both versions
public struct DownloadStatus
{
    public long DownloadedBytes;
    public long TotalBytes;
    public bool IsDone;
    public float Percent => TotalBytes == 0 ? 1f : (float)DownloadedBytes / TotalBytes;
}
```

Get via `handle.GetDownloadStatus()` (`AsyncOperationHandle.cs:2.9.1:47, 513`). Walks the dependency tree once per call — don't call 120×/frame; cache per-frame.

## `ClearDependencyCacheAsync`

Deletes cached bundles matching the keys. Two families: void and `AsyncOperationHandle<bool>`.

### [1.22.3]

| Signature | Line | Returns |
|-----------|:----:|:-------:|
| `ClearDependencyCacheAsync(object key)` | `:1701` | void |
| `ClearDependencyCacheAsync(IList<IResourceLocation> locations)` | `:1716` | void |
| `ClearDependencyCacheAsync(IList<object> keys)` | `:1732` | void |
| `ClearDependencyCacheAsync(IEnumerable keys)` | `:1747` | void |
| `ClearDependencyCacheAsync(string key)` | `:1762` | void |
| `ClearDependencyCacheAsync(object key, bool autoReleaseHandle)` | `:1779` | `AsyncOperationHandle<bool>` |
| `ClearDependencyCacheAsync(IList<IResourceLocation> locations, bool autoReleaseHandle)` | `:1796` | `AsyncOperationHandle<bool>` |
| `ClearDependencyCacheAsync(IList<object> keys, bool autoReleaseHandle)` | `:1814` | `AsyncOperationHandle<bool>` |
| `ClearDependencyCacheAsync(IEnumerable keys, bool autoReleaseHandle)` | `:1831` | `AsyncOperationHandle<bool>` |
| `ClearDependencyCacheAsync(string key, bool autoReleaseHandle)` | `:1848` | `AsyncOperationHandle<bool>` |

### [2.9.1]

| Signature | Line | Returns |
|-----------|:----:|:-------:|
| `ClearDependencyCacheAsync(object key)` | `:1651` | void |
| `ClearDependencyCacheAsync(IList<IResourceLocation> locations)` | `:1666` | void |
| `ClearDependencyCacheAsync(IEnumerable keys)` | `:1681` | void |
| `ClearDependencyCacheAsync(string key)` | `:1696` | void |
| `ClearDependencyCacheAsync(object key, bool autoReleaseHandle)` | `:1713` | `AsyncOperationHandle<bool>` |
| `ClearDependencyCacheAsync(IList<IResourceLocation> locations, bool autoReleaseHandle)` | `:1730` | `AsyncOperationHandle<bool>` |
| `ClearDependencyCacheAsync(IEnumerable keys, bool autoReleaseHandle)` | `:1747` | `AsyncOperationHandle<bool>` |
| `ClearDependencyCacheAsync(string key, bool autoReleaseHandle)` | `:1764` | `AsyncOperationHandle<bool>` |

**2.9.1 removed** the `IList<object>` overloads (both the void and bool versions). Convert to `IEnumerable`.

## Canonical pre-download flow

```csharp
async Task PreloadAsync(string label)
{
    // 1. Size check
    var sizeOp = Addressables.GetDownloadSizeAsync(label);
    long bytes = await sizeOp.Task;
    Addressables.Release(sizeOp);

    if (bytes == 0) return;                // already cached

    // 2. User prompt (respect metered connections)
    if (!await ConfirmDownloadAsync(bytes)) return;

    // 3. Download with progress
    var dlOp = Addressables.DownloadDependenciesAsync(label, autoReleaseHandle: false);
    while (!dlOp.IsDone)
    {
        var st = dlOp.GetDownloadStatus();
        UpdateUI(st.DownloadedBytes, st.TotalBytes);
        await Task.Yield();
    }

    if (dlOp.Status != AsyncOperationStatus.Succeeded)
        Debug.LogError(dlOp.OperationException);

    Addressables.Release(dlOp);            // always release — autoReleaseHandle was false
}
```

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Calling `DownloadDependencies` (no Async) — 1.22.3 Obsolete, 2.9.1 removed

```csharp
// ❌ WRONG
Addressables.DownloadDependencies("PreloadLabel");

// ✅ CORRECT
await Addressables.DownloadDependenciesAsync("PreloadLabel").Task;
```

### 2. Passing `IList<object>` on 2.9.1

```csharp
// ❌ DOES NOT COMPILE [2.9.1] — IList<object> overload removed for Download/GetDownloadSize/ClearCache
IList<object> keys = new List<object>{ "A", "B" };
Addressables.DownloadDependenciesAsync(keys, MergeMode.Union);

// ✅ Works on both — List<T> implements IEnumerable
List<object> keys = new List<object>{ "A", "B" };                       // or string[]
Addressables.DownloadDependenciesAsync(keys, MergeMode.Union);          // picks IEnumerable overload
```

### 3. Releasing the GetDownloadSize handle before reading Result

```csharp
// ❌ WRONG — Result becomes invalid after Release
var h = Addressables.GetDownloadSizeAsync("Label");
Addressables.Release(h);
long b = h.Result;   // exception: Attempting to use an invalid operation handle

// ✅ CORRECT
var h = Addressables.GetDownloadSizeAsync("Label");
long b = await h.Task;
Addressables.Release(h);
```

### 4. Using `PercentComplete` as download progress

```csharp
// ❌ MISLEADING — PercentComplete averages init + catalog + download + load phases
float p = dlOp.PercentComplete;

// ✅ CORRECT — GetDownloadStatus returns pure download bytes
var st = dlOp.GetDownloadStatus();
float p = st.Percent;
```

### 5. Calling `ClearDependencyCacheAsync` while bundles are still held

```csharp
// ❌ UNDEFINED — clearing the cache of a bundle whose file handle is open on disk
var h = Addressables.LoadAssetAsync<GameObject>("Enemy");
await h.Task;
await Addressables.ClearDependencyCacheAsync("Enemy", true).Task;
// Subsequent use of h may fail when a dependency is needed from disk

// ✅ CORRECT — release first
Addressables.Release(h);
await Addressables.ClearDependencyCacheAsync("Enemy", true).Task;
```

### 6. Awaiting a `DownloadDependencies` handle without reading its Result

`DownloadDependenciesAsync` returns a non-typed `AsyncOperationHandle` — its `Result` is the internal dependency tree (rarely used). You care about `Status` and `GetDownloadStatus()`, not `Result`. This is the one place where `autoReleaseHandle: true` doesn't make sense — you need to poll the handle while it runs.

## Bandwidth and retry behavior

- Addressables uses `UnityWebRequest` under the hood. Network errors cause the handle to transition to `AsyncOperationStatus.Failed`; it does NOT retry automatically.
- You must implement retry logic yourself — typically a loop that releases a failed handle, waits, then re-issues `DownloadDependenciesAsync`.

```csharp
async Task<bool> DownloadWithRetryAsync(string key, int maxAttempts = 3)
{
    for (int i = 0; i < maxAttempts; i++)
    {
        var h = Addressables.DownloadDependenciesAsync(key, autoReleaseHandle: false);
        await h.Task;
        if (h.Status == AsyncOperationStatus.Succeeded) { Addressables.Release(h); return true; }
        Debug.LogWarning($"Download attempt {i+1} failed: {h.OperationException}");
        Addressables.Release(h);
        await Task.Delay(1000 * (i + 1));      // linear backoff
    }
    return false;
}
```

## WebGL caveats

- Bundles over ~2GB cannot fit in IndexedDB on some browsers. Split into smaller groups.
- The browser's download manager is NOT involved — UnityWebRequest streams into Unity's internal cache directly.
- `GetDownloadSizeAsync` requires the remote catalog to be reachable — offline returns an error, not 0.
