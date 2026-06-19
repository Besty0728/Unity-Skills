---
name: unity-yooasset-filesystem
description: "YooAsset FileSystem parameters and services (v2.3.18) — FileSystemParameters, decryption and remote services. YooAsset 文件系统参数与服务(v2.3.18:FileSystemParameters、解密与远端服务)。"
type: reference
---

# YooAsset - FileSystem Parameters & Services

All rules come from `Runtime/FileSystem/FileSystemParameters.cs`, `Runtime/FileSystem/FileSystemParametersDefine.cs`, and `Runtime/Services/IDecryptionServices.cs` / `IRemoteServices.cs`.

## The `FileSystemParameters` class

```csharp
// Runtime/FileSystem/FileSystemParameters.cs:9
public class FileSystemParameters
{
    public string FileSystemClass { get; }     // "namespace.class,assembly"  :18
    public string PackageRoot { get; }         // optional directory override :23

    public FileSystemParameters(string fileSystemClass, string packageRoot);   // :26
    public void AddParameter(string name, object value);                       // :35
    // plus five static factory helpers — see below
}
```

Notes:

- `FileSystemClass` is an assembly-qualified type name: `"YooAsset.DefaultBuildinFileSystem"` (Unity resolves via `Type.GetType`).
- `PackageRoot` is optional; it overrides the default per-file-system root directory.
- `AddParameter(name, value)` stores into an internal `Dictionary<string, object>`; the concrete `IFileSystem.SetParameter` reads it at `OnCreate` time. Parameter `name`s must come from `FileSystemParametersDefine`.

## The 24 parameter constants

```csharp
// Runtime/FileSystem/FileSystemParametersDefine.cs:4-30
public class FileSystemParametersDefine
{
    public const string FILE_VERIFY_LEVEL                        = "FILE_VERIFY_LEVEL";                     // :6
    public const string FILE_VERIFY_MAX_CONCURRENCY              = "FILE_VERIFY_MAX_CONCURRENCY";           // :7  (added 2.3.15)
    public const string INSTALL_CLEAR_MODE                       = "INSTALL_CLEAR_MODE";                    // :8
    public const string REMOTE_SERVICES                          = "REMOTE_SERVICES";                       // :9
    public const string DECRYPTION_SERVICES                      = "DECRYPTION_SERVICES";                   // :10
    public const string MANIFEST_SERVICES                        = "MANIFEST_SERVICES";                     // :11
    public const string APPEND_FILE_EXTENSION                    = "APPEND_FILE_EXTENSION";                 // :12
    public const string DISABLE_CATALOG_FILE                     = "DISABLE_CATALOG_FILE";                  // :13
    public const string DISABLE_UNITY_WEB_CACHE                  = "DISABLE_UNITY_WEB_CACHE";               // :14
    public const string DISABLE_ONDEMAND_DOWNLOAD                = "DISABLE_ONDEMAND_DOWNLOAD";             // :15
    public const string DOWNLOAD_MAX_CONCURRENCY                 = "DOWNLOAD_MAX_CONCURRENCY";              // :16
    public const string DOWNLOAD_MAX_REQUEST_PER_FRAME           = "DOWNLOAD_MAX_REQUEST_PER_FRAME";        // :17
    public const string DOWNLOAD_WATCH_DOG_TIME                  = "DOWNLOAD_WATCH_DOG_TIME";               // :18  (added 2.3.16)
    public const string RESUME_DOWNLOAD_MINMUM_SIZE              = "RESUME_DOWNLOAD_MINMUM_SIZE";           // :19
    public const string RESUME_DOWNLOAD_RESPONSE_CODES           = "RESUME_DOWNLOAD_RESPONSE_CODES";        // :20
    public const string VIRTUAL_WEBGL_MODE                       = "VIRTUAL_WEBGL_MODE";                    // :21  (added 2.3.16)
    public const string VIRTUAL_DOWNLOAD_MODE                    = "VIRTUAL_DOWNLOAD_MODE";                 // :22  (added 2.3.16)
    public const string VIRTUAL_DOWNLOAD_SPEED                   = "VIRTUAL_DOWNLOAD_SPEED";                // :23
    public const string ASYNC_SIMULATE_MIN_FRAME                 = "ASYNC_SIMULATE_MIN_FRAME";              // :24
    public const string ASYNC_SIMULATE_MAX_FRAME                 = "ASYNC_SIMULATE_MAX_FRAME";              // :25
    public const string COPY_BUILDIN_PACKAGE_MANIFEST            = "COPY_BUILDIN_PACKAGE_MANIFEST";         // :26
    public const string COPY_BUILDIN_PACKAGE_MANIFEST_DEST_ROOT  = "COPY_BUILDIN_PACKAGE_MANIFEST_DEST_ROOT"; // :27
    public const string COPY_LOCAL_FILE_SERVICES                 = "COPY_LOCAL_FILE_SERVICES";              // :28
    public const string UNPACK_FILE_SYSTEM_ROOT                  = "UNPACK_FILE_SYSTEM_ROOT";               // :29  (added 2.3.18)
}
```

| Constant | Value type | Applies to | Purpose |
|----------|------------|------------|---------|
| `FILE_VERIFY_LEVEL` | `EFileVerifyLevel` | Buildin / Cache | How aggressively to CRC-verify cached files (`Low`/`Middle`/`High`/`Full`) |
| `FILE_VERIFY_MAX_CONCURRENCY` | `int` | Buildin / Cache | Parallel-verify worker count (2.3.15+) |
| `REMOTE_SERVICES` | `IRemoteServices` | Cache / WebRemote | Supplies CDN URLs |
| `DECRYPTION_SERVICES` | `IDecryptionServices` | Buildin / Cache | Supplies custom bundle decryption |
| `MANIFEST_SERVICES` | `IManifestServices` | All | Custom manifest deserialization hook |
| `DISABLE_UNITY_WEB_CACHE` | `bool` | WebServer / WebRemote | Disable Unity's internal web cache |
| `DOWNLOAD_MAX_CONCURRENCY` | `int` | Cache | Parallel download count |
| `DOWNLOAD_MAX_REQUEST_PER_FRAME` | `int` | Cache | Max requests dispatched each frame |
| `DOWNLOAD_WATCH_DOG_TIME` | `int` (seconds) | Cache | Drop a download if no bytes arrive within this window (2.3.16+; replaces the legacy `timeout` parameter) |
| `VIRTUAL_WEBGL_MODE` | `bool` | Editor | Simulate the WebGL runtime layout in-Editor (2.3.16+) |
| `VIRTUAL_DOWNLOAD_MODE` | `bool` | Editor | Simulate a remote CDN in-Editor without building bundles (2.3.16+) |
| `VIRTUAL_DOWNLOAD_SPEED` | `int` (bytes/s) | Editor | Simulated bandwidth (default 1024) |
| `ASYNC_SIMULATE_MIN_FRAME` / `ASYNC_SIMULATE_MAX_FRAME` | `int` | Editor | Fake async latency bounds during simulation |
| `COPY_BUILDIN_PACKAGE_MANIFEST` | `bool` | Cache | Copy the shipped manifest into the cache during init |
| `COPY_BUILDIN_PACKAGE_MANIFEST_DEST_ROOT` | `string` | Cache | Destination dir for the above |
| `COPY_LOCAL_FILE_SERVICES` | custom | Cache | Allows buildin → cache copy via a user-supplied strategy |
| `UNPACK_FILE_SYSTEM_ROOT` | `string` | Buildin | Override the unpack root directory (2.3.18+) |
| `APPEND_FILE_EXTENSION` | `bool` | Buildin / Cache | Append `.ab` (or similar) extension to cached files |
| `DISABLE_CATALOG_FILE` | `bool` | Buildin | Skip catalog generation |
| `DISABLE_ONDEMAND_DOWNLOAD` | `bool` | Cache | Block the on-demand (per-bundle) download path |
| `INSTALL_CLEAR_MODE` | enum | Cache | Cleanup policy after a version upgrade |
| `RESUME_DOWNLOAD_MINMUM_SIZE` | `long` | Cache | Minimum file size that is eligible for range-resume |
| `RESUME_DOWNLOAD_RESPONSE_CODES` | `int[]` / `List<int>` | Cache | HTTP response codes treated as resumable |

Source anchors: `CHANGELOG.md:5-275` for the "added in X.Y.Z" annotations; `FileSystemParameters.cs:86-137` for the defaults each factory applies.

## Five factory helpers

```csharp
// Runtime/FileSystem/FileSystemParameters.cs:70-137

public static FileSystemParameters CreateDefaultEditorFileSystemParameters(string packageRoot);          // :74

public static FileSystemParameters CreateDefaultBuildinFileSystemParameters(
    IDecryptionServices decryptionServices = null,
    string packageRoot = null);                                                                          // :86

public static FileSystemParameters CreateDefaultCacheFileSystemParameters(
    IRemoteServices remoteServices,
    IDecryptionServices decryptionServices = null,
    string packageRoot = null);                                                                          // :100

public static FileSystemParameters CreateDefaultWebServerFileSystemParameters(
    IWebDecryptionServices decryptionServices = null,
    bool disableUnityWebCache = false);                                                                  // :114

public static FileSystemParameters CreateDefaultWebRemoteFileSystemParameters(
    IRemoteServices remoteServices,
    IWebDecryptionServices decryptionServices = null,
    bool disableUnityWebCache = false);                                                                  // :129
```

### Which factory goes with which PlayMode

| `EPlayMode` | Required factories |
|-------------|-------------------|
| `EditorSimulateMode` | `CreateDefaultEditorFileSystemParameters(packageRoot)` (required). `packageRoot` comes from `EditorSimulateModeHelper.SimulateBuild(packageName).PackageRootDirectory`. |
| `OfflinePlayMode` | `CreateDefaultBuildinFileSystemParameters([decryption])` |
| `HostPlayMode` | buildin = `CreateDefaultBuildinFileSystemParameters([decryption])` + cache = `CreateDefaultCacheFileSystemParameters(remoteServices, [decryption])` |
| `WebPlayMode` | `CreateDefaultWebServerFileSystemParameters([webDecryption], disableUnityWebCache)` — plus optionally `CreateDefaultWebRemoteFileSystemParameters(remoteServices, ...)` for remote CDN |

Source: `FileSystemParameters.cs:70-137`, `ResourcePackage.cs:107-135`.

### Adding custom parameters

```csharp
var cache = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remote);
cache.AddParameter(FileSystemParametersDefine.DOWNLOAD_MAX_CONCURRENCY, 8);
cache.AddParameter(FileSystemParametersDefine.DOWNLOAD_WATCH_DOG_TIME, 30);       // seconds
cache.AddParameter(FileSystemParametersDefine.FILE_VERIFY_MAX_CONCURRENCY, 4);

var host = new HostPlayModeParameters {
    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
    CacheFileSystemParameters   = cache,
};
```

## The `IRemoteServices` contract (CDN URL resolver)

```csharp
// Runtime/Services/IRemoteServices.cs:4
public interface IRemoteServices
{
    string GetRemoteMainURL(string fileName);       // primary CDN
    string GetRemoteFallbackURL(string fileName);   // backup CDN (used on main-URL failure)
}
```

Sample implementation (from `Samples~/Space Shooter/.../FsmInitializePackage.cs:135-153`):

```csharp
private class RemoteServices : IRemoteServices
{
    private readonly string _defaultHostServer;
    private readonly string _fallbackHostServer;

    public RemoteServices(string defaultHostServer, string fallbackHostServer)
    {
        _defaultHostServer  = defaultHostServer;
        _fallbackHostServer = fallbackHostServer;
    }

    string IRemoteServices.GetRemoteMainURL(string fileName)     => $"{_defaultHostServer}/{fileName}";
    string IRemoteServices.GetRemoteFallbackURL(string fileName) => $"{_fallbackHostServer}/{fileName}";
}
```

`fileName` is the CDN-relative filename YooAsset needs; your implementation concatenates the host URL it belongs to. Use the same server for both when you only have one CDN.

## The `IDecryptionServices` contract (for encrypted bundles)

```csharp
// Runtime/Services/IDecryptionServices.cs

public struct DecryptFileInfo                  // :6
{
    public string BundleName;                  // :12
    public string FileLoadPath;                // :17
    public uint   FileLoadCRC;                 // :22
}

public struct DecryptResult                    // :23
{
    public AssetBundle              Result;          // :29
    public AssetBundleCreateRequest CreateRequest;   // :34
    public Stream                   ManagedStream;   // :40   (auto-disposed with the bundle)
}

public interface IDecryptionServices                 // :42
{
    DecryptResult LoadAssetBundle(DecryptFileInfo fileInfo);         // :47  sync
    DecryptResult LoadAssetBundleAsync(DecryptFileInfo fileInfo);    // :52  async
    DecryptResult LoadAssetBundleFallback(DecryptFileInfo fileInfo); // :60  called if the normal decrypt fails
    byte[] ReadFileData(DecryptFileInfo fileInfo);                   // :65
    string ReadFileText(DecryptFileInfo fileInfo);                   // :70
}
```

All five methods are required. If your project ships unencrypted bundles, pass `null` to the `decryptionServices` argument of the factory helpers (see the default-argument in `CreateDefaultBuildinFileSystemParameters:86` and `CreateDefaultCacheFileSystemParameters:100`).

### `LoadAssetBundleFallback`

Documented at `IDecryptionServices.cs:55-59`:

> 当正常解密方法失败后，会触发后备加载！建议通过 `AssetBundle.LoadFromMemory()` 方法加载资源包作为保底机制。
> Issue: <https://github.com/tuyoogame/YooAsset/issues/562>

Intended as a safety net: for example, if the async decrypt path's streaming variant hits a transient error, fall back to a synchronous `LoadFromMemory`.

## The `IWebDecryptionServices` contract

Web-platform-specific variant used by `CreateDefaultWebServerFileSystemParameters` / `CreateDefaultWebRemoteFileSystemParameters` when bundles served from the web server are encrypted. The shape mirrors `IDecryptionServices` but is specialized for `UnityWebRequestAssetBundle`-style access paths.

Read `Runtime/Services/IWebDecryptionServices.cs` for the exact signatures if your project uses it.

## Custom `IFileSystem`

`FileSystemParameters` delegates creation to `Type.GetType(FileSystemClass)`, then calls `IFileSystem.SetParameter` for each entry and finally `OnCreate(packageName, PackageRoot)`. This is exactly how the mini-game integrations work:

- `Samples~/Mini Game/Runtime/WechatFileSystem/WechatFileSystem.cs`
- `Samples~/Mini Game/Runtime/TiktokFileSystem/TiktokFileSystem.cs`
- `Samples~/Mini Game/Runtime/AlipayFileSystem/AlipayFileSystem.cs`
- `Samples~/Mini Game/Runtime/TaptapFileSystem/TaptapFileSystem.cs`
- `Samples~/Mini Game/Runtime/GooglePlayFileSystem/GooglePlayFileSystem.cs`

Build one by:

1. Implementing `IFileSystem` (check the interface definition in `Runtime/FileSystem/` for required members).
2. Exposing a `CreateFileSystemParameters(...)` static helper mirroring the built-in factories.
3. Feeding the result into `CustomPlayModeParameters.FileSystemParameterList` (last element is the main FS — see PLAYMODE.md).

Source: `FileSystemParameters.cs:42-67`.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Misspelled parameter constants

```csharp
// ❌ Wrong — silently ignored; no such key
cache.AddParameter("DownloadMaxConcurrency", 8);

// ✅ Always use FileSystemParametersDefine
cache.AddParameter(FileSystemParametersDefine.DOWNLOAD_MAX_CONCURRENCY, 8);
```

### 2. Assigning the wrong value type

```csharp
// ❌ WRONG — DOWNLOAD_WATCH_DOG_TIME expects int, not TimeSpan
cache.AddParameter(FileSystemParametersDefine.DOWNLOAD_WATCH_DOG_TIME, TimeSpan.FromSeconds(30));

// ✅ CORRECT
cache.AddParameter(FileSystemParametersDefine.DOWNLOAD_WATCH_DOG_TIME, 30);
```

### 3. Constructing `FileSystemParameters` with a bad class string

```csharp
// ❌ WRONG — Type.GetType returns null; YooLogger.Error logs "Can not found file system class type …"
var fs = new FileSystemParameters("MyNamespace.MyFs", null);

// ✅ CORRECT — assembly-qualified name or a type known to the default AppDomain
var fs = new FileSystemParameters("MyNamespace.MyFs, MyAssembly", null);
// or use the factory helpers when possible
```

Source: `FileSystemParameters.cs:42-52`.

### 4. Returning `null` from `IRemoteServices.GetRemoteFallbackURL`

```csharp
// ❌ WRONG — downloader tries to hit null; NRE later
string GetRemoteFallbackURL(string fileName) => null;

// ✅ CORRECT — when you don't have a real fallback, return the main URL
string GetRemoteFallbackURL(string fileName) => GetRemoteMainURL(fileName);
```

### 5. Using `IWebDecryptionServices` on non-WebGL

```csharp
// ❌ WRONG — IWebDecryptionServices only flows through Web*FileSystemParameters
var cache = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remote, webDecryption);  // type mismatch

// ✅ CORRECT — use IDecryptionServices for cache/buildin
var cache = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remote, standardDecryption);
```

Source: `FileSystemParameters.cs:86-137`.

### 6. Setting `DOWNLOAD_WATCH_DOG_TIME = int.MaxValue` and expecting it to mean "no timeout"

```csharp
// ⚠ Not actually broken — int.MaxValue is the default (per :169 of DefaultCacheFIleSystem),
//   so a very large value effectively disables the watchdog.
// But DO NOT rely on 0 / -1 as "infinite" — those are not supported sentinels.
```

## Canonical FileSystem templates

### Offline play with a custom decrypter

```csharp
var decrypt = new MyDecryptionServices();
var offline = new OfflinePlayModeParameters {
    BuildinFileSystemParameters =
        FileSystemParameters.CreateDefaultBuildinFileSystemParameters(decrypt),
};
offline.BuildinFileSystemParameters
       .AddParameter(FileSystemParametersDefine.FILE_VERIFY_LEVEL, EFileVerifyLevel.High);
package.InitializeAsync(offline);
```

### Host play with watchdog and concurrency tuning

```csharp
var cache = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remote);
cache.AddParameter(FileSystemParametersDefine.DOWNLOAD_MAX_CONCURRENCY, 8);
cache.AddParameter(FileSystemParametersDefine.DOWNLOAD_WATCH_DOG_TIME, 30);
cache.AddParameter(FileSystemParametersDefine.FILE_VERIFY_MAX_CONCURRENCY, 4);

var host = new HostPlayModeParameters {
    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
    CacheFileSystemParameters   = cache,
};
```

### Editor simulate with virtual download

```csharp
var editor = FileSystemParameters.CreateDefaultEditorFileSystemParameters(packageRoot);
editor.AddParameter(FileSystemParametersDefine.VIRTUAL_DOWNLOAD_MODE, true);
editor.AddParameter(FileSystemParametersDefine.VIRTUAL_DOWNLOAD_SPEED, 1024 * 1024); // 1 MB/s

var sim = new EditorSimulateModeParameters { EditorFileSystemParameters = editor };
package.InitializeAsync(sim);
```
