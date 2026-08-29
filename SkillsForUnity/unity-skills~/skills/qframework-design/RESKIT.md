# ResKit

Sub-doc of [qframework-design](./SKILL.md). Resource management: AssetBundle-first, reference-counted loading, simulate mode for dev iteration.

## 推荐用 AssetBundle，不是 Resources

Res Kit 支持从 dataPath、Resources、StreamingAssetPath、PersistentDataPath、网络等来源加载，但官方推荐路线是 AssetBundle 方式（Doc.md:6553），因为 ResKit 封装后的 AB 用法比 Resources 更好用，且 AB 本身包体更小、支持热更。

## 规则：一次标记 = 一个 AssetBundle

右键 `@ResKit - AssetBundle Mark` 标记文件或文件夹，一次标记对应生成一个 AB。多个资源要打进同一个 AB，做法是把它们放进同一个文件夹再标记文件夹，而不是分别标记每个文件（Doc.md:6590）。

```csharp
// ResKitAssetsMenu.cs:69 起 —— MarkAB 是 toggle，再点一次会取消标记
public static void MarkAB(string path)
{
    if (Marked(path)) { ai.assetBundleName = null; }        // 已标记 → 取消
    else               { ai.assetBundleName = dir.Name.Replace(".", "_"); } // 未标记 → 标记
}
```

标记状态可以从 Inspector 的勾选框或菜单项的 checked 状态判断，误以为菜单项是「只标记不取消」的一次性操作会导致重复点击后又把标记去掉了。

## 硬坑：场景必须独占一个 AssetBundle

加载场景前要确保这个场景本身单独打进一个 AB，不能和其他资源共享（Doc.md:6821）：

> 注意：标记场景时要确保，一个场景是一个 AssetBundle。

```csharp
mResLoader.LoadSceneSync("SceneRes");
mResLoader.LoadSceneAsync("SceneRes");
mResLoader.LoadSceneAsync("SceneRes", onStartLoading: operation => { /* 加载中回调 */ });
```

## 模拟模式与非模拟模式

模拟模式（Simulation Mode）不真正加载 AB，而是直接读 `Application.dataPath`（即 Assets 目录）下的资源，用来省去开发阶段频繁打包的成本（Doc.md:6668-6680）。开关是编辑器面板里的勾选框，底层落在 `EditorPrefs` key `"SimulateAssetBundles"`，默认值为 `true`——新建工程或新拉的分支默认就是模拟模式。

阶段划分（Doc.md:6686-6717）：

- **开发阶段**：勾选模拟模式，资源改了立即生效，不用重新打包。
- **真机/测试阶段**：取消勾选模拟模式，每次打 App 包前先 Build 一次 AB；也可以在 Editor 里取消模拟模式来验证真实 AB 加载路径是否正常。

不确认当前处于哪种模式就去调试「资源没更新」的问题，很容易把模拟模式的假象当成真实 AB 的 bug。

## ResLoader：引用获取，不是真加载

```csharp
private ResLoader mResLoader = ResLoader.Allocate();

private void Start()
{
    ResKit.Init(); // 项目启动调用一次
    var prefab = mResLoader.LoadSync<GameObject>("AssetObj");
}

private void OnDestroy()
{
    mResLoader.Recycle2Cache(); // 释放引用，不一定真卸载
    mResLoader = null;
}
```

`ResLoader` 本身不做真正的资源加载，只是维护一份「这个单元加载过哪些资源」的引用记录；真正的加载和引用计数发生在 `ResMgr`（Doc.md:7278-7288）。`Recycle2Cache()` 释放的是引用计数，只有计数归零才会真正卸载底层资源。

### 规则：每个脚本/界面申请一个 ResLoader

推荐粒度是「每个需要加载资源的单元（脚本、UIPanel）申请一个 ResLoader」，退出时统一 `Recycle2Cache()`（Doc.md:7225-7292）。`ResLoader` 从对象池申请，几乎零开销，不需要为了省 `Allocate` 调用而多个单元共用一个 ResLoader——共用会让释放粒度绑死，一个单元还没退出就没法单独释放另一个单元用过的资源。

## 异步加载：先 Add2Load，再 LoadAsync

```csharp
mResLoader.Add2Load("TestObj", (succeed, res) =>
{
    if (succeed) res.Asset.As<GameObject>().Instantiate();
});
mResLoader.LoadAsync(); // 统一触发，可以支持一次性并发加载多个资源
```

分两步是为了让多个 `Add2Load` 调用先排进队列，再用一次 `LoadAsync()` 并发拉起，而不是每 `Add2Load` 一个就立刻各自发一次请求（Doc.md:6748-6750）。

## 硬坑：AB 跨包 Prefab 依赖会 Missing（Unity 官方 bug）

如果一个 AB 里的 Prefab 依赖了另一个 AB 里的 Prefab（注意：是 Prefab 类型的依赖，同为其他 AB 的 texture 等资源不受影响），加载后会出现 Missing Prefab（Doc.md:8798-8819）。这是 Unity 的已知 bug（`AssetDatabase.GetAssetBundleDependencies` 返回的依赖列表在依赖项是 Prefab 时会漏掉），受影响版本见官方 issue tracker 链接。

规避方式：需要跨 AB 引用的 Prefab 资源，改用代码显式加载该 Prefab 所在的 AB 并取得引用，而不是依赖 Unity 自动解析的跨包依赖关系。

## 构建输出目录固定，不可参数化

`BuildScript.BuildAssetBundles` 的输出路径是硬编码的 `AssetBundles/<平台名>`（`BuildScript.cs:33,43`）：

```csharp
var outputPath = Path.Combine(ResKitAssetsMenu.AssetBundlesOutputPath, GetPlatformName());
// ResKitAssetsMenu.AssetBundlesOutputPath == "AssetBundles"
```

构建完成后会把这个目录整体搬进 `Application.streamingAssetsPath + "/AssetBundles/" + 平台名`（`BuildScript.cs:61-66`）。想改输出根目录，只能改 `ResKitAssetsMenu.AssetBundlesOutputPath` 这个常量本身，没有暴露参数或配置项。

## SpriteAtlas / 网络图片 / PersistentDataPath 图片

三者都走同一套 `ResLoader.Add2Load` / `LoadSync` API，区别只在资源名前缀（`resources://`、URL 走 `.ToNetImageResName()`、本地文件走 `.ToLocalImageResName()`），本质上都是`自定义 Res` 机制的内置实现（Doc.md:6949-7095, 7096-7175）。需要关联对象（如 `Sprite.Create` 出来的 Sprite）跟随资源一起释放时，用：

```csharp
resLoader.AddObjectForDestroyWhenRecycle2Cache(sprite); // Recycle2Cache 时一并 Destroy
```

## 自定义 Res

内置的 AssetBundle/Resources/网络图片/PersistentDataPath 加载全部是通过「自定义 Res」扩展出来的，可以照着同样的模式接入 Addressables 或其他资源方案（Doc.md:7096-7175）：

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

ResFactory.AddResCreator<MyResCreator>(); // 注册后 myres:// 前缀的资源名会走这条路径
```

## 代码生成资源名常量

代码生成按钮会生成 `QAssets.cs`（命名空间 `QAssetBundle`），把 AB 名和资源名固化成常量类，配合 IDE 补全避免手写字符串拼错（Doc.md:7185-7217）。

## WebGL 注意事项

WebGL 平台 AB 加载只支持异步，`ResKit.Init()` 要换成 `ResKit.InitAsync()`（Doc.md:7306-7321）：

```csharp
StartCoroutine(ResKit.InitAsync());
// 或
ResKit.InitAsync().ToAction().StartGlobal();
```

异步加载资源固定两步：先 `Add2Load`，再调用 `LoadAsync()`；跳过第一步直接调用会没有队列内容可加载。
