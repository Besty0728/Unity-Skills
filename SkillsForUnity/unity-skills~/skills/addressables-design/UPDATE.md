---
name: unity-addressables-update
description: "Addressables catalog update flow (1.22.3 & 2.9.1) — the strictly-ordered CheckForCatalogUpdates / UpdateCatalogs / CleanBundleCache sequence and why skipping steps wastes bandwidth. Addressables 目录更新流程(1.22.3 与 2.9.1:严格有序的 CheckForCatalogUpdates/UpdateCatalogs/CleanBundleCache 序列,以及跳步为何浪费带宽)。"
type: reference
---

# Addressables - Catalog Update Flow

All rules here come from `Runtime/Addressables.cs` and `Runtime/Initialization/CheckCatalogsOperation.cs` / `UpdateCatalogsOperation.cs` / `CleanBundleCacheOperation.cs` — versions **1.22.3** (Unity 2022) and **2.9.1** (Unity 6).

The update flow is strictly ordered. Skipping a step does NOT error out — it silently wastes bandwidth or leaves stale bundles on disk.

```
Addressables.InitializeAsync()                                  // see INIT.md
  ↓
Addressables.CheckForCatalogUpdates(autoReleaseHandle)          // discovers catalogs with new hashes
  ↓
if (handle.Result.Count > 0)
  Addressables.UpdateCatalogs(catalogs, autoReleaseHandle[, autoCleanBundleCache])
  ↓
Addressables.CleanBundleCache(catalogsIds)                      // (optional on 2.9.1; same contract on 1.22.3)
```

## `CheckForCatalogUpdates`

```csharp
// [1.22.3] Addressables.cs:2303
public static AsyncOperationHandle<List<string>> CheckForCatalogUpdates(bool autoReleaseHandle = true)

// [2.9.1] Addressables.cs:2092 — identical signature
public static AsyncOperationHandle<List<string>> CheckForCatalogUpdates(bool autoReleaseHandle = true)
```

- `Result` is the list of catalog **locator IDs** whose remote hash differs from the local version. Empty list = nothing to update.
- Pass `autoReleaseHandle: false` to retain the handle for `Status` / error inspection.

## `UpdateCatalogs`

### [1.22.3]

```csharp
// :2344
public static AsyncOperationHandle<List<IResourceLocator>> UpdateCatalogs(
    IEnumerable<string> catalogs = null,
    bool autoReleaseHandle = true)

// :2356 — with autoCleanBundleCache
public static AsyncOperationHandle<List<IResourceLocator>> UpdateCatalogs(
    bool autoCleanBundleCache,
    IEnumerable<string> catalogs = null,
    bool autoReleaseHandle = true)
```

### [2.9.1]

```csharp
// :2132 — identical no-clean variant
public static AsyncOperationHandle<List<IResourceLocator>> UpdateCatalogs(
    IEnumerable<string> catalogs = null,
    bool autoReleaseHandle = true)

// :2147-2155 (body follows) — with autoCleanBundleCache
public static AsyncOperationHandle<List<IResourceLocator>> UpdateCatalogs(
    bool autoCleanBundleCache,
    IEnumerable<string> catalogs = null,
    bool autoReleaseHandle = true)
```

Passing `null` for `catalogs` updates EVERY registered locator. Pass the list returned by `CheckForCatalogUpdates` to update only the stale ones — saves time and bandwidth.

`autoCleanBundleCache = true` runs `CleanBundleCache` as part of the update, removing any bundle whose referencing location no longer exists in the new catalog. Safer than doing it manually — it uses the locator set from both OLD and NEW catalogs to decide.

## `CleanBundleCache`

```csharp
// [1.22.3] :2433
public static AsyncOperationHandle<bool> CleanBundleCache(IEnumerable<string> catalogsIds = null)

// [2.9.1] :2221 — identical signature
public static AsyncOperationHandle<bool> CleanBundleCache(IEnumerable<string> catalogsIds = null)
```

- Result is `true` when the cleanup succeeded.
- `catalogsIds` — the set of locator IDs whose bundles should be INSPECTED. Bundles not referenced by any of the listed locators are deleted from the cache. Pass `null` to inspect every locator.

This operates on Unity's AssetBundle disk cache (`Application.persistentDataPath/.../com.unity.addressables/`). Local catalogs shipped in StreamingAssets are not affected.

## `ResourceLocatorInfo` (2.9.1 only)

Introduced in 2.9.1 to expose locator metadata without touching the locator object directly.

```csharp
// [2.9.1] Addressables.cs:1782
public static ResourceLocatorInfo GetLocatorInfo(string locatorId);
// [2.9.1] Addressables.cs:1792
public static ResourceLocatorInfo GetLocatorInfo(IResourceLocator locator);
```

`ResourceLocatorInfo` carries:
- `LocalHash` — hash of the catalog currently installed locally.
- `CatalogLocation` — the resource location used to resolve this catalog.

Use when you need to display "you are on catalog version X" in a UI, or to log which catalog a bundle came from.

On **1.22.3** you must inspect `ResourceManager.ResourceLocators` manually.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Skipping `CheckForCatalogUpdates` and always calling `UpdateCatalogs`

```csharp
// ❌ WRONG — re-downloads EVERY catalog every launch, even if nothing changed
var h = Addressables.UpdateCatalogs();
await h.Task;
```

Each catalog is re-downloaded to compare hashes server-side. For a game with multiple live-ops catalogs, this is meaningful data + latency cost on every launch.

```csharp
// ✅ CORRECT
var check = Addressables.CheckForCatalogUpdates(autoReleaseHandle: false);
await check.Task;
var stale = check.Result;
Addressables.Release(check);

if (stale.Count > 0)
{
    var upd = Addressables.UpdateCatalogs(stale, autoReleaseHandle: false);
    await upd.Task;
    Addressables.Release(upd);
}
```

### 2. Running `UpdateCatalogs` with pending handles alive

```csharp
// ❌ RISKY — open handles reference old catalog state; update invalidates them
var enemy = Addressables.LoadAssetAsync<GameObject>("Enemy");
await Addressables.UpdateCatalogs().Task;
Instantiate(enemy.Result);   // behavior undefined — may work, may not
```

Flush / release load handles before updating. The update reconfigures provider mappings — existing handles are not automatically migrated.

### 3. Running `CleanBundleCache` while handles are alive

```csharp
// ❌ WRONG — clean removes bundles currently mapped by live handles
var h = Addressables.LoadAssetAsync<GameObject>("Enemy");
await h.Task;
Addressables.CleanBundleCache();
// subsequent operation on h may fail to page additional dependencies
```

`CleanBundleCache` inspects what's referenced by CATALOGS, not by live handles. A bundle with an active handle can still be deleted if no catalog still lists it — happens when the catalog update dropped a bundle but you hadn't released the handle yet.

### 4. Updating catalogs without awaiting the previous `UpdateCatalogs`

```csharp
// ❌ WRONG — overlapping updates
Addressables.UpdateCatalogs(new[]{"CatalogA"});
Addressables.UpdateCatalogs(new[]{"CatalogB"});   // may race with A
```

Await each update sequentially, or batch them into one call passing all IDs at once.

### 5. Calling 2.9.1's `UpdateCatalogs(bool autoCleanBundleCache, ...)` on 1.22.3 — signature mismatch

Both versions HAVE this overload (`:2356` on 1.22.3 / `:2147` on 2.9.1), so the call itself portable. What is NOT portable:

- On 1.22.3, `UpdateCatalogs` cleanup logic is slightly different (older `ContentCatalogData` format).
- On 2.9.1, a `BinaryCatalogInitializationData` code path executes for projects built with the binary catalog format.

No user-visible API difference — both return the same handle type.

## Canonical patch-flow template

Use this as a loading-screen script. Works unchanged on both 1.22.3 and 2.9.1.

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocators;

public static class PatchFlow
{
    public static async Task RunAsync(System.Action<string, float> onProgress = null)
    {
        onProgress?.Invoke("init", 0f);
        await Addressables.InitializeAsync().Task;

        onProgress?.Invoke("check-catalogs", 0.1f);
        var check = Addressables.CheckForCatalogUpdates(autoReleaseHandle: false);
        await check.Task;

        List<string> stale = check.Result ?? new List<string>();
        Addressables.Release(check);

        if (stale.Count == 0)
        {
            onProgress?.Invoke("up-to-date", 1f);
            return;
        }

        onProgress?.Invoke("update-catalogs", 0.2f);
        var upd = Addressables.UpdateCatalogs(
            autoCleanBundleCache: true,       // 2.9.1 cleans orphans automatically; 1.22.3 honors too
            catalogs: stale,
            autoReleaseHandle: false);
        await upd.Task;

        if (upd.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError("Catalog update failed: " + upd.OperationException);
            Addressables.Release(upd);
            throw upd.OperationException ?? new System.Exception("catalog-update-failed");
        }

        List<IResourceLocator> newLocators = upd.Result;
        Addressables.Release(upd);

        onProgress?.Invoke("download-preflight", 0.5f);
        // Optional: prefetch bundles now — see DOWNLOAD.md
        // await Addressables.DownloadDependenciesAsync("PreloadLabel", autoReleaseHandle:true).Task;

        onProgress?.Invoke("done", 1f);
    }
}
```

## Pitfall: catalog updates on WebGL

- Hash files at `<catalog>.hash` MUST be served with the same `Access-Control-Allow-Origin` headers as the catalog itself. Unity makes no attempt to bypass CORS.
- `UpdateCatalogs` triggers additional UnityWebRequests at runtime — make sure your bundled assets are hosted on a CDN that allows cross-origin range requests.

## Pitfall: multiple catalogs with overlapping keys

If two catalogs register the same key for different assets (e.g. a DLC catalog overriding a base key), the LAST-loaded catalog wins. Load order matters — `LoadContentCatalogAsync` pushes locators onto a list in registration order.
