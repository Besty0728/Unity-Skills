---
name: unity-yooasset-loading
description: "YooAsset loading of assets, scenes, and raw files (v2.3.18) — the five load families, sync vs async, and the type guard. YooAsset 加载资源、场景与原始文件(v2.3.18:五大加载族、同步与异步、类型守卫)。"
type: reference
---

# YooAsset - Loading Assets, Scenes, Raw Files

All rules come from `Runtime/ResourcePackage/ResourcePackage.cs:516-964`, `Runtime/YooAssetsExtension.cs:16, 217-506`, and `Runtime/ResourcePackage/ResourcePackage.cs:1172-1187` (the type guard).

## Five load families

| Return type | Sync | Async | Purpose |
|-------------|------|-------|---------|
| `AssetHandle` | `LoadAssetSync` | `LoadAssetAsync` | One `UnityEngine.Object` |
| `SubAssetsHandle` | `LoadSubAssetsSync` | `LoadSubAssetsAsync` | Sub-objects of one asset (e.g. sprite-sheet sub-sprites, mesh sub-meshes) |
| `AllAssetsHandle` | `LoadAllAssetsSync` | `LoadAllAssetsAsync` | Every asset inside one bundle |
| `RawFileHandle` | `LoadRawFileSync` | `LoadRawFileAsync` | Plain bytes/text from a raw-file bundle |
| `SceneHandle` | `LoadSceneSync` | `LoadSceneAsync` | Unity scene load |

Every load family is available as an instance method on `ResourcePackage`. YooAsset 2.3.18 also exposes default-package static shortcuts on `YooAssets` via `YooAssetsExtension.cs`; use them only after `YooAssets.SetDefaultPackage(package)`. Prefer explicit `ResourcePackage` references in multi-package code, libraries, and tests.

## Three ways to address an asset

1. **Location string** — `"player"`, `"prefabs/enemy"`, the path/address configured in your `AssetBundleCollector`. Most common.
2. **`AssetInfo`** — pre-resolved struct returned by `package.GetAssetInfo(location)`. Useful when you batch-prepare then load.
3. **Asset GUID** — `package.GetAssetInfoByGUID(guid)` → `AssetInfo` → load. Stable across asset renames.

Source: `Runtime/ResourcePackage/ResourcePackage.cs:441-477`.

## Priority and waitForAsyncComplete

All `Load*Async` calls accept `uint priority = 0` — a higher value is pumped sooner by `OperationSystem`. All `Load*Sync` calls internally call `handle.WaitForAsyncComplete()` after starting the same async provider.

## `LoadAssetSync/Async` — verbatim signatures

```csharp
// ResourcePackage.cs:641-732 — AssetHandle returns

// Sync
public AssetHandle LoadAssetSync(AssetInfo assetInfo);                         // :641
public AssetHandle LoadAssetSync<TObject>(string location) where TObject : UnityEngine.Object;  // :652
public AssetHandle LoadAssetSync(string location, System.Type type);           // :664
public AssetHandle LoadAssetSync(string location);                             // :675  defaults to UnityEngine.Object

// Async
public AssetHandle LoadAssetAsync(AssetInfo assetInfo, uint priority = 0);                         // :689
public AssetHandle LoadAssetAsync<TObject>(string location, uint priority = 0) where TObject : UnityEngine.Object;  // :701
public AssetHandle LoadAssetAsync(string location, System.Type type, uint priority = 0);           // :714
public AssetHandle LoadAssetAsync(string location, uint priority = 0);                             // :726
```

### Default-package static shortcuts

```csharp
// YooAssetsExtension.cs:16, 217-295 — delegates to the default ResourcePackage
YooAssets.SetDefaultPackage(package);
AssetHandle h1 = YooAssets.LoadAssetAsync<GameObject>("player");
AssetHandle h2 = YooAssets.LoadAssetSync("ui/icon", typeof(Sprite));
```

These shortcuts are valid in YooAsset 2.3.18, but they hide package ownership. If a project has more than one package, pass the `ResourcePackage` explicitly:

```csharp
var package = YooAssets.GetPackage("DefaultPackage");
var h = package.LoadAssetAsync<GameObject>("player");
```

### The Type guard you must not fight

```csharp
[Conditional("DEBUG")]
private void DebugCheckAssetLoadType(System.Type type)                         // ResourcePackage.cs:1172
{
    if (type == null) return;

    if (typeof(UnityEngine.Behaviour).IsAssignableFrom(type))
        throw new Exception($"Load asset type is invalid : {type.FullName} !");

    if (typeof(UnityEngine.Object).IsAssignableFrom(type) == false)
        throw new Exception($"Load asset type is invalid : {type.FullName} !");
}
```

Translation:

- **Allowed**: `GameObject`, `Texture2D`, `Sprite`, `AudioClip`, `Material`, `ScriptableObject`, `TextAsset`, etc. Anything derived from `UnityEngine.Object` but NOT from `UnityEngine.Behaviour`.
- **Rejected**: `MonoBehaviour`, `Behaviour`, `Collider`, `Renderer`, any concrete `Component` that extends `Behaviour`. In `DEBUG` builds this throws; in release it silently returns a broken handle.
- **Rejected**: plain C# types (`string`, `List<int>`, custom POCOs) — not `UnityEngine.Object`.

> **Why Behaviour is rejected**: Components are attached to GameObjects; loading them as a standalone asset doesn't make sense. Load the GameObject prefab and then `GetComponent<T>()`.

## `LoadSubAssetsSync/Async` — for sub-objects

```csharp
// ResourcePackage.cs:751-842

public SubAssetsHandle LoadSubAssetsSync(AssetInfo assetInfo);                           // :751
public SubAssetsHandle LoadSubAssetsSync<TObject>(string location) where TObject : UnityEngine.Object;  // :762
public SubAssetsHandle LoadSubAssetsSync(string location, System.Type type);             // :774
public SubAssetsHandle LoadSubAssetsSync(string location);                               // :785
public SubAssetsHandle LoadSubAssetsAsync(AssetInfo assetInfo, uint priority = 0);       // :799
public SubAssetsHandle LoadSubAssetsAsync<TObject>(string location, uint priority = 0) where TObject : UnityEngine.Object;  // :811
public SubAssetsHandle LoadSubAssetsAsync(string location, System.Type type, uint priority = 0);  // :824
public SubAssetsHandle LoadSubAssetsAsync(string location, uint priority = 0);            // :836
```

Typical use case: a sprite sheet (Texture2D imported as "Multiple" sprite mode). `LoadSubAssetsAsync<Sprite>("ui/heroes")` returns a handle whose `GetSubAssetObjects<Sprite>()` lists every slice.

## `LoadAllAssetsSync/Async` — every asset in a bundle

```csharp
// ResourcePackage.cs:861-952

public AllAssetsHandle LoadAllAssetsSync(AssetInfo assetInfo);                            // :861
public AllAssetsHandle LoadAllAssetsSync<TObject>(string location) where TObject : UnityEngine.Object;  // :872
public AllAssetsHandle LoadAllAssetsSync(string location, System.Type type);              // :884
public AllAssetsHandle LoadAllAssetsSync(string location);                                // :895
public AllAssetsHandle LoadAllAssetsAsync(AssetInfo assetInfo, uint priority = 0);        // :909
public AllAssetsHandle LoadAllAssetsAsync<TObject>(string location, uint priority = 0) where TObject : UnityEngine.Object;  // :921
public AllAssetsHandle LoadAllAssetsAsync(string location, System.Type type, uint priority = 0);  // :934
public AllAssetsHandle LoadAllAssetsAsync(string location, uint priority = 0);            // :946
```

Use when you genuinely want everything: e.g. all Sprites inside a UI atlas bundle. Returns an array accessor on the handle.

## `LoadRawFileSync/Async` — raw bytes

```csharp
// ResourcePackage.cs:518-556

public RawFileHandle LoadRawFileSync(AssetInfo assetInfo);                                // :518
public RawFileHandle LoadRawFileSync(string location);                                    // :528
public RawFileHandle LoadRawFileAsync(AssetInfo assetInfo, uint priority = 0);            // :540
public RawFileHandle LoadRawFileAsync(string location, uint priority = 0);                // :551
```

Raw-file bundles are configured in the build (see BUILD.md). Call `handle.GetRawFileData()` / `GetRawFileText()` on completion. Useful for JSON configs, CSV data, serialized formats Unity doesn't recognize natively.

> **Note**: there are no generic `<TObject>` overloads for raw files — you always deal with raw bytes / text.

## `LoadSceneSync/Async` — scenes

```csharp
// ResourcePackage.cs:576-622

public SceneHandle LoadSceneSync(string location,
    LoadSceneMode sceneMode   = LoadSceneMode.Single,
    LocalPhysicsMode physicsMode = LocalPhysicsMode.None);                                // :576

public SceneHandle LoadSceneSync(AssetInfo assetInfo,
    LoadSceneMode sceneMode   = LoadSceneMode.Single,
    LocalPhysicsMode physicsMode = LocalPhysicsMode.None);                                // :589

public SceneHandle LoadSceneAsync(string location,
    LoadSceneMode sceneMode   = LoadSceneMode.Single,
    LocalPhysicsMode physicsMode = LocalPhysicsMode.None,
    bool suspendLoad = false,
    uint priority = 0);                                                                   // :603

public SceneHandle LoadSceneAsync(AssetInfo assetInfo,
    LoadSceneMode sceneMode   = LoadSceneMode.Single,
    LocalPhysicsMode physicsMode = LocalPhysicsMode.None,
    bool suspendLoad = false,
    uint priority = 0);                                                                   // :618
```

Internally, each overload wraps a `LoadSceneParameters(sceneMode, physicsMode)` before dispatch (`:628`). This matches Unity's own `SceneManager.LoadSceneAsync(..., LoadSceneParameters)` contract.

### `suspendLoad`

```csharp
package.LoadSceneAsync("stages/level01", LoadSceneMode.Single, LocalPhysicsMode.None, suspendLoad: true);
```

When `suspendLoad = true`, the scene loads to **90%** and pauses until you call `handle.UnSuspend()` (or analogous). Use it to gate the last step on a "Press any key to continue" prompt.

## Listing & probing assets before loading

```csharp
// ResourcePackage.cs:390-488
public bool IsNeedDownloadFromRemote(string location);                                    // :390
public bool IsNeedDownloadFromRemote(AssetInfo assetInfo);                                // :401
public AssetInfo[] GetAllAssetInfos();                                                    // :410
public AssetInfo[] GetAssetInfos(string tag);                                             // :420
public AssetInfo[] GetAssetInfos(string[] tags);                                          // :431
public AssetInfo GetAssetInfo(string location);                                           // :441
public AssetInfo GetAssetInfo(string location, System.Type type);                         // :452
public AssetInfo GetAssetInfoByGUID(string assetGUID);                                    // :462
public AssetInfo GetAssetInfoByGUID(string assetGUID, System.Type type);                  // :473
public bool CheckLocationValid(string location);                                          // :483
```

Typical combo: `CheckLocationValid` before `LoadAssetAsync` when the location is user-derived; `IsNeedDownloadFromRemote` to decide if you need to show a progress bar.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Loading a `Behaviour`-derived type directly

```csharp
// ❌ WRONG — DebugCheckAssetLoadType throws in DEBUG
package.LoadAssetAsync<Rigidbody>("physics/body");

// ✅ CORRECT — load the prefab, then GetComponent
var h = package.LoadAssetAsync<GameObject>("physics/body");
yield return h;
var rb = h.InstantiateSync().GetComponent<Rigidbody>();
h.Release();
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:1178-1186`.

### 2. Using `YooAssets.LoadAssetAsync(...)` before setting the default package

```csharp
// ❌ WRONG — default package is null; DebugCheckDefaultPackageValid throws
YooAssets.LoadAssetAsync<GameObject>("player");

// ✅ CORRECT — explicit package, preferred for clarity
var package = YooAssets.GetPackage("DefaultPackage");
var h = package.LoadAssetAsync<GameObject>("player");

// ✅ ALSO VALID — default-package shortcut
YooAssets.SetDefaultPackage(package);
var h2 = YooAssets.LoadAssetAsync<GameObject>("player");
```

Source: `Runtime/YooAssetsExtension.cs:16, 260-295, 612-616`.

### 3. Using `UnityEngine.SceneManagement.SceneManager.LoadScene` on a YooAsset scene

```csharp
// ❌ WRONG — bypasses YooAsset; the matching bundle is never loaded; scene is missing
UnityEngine.SceneManagement.SceneManager.LoadScene("stages/level01");

// ✅ CORRECT
var h = package.LoadSceneAsync("stages/level01");
yield return h;
```

### 4. Holding a `SceneHandle` and also calling `SceneManager.UnloadSceneAsync`

```csharp
// ❌ WRONG — the bundle refcount desyncs
UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(h.SceneObject);

// ✅ CORRECT — Release the handle; YooAsset performs the scene unload
h.Release();
```

### 5. Sync-loading on WebGL without `WebGLForceSyncLoadAsset`

```csharp
// ❌ WRONG — on WebGL, LoadAssetSync can return a not-yet-ready handle for some asset types
var h = package.LoadAssetSync<Texture2D>("ui/splash");

// ✅ CORRECT — either flag the init params, or use async + await
initParams.WebGLForceSyncLoadAsset = true;
// or
var h = package.LoadAssetAsync<Texture2D>("ui/splash");
yield return h;
```

Source: `Runtime/InitializeParameters.cs:54`.

### 6. Loading with `type = null` then assuming a specific type

```csharp
// ❌ Ambiguous — runtime resolves to UnityEngine.Object; GetAssetObject<Sprite>() might return null
var h = package.LoadAssetAsync("ui/icon");   // type defaults to Object
var s = h.GetAssetObject<Sprite>();

// ✅ CORRECT — be explicit
var h = package.LoadAssetAsync<Sprite>("ui/icon");
var s = h.GetAssetObject<Sprite>();
```

### 7. Assuming an address rename is transparent

```csharp
// ❌ Fragile — location strings are stable per bundle build, not per asset identity
package.LoadAssetAsync<GameObject>("player_v1");  // breaks when artists rename to "player"

// ✅ More robust for cross-build stability — load by GUID
var info = package.GetAssetInfoByGUID("abc12345...", typeof(GameObject));
var h = package.LoadAssetAsync(info);
```

Source: `Runtime/ResourcePackage/ResourcePackage.cs:462-477`.

## Canonical load templates

### Prefab, instantiate, release

```csharp
IEnumerator SpawnPlayer()
{
    var h = package.LoadAssetAsync<GameObject>("player");
    yield return h;
    if (h.Status != EOperationStatus.Succeed) { Debug.LogError(h.LastError); yield break; }
    h.InstantiateSync();
    h.Release();
}
```

### Sprite sheet sub-sprites

```csharp
async Task LoadAtlasAsync(string location)
{
    var h = package.LoadSubAssetsAsync<Sprite>(location);
    await h.Task;
    if (h.Status == EOperationStatus.Succeed)
    {
        // SubAssetsHandle exposes an array accessor
        // foreach (var sprite in h.GetSubAssetObjects<Sprite>()) { ... }
    }
    h.Release();
}
```

### Scene load with suspend

```csharp
IEnumerator LoadLevel(string location)
{
    var h = package.LoadSceneAsync(location, LoadSceneMode.Single, LocalPhysicsMode.None, suspendLoad: true);
    yield return new WaitUntil(() => h.Progress >= 0.9f);
    // show "press any key", then:
    // h.UnSuspend();
    yield return h;
    // scene is active; do NOT Release until you unload it
}
```

### Raw config file

```csharp
IEnumerator LoadConfig()
{
    var h = package.LoadRawFileAsync("configs/balance.json");
    yield return h;
    if (h.Status == EOperationStatus.Succeed)
    {
        string json = h.GetRawFileText();
        // parse json
    }
    h.Release();
}
```
