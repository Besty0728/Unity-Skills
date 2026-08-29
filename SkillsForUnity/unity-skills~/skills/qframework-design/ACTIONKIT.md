# ActionKit, SingletonKit, AudioKit & ScreenTransition

Sub-doc of [qframework-design](./SKILL.md). ActionKit is QFramework's time-sequenced action system (composite + command + builder pattern); SingletonKit is the singleton toolbox it commonly pairs with; AudioKit and ScreenTransition are built on top of ActionKit.

## ActionKit：链式动作组合

核心 API 都是静态工厂方法，返回的 `IAction`/`ISequence`/`IParallel`/`IRepeat` 通过 `.Start(this, onFinish)` 驱动（`ActionKit.cs`）：

```csharp
ActionKit.Delay(1.0f, () => Debug.Log("1s later")).Start(this);

ActionKit.Sequence()
    .Callback(() => Debug.Log("Delay Start"))
    .Delay(1.0f)
    .Callback(() => Debug.Log("Delay Finish"))
    .Start(this, _ => Debug.Log("Sequence Finish"));

ActionKit.Parallel()
    .Delay(1.0f, () => Debug.Log("a"))
    .Delay(2.0f, () => Debug.Log("b"))
    .Start(this, () => Debug.Log("Parallel Finish"));

ActionKit.Repeat(5) // -1 或 0 = 永远重复，1 = 一次，2 = 两次
    .Condition(() => Input.GetMouseButtonDown(1))
    .Callback(() => Debug.Log("right click"))
    .Start(this, () => Debug.Log("finished"));
```

`Sequence`/`Repeat`/`Parallel` 都可以嵌套组合（Doc.md:5729-5773），`Custom` 用来接自定义生命周期（`OnStart`/`OnExecute`/`OnFinish`），支持带类型数据（`ActionKit.Custom<TData>`）在多次 `OnExecute` 之间保存状态（Doc.md:5782-5855）：

```csharp
ActionKit.Custom<SomeData>(a =>
{
    a.OnStart(() => a.Data = new SomeData())
     .OnExecute(dt => { a.Data.Count++; if (a.Data.Count >= 5) a.Finish(); })
     .OnFinish(() => Debug.Log("Finished"));
}).Start(this);
```

协程和 `Task` 也能直接接入（Doc.md:5863-5896, ActionKit.cs:359-389）：

```csharp
ActionKit.Coroutine(SomeCoroutine).Start(this);
ActionKit.Task(SomeTaskAsync).Start(this);
```

### 全局生命周期事件，不依赖某个 MonoBehaviour

```csharp
ActionKit.OnUpdate.Register(() => { /* ... */ }).UnRegisterWhenGameObjectDestroyed(gameObject);
ActionKit.OnFixedUpdate.Register(() => { /* ... */ });
ActionKit.OnApplicationPause.Register(pause => Debug.Log(pause));
```

这些是 `EasyEvent`（`ActionKit.cs:410-494`），底层由单例 `ActionKitMonoBehaviourEvents.Instance` 驱动的 mono 事件，不需要自己再挂一个 MonoBehaviour 去转发 Update。

### 规则：不受 Time.timeScale 限制用 IgnoreTimeScale

```csharp
Time.timeScale = 0.25f;
ActionKit.Sequence()
    .Delay(3.0f)
    .Callback(() => Debug.Log("Unscaled Time:" + Time.unscaledTime))
    .Start(this)
    .IgnoreTimeScale();
```

命名和用法都是照抄 DOTween 的 `SetUpdate(isIndependentUpdate: true)` 习惯设计的（Doc.md:6129-6167）。默认情况下 ActionKit 的 Delay/Sequence 计时是跟随 `Time.timeScale` 的，暂停游戏（`timeScale = 0`）也会连带把正在跑的 ActionKit 动作序列冻住——UI 动画之类不该被暂停的序列要显式 `.IgnoreTimeScale()`。

### 规则：场景切换自动停止用 StartCurrentScene

```csharp
ActionKit.Sequence()
    .Delay(1.0f)
    .Callback(() => SceneManager.LoadScene(SceneManager.GetActiveScene().name))
    .Delay(1.0f)
    .Callback(() => Debug.Log("Not print")) // 场景重载后这行永远不会跑到
    .StartCurrentScene();
```

`StartCurrentScene()`（相对 `Start(component)` / `StartGlobal()` 的第三种启动方式）会在当前场景切换时自动停止并回收这个 Action（Doc.md:6071-6121）。用普通 `Start(this)` 挂在会被销毁的 MonoBehaviour 上时，组件销毁后动作也会跟着停，但如果动作挂在跨场景常驻对象上又没用 `StartCurrentScene`，场景切换不会自动打断它。

### DOTween / UniRx 集成是"教你接入"，不是内置依赖

```csharp
// 需要先自行导入 DOTween，再导入 Example 里的适配包
ActionKit.Custom(c => c.OnStart(() => transform.DOLocalMove(Vector3.one, 0.5f).OnComplete(c.Finish))).Start(this);
ActionKit.Sequence().DOTween(() => transform.DOScale(Vector3.one, 0.5f)).Start(this);

// UniRx 同理
ActionKit.Sequence().UniRx(() => Observable.Timer(TimeSpan.FromSeconds(4.0f))).Start(this, () => LogKit.I("done"));
```

QFramework 本体不强制依赖 DOTween 或 UniRx（Doc.md:5965-6070）；`.DOTween(...)` / `.UniRx(...)` 这些 Sequence 扩展方法来自需要额外导入的适配包，脚本里出现 `using DG.Tweening;` 却编译不过时，先确认有没有导入这个适配包，而不是怀疑 ActionKit 主包缺依赖。

## SingletonKit：6 种单例，按场景选

QFramework 收纳了 6 种单例实现（Doc.md:6185-6531），选择依据是场景需求而非优劣：

| 类型 | 用途 |
|---|---|
| `Singleton<T>` | 纯 C# 单例，非 MonoBehaviour，构造私有 |
| `MonoSingleton<T>` | MonoBehaviour 单例，自动挂载 GameObject |
| `MonoSingletonProperty<T>` | 已有 MonoBehaviour 类想要单例访问，不改继承链 |
| `SingletonProperty<T>` | 纯 C# 类想要单例访问，不改继承链 |
| `MonoSingletonPath` 特性 | 配合上面几种，指定单例挂载的场景路径/命名，如 `[MonoSingletonPath("[Audio]/AudioManager")]` |
| `PersistentMonoSingleton<T>` | 跨场景常驻；场景里出现第二个实例时保留**先创建**的那个 |
| `ReplaceableMonoSingleton<T>` | 跨场景常驻；场景里出现第二个实例时保留**后创建**的那个 |

`PersistentMonoSingleton` 与 `ReplaceableMonoSingleton` 只有"保先"还是"保后"这一个语义差异（Doc.md:6466-6524），选错会导致场景切换后拿到的实例和预期的生命周期不一致（比如以为拿到的是新场景刚创建的配置，实际拿到的是旧场景遗留的单例）。

```csharp
[MonoSingletonPath("[Audio]/AudioManager")]
public class AudioManager : ManagerBase, ISingleton
{
    public static AudioManager Instance => QMonoSingletonProperty<AudioManager>.Instance;
    public void OnSingletonInit() { }
    public void Dispose() => QMonoSingletonProperty<AudioManager>.Dispose();
}
```

## AudioKit：三通道音频

三个独立通道：Music（背景音乐，同时只能播一个，切换会直接卸载上一个）、Sound（音效，可同时播多个）、Voice（人声，同时只能播一个，适合旁白）（Doc.md:8188-8194）：

```csharp
AudioKit.PlayMusic("resources://game_bg");
AudioKit.PlaySound("resources://game_bg");
AudioKit.PlayVoice("resources://game_bg");
```

链式 API（较新，逐步替代早期不够统一的调用方式）：

```csharp
AudioKit.Music().WithName("resources://game_bg").Loop(false).VolumeScale(0.5f).Play();
AudioKit.Sound().WithName("resources://button_clicked").VolumeScale(0.7f).Play().OnFinish(() => "done".LogInfo());
```

配合 ActionKit 的 `Sequence`，可以用 `PlaySound` 做「延时几秒后播放某个音效」这类时序编排（Doc.md:8358-8384）。

### 独家陷阱：纯编辑态访问 AudioKit.Settings 会 NullReferenceException

```csharp
AudioKit.Settings.IsSoundOn.Value = true; // 编辑器里从未进过 Play Mode 时，这行 NRE
```

`AudioKit.Settings` 是 `Architecture.SettingsModel` 的直接引用（`AudioKit.cs:233`）：

```csharp
public static AudioKitSettingsModel Settings => Architecture.SettingsModel;
```

`Architecture.SettingsModel` 对象本身作为静态字段在类型加载时就存在（`AudioKitArchitecture.cs:17`），但它内部的 `IsSoundOn` / `IsMusicOn` / `MusicVolume` 等属性只在 `OnInit()` 里被赋值（`AudioKitSettings.cs:38-61`）：

```csharp
protected override void OnInit()
{
    IsSoundOn = new PlayerPrefsBooleanProperty(KeyAudioManagerSoundOn, true);
    // ... 其余字段同理，赋值前全部是 null
}
```

而 `OnInit()` 只有通过 `RegisterModel` 注册进 Architecture 才会被调用，触发点是 `AudioKitArchitecture.cs:23-28` 的 `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`：

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
public static void AutoInit() { InitArchitecture(); }
```

`RuntimeInitializeOnLoadMethod` 只在进入 Play Mode（或真机运行）时触发一次，纯编辑态、从未点过播放按钮的会话里这段代码不会执行。结论：**Editor 脚本、AI 编辑器工具、`[InitializeOnLoad]` 静态初始化代码禁止直接访问 `AudioKit.Settings.*`**，这类代码只能在运行时（Play Mode 已启动之后）访问；编辑器扩展如果确实需要读音频设置，要么等 Play Mode 触发后再读，要么直接读底层的 `PlayerPrefs` key（如 `KEY_AUDIO_MANAGER_SOUND_ON`）绕开 Architecture。

### 自定义音频加载

默认 `AudioKit.Config.AudioLoaderPool` 由 `AudioKitWithResKitInit`（同样是 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`）设成走 ResKit 加载；换成自定义来源（如 Resources）时要注释掉这段默认注册，否则两边会互相覆盖（Doc.md:8248-8330），和 UIKit 自定义加载器的坑是同一个模式。

## ScreenTransition：屏幕过渡

内置 `FadeIn`/`FadeOut`/`FadeInOut` 三种常用过渡，本质也是 ActionKit 的一个封装，同样用 `.Start(this)` 驱动（Doc.md:8713-8790）：

```csharp
ActionKit.ScreenTransition.FadeIn().Start(this);
ActionKit.ScreenTransition.FadeOut().Color(Color.red).Start(this);

ActionKit.ScreenTransition
    .FadeInOut()
    .In(fadeIn => fadeIn.Duration(0.5f).Color(Color.green))
    .Out(fadeOut => fadeOut.Duration(0.5f).Color(Color.blue))
    .OnInFinish(() => Debug.Log("load scene here")) // FadeIn 完成、切场景的时机点
    .Start(this);
```

`OnInFinish` 是典型的换场景钩子：先 FadeIn 盖黑屏，`OnInFinish` 里做真正的场景加载，再交给 `FadeOut` 淡出。

## 参见

- 事件/绑定相关 API（`EasyEvent` 之外的响应式绑定）见 [EVENT_TOOLS.md](./EVENT_TOOLS.md)。
