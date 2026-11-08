# EVENT_TOOLS - TypeEventSystem / EasyEvent / IOCContainer vs IOCKit / Unregister Patterns

`Doc.md:3198` says verbatim: "QFramework also provides three tools that can be used outside the architecture: TypeEventSystem, EasyEvent, BindableProperty, IOCContainer". The original text says "three" but lists four names, a typo in the tutorial. The tool count is not really three. These four tools are the building blocks that `Architecture<T>` assembles internally (for BindableProperty see [BINDABLE_QUERY.md](./BINDABLE_QUERY.md)).

## 1. TypeEventSystem

```csharp
public class TypeEventSystem
{
    private readonly EasyEvents mEvents = new EasyEvents();
    public static readonly TypeEventSystem Global = new TypeEventSystem();

    public void Send<T>() where T : new() => mEvents.GetEvent<EasyEvent<T>>()?.Trigger(new T());
    public void Send<T>(T e) => mEvents.GetEvent<EasyEvent<T>>()?.Trigger(e);
    public IUnRegister Register<T>(Action<T> onEvent) => mEvents.GetOrAddEvent<EasyEvent<T>>().Register(onEvent);
    public void UnRegister<T>(Action<T> onEvent) => mEvents.GetEvent<EasyEvent<T>>()?.UnRegister(onEvent);
}
```
Source: `QFramework.cs:624-641`. It looks up the `EasyEvents` dictionary by the **generic type argument** (not the runtime type of the event value). `Architecture<T>.SendEvent/RegisterEvent` forward to a private `mTypeEventSystem` instance (`QFramework.cs:197-205`). The architecture's event system and `TypeEventSystem.Global` are two independent instances that do not interfere with each other.

**Key detail of event inheritance**: `Register<IEventA>` creates an entry keyed `EasyEvent<IEventA>`. `Send<IEventA>(new EventB())` hits it (`EventB : IEventA` converts implicitly). But `Send<EventB>()` looks up the key `EasyEvent<EventB>`, which was never registered, so `GetEvent` returns `default` and the `?.Trigger` short-circuits. It is **silently ineffective and raises no error** (`Doc.md:3288-3301`; "ineffective" marked at 3291). Pitfall: the caller assumes events are keyed by runtime type, but they are keyed by the `T` written in the `Send<T>` / `Register<T>` call.

**The interface-event pattern** relies on `IOnEvent<T>` plus `OnGlobalEventExtension`:
```csharp
public static class OnGlobalEventExtension
{
    public static IUnRegister RegisterEvent<T>(this IOnEvent<T> self) where T : struct =>
        TypeEventSystem.Global.Register<T>(self.OnEvent);
    public static void UnRegisterEvent<T>(this IOnEvent<T> self) where T : struct =>
        TypeEventSystem.Global.UnRegister<T>(self.OnEvent);
}
```
Source: `QFramework.cs:208-220`. **These two extension methods require `T : struct`**, so the interface-event pattern only works with struct events. Defining an event type as a class and going this route fails to compile. Explicit `Action<T>` registration (`TypeEventSystem.Global.Register<T>(handler)`) has no such restriction, and both class and struct events work there.

## 2. Manual vs automatic unregistration

- **Manual unregistration must use named methods**: `Register<EventA>(OnEventA)` paired with `UnRegister<EventA>(OnEventA)` (`Doc.md:3305-3338`). If the registration used an anonymous lambda, there is no reference to `-=` with, so manual unregistration is impossible.
- **Automatic unregistration**: `.UnRegisterWhenGameObjectDestroyed(gameObject)` attaches `UnRegisterOnDestroyTrigger`; `.UnRegisterWhenDisabled(gameObject)` attaches `UnRegisterOnDisableTrigger`. Both use idempotent `GetOrAddComponent<T>` (`QFramework.cs:592-608`).
- **`.UnRegisterWhenCurrentSceneUnloaded()`** (`QFramework.cs:610-611`) needs no GameObject argument. Internally, it is a `DontDestroyOnLoad` singleton with `HideFlags.HideInHierarchy` named `UnRegisterCurrentSceneUnloadedTrigger`, which subscribes to `SceneManager.sceneUnloaded` and unregisters when the current scene unloads (`QFramework.cs:546-573`).
- **Non-MonoBehaviour classes** use `IUnRegisterList` + `AddToUnregisterList` + `UnRegisterAll()` to clean up in batch manually (`QFramework.cs:473-492`, example `Doc.md:3417-3434`).

Summary (`Doc.md:3440-3461`): define event types as `struct` to reduce GC. The interface-event pattern is more constrained but lets the IDE generate boilerplate; the cost is the `struct` requirement.

## 3. EasyEvent family

```csharp
public class EasyEvent : IEasyEvent           // no parameters
public class EasyEvent<T> : IEasyEvent        // 1 generic argument
public class EasyEvent<T, K> : IEasyEvent      // 2 generic arguments
public class EasyEvent<T, K, S> : IEasyEvent    // 3 generic arguments, the QFramework upper limit
```
Source: `QFramework.cs:800-883`. All return an `IUnRegister` from `Register(...)`, backed by a `CustomUnRegister`, and fire via `Trigger(...)`. `EasyEvents` (`QFramework.cs:885-913`) is a dictionary storing `IEasyEvent` by type; `TypeEventSystem` internally wraps an `EasyEvents`.

Trade-offs (`Doc.md:3549-3560`):

| | Auto unregister | Relative performance | Parameter readability | Typical use |
|---|---|---|---|---|
| C# native delegate/event | No | Highest | Named | - |
| `EasyEvent` | Yes | Close to native delegate | No names; you must remember parameter order | Internals of general systems (inventory, dialogue); rapid early prototyping |
| `TypeEventSystem` | Yes | Slightly below EasyEvent | Self-documenting via event type | Cross-module broadcast, collaborative projects, long-term maintenance |

`OrEvent` (`QFramework.cs:921-957`) merges several `IEasyEvent`s (including `BindableProperty`, since it implements `IEasyEvent`) into "fires when any one fires": `a.Or(b).Or(c).Register(...)`.

## 4. Core IOCContainer vs Toolkits IOCKit: two entirely different containers

### Core IOCContainer
```csharp
public class IOCContainer
{
    private Dictionary<Type, object> mInstances = new Dictionary<Type, object>();
    public void Register<T>(T instance) { /* overwrite if present, otherwise Add */ }
    public T Get<T>() where T : class { /* returns null if not found, does not throw */ }
    public IEnumerable<T> GetInstancesByType<T>() => mInstances.Values.Where(i => typeof(T).IsInstanceOfType(i)).Cast<T>();
    public void Clear() => mInstances.Clear();
}
```
Source: `QFramework.cs:647-684`. It is a very thin wrapper over a `Dictionary<Type,object>`: **it only supports singleton registration and retrieval**, with no factories, no object pools and no reflection-based injection. `Architecture<T>` holds one instance privately (`QFramework.cs:130`), and `RegisterSystem/RegisterModel/RegisterUtility` and `GetSystem/GetModel/GetUtility` all delegate to it. The author's words: this is a "very simple inversion-of-control container". Compared with containers like Zenject that have built-in object pools and object factories, it is deliberately simpler (`Doc.md:3641-3647`).

### Toolkits IOCKit (`_CoreKit/IOCKit/IOCKit.cs`, not in QFramework.cs)
The interface `IQFrameworkContainer` (`IOCKit.cs:33-136`) is far heavier than the core `IOCContainer`:

- `[Inject]` attribute (`IOCKit.cs:19-31`, `AttributeUsage(Field | Property)`, optional `Name` for named injection)
- `Register<TSource,TTarget>(name)` / `RegisterInstance<TBase>(instance, name, injectNow)`: supports both type mapping (creating instances on demand with `Activator.CreateInstance`) and direct instance registration. Both accept a namespace-like `name` to distinguish multiple implementations
- `RegisterRelation<TFor,TBase,TConcrete>()` / `ResolveRelation<TFor,TBase>()`: relationship mapping, meaning "in the context of TFor, resolve TBase to TConcrete". The core IOCContainer has no such concept
- `Inject(object obj)` (`IOCKit.cs:239-263`) scans members by reflection (public only by default, see `MemberSearchModes`, `IOCKit.cs:143-150,220-233`). Every Field/Property with `[Inject]` is filled via `Resolve(memberType, name)`
- `CreateInstance(Type, args)` (`IOCKit.cs:315-358`) picks the **public constructor with the most parameters**, recursively calls `Resolve` for each parameter type, constructs, then calls `Inject`. This automatic assembly is a capability the core IOCContainer completely lacks

**Conclusion: do not apply `[Inject]` / `IQFrameworkContainer` usage to the `Architecture<T>` core container, and do not assume `RegisterSystem/RegisterModel/RegisterUtility` support attribute injection behind the scenes.** That is an IOCKit capability. The core architecture only performs `Dictionary<Type,object>`-level singleton registration. Apart from both being called "IOC" and using similar verbs like `Register` and `Resolve` (the core calls it `Get`), they share no code.
