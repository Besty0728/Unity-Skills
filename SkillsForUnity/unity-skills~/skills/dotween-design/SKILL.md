---
name: unity-dotween-design
description: "Source-anchored design rules for DOTween 1.3.015 (Unity 2018+). Load before writing any DOTween.Init / DOMove / Sequence.Append / SetLoops / SetLink / AsyncWaitForCompletion / ToUniTask code to avoid missing-target NRE, tween leaks, Safe Mode false positives, autoKill confusion, module compile errors. Triggers: DOTween, DOMove, DORotate, DOScale, DOColor, DOFade, DOShakePosition, DOShakeRotation, DOPath, DOPunchScale, DOJump, DOLocalMove, DOAnchorPos, DOSizeDelta, Sequence, Tweener, TweenerCore, SetEase, SetDelay, SetLoops, SetAutoKill, SetLink, SetUpdate, SetId, SetTarget, SetRelative, SetSnapping, SetRecyclable, OnComplete, OnKill, OnStart, OnUpdate, OnStepComplete, AsyncWaitForCompletion, AsyncWaitForKill, AsyncWaitForRewind, ToUniTask, DOVirtual, PathType, PathMode, LoopType, UpdateType, DOTweenComponent, DOTweenSettings, 补间动画, 序列动画, 缓动曲线, 生命周期绑定, 动画回调, DOTween init, tween lifecycle, safe mode, tween capacity, kill target."
---

# DOTween - Design Rules

Advisory module. Every rule is distilled from Demigiant DOTween source at:
- **1.3.015** — bundled in `_DOTween.Assembly/DOTween/` and `bin/Modules/` module files (Unity 2018+)

Each rule cites a concrete file/line so the reasoning is auditable and the AI does not improvise against stale memory.

> **Mode**: Both (Semi-Auto + Full-Auto) — documentation only, no REST skills.

## When to Load This Module

Load before writing or reviewing any of:

- `DOTween.Init(...)` bootstrap, DOTweenSettings configuration, SetTweensCapacity adjustments
- Any `.DOMove / .DORotate / .DOScale / .DOColor / .DOFade / .DOShake* / .DOPath / .DOPunch*` shortcut call
- `DOTween.Sequence()` + `.Append / .Join / .Insert / .Prepend / .AppendInterval / .PrependInterval / .AppendCallback`
- `.SetEase / .SetDelay / .SetLoops / .SetAutoKill / .SetLink / .SetId / .SetTarget / .SetUpdate / .SetRelative / .SetRecyclable / .SetSpeedBased`
- `.OnStart / .OnUpdate / .OnStepComplete / .OnComplete / .OnKill / .OnPlay / .OnPause / .OnRewind`
- `.AsyncWaitForCompletion / .AsyncWaitForKill / .AsyncWaitForRewind / .AsyncWaitForElapsedLoops / .AsyncWaitForPosition`
- `tween.ToUniTask(TweenCancelBehaviour, CancellationToken)` (UniTask bridge)
- `DOTween.Kill(target, complete)` / `DOTween.KillAll()` batch operations
- Safe Mode debugging — `useSafeMode`, `safeModeLogBehaviour`, `nestedTweenFailureBehaviour`
- DOTween Module generation (Audio / Physics / Physics2D / Sprite / UI / UnityUI / TextMeshPro)

## Critical Rule Summary

| # | Rule | Source anchor |
|---|------|---------------|
| 1 | `DOTween.Init(recycleAllByDefault, useSafeMode, logBehaviour)` returns `IDOTweenInit` for fluent `.SetCapacity(...)`. If not called explicitly, Auto-init runs on first tween creation (`AutoInit` at `DOTween.cs:236`). `DOTween.Version = "1.3.015"` is exposed as a const. | `DOTween.cs:38,227,236,243` |
| 2 | Every tween is driven by a single `[DOTween]` GameObject MonoBehaviour (`DOTweenComponent`) marked `DontDestroyOnLoad`. It is created on first init and survives scene loads. Don't destroy it — `DOTween.Clear(destroy: true)` is the only supported teardown. | `DOTweenComponent.cs:268-270`, `DOTween.cs:311` |
| 3 | **Default `autoKill = true`**: tween is killed on completion. After kill, calls like `.Restart()` / `.Kill()` / `.Complete()` are no-ops (Safe Mode) or NullReferenceException (Safe Mode off). Use `.SetAutoKill(false)` to keep tween alive for replay. | `DOTween.cs useSafeMode:49`, `TweenSettingsExtensions.cs:39,49` |
| 4 | **Safe Mode is ON by default** (`useSafeMode = true`). Target destroyed while tween plays → Safe Mode catches the exception and logs per `safeModeLogBehaviour` (default: Warning). Disable Safe Mode to locate missing-link bugs during development. | `DOTween.cs:49,51` |
| 5 | `SetLink(gameObject, LinkBehaviour)` binds tween lifecycle to a GameObject. Default `LinkBehaviour.KillOnDestroy`. Without `SetLink`, tween continues running after target is destroyed and relies on Safe Mode for protection. | `TweenSettingsExtensions.cs:91,103`, `LinkBehaviour.cs` |
| 6 | `SetTarget(object target)` is required for `DOTween.Kill(target)` / `DOTween.KillAll(target)` grouping. Shortcut extensions (`.DOMove`, `.DOFade`, etc.) set target automatically; manual `DOTween.To(...)` does NOT. | `TweenSettingsExtensions.cs:116`, `DOTween.cs:884,892` |
| 7 | `Sequence.Append(t)` runs after previous. `Sequence.Join(t)` runs in parallel with previous. `Sequence.Insert(float atPosition, t)` inserts at absolute time. `Sequence.Prepend(t)` pushes to front, shifting existing. | `TweenSettingsExtensions.cs:499,508,517,528` |
| 8 | `AsyncWaitForCompletion / AsyncWaitForKill / AsyncWaitForRewind` live in `DOTweenModuleUnityVersion.cs` (MODULE file, gated by `UNITY_2018_1_OR_NEWER && (NET_4_6 || NET_STANDARD_2_0)`). If Modules are not generated (Tools → DOTween Utility Panel → Create ASMDEF), these APIs don't exist. | `bin/Modules/DOTweenModuleUnityVersion.cs:216,244,230` |
| 9 | UniTask bridge `tween.ToUniTask(TweenCancelBehaviour, CancellationToken)` lives in UniTask's `External/DOTween/` extensions and is the **recommended** await path over `AsyncWaitForCompletion`. `TweenCancelBehaviour` has 9 values (Kill / Complete / CancelAwait / KillAndCancelAwait / ...). | UniTask `DOTweenAsyncExtensions.cs:14-27,54` |
| 10 | `Ease` enum has **38 entries** (36 user-facing + 2 reserved `INTERNAL_*`): `Unset / Linear` + In/Out/InOut variants of Sine/Quad/Cubic/Quart/Quint/Expo/Circ/Elastic/Back/Bounce (10 × 3 = 30 math eases) + `Flash / InFlash / OutFlash / InOutFlash`. `INTERNAL_Zero` and `INTERNAL_Custom` are auto-assigned by DOTween for zero-duration and AnimationCurve-based tweens. | `Ease.cs:9-53` |

## Sub-doc Routing

| Sub-doc | When to read |
|---------|--------------|
| [BASICS.md](./BASICS.md) | `DOTween.Init`, driver GameObject, DOTweenSettings asset, Modules architecture, Safe Mode, tween capacity |
| [TWEEN.md](./TWEEN.md) | Tween base class, Tweener vs Sequence, autoKill, Play/Pause/Rewind/Restart/Kill/Complete/Goto lifecycle |
| [SEQUENCE.md](./SEQUENCE.md) | `Sequence.Append / Join / Insert / Prepend / AppendInterval / PrependInterval / AppendCallback / InsertCallback`, nesting, loop composition |
| [SHORTCUTS.md](./SHORTCUTS.md) | `.DOMove / DOLocalMove / DOAnchorPos / DORotate / DOScale / DOColor / DOFade / DOPath / DOJump / DOPunch / DOShake*` cheat sheet by target type |
| [EASE.md](./EASE.md) | Full `Ease` enum, `SetEase` overloads (Ease / amplitude+period / AnimationCurve / EaseFunction), Flash family, Elastic/Back overshoot |
| [LIFETIME.md](./LIFETIME.md) | `SetAutoKill / SetLink / SetRecyclable / SetId / SetTarget / DOTween.Kill / DOTween.KillAll`, Safe Mode, tween pool capacity |
| [INTEGRATION.md](./INTEGRATION.md) | DOTween + UniTask (`ToUniTask`), DOTween + Coroutine (`WaitForCompletion`), DOTween + Addressables lifetime, DOTween + Netcode deterministic replay |
| [PITFALLS.md](./PITFALLS.md) | 30 concrete hallucination / runtime pitfalls (missing target, stale tween handle, OnComplete not firing on infinite loop, Safe Mode hiding bugs, etc.) |

## Routing to Other Modules

- Async integration (`ToUniTask`, `AsyncWaitForCompletion`) → load [unitask-design](../unitask-design/SKILL.md) and [async](../async/SKILL.md)
- UI animation (uGUI / UIToolkit) choice between DOTween and native animators → load [inspector](../inspector/SKILL.md)
- Architecture / performance review of tween-heavy scenes → load [performance](../performance/SKILL.md)
- Asmdef layout for DOTween consumers (`DOTween.asmdef`, DOTween Modules asmdef) → load [asmdef](../asmdef/SKILL.md)
- Addressables-loaded prefab tween lifetime (bundle unload vs tween running) → load [addressables-design](../addressables-design/SKILL.md)

## Version Scope

Targets **DOTween 1.3.015** (the Pro/Free source bundled in `_DOTween.Assembly/`). Version is exposed as the const `DOTween.Version = "1.3.015"` at `DOTween.cs:38`.

Earlier 1.2.x versions are source-compatible for most APIs. Key differences:
- Modules system (UI / TMP / Physics / etc.) requires post-1.2.0 source and the Utility Panel scanner.
- `SetLink` was added in 1.2.x series; `LinkBehaviour.KillOnDestroy` vs `KillOnDisable` variants in later 1.2/1.3.
- `TweenCancelBehaviour` (UniTask bridge) shipped separately via the UniTask package.

When in doubt, read the cited source — not your memory.
