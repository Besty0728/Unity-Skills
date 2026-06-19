---
name: unity-yooasset-init
description: "YooAsset initialization, destroy, and package lifecycle (v2.3.18) — the only supported YooAssets.Initialize / ResourcePackage startup sequence. YooAsset 初始化、销毁与 package 生命周期(v2.3.18:唯一受支持的 YooAssets.Initialize/ResourcePackage 启动序列)。"
type: reference
---

# YooAsset - Initialize, Destroy, Package Lifecycle

All rules come from `Runtime/YooAssets.cs` and `Runtime/ResourcePackage/ResourcePackage.cs`.

## Startup order (the only supported sequence)

```
YooAssets.Initialize(logger?)              // once per process — creates driver GameObject + OperationSystem
   ↓
var package = YooAssets.CreatePackage("DefaultPackage")
   ↓
var op = package.InitializeAsync(<EditorSimulate|Offline|Host|Web|Custom>ModeParameters)
yield return op                            // op.Status must be Succeed before any Load/Update call
   ↓
(optional) patch flow — see UPDATE.md
   ↓
package.LoadAssetAsync<T>(location)        // per-asset; release handle when done
```

**Point of no return**: until `package.InitializeAsync` completes with `Status == Succeed`, every call guarded by `DebugCheckInitialize` on that package will throw. `DebugCheckInitialize` is annotated `[Conditional("DEBUG")]`, so in release builds the guard is compiled out and misuse silently NREs instead. Treat the rule as "mandatory in all configurations".

Source: `Runtime/ResourcePackage/ResourcePackage.cs:1157-1170`.

`YooAssets.Initialize()` is idempotent in source: if the static system is already initialized, YooAsset logs a warning and returns. Still treat it as a process-level singleton and guard boot code with `if (!YooAssets.Initialized) YooAssets.Initialize();` so ownership of the driver GameObject stays explicit.

Source: `Runtime/YooAssets.cs:38-64`.

## Shutdown order (reverse of startup)

```
handle.Release() / Dispose()               // release every live AssetHandle/Scene/Raw/SubAssets/AllAssets
   ↓
package.UnloadAllAssetsAsync()             // force-drops remaining loaders
   ↓
var destroyOp = package.DestroyAsync()     // returns DestroyOperation; await it
yield return destroyOp
   ↓
YooAssets.RemovePackage(package)           // refused if InitializeStatus != None
   ↓
(process exit or full restart)  YooAssets.Destroy()   // tears down driver + OperationSystem + all AssetBundles
```

Calling `YooAssets.Destroy()` internally:

1. Flips `_isInitialize = false`.
2. Destroys the `[YooAssets]` driver GameObject.
3. Calls `ClearAllPackageOperation()` — terminates all async operations.
4. `AssetBundle.UnloadAllAssetBundles(true)` — **unloads every bundle Unity currently holds**, including bundles unrelated to YooAsset.
5. Clears the package list.

Source: `Runtime/YooAssets.cs:69-87, 103-110`.

> **Warning**: step 4 means `YooAssets.Destroy()` is project-global. Do not call it as a per-scene cleanup.

## Core APIs (verbatim from source)

### YooAssets (static core)

```csharp
public static bool Initialized { get; }                                    // :29
public static void Initialize(ILogger logger = null)                       // :38
public static void Destroy()                                               // :69
public static ResourcePackage CreatePackage(string packageName)            // :116
public static ResourcePackage GetPackage(string packageName)               // :132  logs error when missing
public static ResourcePackage TryGetPackage(string packageName)            // :145  returns null when missing
public static List<ResourcePackage> GetAllPackages()                       // :154
public static bool RemovePackage(string packageName)                       // :163
public static bool RemovePackage(ResourcePackage package)                  // :177  refused when InitializeStatus != None
public static bool ContainsPackage(string packageName)                     // :196
public static void StartOperation(GameAsyncOperation operation)            // :207  for arbitrary user GameAsyncOperations
public static void SetDownloadSystemUnityWebRequest(UnityWebRequestDelegate createDelegate);  // :244
public static void SetOperationSystemMaxTimeSlice(long milliseconds);                         // :252 — minimum 10 ms
```

All of the above live in namespace `YooAsset` (file-scoped at `Runtime/YooAssets.cs:8`).

### YooAssets default-package shortcuts

`YooAssetsExtension.cs` adds a default package layer on top of the core static class:

```csharp
public static void SetDefaultPackage(ResourcePackage package);                 // YooAssetsExtension.cs:16
public static AssetHandle LoadAssetSync<TObject>(string location);             // :228
public static AssetHandle LoadAssetAsync<TObject>(string location, uint priority = 0);  // :271
public static RawFileHandle LoadRawFileAsync(string location, uint priority = 0);        // :151
public static SceneHandle LoadSceneAsync(string location, ...);                // :191
public static ResourceDownloaderOperation CreateResourceDownloader(int downloadingMaxNumber, int failedTryAgain);  // :479
```

These methods are real in YooAsset 2.3.18. They throw if no default package has been assigned (`DebugCheckDefaultPackageValid`, `YooAssetsExtension.cs:612-616`). Prefer explicit `ResourcePackage` references in reusable code and multi-package projects.

### ResourcePackage (instance)

```csharp
public readonly string PackageName;                                        // :24
public EOperationStatus InitializeStatus { get; }                          // :29
public bool PackageValid { get; }                                          // :37  => _playModeImpl?.ActiveManifest != null

public InitializationOperation InitializeAsync(InitializeParameters parameters);  // :83
public DestroyOperation DestroyAsync();                                    // :210
// (patch-flow and load APIs are documented in UPDATE.md and LOADING.md)
```

`InitializeAsync` dispatches to the matching file-system impl based on the runtime type of `parameters` (see PLAYMODE.md).

### The driver GameObject

`YooAssets.Initialize` creates one GameObject named `[YooAssets]` with a `YooAssetsDriver` component, then marks it `DontDestroyOnLoad`. Repeated `Initialize()` calls do not create another driver; they warn and return. The driver's `Update()` pumps `OperationSystem.Update()`, which in turn advances every registered operation. If you destroy the driver manually, every async operation stalls forever.

Source: `Runtime/YooAssets.cs:50-63, 92-98`.

## `EOperationStatus`

```csharp
public enum EOperationStatus { None, Processing, Succeed, Failed }
```

Source: `Runtime/OperationSystem/EOperationStatus.cs:4-10`. Every operation (init, version, manifest, download, load) exposes `.Status` and a string `.Error`. Treat `Succeed` as the only go-forward signal; `None` means "not started", `Processing` means "still running".

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Calling `CreatePackage` before `YooAssets.Initialize()`

```csharp
// ❌ WRONG — throws `Exception: YooAssets not initialize !`
var package = YooAssets.CreatePackage("DefaultPackage");

// ✅ CORRECT
YooAssets.Initialize();
var package = YooAssets.CreatePackage("DefaultPackage");
```

Source: the check at `CheckException` (`YooAssets.cs:223-229`) runs inside every public method of the static class.

### 2. Calling `CreatePackage` twice with the same name

```csharp
// ❌ WRONG — throws `Exception: Package DefaultPackage already existed !`
YooAssets.CreatePackage("DefaultPackage");
YooAssets.CreatePackage("DefaultPackage");

// ✅ CORRECT — check first or use TryGetPackage
var package = YooAssets.TryGetPackage("DefaultPackage")
           ?? YooAssets.CreatePackage("DefaultPackage");
```

Source: `Runtime/YooAssets.cs:116-126`.

### 3. Removing a package without destroying it first

```csharp
// ❌ WRONG — YooAsset logs an error and returns false
YooAssets.RemovePackage("DefaultPackage");

// ✅ CORRECT
yield return package.DestroyAsync();
YooAssets.RemovePackage("DefaultPackage");   // or YooAssets.RemovePackage(package)
```

Source: `Runtime/YooAssets.cs:177-190` — the guard is `package.InitializeStatus != EOperationStatus.None`.

### 4. Using `YooAssets.Destroy()` as a scene-change cleanup

```csharp
// ❌ WRONG — this tears down every AssetBundle Unity owns, not just yours
void OnSceneUnloaded() {
    YooAssets.Destroy();
}

// ✅ CORRECT — destroy individual packages, keep the static system alive
void OnSceneUnloaded() {
    if (_package != null) {
        StartCoroutine(DestroyAsync(_package));
        _package = null;
    }
}
IEnumerator DestroyAsync(ResourcePackage pkg) {
    yield return pkg.UnloadAllAssetsAsync();
    yield return pkg.DestroyAsync();
    YooAssets.RemovePackage(pkg);
}
```

### 5. Manually `Destroy`-ing the `[YooAssets]` driver GameObject

```csharp
// ❌ WRONG — OperationSystem stops pumping; every future async op hangs
var driver = GameObject.Find("[YooAssets]");
GameObject.Destroy(driver);

// ✅ CORRECT — leave it alone. Use YooAssets.Destroy() for full teardown.
```

Source: `Runtime/YooAssets.cs:52-54`.

### 6. Reading `GetPackageVersion()` before `InitializeAsync` completes

```csharp
// ❌ WRONG — DebugCheckInitialize throws in DEBUG; NRE in release
var version = package.GetPackageVersion();

// ✅ CORRECT — gate on InitializeStatus
if (package.InitializeStatus == EOperationStatus.Succeed) {
    var version = package.GetPackageVersion();
}
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:303-307, 1157-1170`.

## `SetOperationSystemMaxTimeSlice` — framerate vs loading trade-off

```csharp
YooAssets.SetOperationSystemMaxTimeSlice(10);   // minimum clamp = 10 ms (see :254-258)
```

This caps how long the driver spends per frame advancing operations. Lower values prioritize frame pacing (e.g. during gameplay); higher values prioritize throughput (e.g. during a loading screen).

Source: `Runtime/YooAssets.cs:252-260`.

## Canonical boot/teardown template

```csharp
using System.Collections;
using UnityEngine;
using YooAsset;

public class YooAssetBoot : MonoBehaviour
{
    public string PackageName = "DefaultPackage";
    public EPlayMode PlayMode = EPlayMode.EditorSimulateMode;
    private ResourcePackage _package;

    IEnumerator Start()
    {
        // 1. Initialize the static system once per process; repeated calls only warn and return
        if (!YooAssets.Initialized)
            YooAssets.Initialize();

        // 2. Create (or reuse) the package
        _package = YooAssets.TryGetPackage(PackageName)
                ?? YooAssets.CreatePackage(PackageName);

        // 3. Build parameters for the chosen mode — see PLAYMODE.md
        var parameters = BuildInitializeParameters(PlayMode, PackageName);

        // 4. Kick off init and wait
        var op = _package.InitializeAsync(parameters);
        yield return op;
        if (op.Status != EOperationStatus.Succeed)
        {
            Debug.LogError($"YooAsset init failed: {op.Error}");
            yield break;
        }

        // 5. (Optional) patch flow — see UPDATE.md
        // 6. Normal load flow — see LOADING.md
    }

    IEnumerator OnApplicationQuit()
    {
        if (_package != null)
        {
            yield return _package.UnloadAllAssetsAsync();
            yield return _package.DestroyAsync();
            YooAssets.RemovePackage(_package);
            _package = null;
        }
        // Do NOT call YooAssets.Destroy() unless you are fully tearing down the process
    }

    private InitializeParameters BuildInitializeParameters(EPlayMode mode, string packageName)
    {
        // see PLAYMODE.md for the full body of this method
        throw new System.NotImplementedException();
    }
}
```
