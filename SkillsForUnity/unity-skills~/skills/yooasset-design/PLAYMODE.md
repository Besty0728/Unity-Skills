---
name: unity-yooasset-playmode
description: "YooAsset PlayMode and InitializeParameters (v2.3.18) — the five EPlayMode options and how to initialize each. YooAsset 运行模式与 InitializeParameters(v2.3.18:五种 EPlayMode 及各自初始化方式)。"
type: reference
---

# YooAsset - PlayMode & InitializeParameters

All rules come from `Runtime/InitializeParameters.cs`, `Runtime/ResourcePackage/ResourcePackage.cs`, and the real sample `Samples~/Space Shooter/GameScript/Runtime/PatchLogic/FsmNode/FsmInitializePackage.cs`.

## The five PlayModes

```csharp
public enum EPlayMode
{
    EditorSimulateMode,   // Editor-only; no real bundles; direct asset-database reads
    OfflinePlayMode,      // Everything ships in the app; no network
    HostPlayMode,         // Standard hot-update path: buildin + cache + CDN
    WebPlayMode,          // WebGL: web-server + web-remote file systems
    CustomPlayMode,       // Bring-your-own list of FileSystemParameters
}
```

Source: `Runtime/InitializeParameters.cs:8-34`.

## Parameter-class ↔ PlayMode matrix

| `EPlayMode` | Required subclass of `InitializeParameters` | Required `FileSystemParameters` field(s) |
|-------------|---------------------------------------------|------------------------------------------|
| `EditorSimulateMode` | `EditorSimulateModeParameters` | `EditorFileSystemParameters` (1) |
| `OfflinePlayMode` | `OfflinePlayModeParameters` | `BuildinFileSystemParameters` (1) |
| `HostPlayMode` | `HostPlayModeParameters` | `BuildinFileSystemParameters` + `CacheFileSystemParameters` (2) |
| `WebPlayMode` | `WebPlayModeParameters` | `WebServerFileSystemParameters` (+ `WebRemoteFileSystemParameters` if remote CDN) |
| `CustomPlayMode` | `CustomPlayModeParameters` | `FileSystemParameterList` (N — the **last** element is the main file system) |

Source: `Runtime/InitializeParameters.cs:66-110`.

### Base class shared by all five

```csharp
public abstract class InitializeParameters                            // :39
{
    public int  BundleLoadingMaxConcurrency = int.MaxValue;           // :44
    public bool AutoUnloadBundleWhenUnused   = false;                 // :49  auto-drop bundles whose refcount hit zero
    public bool WebGLForceSyncLoadAsset      = false;                 // :54  WebGL only
#if YOOASSET_EXPERIMENTAL
    public bool UseWeakReferenceHandle       = false;                 // :60  preview; requires the experimental define
#else
    internal bool UseWeakReferenceHandle     = false;                 // :62
#endif
}
```

`BundleLoadingMaxConcurrency <= 0` throws at `InitializeAsync` (see `ResourcePackage.cs:166-167`). `int.MaxValue` disables throttling.

`AutoUnloadBundleWhenUnused` was added in 2.3.15. **It requires that you still call `Handle.Release()`** — the refcount drops only when the last handle is released; forgetting `Release()` still leaks. Source: `CHANGELOG.md:98-108`.

### Subclass bodies (verbatim)

```csharp
public class EditorSimulateModeParameters : InitializeParameters       // :69
{
    public FileSystemParameters EditorFileSystemParameters;
}

public class OfflinePlayModeParameters : InitializeParameters          // :77
{
    public FileSystemParameters BuildinFileSystemParameters;
}

public class HostPlayModeParameters : InitializeParameters             // :85
{
    public FileSystemParameters BuildinFileSystemParameters;
    public FileSystemParameters CacheFileSystemParameters;
}

public class WebPlayModeParameters : InitializeParameters              // :94
{
    public FileSystemParameters WebServerFileSystemParameters;
    public FileSystemParameters WebRemoteFileSystemParameters;
}

public class CustomPlayModeParameters : InitializeParameters           // :103
{
    // The last entry is treated as the main file system
    public readonly List<FileSystemParameters> FileSystemParameterList = new();
}
```

## Runtime dispatch (what `InitializeAsync` actually does)

```csharp
public InitializationOperation InitializeAsync(InitializeParameters parameters)  // ResourcePackage.cs:83
{
    // ... reset + create _resourceManager + PlayModeImpl ...

    if (_playMode == EPlayMode.EditorSimulateMode)                              // :107
        op = playModeImpl.InitializeAsync(((EditorSimulateModeParameters)parameters).EditorFileSystemParameters);
    else if (_playMode == EPlayMode.OfflinePlayMode)                            // :112
        op = playModeImpl.InitializeAsync(((OfflinePlayModeParameters)parameters).BuildinFileSystemParameters);
    else if (_playMode == EPlayMode.HostPlayMode)                               // :117
        op = playModeImpl.InitializeAsync(
            ((HostPlayModeParameters)parameters).BuildinFileSystemParameters,
            ((HostPlayModeParameters)parameters).CacheFileSystemParameters);
    else if (_playMode == EPlayMode.WebPlayMode)                                // :122
        op = playModeImpl.InitializeAsync(
            ((WebPlayModeParameters)parameters).WebServerFileSystemParameters,
            ((WebPlayModeParameters)parameters).WebRemoteFileSystemParameters);
    else if (_playMode == EPlayMode.CustomPlayMode)                             // :127
        op = playModeImpl.InitializeAsync(((CustomPlayModeParameters)parameters).FileSystemParameterList);
    else
        throw new NotImplementedException();                                    // :134
}
```

The `_playMode` field is chosen by runtime-type-checking `parameters` in `CheckInitializeParameters` (`:170-181`). There is no separate `EPlayMode` argument to cross-check against the subclass. If `parameters` is not one of the five recognized subclasses, that branch throws `NotImplementedException`; if it is a recognized subclass but not the topology you intended, initialization follows that subclass and later operations fail according to the file systems you actually wired.

## Platform guards

```csharp
// ResourcePackage.cs:160-197
#if !UNITY_EDITOR
if (parameters is EditorSimulateModeParameters)
    throw new Exception("Editor simulate mode only support unity editor.");
#endif

if (_playMode != EPlayMode.EditorSimulateMode)
{
#if UNITY_WEBGL
    if (_playMode != EPlayMode.WebPlayMode)
        throw new Exception($"{_playMode} can not support WebGL plateform !");
#else
    if (_playMode == EPlayMode.WebPlayMode)
        throw new Exception($"{nameof(EPlayMode.WebPlayMode)} only support WebGL plateform !");
#endif
}
```

Practical implications:

- **EditorSimulateMode** is guarded by `#if !UNITY_EDITOR`. Build-time error / runtime throw in any player build.
- **WebPlayMode** is the **only** mode permitted on WebGL. All four other modes throw.
- **HostPlayMode** on WebGL throws; use `WebPlayMode` with a remote file system instead.

## Mode-by-mode builders (real code from Space Shooter sample)

The sample at `Samples~/Space Shooter/GameScript/Runtime/PatchLogic/FsmNode/FsmInitializePackage.cs:29-86` is the closest to canonical you will find. The shape:

### EditorSimulateMode

```csharp
var buildResult = EditorSimulateModeHelper.SimulateBuild(packageName);
var packageRoot = buildResult.PackageRootDirectory;

var p = new EditorSimulateModeParameters
{
    EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(packageRoot),
};
var op = package.InitializeAsync(p);
yield return op;
```

`EditorSimulateModeHelper.SimulateBuild` lives in the Editor assembly; you call it at boot when `Application.isEditor` is true to produce the virtual package layout. Source: `FsmInitializePackage.cs:41-48`.

### OfflinePlayMode

```csharp
var p = new OfflinePlayModeParameters
{
    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
};
var op = package.InitializeAsync(p);
yield return op;
```

Source: `FsmInitializePackage.cs:51-56`.

### HostPlayMode

```csharp
IRemoteServices remoteServices = new MyRemoteServices(defaultHost, fallbackHost);

var p = new HostPlayModeParameters
{
    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
    CacheFileSystemParameters   = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remoteServices),
};
var op = package.InitializeAsync(p);
yield return op;
```

Source: `FsmInitializePackage.cs:59-68`. `IRemoteServices` has exactly two methods, `GetRemoteMainURL(fileName)` and `GetRemoteFallbackURL(fileName)`. See FILESYSTEM.md for the full contract.

### WebPlayMode

```csharp
// Vanilla WebGL (content shipped alongside the build):
var p = new WebPlayModeParameters
{
    WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
};

// WebGL + remote CDN:
var p = new WebPlayModeParameters
{
    WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
    WebRemoteFileSystemParameters = FileSystemParameters.CreateDefaultWebRemoteFileSystemParameters(remoteServices),
};

var op = package.InitializeAsync(p);
yield return op;
```

Source: `FsmInitializePackage.cs:71-86` and the real factory helpers at `FileSystemParameters.cs:114-137`.

### CustomPlayMode

```csharp
var p = new CustomPlayModeParameters();
p.FileSystemParameterList.Add(FileSystemParameters.CreateDefaultBuildinFileSystemParameters());
p.FileSystemParameterList.Add(FileSystemParameters.CreateDefaultCacheFileSystemParameters(remoteServices));
// IMPORTANT: the LAST element is used as the main file system
// Source: InitializeParameters.cs:107-109 docstring
var op = package.InitializeAsync(p);
```

`CustomPlayMode` is the escape hatch when you have, e.g., a mini-game platform (WeChat / Tiktok / Alipay / Taptap — see `Samples~/Mini Game/.../*FileSystem.cs`) and need a stack of custom file systems.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Wrong parameter subclass for the intended topology

```csharp
// ❌ WRONG — CheckInitializeParameters sees OfflinePlayModeParameters and sets _playMode = OfflinePlayMode;
//            later code follows offline semantics, not a host/cache setup
var p = new OfflinePlayModeParameters {
    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remoteServices)
};
package.InitializeAsync(p);   // runtime fails later when updating manifests because no cache FS is wired

// ✅ CORRECT — use HostPlayModeParameters for host-play
var p = new HostPlayModeParameters {
    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
    CacheFileSystemParameters   = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remoteServices),
};
package.InitializeAsync(p);
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:170-181`.

### 2. EditorSimulateMode in a player build

```csharp
// ❌ WRONG — throws "Editor simulate mode only support unity editor."
#if !UNITY_EDITOR
// ...
var p = new EditorSimulateModeParameters { ... };
package.InitializeAsync(p);
#endif

// ✅ CORRECT — branch on build target at runtime
EPlayMode mode =
#if UNITY_EDITOR
    EPlayMode.EditorSimulateMode;
#elif UNITY_WEBGL
    EPlayMode.WebPlayMode;
#else
    EPlayMode.HostPlayMode;
#endif
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:160-164`.

### 3. Using HostPlayMode on WebGL

```csharp
// ❌ WRONG — throws "HostPlayMode can not support WebGL plateform !"
#if UNITY_WEBGL
var p = new HostPlayModeParameters { ... };   // throws
#endif

// ✅ CORRECT — WebPlayMode is the only mode permitted on WebGL
#if UNITY_WEBGL
var p = new WebPlayModeParameters {
    WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
    WebRemoteFileSystemParameters = FileSystemParameters.CreateDefaultWebRemoteFileSystemParameters(remoteServices),
};
#endif
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:186-196`.

### 4. Setting `BundleLoadingMaxConcurrency = 0`

```csharp
// ❌ WRONG — InitializeAsync throws
p.BundleLoadingMaxConcurrency = 0;

// ✅ CORRECT — leave the default (int.MaxValue) unless you profile a specific need; a sensible conservative cap is 8–16
p.BundleLoadingMaxConcurrency = 16;
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:166-167`.

### 5. `CustomPlayMode` with the main file system not last

```csharp
// ❌ WRONG — docstring at InitializeParameters.cs:107-109 states the LAST entry is the main FS;
//           if your primary FS is first, lookups fall through to the wrong system
p.FileSystemParameterList.Add(mainFs);
p.FileSystemParameterList.Add(fallbackFs);

// ✅ CORRECT
p.FileSystemParameterList.Add(fallbackFs);   // earlier entries = auxiliary / probe
p.FileSystemParameterList.Add(mainFs);       // last = main
```

## Canonical mode-builder template

```csharp
using YooAsset;

public static InitializeParameters BuildInitializeParameters(EPlayMode mode, string packageName, IRemoteServices remote)
{
#if UNITY_WEBGL && !UNITY_EDITOR
    // WebGL only supports WebPlayMode
    var p = new WebPlayModeParameters {
        WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
        WebRemoteFileSystemParameters = FileSystemParameters.CreateDefaultWebRemoteFileSystemParameters(remote),
    };
    return p;
#else
    switch (mode)
    {
        case EPlayMode.EditorSimulateMode:
        {
            #if UNITY_EDITOR
            var buildResult = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("...");  // placeholder
            var simulate = YooAsset.Editor.EditorSimulateModeHelper.SimulateBuild(packageName);
            return new EditorSimulateModeParameters {
                EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(simulate.PackageRootDirectory),
            };
            #else
            throw new System.InvalidOperationException("EditorSimulateMode not available in player builds.");
            #endif
        }
        case EPlayMode.OfflinePlayMode:
            return new OfflinePlayModeParameters {
                BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
            };
        case EPlayMode.HostPlayMode:
            return new HostPlayModeParameters {
                BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
                CacheFileSystemParameters   = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remote),
            };
        default:
            throw new System.NotSupportedException($"Unsupported play mode {mode} on this platform.");
    }
#endif
}
```
