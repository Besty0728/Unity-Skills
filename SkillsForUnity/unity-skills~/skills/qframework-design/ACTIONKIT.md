# ActionKit, SingletonKit, AudioKit & ScreenTransition

Sub-doc of [qframework-design](./SKILL.md). ActionKit is QFramework's time-sequenced action system (composite + command + builder pattern); SingletonKit is the singleton toolbox it commonly pairs with; AudioKit and ScreenTransition are built on top of ActionKit.

## ActionKit: chained action composition

The core APIs are all static factory methods. The returned `IAction` / `ISequence` / `IParallel` / `IRepeat` are driven through `.Start(this, onFinish)` (`ActionKit.cs`):

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

ActionKit.Repeat(5) // -1 or 0 = repeat forever, 1 = once, 2 = twice
    .Condition(() => Input.GetMouseButtonDown(1))
    .Callback(() => Debug.Log("right click"))
    .Start(this, () => Debug.Log("finished"));
```

`Sequence` / `Repeat` / `Parallel` can all be nested (Doc.md:5729-5773). `Custom` hooks into a custom lifecycle (`OnStart` / `OnExecute` / `OnFinish`). It supports typed data (`ActionKit.Custom<TData>`) that is kept across multiple `OnExecute` calls (Doc.md:5782-5855):

```csharp
ActionKit.Custom<SomeData>(a =>
{
    a.OnStart(() => a.Data = new SomeData())
     .OnExecute(dt => { a.Data.Count++; if (a.Data.Count >= 5) a.Finish(); })
     .OnFinish(() => Debug.Log("Finished"));
}).Start(this);
```

Coroutines and `Task` can be plugged in directly (Doc.md:5863-5896, ActionKit.cs:359-389):

```csharp
ActionKit.Coroutine(SomeCoroutine).Start(this);
ActionKit.Task(SomeTaskAsync).Start(this);
```

### Global lifecycle events, independent of any MonoBehaviour

```csharp
ActionKit.OnUpdate.Register(() => { /* ... */ }).UnRegisterWhenGameObjectDestroyed(gameObject);
ActionKit.OnFixedUpdate.Register(() => { /* ... */ });
ActionKit.OnApplicationPause.Register(pause => Debug.Log(pause));
```

These are `EasyEvent`s (`ActionKit.cs:410-494`), driven by the singleton `ActionKitMonoBehaviourEvents.Instance`. You do not need to attach your own MonoBehaviour to forward Update.

### Rule: use IgnoreTimeScale to ignore Time.timeScale

```csharp
Time.timeScale = 0.25f;
ActionKit.Sequence()
    .Delay(3.0f)
    .Callback(() => Debug.Log("Unscaled Time:" + Time.unscaledTime))
    .Start(this)
    .IgnoreTimeScale();
```

The naming and usage are modeled on DOTween's `SetUpdate(isIndependentUpdate: true)` (Doc.md:6129-6167). By default, ActionKit Delay/Sequence timing follows `Time.timeScale`, so pausing the game (`timeScale = 0`) also freezes running ActionKit sequences. Sequences that must keep running while paused, such as UI animations, need `.IgnoreTimeScale()` explicitly.

### Rule: use StartCurrentScene to auto-stop on scene switch

```csharp
ActionKit.Sequence()
    .Delay(1.0f)
    .Callback(() => SceneManager.LoadScene(SceneManager.GetActiveScene().name))
    .Delay(1.0f)
    .Callback(() => Debug.Log("Not print")) // this line never runs after the scene reloads
    .StartCurrentScene();
```

`StartCurrentScene()`, the third startup method alongside `Start(component)` and `StartGlobal()`, automatically stops and recycles the action when the current scene changes (Doc.md:6071-6121). With plain `Start(this)` on a MonoBehaviour that gets destroyed, the action also stops when the component is destroyed. But if the action is attached to a cross-scene persistent object without `StartCurrentScene`, a scene switch will not interrupt it.

### DOTween / UniRx integration is "how to connect", not a built-in dependency

```csharp
// Import DOTween first, then import the adapter package from the Example
ActionKit.Custom(c => c.OnStart(() => transform.DOLocalMove(Vector3.one, 0.5f).OnComplete(c.Finish))).Start(this);
ActionKit.Sequence().DOTween(() => transform.DOScale(Vector3.one, 0.5f)).Start(this);

// UniRx works the same way
ActionKit.Sequence().UniRx(() => Observable.Timer(TimeSpan.FromSeconds(4.0f))).Start(this, () => LogKit.I("done"));
```

QFramework itself does not require DOTween or UniRx (Doc.md:5965-6070). The `.DOTween(...)` / `.UniRx(...)` Sequence extensions come from adapter packages that must be imported separately. If a script contains `using DG.Tweening;` and fails to compile, first check whether that adapter package is imported. Do not suspect the ActionKit core package for a missing dependency.

## SingletonKit: six singletons, chosen by scenario

QFramework includes six singleton implementations (Doc.md:6185-6531). The choice depends on the scenario requirement, not on which is "better":

| Type | Purpose |
|---|---|
| `Singleton<T>` | Plain C# singleton, not a MonoBehaviour, private constructor |
| `MonoSingleton<T>` | MonoBehaviour singleton, attaches its GameObject automatically |
| `MonoSingletonProperty<T>` | An existing MonoBehaviour class that needs singleton access without changing its inheritance chain |
| `SingletonProperty<T>` | A plain C# class that needs singleton access without changing its inheritance chain |
| `MonoSingletonPath` attribute | Used with the above; specifies the scene path/name where the singleton is mounted, e.g. `[MonoSingletonPath("[Audio]/AudioManager")]` |
| `PersistentMonoSingleton<T>` | Persists across scenes; if a second instance appears in a scene, keeps the one **created first** |
| `ReplaceableMonoSingleton<T>` | Persists across scenes; if a second instance appears in a scene, keeps the one **created last** |

`PersistentMonoSingleton` and `ReplaceableMonoSingleton` differ in one semantic only: keep the first or keep the last (Doc.md:6466-6524). Choosing the wrong one makes the instance you get after a scene switch differ from the lifecycle you expect. For example, you think you got the freshly created config from the new scene, but actually you got a singleton left over from the old scene.

```csharp
[MonoSingletonPath("[Audio]/AudioManager")]
public class AudioManager : ManagerBase, ISingleton
{
    public static AudioManager Instance => QMonoSingletonProperty<AudioManager>.Instance;
    public void OnSingletonInit() { }
    public void Dispose() => QMonoSingletonProperty<AudioManager>.Dispose();
}
```

## AudioKit: three channels

Three independent channels: Music (background music; only one plays at a time, and switching unloads the previous one), Sound (sound effects; several can play simultaneously), and Voice (speech; only one at a time, suited to narration) (Doc.md:8188-8194):

```csharp
AudioKit.PlayMusic("resources://game_bg");
AudioKit.PlaySound("resources://game_bg");
AudioKit.PlayVoice("resources://game_bg");
```

Chained API (newer, gradually replacing earlier, less uniform calls):

```csharp
AudioKit.Music().WithName("resources://game_bg").Loop(false).VolumeScale(0.5f).Play();
AudioKit.Sound().WithName("resources://button_clicked").VolumeScale(0.7f).Play().OnFinish(() => "done".LogInfo());
```

Combined with ActionKit's `Sequence`, you can use `PlaySound` for timed scheduling such as "play a sound effect after a few seconds" (Doc.md:8358-8384).

### Unique pitfall: accessing AudioKit.Settings in edit mode throws NullReferenceException

```csharp
AudioKit.Settings.IsSoundOn.Value = true; // throws NRE if Play Mode has never been entered in the Editor
```

`AudioKit.Settings` is a direct reference to `Architecture.SettingsModel` (`AudioKit.cs:233`):

```csharp
public static AudioKitSettingsModel Settings => Architecture.SettingsModel;
```

The `Architecture.SettingsModel` object itself exists as a static field from type load (`AudioKitArchitecture.cs:17`). But its inner properties `IsSoundOn` / `IsMusicOn` / `MusicVolume` and others are assigned only in `OnInit()` (`AudioKitSettings.cs:38-61`):

```csharp
protected override void OnInit()
{
    IsSoundOn = new PlayerPrefsBooleanProperty(KeyAudioManagerSoundOn, true);
    // ... the other fields are the same; all are null until assigned
}
```

`OnInit()` runs only when the model is registered with the Architecture via `RegisterModel`, triggered by `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]` at `AudioKitArchitecture.cs:23-28`:

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
public static void AutoInit() { InitArchitecture(); }
```

`RuntimeInitializeOnLoadMethod` fires only once when entering Play Mode (or running on a device). In an edit-only session where Play was never pressed, this code never runs. Conclusion: **Editor scripts, AI editor tools, and `[InitializeOnLoad]` static initialization code must not directly access `AudioKit.Settings.*`**. That code may access it only at runtime, after Play Mode has started. If editor extensions really need to read audio settings, either wait until Play Mode has triggered, or read the underlying `PlayerPrefs` keys directly (such as `KEY_AUDIO_MANAGER_SOUND_ON`) to bypass the Architecture.

### Custom audio loading

By default, `AudioKit.Config.AudioLoaderPool` is set to load via ResKit by `AudioKitWithResKitInit`, which is also `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`. To use a custom source such as Resources, comment out this default registration. Otherwise the two overwrite each other (Doc.md:8248-8330). This is the same pattern as the UIKit custom-loader pitfall.

## ScreenTransition

Built-in `FadeIn` / `FadeOut` / `FadeInOut` cover three common transitions. It is also a wrapper around ActionKit and is driven by `.Start(this)` (Doc.md:8713-8790):

```csharp
ActionKit.ScreenTransition.FadeIn().Start(this);
ActionKit.ScreenTransition.FadeOut().Color(Color.red).Start(this);

ActionKit.ScreenTransition
    .FadeInOut()
    .In(fadeIn => fadeIn.Duration(0.5f).Color(Color.green))
    .Out(fadeOut => fadeOut.Duration(0.5f).Color(Color.blue))
    .OnInFinish(() => Debug.Log("load scene here")) // the moment FadeIn completes, where the scene switch happens
    .Start(this);
```

`OnInFinish` is the typical scene-switch hook: FadeIn covers the screen in black, the actual scene load happens in `OnInFinish`, and then control passes to `FadeOut` to fade back in.

## See also

- For event and binding APIs beyond `EasyEvent` (reactive bindings), see [EVENT_TOOLS.md](./EVENT_TOOLS.md).
