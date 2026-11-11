# ResKit

Sub-doc of [qframework-design](./MODULE.md). Resource management: AssetBundle-first, reference-counted loading, simulate mode for dev iteration.

## Prefer AssetBundles, not Resources

ResKit can load from dataPath, Resources, StreamingAssetPath, PersistentDataPath, the network and other sources. The officially recommended route is AssetBundles (Doc.md:6553), because ResKit's wrapped AssetBundle usage is easier to use than Resources, and AssetBundles keep package size smaller and support hot updates.

## Rule: one mark = one AssetBundle

Right-click `@ResKit - AssetBundle Mark` on a file or folder. Each mark generates one AssetBundle. To put several assets into the same bundle, put them in one folder and mark the folder, rather than marking each file separately (Doc.md:6590).

```csharp
// ResKitAssetsMenu.cs:69 onward - MarkAB is a toggle; clicking again removes the mark
public static void MarkAB(string path)
{
    if (Marked(path)) { ai.assetBundleName = null; }        // already marked -> unmark
    else               { ai.assetBundleName = dir.Name.Replace(".", "_"); } // not marked -> mark
}
```

The mark state can be read from the Inspector checkbox or from the checked state of the menu item. Assuming the menu item is a one-way "mark only" action leads to repeated clicks removing the mark again.

## Pitfall: a scene must occupy its own AssetBundle

Before loading a scene, make sure the scene itself is packed into its own AssetBundle, not shared with other assets (Doc.md:6821):

> Note: when marking a scene, make sure one scene is one AssetBundle.

```csharp
mResLoader.LoadSceneSync("SceneRes");
mResLoader.LoadSceneAsync("SceneRes");
mResLoader.LoadSceneAsync("SceneRes", onStartLoading: operation => { /* callback while loading */ });
```

## Simulation mode vs non-simulation mode

Simulation mode does not actually load AssetBundles. It reads assets directly under `Application.dataPath` (the Assets folder), which saves the cost of frequent builds during development (Doc.md:6668-6680). The switch is a checkbox in the editor panel, stored in the `EditorPrefs` key `"SimulateAssetBundles"`, with default value `true`. A new project or a freshly pulled branch starts in simulation mode.

Phases (Doc.md:6686-6717):

- **Development**: enable simulation mode. Asset changes take effect immediately without rebuilding bundles.
- **Device / test phase**: disable simulation mode. Build the AssetBundles before each app build. You can also disable simulation mode in the Editor to verify that the real AssetBundle load path works.

If you do not know which mode is active while debugging "assets did not update", you can easily mistake a simulation-mode artifact for a real AssetBundle bug.

## ResLoader: acquires references, does not load

```csharp
private ResLoader mResLoader = ResLoader.Allocate();

private void Start()
{
    ResKit.Init(); // call once at project startup
    var prefab = mResLoader.LoadSync<GameObject>("AssetObj");
}

private void OnDestroy()
{
    mResLoader.Recycle2Cache(); // releases references; does not necessarily unload
    mResLoader = null;
}
```

`ResLoader` itself does not perform the actual loading. It only keeps a record of which assets this unit has loaded. Actual loading and reference counting happen in `ResMgr` (Doc.md:7278-7288). `Recycle2Cache()` releases reference counts; the underlying asset is actually unloaded only when its count reaches zero.

### Rule: each script or panel requests its own ResLoader

The recommended granularity is "each unit that loads assets (a script, a UIPanel) requests its own ResLoader", and calls `Recycle2Cache()` on exit (Doc.md:7225-7292). `ResLoader` is taken from an object pool and has almost zero overhead. Do not share one ResLoader among several units just to save `Allocate` calls: sharing ties their release granularity together, so one unit cannot release its assets while another unit still needs them.

## Async loading: Add2Load first, then LoadAsync

```csharp
mResLoader.Add2Load("TestObj", (succeed, res) =>
{
    if (succeed) res.Asset.As<GameObject>().Instantiate();
});
mResLoader.LoadAsync(); // trigger once; supports loading several assets concurrently in one go
```

The two steps exist so that several `Add2Load` calls queue up first, and one `LoadAsync()` then starts them concurrently. This avoids issuing a separate request for each `Add2Load` (Doc.md:6748-6750).

## Pitfall: cross-bundle Prefab dependencies go Missing (Unity official bug)

If a Prefab in one AssetBundle depends on a Prefab in another AssetBundle (note: only Prefab-type dependencies; other bundles' textures and similar assets are not affected), the Prefab shows Missing after loading (Doc.md:8798-8819). This is a known Unity bug: the dependency list returned by `AssetDatabase.GetAssetBundleDependencies` drops entries when the dependency is a Prefab. See the official issue tracker link in the tutorial for affected versions.

Workaround: for Prefabs that must be referenced across bundles, explicitly load the AssetBundle containing that Prefab in code and obtain the reference, rather than relying on Unity's automatic cross-bundle dependency resolution.

## Build output directory is fixed, not parameterized

The output path of `BuildScript.BuildAssetBundles` is hard-coded to `AssetBundles/<platform name>` (`BuildScript.cs:33,43`):

```csharp
var outputPath = Path.Combine(ResKitAssetsMenu.AssetBundlesOutputPath, GetPlatformName());
// ResKitAssetsMenu.AssetBundlesOutputPath == "AssetBundles"
```

After the build, the whole directory is moved into `Application.streamingAssetsPath + "/AssetBundles/" + platform name` (`BuildScript.cs:61-66`). To change the output root, the only option is to change the `ResKitAssetsMenu.AssetBundlesOutputPath` constant itself. No parameter or configuration is exposed.

## SpriteAtlas / network images / PersistentDataPath images

All three use the same `ResLoader.Add2Load` / `LoadSync` API. They differ only in the resource name prefix (`resources://`, URLs go through `.ToNetImageResName()`, local files through `.ToLocalImageResName()`). Essentially they are built-in implementations of the custom-Res mechanism (Doc.md:6949-7095, 7096-7175). When an associated object (such as a Sprite created with `Sprite.Create`) must be released together with the resource, use:

```csharp
resLoader.AddObjectForDestroyWhenRecycle2Cache(sprite); // destroyed together on Recycle2Cache
```

## Custom Res

The built-in AssetBundle / Resources / network image / PersistentDataPath loading are all extended through "custom Res". You can follow the same pattern to plug in Addressables or another resource system (Doc.md:7096-7175):

```csharp
public class MyRes : Res
{
    public override bool LoadSync() { State = ResState.Ready; return true; }
    public override void LoadAsync() { State = ResState.Ready; }
    protected override void OnReleaseRes() { State = ResState.Waiting; }
}

public class MyResCreator : IResCreator
{
    public bool Match(ResSearchKeys keys) => keys.AssetName.StartsWith("myres://");
    public IRes Create(ResSearchKeys keys) => new MyRes(keys.AssetName);
}

ResFactory.AddResCreator<MyResCreator>(); // after registration, resource names with the myres:// prefix take this path
```

## Code-generated resource name constants

The code generation button produces `QAssets.cs` (namespace `QAssetBundle`), which bakes AssetBundle names and asset names into constant classes. Combined with IDE completion, this avoids typos from hand-written strings (Doc.md:7185-7217).

## WebGL notes

On WebGL, AssetBundle loading supports only async. Replace `ResKit.Init()` with `ResKit.InitAsync()` (Doc.md:7306-7321):

```csharp
StartCoroutine(ResKit.InitAsync());
// or
ResKit.InitAsync().ToAction().StartGlobal();
```

Asynchronous asset loading always takes two steps: first `Add2Load`, then call `LoadAsync()`. Skipping the first step and calling `LoadAsync()` directly leaves the queue empty, so nothing is loaded.
