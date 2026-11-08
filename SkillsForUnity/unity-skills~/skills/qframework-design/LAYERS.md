# LAYERS - Four-Layer Architecture and Layering Rules

Four layers: presentation `IController`, system `ISystem`, data `IModel`, utility `IUtility`, plus the two stateless operation objects `ICommand` and `IQuery<TResult>`. All are defined in the single file `QFramework.cs`. Interfaces declare what a layer can do by composing several `ICan*` capability interfaces.

## 1. Interface list and capability composition

| Interface | Defined at | Composed capability interfaces |
|-----------|-----------|--------------------------------|
| `IController` | `QFramework.cs:226` | `IBelongToArchitecture, ICanSendCommand, ICanGetSystem, ICanGetModel, ICanRegisterEvent, ICanSendQuery, ICanGetUtility` |
| `ISystem` | `QFramework.cs:235` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetModel, ICanGetUtility, ICanRegisterEvent, ICanSendEvent, ICanGetSystem, ICanInit` |
| `IModel` | `QFramework.cs:264` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetUtility, ICanSendEvent, ICanInit` |
| `IUtility` | `QFramework.cs:291` | Empty interface, composes no capabilities. The utility layer is designed to be unable to do anything beyond providing infrastructure or wrapping third-party libraries |
| `ICommand` / `ICommand<TResult>` | `QFramework.cs:299 / 305` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetSystem, ICanGetModel, ICanGetUtility, ICanSendEvent, ICanSendCommand, ICanSendQuery` |
| `IQuery<TResult>` | `QFramework.cs:342` | `IBelongToArchitecture, ICanSetArchitecture, ICanGetModel, ICanGetSystem, ICanSendQuery`. **Generic form only**, there is no non-generic `IQuery` |

Capability interfaces are empty marker interfaces (`ICanGetModel` / `ICanSendEvent` / ...). The real methods are attached through extension methods, for example `CanGetModelExtension.GetModel<T>()` (`QFramework.cs:380-384`) and `CanSendCommandExtension.SendCommand<T>()` (`QFramework.cs:423-433`). This means **to give a layer a new capability, just add one `ICan*` to that layer's interface**; no abstract base class needs to change.

```csharp
// Relaxation example: let IController also send events (Doc.md:2793-2805)
public interface IController : IBelongToArchitecture, ICanSendCommand, ICanGetSystem, ICanGetModel,
    ICanRegisterEvent, ICanSendQuery,
    ICanSendEvent // added
{
}
```

## 2. General rules (ideal state, relax as needed)

Rules given in `Doc.md:2682-2820`:

1. `IController` **must** use a Command to change `ISystem` / `IModel` state
2. After `ISystem` / `IModel` state changes, notifying `IController` **must** use an Event or BindableProperty
3. `IController` may directly obtain `ISystem` / `IModel` objects to query them
4. `ICommand` / `IQuery` **must not hold state** (no fields that persist across calls)
5. Upper layers may obtain lower-layer objects directly; **lower layers may not obtain upper-layer objects**
6. Lower layers communicate upward through events (Event / BindableProperty)
7. Upper layers communicate downward through method calls (queries use method calls, state changes use Commands); the interaction logic of `IController` is a special case and must use Commands

These rules are the "ideal state". The author's own words (`Doc.md:2791`): **applying them to a real project will likely require modifying the rules above**, and the way to do that is the one shown in section 1, adding `ICan*` to the interface. Do not treat the seven rules as inviolable dogma. When an IController genuinely needs to send events in your project, extending the interface is more faithful to the framework's design intent than working around it.

## 3. Shared-data criteria: what belongs in a Model

`Doc.md:4051-4077` names three kinds of "shared data". Only data meeting at least one of them belongs in an `IModel`:

- **Shared over time**: needs persistence; the data survives an application restart
- **Shared physically**: used across screens, across MonoBehaviours or across scenes; memory is not freed by the lifecycle of any single screen
- **Configuration tables**: configured during development and only queried or displayed at runtime

Data that meets none of these can stay local to a MonoBehaviour. **Counter-example (`Doc.md:4064-4066`): an enemy's health does not go in a Model.** The enemy script manages it itself. If health must be saved, convert it into the appropriate data structure only at save time; the Model does not need to hold this runtime state. Likewise, a queryable static "max health" configuration may go in a Model, a System, or simply on the enemy prefab as configuration. Not all queryable data must be put into a Model.

## 4. Command and Query: CQRS split

- `ICommand` (`QFramework.cs:299`): responsible for **creating, updating and deleting** data, `void Execute()`
- `ICommand<TResult>` (`QFramework.cs:305`): a Command that returns a value, `TResult Execute()`
- `IQuery<TResult>` (`QFramework.cs:342`): responsible for **reading** data, `TResult Do()`. Use it only for composed or transformed queries that are heavy; for simple logic, query directly inside the Controller's presentation logic (`Doc.md:2666`, Query is an optional concept)
- CQRS split: Command handles create/update/delete, Query handles reads (`Doc.md:2669`)
- If the game must sync data with a server: fetching uses Query, create/update/delete uses Command (`Doc.md:2672`). This rule of thumb also applies to splitting reads and writes for local storage and configuration tables

`AbstractCommand` / `AbstractCommand<TResult>` / `AbstractQuery<T>` are at `QFramework.cs:312-336` and `348-360`. Each only forwards `Execute()` / `Do()` to `protected abstract OnExecute()` / `OnDo()` and holds no state. Do not add mutable fields in subclasses to get around the stateless rule.

## 5. Command interception

Overriding `Architecture<T>.ExecuteCommand` (the void overload is at `QFramework.cs:183-187`, the value-returning overload at `177-181`) inserts logic around every `SendCommand`:

```csharp
// Doc.md:3137-3186
protected override void ExecuteCommand(ICommand command)
{
    Debug.Log("Before " + command.GetType().Name + " Execute");
    base.ExecuteCommand(command);
    Debug.Log("After " + command.GetType().Name + " Execute");
}
```

Uses: Command logging, Command middleware, undo, and automated tests driven through Commands. The default implementation is just `command.SetArchitecture(this); command.Execute();` with no interception logic.

## 6. Register* return value change (2026-08-12): current usage

`RegisterSystem<TSystem>/RegisterModel<TModel>/RegisterUtility<TUtility>` now **return the registered instance**. The signatures and implementation are at `QFramework.cs:43,45,47` (interface declarations) and `132-163` (`Architecture<T>` implementation, `return system;` / `return model;` / `return utility;`). The header comment of QFramework.cs states explicitly `Latest Update: 2026.8.12 ... return module instance after register`.

**All official Doc.md tutorial examples still use the old form as of this fetch**, including the "interface-based module design" section (`Doc.md:2408-2422`), which still discards the return value, e.g. `this.RegisterModel<ICounterAppModel>(new CounterAppModel());` with no return captured. The documentation lags the source; this is not the current recommended form. Current usage captures the return value:

```csharp
public class GameArchitecture : Architecture<GameArchitecture>
{
    protected override void Init()
    {
        // Capture the return value: registration yields the instance, saving a GetUtility/GetModel round trip
        var storage = this.RegisterUtility<IStorage>(new Storage());
        var counterModel = this.RegisterModel<ICounterAppModel>(new CounterAppModel());

        counterModel.Count.SetValueWithoutEvent(storage.LoadInt(nameof(counterModel.Count)));
    }
}
```

`IStorage` / `ICounterAppModel` reuse the interface-based example types from `Doc.md:2336-2422`. The only difference here is **not discarding the return value** and using the instance directly.

## 7. Architecture<T> lifecycle

- On first access to `Architecture<T>.Interface`, `InitArchitecture()` runs (`QFramework.cs:79-113`). It first `new T()`s the architecture and calls its `Init()` (where subclasses perform Register), then runs the `OnRegisterPatch` hook, then **initializes all uninitialized Models first, then all uninitialized Systems** (`98-109`)
- `Deinit()` (`QFramework.cs:117-124`) runs in reverse: **Deinit all Systems first, then all Models**, and finally clears the container and nulls the static singleton
- If `RegisterSystem/RegisterModel` is called after the architecture is already `mInited` (dynamic registration at runtime), it immediately calls that instance's `Init()` once (`QFramework.cs:137-141,151-155`), without waiting for the next global initialization
- `RegisterUtility` does not take part in the Init/Deinit lifecycle (`IUtility` has no `ICanInit`); it is usable as soon as it is registered

The container itself (`IOCContainer`, `private IOCContainer mContainer`, `QFramework.cs:130`) and the event system (`TypeEventSystem`, `QFramework.cs:197`) are detailed in [EVENT_TOOLS.md](./EVENT_TOOLS.md). Do not confuse them with the Toolkits `IOCKit`.
