# QFramework Design Rules - Critical Summary, Version Scope, Known Gaps

Sub-doc of [qframework-design](./SKILL.md). Holds the critical rule table, version scope and known gaps that do not fit the entry page.

Sources: **QFramework.cs** (core single-file architecture, repo root; its header comment reads `Latest Update: 2026.8.12 return module instance after register`), **Doc.md** (official tutorial, 8820 lines, repo root), and **QFramework API.md** (core signature index; it has no Toolkits coverage and, as of this fetch, still lacks the 2026-08-12 return-value change). Line numbers refer to the upstream [liangxiegame/QFramework](https://github.com/liangxiegame/QFramework) v1.0.257 snapshot; Toolkits source lives under `QFramework.Unity2018+/Assets/QFramework/`. When sources disagree, follow `QFramework.cs`; Doc.md and API.md only corroborate, and the tutorial may lag behind the source.

> **Mode**: Documentation only. There are no REST skills to gate, so load freely under any operating mode (Approval / Auto / Bypass).

## When to load

Load before generating or reviewing any of the following:

- The `Init()` startup logic of an `Architecture<T>` subclass, and `RegisterSystem/RegisterModel/RegisterUtility` calls
- Implementations of `IController / ISystem / IModel / IUtility`, or adding/removing `ICan*` capability interfaces on them
- `SendCommand / SendQuery / SendEvent / RegisterEvent` calls, and `ICommand / ICommand<TResult> / IQuery<TResult>` implementations
- Declarations and Register / RegisterWithInitValue / Comparer usage of `BindableProperty<T> / BindableList<T> / BindableDictionary<TKey,TValue>`
- Registration and manual unregistration of `TypeEventSystem` / the `EasyEvent` family
- Overriding `Architecture<T>.ExecuteCommand` to intercept Commands (logging, middleware, undo, automated tests)
- Deciding whether data belongs in a Model or stays local to a MonoBehaviour
- Choosing between the core `IOCContainer` and the Toolkits `CoreKit.IOCKit` (`IQFrameworkContainer` + `[Inject]`)
- Confirming the Toolkits install form (`.unitypackage` into `Assets/`, no UPM) and version number

## Critical rule summary

| # | Rule | Source anchor |
|---|------|---------------|
| 1 | `RegisterSystem<T>/RegisterModel<T>/RegisterUtility<T>` **return the registered instance** (no longer void), effective since 2026-08-12; the `IArchitecture` interface declares `TSystem/TModel/TUtility` return types. The official Doc.md examples (including the "interface-based module design" section, lines 2408-2422) still discard the return value. **They are outdated examples, not the current recommended usage** | `QFramework.cs:43,45,47,132-163` |
| 2 | Four-layer interfaces: `IController`(226) `ISystem`(235) `IModel`(264) `IUtility`(291); `ICommand`(299) / `ICommand<TResult>`(305); `IQuery<TResult>` exists only in generic form, **there is no non-generic `IQuery`** (342) | `QFramework.cs:226,235,264,291,299,305,342` |
| 3 | General layering rules: IController must change System/Model state through a Command; System/Model must notify upper layers via Event or BindableProperty; upper layers may access lower layers directly, **lower layers may not access upper layers**; Commands/Queries must be stateless. These are the "ideal state"; the author explicitly allows relaxing them as needed (e.g., adding `ICanSendEvent` to IController) | `Doc.md:2682-2820` (relaxation quote 2791, interface change example 2793-2805) |
| 4 | Whether data belongs in a Model: only three kinds qualify, namely data shared over time (persisted across launches), shared physically (kept in memory across screens or scenes), and configuration tables. Counter-example: **enemy health does not go in a Model**; the enemy script manages it itself | `Doc.md:4051-4077` (counter-example 4064-4066) |
| 5 | `BindableProperty<T>.Value` setter uses `Comparer` to test equality before firing the event; `ComparerAutoRegister` (`[RuntimeInitializeOnLoadMethod]`) already swaps the default `Equals` for `==` for int/float/string/Vector2-4/Color/Quaternion and similar types. `Register` subscribes only to future changes; `RegisterWithInitValue` calls back once immediately with the current value | `QFramework.cs:711,762-789,740-749` |
| 6 | `BindableList<T>` / `BindableDictionary<TKey,TValue>` are Toolkits add-ons introduced on 2024-09-18/19 (in `_CoreKit/BindableKit/`, not in the QFramework.cs single file). They are peripheral tools, not core concepts. The author admits they do not know what use cases BindableDictionary has, so evaluate them case by case and do not treat them as the default choice. `BindableList` index assignment fires only `OnReplace`, and `Move` fires only `OnMove` (neither also fires `OnAdd`/`OnRemove`) | `Doc.md:8438-8592,8593-8712` (quote 8595); `BindableList.cs:38-115`, `BindableDictionary.cs:17-44` |
| 7 | Choose one event tool: `TypeEventSystem` (cross-object, supports interface-inheritance events, suited to general systems), `EasyEvent` (usable without the architecture, lighter, no field names), or the architecture's built-in `SendEvent` (backed by a TypeEventSystem instance). Manual unregistration must use **named methods** (anonymous delegates cannot be `-=`'d). `UnRegisterWhenCurrentSceneUnloaded()` needs no GameObject and clears automatically on scene change. Prefer `struct` for event types to reduce GC | `Doc.md:3305-3461` (summary 3440-3461), `QFramework.cs:610-611,624-641` |
| 8 | Command interception: overriding `Architecture<T>.ExecuteCommand(ICommand)` (inserting logic before and after) enables logging, middleware, undo and automated tests. The official default implementation simply calls `command.Execute()` | `Doc.md:3137-3186`, `QFramework.cs:183-187` |
| 9 | **Do not confuse the two containers**: core `IOCContainer` (`Dictionary<Type,object>`, only singleton register/get, held privately by `Architecture<T>`, no reflection); Toolkits `CoreKit.IOCKit` (`IQFrameworkContainer` / `QFrameworkContainer`) is a fully separate reflection-based DI implementation that supports type mapping, named registration, relationship mapping, field/property injection via the `[Inject]` attribute, and automatic dependency resolution through the public constructor with the most parameters. It is not in the QFramework.cs source file | Core: `QFramework.cs:130,647-684`; Toolkits: `IOCKit.cs:19-31,33-136,141,239-263,315-358` (`_CoreKit/IOCKit/IOCKit.cs`) |
| 10 | Toolkits install form: `.unitypackage` imported into `Assets/`, **no UPM package**. The installed version is shown in the PackageKit editor panel or in `PackageVersion.json` under the install directory; GitHub/Gitee Release tags can lag behind the live PackageKit source | `Doc.md:4178-4182`; `PackageData.cs:286` (`PackageVersion.json` write path, Toolkits source) |

## Version scope

Anchored to **v1.0.257** (QFramework.cs internal comment last updated 2026-08-12; this module fetched 2026-08).

- QFramework.cs is a single-file core architecture. Its version number is not released independently of Toolkits and follows the GitHub/Gitee main-repo tags.
- Toolkits (CoreKit / UIKit / ResKit / ActionKit / AudioKit / PackageKit ...) versions follow the PackageKit panel or `PackageVersion.json`, and may be ahead of the unitypackage snapshot this module references.
- Key watershed: the return-value semantics of `RegisterSystem/RegisterModel/RegisterUtility` changed from void to returning the instance on 2026-08-12. Third-party tutorials or legacy code that predate this may not match current signatures; follow QFramework.cs.

When in doubt, read the source lines cited, rather than relying on memory or older tutorial examples.

## Known gaps

- **LocaleKit has zero coverage in the official tutorial**: Doc.md mentions LocaleKit only once, in the toolkit list, as a "localization & multi-language toolkit", with no usage example or API description. From the source, it currently serves mainly as an editor-side CN/EN display switch (`LocaleKitEditor.IsCN`, `EditorPrefs` key `EDITOR_CN`) plus a language-definition ScriptableObject (`LanguageDefineConfig`). **It is not a complete multi-language solution for in-game runtime text**, which falls short of its own self-description. Do not assume LocaleKit can handle runtime localization directly; read the source first.
- **FluentAPI method list not catalogued**: `_CoreKit/FluentAPI/` provides many chained extensions for GameObject / Transform / Camera / Color / Graphic / Vector / RectTransform and C# primitive types. The count is too large to expand in this module; look up the matching source file for the target type when needed.
