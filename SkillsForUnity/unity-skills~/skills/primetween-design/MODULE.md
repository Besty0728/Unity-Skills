# PrimeTween - Design Rules

**Version scope:** PrimeTween 1.4.6 (`com.kyrylokuzyk.primetween`). Its UPM package supports Unity 2018.4+, but Unity Skills maintains a 2022.3+ baseline. Check the installed package version before relying on a newer factory overload or PrimeTween Pro feature.

Advisory module. Rules are grounded in the PrimeTween **1.4.6** package installed through `com.kyrylokuzyk.primetween`.

## When to use

Load before writing or reviewing:

- `Tween.Position`, `LocalPosition`, `EulerAngles`, `LocalEulerAngles`, `Scale`, `Alpha`, `Color`, `Custom`, delay, or shake calls.
- `Sequence.Create`, `Chain`, `Group`, `Insert`, callbacks, or sequence cycles.
- `Tween.Stop`, `Complete`, `StopAll`, `CompleteAll`, `PausedAll`, progress, time scale, or manual update control.
- `OnComplete`, `OnUpdate`, zero-allocation callback overloads, or target-destruction handling.
- `await tween`, `ToYieldInstruction`, serialized `TweenSettings`, `PrimeTweenConfig`, or capacity tuning.

## Critical Rule Summary

| # | Rule | Source anchor |
|---|------|---------------|
| 1 | PrimeTween uses static `Tween` factories. Its `Tween` handle is a struct, and a completed or stopped handle is dead and non-reusable; create a new tween to replay or reverse an animation. | `Runtime/Tween.cs:53,256-264`; `readme.md` “Controlling tweens” |
| 2 | Use `Sequence.Chain` for sequential work, `Group` for parallel work against the previous item, and `Insert` for absolute-time overlap. This naming differs from DOTween’s Append/Join vocabulary. | `Runtime/Sequence.cs:154,193,202` |
| 3 | A `Sequence` has its own cycles, cycle mode, easing, update type, and unscaled-time settings at creation. Configure those on `Sequence.Create`, not through DOTween-style fluent setters. | `Runtime/Sequence.cs:108` |
| 4 | Callbacks can bind a target explicitly with `OnComplete<T>` or sequence callback overloads. That avoids a capturing closure and lets PrimeTween suppress the callback if its target is destroyed. | `Runtime/Tween.cs:365-382`; `Runtime/Sequence.cs:228-281` |
| 5 | PrimeTween detects destroyed Unity targets and exposes targeted warnings through `PrimeTweenConfig`. Do not assume a DOTween `SetLink` API exists; own a handle and stop it when your component is disabled when that is the intended lifecycle. | `Runtime/Internal/PrimeTweenManager.cs:870-880`; `Runtime/PrimeTweenConfig.cs:56-87` |
| 6 | `await tween` and `ToYieldInstruction()` are supported, but both async state machines and coroutines allocate. For allocation-sensitive animation flows, prefer `Sequence`. | `Runtime/Internal/AsyncAwaitSupport.cs`; `Runtime/Internal/CoroutinesSupport.cs`; `readme.md` “Async/await” |
| 7 | `TweenSettings` and `TweenSettings<T>` are serializable configuration carriers for Inspector-authored tween values; use them instead of inventing a separate animation settings component. | `Runtime/TweenSettings.cs:25-54`; `readme.md` “Inspector integration” |
| 8 | `PrimeTweenConfig` is static runtime configuration. It has capacity, default ease/update type, warning controls, and manual-update APIs, but it is not a `Resources` settings asset. | `Runtime/PrimeTweenConfig.cs:25-101` |

## Routing to Other Modules

- Async control flow or cancellation policy → load [async](../async/MODULE.md) and [unitask-design](../unitask-design/MODULE.md).
- Inspector-authored `TweenSettings` fields → load [inspector](../inspector/MODULE.md).
- Tween-heavy performance analysis → load [performance](../performance/MODULE.md).
- Addressables-loaded targets or scene lifetime → load [addressables-design](../addressables-design/MODULE.md).

## Version Scope

This module targets PrimeTween **1.4.6**. Its UPM package supports Unity 2018.4+, but Unity Skills itself maintains a Unity 2022.3+ baseline. Check the installed package version before relying on a newer factory overload or PrimeTween Pro feature.
