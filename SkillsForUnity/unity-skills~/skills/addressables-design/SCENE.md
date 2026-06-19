---
name: unity-addressables-scene
description: "Addressables scene loading and SceneReleaseMode rules (1.22.3 & 2.9.1) — LoadSceneAsync, UnloadSceneAsync, and the biggest cross-version scene-API difference. Addressables 场景加载与 SceneReleaseMode 规则(1.22.3 与 2.9.1:LoadSceneAsync、UnloadSceneAsync,以及最大的跨版本场景 API 差异)。"
type: reference
---

# Addressables - Scene Loading & `SceneReleaseMode`

All rules here come from `Runtime/Addressables.cs`, `Runtime/ResourceManager/ResourceProviders/ISceneProvider.cs`, and `Runtime/ResourceManager/ResourceProviders/SceneProvider.cs` — versions **1.22.3** (Unity 2022) and **2.9.1** (Unity 6).

`SceneReleaseMode` is the single biggest scene-API difference between the two versions.

## `SceneReleaseMode` (2.9.1 only)

Full definition — `ISceneProvider.cs:2.9.1:14-26`:

```csharp
namespace UnityEngine.ResourceManagement.ResourceProviders
{
    public enum SceneReleaseMode
    {
        /// Release the scene handle when the scene is unloaded
        ReleaseSceneWhenSceneUnloaded = 0,

        /// Do not release the scene handle on scene unload. Requires manual call to Release
        /// in order to ensure AssetBundle is unloaded properly
        OnlyReleaseSceneOnHandleRelease,
    }
}
```

This enum **does not exist in 1.22.3** — every 1.22.3 scene unload also releases the bundle, with no opt-out.

## `LoadSceneAsync`

### [1.22.3] signatures

| Signature | Line |
|-----------|:----:|
| `LoadSceneAsync(object key, LoadSceneMode loadMode = LoadSceneMode.Single, bool activateOnLoad = true, int priority = 100)` | `:2126` |
| `LoadSceneAsync(object key, LoadSceneParameters loadSceneParameters, bool activateOnLoad = true, int priority = 100)` | `:2139` |
| `LoadSceneAsync(IResourceLocation location, LoadSceneMode loadMode = LoadSceneMode.Single, ...)` | `:2152` |
| `LoadSceneAsync(IResourceLocation location, LoadSceneParameters loadSceneParameters, ...)` | `:2165` |
| `LoadScene(...)` (no `Async`, 2 overloads) | `:2090, 2106` — `[Obsolete]` |

### [2.9.1] signatures — add `SceneReleaseMode releaseMode`

| Signature | Line |
|-----------|:----:|
| `LoadSceneAsync(object key, LoadSceneMode loadMode = LoadSceneMode.Single, bool activateOnLoad = true, int priority = 100, SceneReleaseMode releaseMode = SceneReleaseMode.ReleaseSceneWhenSceneUnloaded)` | `:1914` |
| `LoadSceneAsync(object key, LoadSceneMode loadMode, SceneReleaseMode releaseMode, bool activateOnLoad = true, int priority = 100)` | `:1935` |
| `LoadSceneAsync(object key, LoadSceneParameters, bool activateOnLoad = true, int priority = 100)` | `:1948` |
| `LoadSceneAsync(object key, LoadSceneParameters, SceneReleaseMode releaseMode, bool activateOnLoad = true, int priority = 100)` | `:1962` |
| `LoadSceneAsync(IResourceLocation, LoadSceneMode = LoadSceneMode.Single, bool activateOnLoad = true, int priority = 100)` | `:1975` |
| `LoadSceneAsync(IResourceLocation, LoadSceneMode, SceneReleaseMode releaseMode, bool activateOnLoad = true, int priority = 100)` | `:1989` |
| `LoadSceneAsync(IResourceLocation, LoadSceneParameters, bool activateOnLoad = true, int priority = 100)` | `:2002` |
| `LoadSceneAsync(IResourceLocation, LoadSceneParameters, SceneReleaseMode releaseMode, bool activateOnLoad = true, int priority = 100)` | `:2016` |

Default `releaseMode` is `ReleaseSceneWhenSceneUnloaded` — i.e. backwards-compatible with 1.22.3 behavior.

## `UnloadSceneAsync`

### [Both] signatures

| Signature | [1.22.3] | [2.9.1] |
|-----------|:--------:|:-------:|
| `UnloadSceneAsync(SceneInstance scene, UnloadSceneOptions unloadOptions, bool autoReleaseHandle = true)` | `:2248` | `:2037` |
| `UnloadSceneAsync(AsyncOperationHandle handle, UnloadSceneOptions unloadOptions, bool autoReleaseHandle = true)` | `:2260` | `:2049` |
| `UnloadSceneAsync(SceneInstance scene, bool autoReleaseHandle = true)` | `:2271` | `:2060` |
| `UnloadSceneAsync(AsyncOperationHandle handle, bool autoReleaseHandle = true)` | `:2282` | `:2071` |
| `UnloadSceneAsync(AsyncOperationHandle<SceneInstance> handle, bool autoReleaseHandle = true)` | `:2293` | `:2082` |
| `UnloadScene(...)` (no Async, 5 overloads) | `:2180-2226` — `[Obsolete]` | **Removed** |

`autoReleaseHandle` defaults to `true` — the scene handle is released when the unload completes. Pass `false` only if you want to inspect the unload `Status` afterwards.

## `SceneInstance`

```csharp
public struct SceneInstance
{
    public Scene Scene { get; }
    public AsyncOperation ActivateAsync();   // activate a scene loaded with activateOnLoad: false
}
```

Defined in `Runtime/ResourceManager/ResourceProviders/SceneProvider.cs` (both versions). The struct is `handle.Result` after `LoadSceneAsync`.

## `activateOnLoad: false` pattern

Same in both versions:

```csharp
var handle = Addressables.LoadSceneAsync("Level1", LoadSceneMode.Single, activateOnLoad: false);
await handle.Task;
// Scene is loaded but not yet active — run fade-out, await server, ...
yield return handle.Result.ActivateAsync();
// Now the scene activates. Unity's normal Scene activation semantics apply
// (Awake / OnEnable / Start fire on root GameObjects).
```

Used for loading-screen patterns where you want everything paged in before the visible switch.

## `LoadSceneParameters` (Unity 2022.2+)

`LoadSceneParameters(LoadSceneMode, LocalPhysicsMode)` gives you per-scene physics isolation. On 1.22.3 this overload is gated by `#if UNITY_2022_2_OR_NEWER`; on 2.9.1 the `LocalPhysicsMode` enum always exists.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Unloading with `SceneManager.UnloadSceneAsync` instead of `Addressables.UnloadSceneAsync`

```csharp
// ❌ WRONG — Addressables does not see this unload
var handle = Addressables.LoadSceneAsync("Level1", LoadSceneMode.Additive);
await handle.Task;
// ...
UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(handle.Result.Scene);
// Bundle refcount still 1 — memory leak
```

On **1.22.3**: the unload succeeds but the bundle remains loaded. You must call `Addressables.Release(handle)` yourself, OR use `Addressables.UnloadSceneAsync`.

On **2.9.1**: same behavior by default. If you explicitly want to unload with `SceneManager.UnloadSceneAsync` AND have the bundle unload — load the scene with `SceneReleaseMode.ReleaseSceneWhenSceneUnloaded` (the default). The provider hooks `SceneManager.sceneUnloaded` and releases the handle when the scene unloads via any path.

```csharp
// ✅ CORRECT — Addressables path
await Addressables.UnloadSceneAsync(handle).Task;

// ✅ ALSO CORRECT on 2.9.1 — ReleaseSceneWhenSceneUnloaded hooks the event
// (default) — SceneManager.UnloadSceneAsync then correctly releases the bundle
```

### 2. Forgetting `activateOnLoad: false` when you need two scenes briefly co-resident

```csharp
// ❌ RESULT — Single-mode load triggers immediate unload of the current scene before new one is ready
var h = Addressables.LoadSceneAsync("Level2", LoadSceneMode.Single);

// ✅ CORRECT — fade-out first, then swap
var h = Addressables.LoadSceneAsync("Level2", LoadSceneMode.Single, activateOnLoad: false);
await h.Task;
await FadeOut();
yield return h.Result.ActivateAsync();
```

### 3. Using `SceneReleaseMode.OnlyReleaseSceneOnHandleRelease` but forgetting to Release

```csharp
// ❌ WRONG [2.9.1] — bundle never unloads
var h = Addressables.LoadSceneAsync("Level1", LoadSceneMode.Additive,
    SceneReleaseMode.OnlyReleaseSceneOnHandleRelease);
// ...
SceneManager.UnloadSceneAsync(h.Result.Scene);
// Bundle stays resident. You signed up for manual release and did not deliver.

// ✅ CORRECT
var h = Addressables.LoadSceneAsync("Level1", LoadSceneMode.Additive,
    SceneReleaseMode.OnlyReleaseSceneOnHandleRelease);
// ...
await SceneManager.UnloadSceneAsync(h.Result.Scene);
Addressables.Release(h);   // required
```

Use `OnlyReleaseSceneOnHandleRelease` when you want to reload the same scene quickly without paging the bundle out — e.g. retrying a level. Otherwise stick with the default.

### 4. Copy-pasting 1.22.3 `LoadSceneAsync` call into 2.9.1 — compiles but allows scene-unload leak

```csharp
// 1.22.3 — all bundles unload with the scene, no choice
// Migrating verbatim to 2.9.1 — compiles, default mode still unloads — but now you have a
// choice you may have wanted to exercise. Read the diff before shipping.
```

### 5. Calling `UnloadScene` (no Async) — 1.22.3 Obsolete, 2.9.1 removed

```csharp
// ❌ WRONG [1.22.3 compile warning] / [2.9.1 compile error]
Addressables.UnloadScene(sceneInstance);

// ✅ CORRECT
await Addressables.UnloadSceneAsync(sceneInstance).Task;
```

## Canonical scene-swap template (works on both versions)

```csharp
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

public class SceneLoader : MonoBehaviour
{
    AsyncOperationHandle<SceneInstance> m_Current;

    public async Task LoadAsync(string key, bool additive = false)
    {
        var mode = additive ? LoadSceneMode.Additive : LoadSceneMode.Single;

        var loader = Addressables.LoadSceneAsync(key, mode, activateOnLoad: false);
        await loader.Task;

        if (loader.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError(loader.OperationException);
            Addressables.Release(loader);
            return;
        }

        await FadeOut();
        var activation = loader.Result.ActivateAsync();
        while (!activation.isDone) await Task.Yield();
        await FadeIn();

        if (!additive && m_Current.IsValid())
        {
            // Single-mode: the previous scene already got unloaded by Unity.
            // With the default ReleaseSceneWhenSceneUnloaded (2.9.1) the bundle
            // released automatically; on 1.22.3 it also released.
            // The handle is now invalid — just forget it.
            m_Current = default;
        }

        m_Current = loader;
    }

    public async Task UnloadAsync()
    {
        if (!m_Current.IsValid()) return;
        await Addressables.UnloadSceneAsync(m_Current).Task;
        m_Current = default;
    }

    Task FadeOut() { /*...*/ return Task.CompletedTask; }
    Task FadeIn() { /*...*/ return Task.CompletedTask; }
}
```

## Version-bridging tip

If you maintain a codebase that must compile on both Unity 2022 and Unity 6:

```csharp
#if UNITY_6000_0_OR_NEWER
var handle = Addressables.LoadSceneAsync(key, LoadSceneMode.Additive,
    SceneReleaseMode.OnlyReleaseSceneOnHandleRelease);
#else
var handle = Addressables.LoadSceneAsync(key, LoadSceneMode.Additive);
// On 1.22.3 the bundle ALWAYS unloads when the scene unloads — behavior matches
// 2.9.1's ReleaseSceneWhenSceneUnloaded default. OnlyReleaseSceneOnHandleRelease has no
// 1.22.3 analogue.
#endif
```

If you do not need the 2.9.1 OnlyReleaseSceneOnHandleRelease behavior, write the call without the `releaseMode` argument — it compiles unchanged on both versions.
