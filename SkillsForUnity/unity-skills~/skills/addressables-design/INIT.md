---
name: unity-addressables-init
description: "Addressables initialization and catalog-loading rules anchored to Addressables.cs / AddressablesImpl.cs / InitializationOperation.cs (1.22.3 & 2.9.1). Addressables 初始化与目录加载规则(锚定 1.22.3 与 2.9.1 引擎源码)。"
type: reference
---

# Addressables - Initialization & Catalog Loading

All rules here come from `Runtime/Addressables.cs`, `Runtime/AddressablesImpl.cs`, and `Runtime/Initialization/InitializationOperation.cs` — versions **1.22.3** (Unity 2022) and **2.9.1** (Unity 6).

## Init entry points

| Signature | [1.22.3] | [2.9.1] | Notes |
|-----------|:--------:|:-------:|-------|
| `AsyncOperationHandle<IResourceLocator> InitializeAsync()` | `Addressables.cs:907` | `Addressables.cs:1024` | Preferred call. Auto-releases handle. |
| `AsyncOperationHandle<IResourceLocator> InitializeAsync(bool autoReleaseHandle)` | `Addressables.cs:917` | `Addressables.cs:1034` | Pass `false` to keep the handle alive for inspection (e.g. reading `.Result` after `await`). |
| `AsyncOperationHandle<IResourceLocator> Initialize()` (no `Async`) | `Addressables.cs:862` — `[Obsolete]` | **Removed** | Source on 1.22.3 also returns `default` — do not use. |
| `AsyncOperationHandle<IResourceLocator> LoadContentCatalogAsync(string catalogPath, string providerSuffix = null)` | `Addressables.cs:950` | `Addressables.cs:1054` | Additively loads a remote catalog on top of the default one. |
| `AsyncOperationHandle<IResourceLocator> LoadContentCatalogAsync(string catalogPath, bool autoReleaseHandle, string providerSuffix = null)` | `Addressables.cs:972` | `Addressables.cs:1076` | |
| `AsyncOperationHandle<IResourceLocator> LoadContentCatalog(string catalogPath, string providerSuffix = null)` (no `Async`) | `Addressables.cs:930` — `[Obsolete]` | **Removed** | |
| `AsyncOperationHandle<IResourceLocator> InitializationOperation { get; }` | `Addressables.cs:981-982` — `[Obsolete]`, returns `default` | **Removed** | Never await or yield on this. |

## Canonical init sequence

Addressables bootstraps lazily on the FIRST call of any Addressables API that needs data (`LoadAssetAsync`, `LoadSceneAsync`, etc. all invoke `InitializeAsync` internally if not yet initialized). Calling `InitializeAsync()` explicitly is still the right pattern when you need to:

1. Await completion before the first frame so `WaitForCompletion` pitfalls are avoided later.
2. Check for remote catalog updates during a loading screen (not during gameplay).
3. Register additional catalogs via `LoadContentCatalogAsync` before any load.

```
Application startup
  ↓
await Addressables.InitializeAsync()               // reads RuntimeData + default catalog
  ↓ (optional)
await Addressables.LoadContentCatalogAsync(url)    // additional catalogs
  ↓ (optional, before gameplay)
await Addressables.CheckForCatalogUpdates()        // see UPDATE.md
await Addressables.UpdateCatalogs(...)
  ↓
LoadAssetAsync / LoadSceneAsync / ... at will
```

Implementation note: `AddressablesImpl` maintains a single `m_InitializationOperation` that any load piggybacks on as a dependency. Calling `InitializeAsync` a second time is cheap — it returns a handle to the same underlying operation.

## `autoReleaseHandle` semantics

- `autoReleaseHandle = true` (default): the handle's refcount is decremented in its `Completed` callback. You can still `await` / `yield` / read `.Result` inside an immediately-scheduled continuation, but you must not retain the handle for later frames.
- `autoReleaseHandle = false`: you own the handle. Release it manually with `Addressables.Release(handle)` once you no longer need `Result`.

If you `await` a default-overload call (`Addressables.InitializeAsync()`) and then try `var locator = handle.Result` on a later frame, you get an `InvalidHandle` exception.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Relying on the `[Obsolete]` `Initialize()` on 1.22.3 — code will not port to 2.9.1

```csharp
// ❌ WRONG — compiles on 1.22.3 with warning, fails on 2.9.1 (method removed)
var handle = Addressables.Initialize();
yield return handle;

// ✅ CORRECT — works on both
var handle = Addressables.InitializeAsync();
yield return handle;
```

### 2. Yielding on `InitializationOperation` (1.22.3-only artifact)

```csharp
// ❌ WRONG — property returns default(AsyncOperationHandle<IResourceLocator>) on 1.22.3
yield return Addressables.InitializationOperation;   // yields on an invalid handle

// ✅ CORRECT
yield return Addressables.InitializeAsync();
```

### 3. Calling `LoadAssetAsync` before `InitializeAsync` completes, from async context

```csharp
// ❌ RISKY — the first LoadAssetAsync call will chain off initialization internally,
//           but your own await does not guarantee that chain has completed across
//           multiple concurrent entry points.
var a = Addressables.LoadAssetAsync<GameObject>("A");
var b = Addressables.LoadAssetAsync<GameObject>("B");
await a.Task;   // B may be stuck mid-init until A completes

// ✅ CORRECT — explicit single init, then branch out
await Addressables.InitializeAsync().Task;
var a = Addressables.LoadAssetAsync<GameObject>("A").Task;
var b = Addressables.LoadAssetAsync<GameObject>("B").Task;
await Task.WhenAll(a, b);
```

### 4. Using `LoadContentCatalog` (no Async) — 1.22.3 `[Obsolete]`, 2.9.1 removed

```csharp
// ❌ WRONG
var cat = Addressables.LoadContentCatalog("https://cdn/catalog.json");

// ✅ CORRECT
var cat = await Addressables.LoadContentCatalogAsync("https://cdn/catalog.json").Task;
```

## Canonical init template

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;

public static class AssetBootstrap
{
    public static async Task InitializeAsync(string extraCatalogUrl = null)
    {
        // 1. Core init — reads RuntimeData + default catalog shipped in StreamingAssets
        var init = Addressables.InitializeAsync(autoReleaseHandle: false);
        await init.Task;
        if (init.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded)
        {
            Addressables.Release(init);
            throw new System.Exception("Addressables init failed: " + init.OperationException);
        }
        Addressables.Release(init);

        // 2. Optional: additional remote catalog (e.g. DLC, live ops)
        if (!string.IsNullOrEmpty(extraCatalogUrl))
        {
            var extra = Addressables.LoadContentCatalogAsync(extraCatalogUrl, autoReleaseHandle: false);
            await extra.Task;
            if (extra.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded)
            {
                Debug.LogError("Extra catalog failed — continuing without it");
            }
            Addressables.Release(extra);
        }

        // 3. See UPDATE.md for the CheckForCatalogUpdates / UpdateCatalogs flow
    }
}
```

## WebGL note

- `InitializeAsync` on WebGL never blocks — but your surrounding gameplay code can still hang if it calls `WaitForCompletion()` on the handle it returns. Don't.
- Remote catalogs require CORS headers on the hosting server; Addressables does not work around missing `Access-Control-Allow-Origin`.
