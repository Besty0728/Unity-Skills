---
name: unity-yooasset-update
description: "YooAsset update flow and Downloader (v2.3.18) — the canonical patch flow from InitializeAsync through version/manifest update to bundle download. YooAsset 更新流程与 Downloader(v2.3.18:从 InitializeAsync 到版本/清单更新再到资源包下载的标准热更流程)。"
type: reference
---

# YooAsset - Update Flow & Downloader

All rules come from `Runtime/ResourcePackage/ResourcePackage.cs:220-1142`, `Runtime/ResourcePackage/Operation/DownloaderOperation.cs`, and the reference implementation in `Samples~/Space Shooter/GameScript/Runtime/PatchLogic/FsmNode/Fsm*.cs`.

## The canonical patch flow

```
InitializeAsync                             — see INIT.md / PLAYMODE.md
   ↓
RequestPackageVersionAsync()                returns RequestPackageVersionOperation
   ↓  .PackageVersion
UpdatePackageManifestAsync(version)         returns UpdatePackageManifestOperation
   ↓
CreateResourceDownloader(max, retry)        returns ResourceDownloaderOperation
   ↓  if TotalDownloadCount > 0
assign callbacks (DownloadFinish/Update/Error/FileBegin)
   ↓
BeginDownload()                             actual network I/O begins
   ↓  (yield / await)
game loop — per-asset LoadAssetAsync/...
```

The real, working state machine is in `Samples~/Space Shooter/GameScript/Runtime/PatchLogic/`:

- `PatchOperation.cs` — orchestration (`FsmInitializePackage → FsmRequestPackageVersion → FsmUpdatePackageManifest → FsmCreateDownloader → FsmDownloadPackageFiles → FsmDownloadPackageOver → FsmClearCacheBundle → FsmStartGame`).
- Read the individual FSM nodes if you need error-handling / retry patterns.

## Step 1 — `RequestPackageVersionAsync`

```csharp
// ResourcePackage.cs:225
public RequestPackageVersionOperation RequestPackageVersionAsync(bool appendTimeTicks = true, int timeout = 60)
```

- Returns **`RequestPackageVersionOperation`** — exposes `.PackageVersion` (`string`) on success. There is **no class named `UpdatePackageVersionOperation`** in 2.3.18.
- `appendTimeTicks = true` appends a cache-busting query string to the request URL (avoids CDN staleness).
- `timeout` is in seconds (default 60). Watchdog-style timeout at the HTTP level.

```csharp
var op = package.RequestPackageVersionAsync();
yield return op;
if (op.Status != EOperationStatus.Succeed) {
    Debug.LogError(op.Error);
    yield break;
}
string version = op.PackageVersion;
```

Source sample: `Samples~/Space Shooter/.../FsmRequestPackageVersion.cs:27-45`.

## Step 2 — `UpdatePackageManifestAsync`

```csharp
// ResourcePackage.cs:238
public UpdatePackageManifestOperation UpdatePackageManifestAsync(string packageVersion, int timeout = 60)
{
    DebugCheckInitialize(false);
    if (_resourceManager.HasAnyLoader())
        YooLogger.Warning("Found loaded bundle before update manifest ! Recommended to call the UnloadAllAssetsAsync method ...");
    // ...
}
```

- Must be called with the `packageVersion` string obtained from step 1.
- If any `AssetHandle` / `SceneHandle` / etc. is still alive, YooAsset logs a **warning**. Call `UnloadAllAssetsAsync()` first.
- `timeout` is in seconds (default 60).

```csharp
yield return package.UnloadAllAssetsAsync();
var op = package.UpdatePackageManifestAsync(version);
yield return op;
if (op.Status != EOperationStatus.Succeed) {
    Debug.LogError(op.Error);
    yield break;
}
```

Source sample: `Samples~/Space Shooter/.../FsmUpdatePackageManifest.cs:27-44`.

## Step 3 — `PreDownloadContentAsync` (optional)

```csharp
// ResourcePackage.cs:258
public PreDownloadContentOperation PreDownloadContentAsync(string packageVersion, int timeout = 60)
```

Loads the manifest for a **different** version than the currently active one, purely so you can call `CreateResourceDownloader` against it. Use it for "pre-download next patch while the player still uses the current version."

## Step 4 — Creating a downloader

`ResourcePackage` exposes three downloader entry points; each returns `ResourceDownloaderOperation`:

```csharp
// ResourcePackage.cs:966-1077

// (a) everything the current manifest says is missing locally
public ResourceDownloaderOperation CreateResourceDownloader(int downloadingMaxNumber, int failedTryAgain);  // :972

// (b) by tag (single or list)
public ResourceDownloaderOperation CreateResourceDownloader(string tag, int downloadingMaxNumber, int failedTryAgain);           // :984
public ResourceDownloaderOperation CreateResourceDownloader(string[] tags, int downloadingMaxNumber, int failedTryAgain);        // :996

// (c) by asset dependency — download the bundle(s) a specific asset needs
public ResourceDownloaderOperation CreateBundleDownloader(string location, bool recursiveDownload, int downloadingMaxNumber, int failedTryAgain);  // :1009
public ResourceDownloaderOperation CreateBundleDownloader(string location, int downloadingMaxNumber, int failedTryAgain);          // :1016
public ResourceDownloaderOperation CreateBundleDownloader(string[] locations, bool recursiveDownload, int downloadingMaxNumber, int failedTryAgain);  // :1028
public ResourceDownloaderOperation CreateBundleDownloader(string[] locations, int downloadingMaxNumber, int failedTryAgain);       // :1039
public ResourceDownloaderOperation CreateBundleDownloader(AssetInfo assetInfo, bool recursiveDownload, int downloadingMaxNumber, int failedTryAgain);  // :1051
public ResourceDownloaderOperation CreateBundleDownloader(AssetInfo assetInfo, int downloadingMaxNumber, int failedTryAgain);       // :1057
public ResourceDownloaderOperation CreateBundleDownloader(AssetInfo[] assetInfos, bool recursiveDownload, int downloadingMaxNumber, int failedTryAgain);  // :1069
public ResourceDownloaderOperation CreateBundleDownloader(AssetInfo[] assetInfos, int downloadingMaxNumber, int failedTryAgain);    // :1074
```

Parameters:

- `downloadingMaxNumber` — parallel connection count. Clamped internally to `[1, 64]` (`DownloaderOperation.cs:108, 16`).
- `failedTryAgain` — per-file retry count before reporting failure.
- `recursiveDownload` — when true, expands to every dependency in every bundle the requested asset touches.

> **The `timeout` overload is GONE in 2.3.16**. If your training data remembers `CreateResourceDownloader(max, retry, timeout)`, it's wrong — set `DOWNLOAD_WATCH_DOG_TIME` on `CacheFileSystemParameters` instead. See FILESYSTEM.md.

## `ResourceDownloaderOperation` — callbacks, progress, control

```csharp
// DownloaderOperation.cs:60-101, 330-370

public int  TotalDownloadCount   { get; }                // :60
public long TotalDownloadBytes   { get; }                // :65
public int  CurrentDownloadCount { get; }                // :70
public long CurrentDownloadBytes { get; }                // :78

// FOUR plain delegate fields — assign once, NOT with +=
public DownloaderFinish   DownloadFinishCallback    { get; set; }  // :86
public DownloadUpdate     DownloadUpdateCallback    { get; set; }  // :91
public DownloadError      DownloadErrorCallback     { get; set; }  // :96
public DownloadFileBegin  DownloadFileBeginCallback { get; set; }  // :101

public void Combine(DownloaderOperation downloader);     // :289  merge two downloaders before BeginDownload
public void BeginDownload();                             // :330
public void PauseDownload();                             // :341
public void ResumeDownload();                            // :349
public void CancelDownload();                            // :357
```

Callback delegate shapes:

```csharp
// DownloaderOperation.cs:22-38
public delegate void DownloaderFinish(DownloaderFinishData data);
public delegate void DownloadUpdate  (DownloadUpdateData   data);
public delegate void DownloadError   (DownloadErrorData    data);
public delegate void DownloadFileBegin(DownloadFileData    data);
```

### Assignment semantics (critical)

Because these are **plain fields**, not `event`s:

- Assign with `=`, not `+=`. `+=` compiles (because `System.Delegate` supports it), but you may unintentionally accumulate subscribers across rebuilds.
- Prefer `downloader.DownloadUpdateCallback = OnProgress;` (single subscriber per field).
- If you truly need multiple subscribers, wrap your own multicaster.

Source: `DownloaderOperation.cs:86-101`.

### Empty downloader — early exit

```csharp
var downloader = package.CreateResourceDownloader(10, 3);
if (downloader.TotalDownloadCount == 0) {
    // Nothing to download — skip straight to the next state
    // Source: Samples~/Space Shooter/.../FsmCreateDownloader.cs:36-40
}
```

### Pause / resume / cancel

- `PauseDownload()` — sets a flag; outstanding requests complete, but no new ones spawn.
- `ResumeDownload()` — flips the flag back.
- `CancelDownload()` — calls `AbortOperation()` on every in-flight downloader, sets `Status = Failed`, `Error = "User cancel."`.

Source: `DownloaderOperation.cs:341-370`.

### Combining downloaders

```csharp
var d1 = package.CreateResourceDownloader("ui", 10, 3);
var d2 = package.CreateResourceDownloader("maps", 10, 3);
d1.Combine(d2);     // d1 now pulls both sets; d2 is consumed
d1.BeginDownload();
```

Both must belong to the **same package** (checked at `:291`). Combining after `BeginDownload()` is rejected.

## Clearing cache files

```csharp
// ResourcePackage.cs:271-296
public ClearCacheFilesOperation ClearCacheFilesAsync(EFileClearMode clearMode, object clearParam = null);  // :271
public ClearCacheFilesOperation ClearCacheFilesAsync(string clearMode, object clearParam = null);          // :287
```

```csharp
// Runtime/FileSystem/EFileClearMode.cs
public enum EFileClearMode
{
    ClearAllBundleFiles,            // wipe every bundle in the cache
    ClearUnusedBundleFiles,         // drop bundles not referenced by the active manifest
    ClearBundleFilesByLocations,    // param: string | string[] | List<string>  (new in 2.3.18)
    ClearBundleFilesByTags,         // param: string | string[] | List<string>
    ClearAllManifestFiles,
    ClearUnusedManifestFiles,
}
```

Practical flows:

```csharp
yield return package.ClearCacheFilesAsync(EFileClearMode.ClearUnusedBundleFiles);          // routine GC
yield return package.ClearCacheFilesAsync(EFileClearMode.ClearBundleFilesByTags, "music"); // drop a whole tag
yield return package.ClearCacheFilesAsync(EFileClearMode.ClearAllBundleFiles);             // full reset
```

Source: `Runtime/FileSystem/EFileClearMode.cs:4-41`, `CHANGELOG.md:43-54` (for the `ClearBundleFilesByLocations` addition in 2.3.18).

## Installed unpacker & importer

Two sibling `DownloaderOperation` subclasses for edge cases:

```csharp
public sealed class ResourceUnpackerOperation : DownloaderOperation;  // DownloaderOperation.cs:390
public sealed class ResourceImporterOperation : DownloaderOperation;  // :407
```

- **`ResourceUnpackerOperation`** — moves files from the buildin (shipped) file system into the cache so they behave like downloaded content. Created via `package.CreateResourceUnpacker(...)` (`ResourcePackage.cs:1086-1113`).
- **`ResourceImporterOperation`** — ingests user-provided local files (e.g. downloaded by a custom channel) into the cache. Created via `package.CreateResourceImporter(...)` (`ResourcePackage.cs:1125-1142`).

Both share the same callback model as `ResourceDownloaderOperation`.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Using `+=` on a callback field

```csharp
// ❌ Ambiguous — looks like an event subscription, but it's a plain delegate field
downloader.DownloadUpdateCallback += OnUpdate;   // accidentally double-adds if ReInit happens

// ✅ CORRECT
downloader.DownloadUpdateCallback = OnUpdate;
```

### 2. Assigning callbacks after `BeginDownload()`

```csharp
// ❌ WRONG — early progress events fire against a null callback, then silently wire up too late
downloader.BeginDownload();
downloader.DownloadUpdateCallback = OnUpdate;

// ✅ CORRECT
downloader.DownloadUpdateCallback = OnUpdate;
downloader.BeginDownload();
```

### 3. Forgetting `UnloadAllAssetsAsync` before a manifest update

```csharp
// ❌ Warning — "Found loaded bundle before update manifest !"
yield return package.UpdatePackageManifestAsync(newVersion);

// ✅ CORRECT
h?.Release(); h = null;
yield return package.UnloadAllAssetsAsync();
yield return package.UpdatePackageManifestAsync(newVersion);
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:242-247`.

### 4. Hallucinating `UpdatePackageVersionOperation`

```csharp
// ❌ Does not exist in 2.3.18
UpdatePackageVersionOperation op = package.RequestPackageVersionAsync();

// ✅ CORRECT
RequestPackageVersionOperation op = package.RequestPackageVersionAsync();
string version = op.PackageVersion;
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:225-231`.

### 5. Passing `timeout` to `CreateResourceDownloader`

```csharp
// ❌ Removed in 2.3.16 — no overload accepts timeout
var d = package.CreateResourceDownloader(10, 3, timeout: 60);   // compile error

// ✅ CORRECT — set DOWNLOAD_WATCH_DOG_TIME on the cache FS
cacheFs.AddParameter(FileSystemParametersDefine.DOWNLOAD_WATCH_DOG_TIME, 60);
var d = package.CreateResourceDownloader(10, 3);
```

Source: `CHANGELOG.md:173-177`, `FileSystemParametersDefine.cs:18`.

### 6. Starting `CreateResourceDownloader` with `downloadingMaxNumber = 0`

```csharp
// ❌ Silently clamped to 1 (see DownloaderOperation.cs:108 Mathf.Clamp)
var d = package.CreateResourceDownloader(0, 3);

// ✅ Use a real value in the [1, 64] range
var d = package.CreateResourceDownloader(10, 3);
```

Source: `Runtime/ResourcePackage/Operation/DownloaderOperation.cs:16, 108`.

### 7. Combining downloaders from different packages

```csharp
// ❌ WRONG — YooAsset logs error: "The downloaders have different resource packages !"
d1.Combine(d2);   // d1 is from "UI", d2 is from "Maps"

// ✅ CORRECT — run them in parallel; don't combine
```

Source: `DownloaderOperation.cs:291-295`.

### 8. Reading `TotalDownloadCount` on a downloader you haven't started yet

```csharp
// ✅ Valid in fact — TotalDownloadCount is computed in the constructor via CalculatDownloaderInfo (:267-283),
//    so reading it before BeginDownload is correct and is exactly how the sample branches:
//    Samples~/Space Shooter/.../FsmCreateDownloader.cs:36-40
```

This is one of the rare "counterintuitive but correct" cases. Do **not** block on `IsDone` to inspect `TotalDownloadCount`.

## Canonical patch template

```csharp
using System.Collections;
using UnityEngine;
using YooAsset;

public class PatchFlow : MonoBehaviour
{
    public ResourcePackage Package;

    public IEnumerator Run()
    {
        // 1. Request version
        var vOp = Package.RequestPackageVersionAsync();
        yield return vOp;
        if (vOp.Status != EOperationStatus.Succeed) { Debug.LogError(vOp.Error); yield break; }

        // 2. Update manifest (make sure no loaders are alive)
        yield return Package.UnloadAllAssetsAsync();
        var mOp = Package.UpdatePackageManifestAsync(vOp.PackageVersion);
        yield return mOp;
        if (mOp.Status != EOperationStatus.Succeed) { Debug.LogError(mOp.Error); yield break; }

        // 3. Create downloader
        var downloader = Package.CreateResourceDownloader(downloadingMaxNumber: 10, failedTryAgain: 3);
        if (downloader.TotalDownloadCount == 0) { Debug.Log("Nothing to download."); yield break; }

        Debug.Log($"Need to download {downloader.TotalDownloadCount} files ({downloader.TotalDownloadBytes} bytes)");

        // 4. Wire callbacks (plain fields — assign once, before BeginDownload)
        downloader.DownloadFileBeginCallback = d => Debug.Log($"Begin {d.FileName} ({d.FileSize} B)");
        downloader.DownloadUpdateCallback    = d => Debug.Log($"Progress {d.Progress:P0}");
        downloader.DownloadErrorCallback     = d => Debug.LogError($"{d.FileName}: {d.ErrorInfo}");
        downloader.DownloadFinishCallback    = d => Debug.Log($"Finish succeed={d.Succeed}");

        // 5. Start and wait
        downloader.BeginDownload();
        yield return downloader;

        if (downloader.Status != EOperationStatus.Succeed) {
            Debug.LogError("Download failed");
            yield break;
        }

        // 6. Loading proceeds (LOADING.md)
    }
}
```
