---
name: unity-qframework-design
description: Source-anchored design rules for QFramework v1.0.257 — layers, CQRS, BindableProperty, UIKit/ResKit/ActionKit, pitfalls
---

# QFramework Design Rules (v1.0.257)

Applies to QFramework v1.0.257. Advisory module: documentation only, no REST skills to gate, so load it under any operating mode.

Before writing or reviewing code, read the sub-doc that matches your task (see **Sub-doc routing** below). The full critical rule table, version scope and known gaps are in [RULES.md](./RULES.md). Every rule cites a file/line anchor; when sources disagree, `QFramework.cs` wins over the tutorial.

## When to use

- Writing or reviewing QFramework architecture code (Controller/System/Model/Utility layers)
- Registering Systems/Models/Utilities, or sending Commands, Queries and Events
- Declaring or binding `BindableProperty` / `BindableList` / `BindableDictionary` in views
- Choosing between the core `IOCContainer` and the Toolkits `IOCKit`, or between architecture events and `EasyEvent`
- Checking the Toolkits install form (`.unitypackage` into `Assets/`, no UPM package) and version

## Sub-doc routing

| Sub-doc | Read when |
|---------|-----------|
| [LAYERS.md](./LAYERS.md) | Four-layer interfaces and responsibilities, general rules and how to relax them, shared-data criteria, Command interception, `Architecture<T>` lifecycle (`InitArchitecture` / `Deinit`) |
| [BINDABLE_QUERY.md](./BINDABLE_QUERY.md) | `BindableProperty` (Comparer, RegisterWithInitValue), `BindableList` / `BindableDictionary`, Query and CQRS usage |
| [EVENT_TOOLS.md](./EVENT_TOOLS.md) | `TypeEventSystem`, the `EasyEvent` family, core `IOCContainer` vs Toolkits `IOCKit`, unregister patterns (`UnRegisterWhenGameObjectDestroyed` / `UnRegisterWhenCurrentSceneUnloaded` / `IUnRegisterList`) |
| [CODEGEN_UIKIT.md](./CODEGEN_UIKIT.md) | CodeGenKit two-phase generation (Designer overwrite rules, namespace migration, ScriptsFolder without fallback, ViewController nesting, OtherBinds) and the UIKit panel workflow (open/close, lifecycle, Apply with the wrong UIRoot, UIKitSettingData) |
| [RESKIT.md](./RESKIT.md) | ResKit asset strategy: AssetBundle mark granularity, scene-exclusive bundles, simulate vs non-simulate mode, ResLoader reference counting, build output directory, Unity's official cross-bundle Prefab dependency bug |
| [ACTIONKIT.md](./ACTIONKIT.md) | ActionKit chained sequences (Delay/Sequence/Parallel/Repeat/Condition), global lifecycle, TimeScale and scene switches; SingletonKit selection among six singleton types; AudioKit three channels and the **NullReferenceException when editor code touches Settings**; ScreenTransition |
| [DATA_KITS.md](./DATA_KITS.md) | FSMKit (chained vs class mode), TableKit (joint queries and its internal role), PoolKit (two pool semantics), GridKit / DynaGrid |

## Routing to other modules

- REST endpoint or operation-level QFramework skill calls: load the [qframework](../qframework/SKILL.md) REST module (this module covers design rules only, not REST parameters)
- UniTask / coroutine bridging (ActionKit sequences vs async): [unitask-design](../unitask-design/SKILL.md) / [async](../async/SKILL.md)
- Overall architecture layering review and state-management choices: [architecture](../architecture/SKILL.md) / [patterns](../patterns/SKILL.md)
- Performance review of frequently fired events or BindableProperty: [performance](../performance/SKILL.md)
- ResKit over Addressables resource lifetime: [addressables-design](../addressables-design/SKILL.md)
- Asmdef layout (QFramework.cs and Toolkits assembled separately): [asmdef](../asmdef/SKILL.md)

## Pitfalls

- `RegisterSystem/RegisterModel/RegisterUtility` return the registered instance (since 2026-08-12). The Doc.md examples still discard the return value; they are outdated, so capture it.
- Do not mix core `IOCContainer` (singleton `Dictionary<Type,object>`, no injection) with Toolkits `IOCKit` (`[Inject]`, constructor auto-resolution). They share no code.
- Events are keyed by the `T` you write in `Send<T>` / `Register<T>`, not the runtime type. `Send<Derived>` with no registration for `Derived` is a silent no-op.
- Manual unregistration needs a named method; anonymous lambdas cannot be removed with `-=`.
- `[Bind]` supports parent-child references only; for other GameObjects or assets use OtherBinds.
- In UIKit, Apply must target `UIBasicPanel`, never `UIRoot`. Choosing `UIRoot` raises no error and the bindings silently stay empty.
- Editor scripts and `[InitializeOnLoad]` code must not access `AudioKit.Settings.*`; its properties are null until runtime initialization runs in Play Mode.
- Call `FSM.Clear()` in `OnDestroy`; the state machine does not clean itself up.
- `SimpleObjectPool.Recycle` does not dedupe or cap; `SafeObjectPool` does both.
- A scene must be its own AssetBundle. Prefab dependencies across bundles can come back Missing (Unity bug).
- `BindableProperty` comparers are static per type; custom class types fall back to `object.Equals`.
- ActionKit Delay/Sequence follow `Time.timeScale`; chain `.IgnoreTimeScale()` for UI animations that must keep running while paused.
