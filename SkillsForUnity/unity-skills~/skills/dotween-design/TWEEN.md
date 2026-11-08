---
name: unity-dotween-tween
description: "Tween base class lifecycle — Play / Pause / Rewind / Restart / Kill / Complete / Goto / SmoothRewind / TogglePause / ManualUpdate / ForceInit; active / IsPlaying / IsComplete / IsBackwards / ElapsedPercentage state getters; callback hooks."
type: reference
---

# DOTween Tween Lifecycle

Sub-doc of [dotween-design](./SKILL.md). Covers the `Tween` base class, its state machine, and callbacks. Source: `DOTween/Tween.cs`, `DOTween/TweenExtensions.cs`.

## Class hierarchy

```
Tween (abstract)
├── Tweener (abstract)
│   └── TweenerCore<TValue, TPlug, TOptions>   // concrete
└── Sequence
```

A **Tweener** animates one property on one target. A **Sequence** composes tweens in time. Both derive from `Tween` and inherit lifecycle.

## Tween state (fields you can inspect)

From `Tween.cs` and `ABSSequentiable.cs`:

| Field / prop | Meaning | Source |
|--------------|---------|--------|
| `active` | Tween is in the active list (false = killed or pre-init) | `Tween.cs` |
| `isPlaying` | Currently running (not paused) | `Tween.cs` |
| `isComplete` | Reached final value (for non-looping) | `Tween.cs` |
| `autoKill` | Kill on completion (default true) | `Tween.cs` |
| `isFrom` | Set by `.From()` shortcut | `Tween.cs` |
| `target` | Automatically set by shortcut extensions | `Tween.cs:38` |
| `onPlay` / `onRewind` / `onStepComplete` / `onComplete` / `onKill` | Callbacks | `Tween.cs:45,52,56,58,60` |
| `duration` | Total duration (excluding loops) | `Tween.cs` |
| `delay` | Start delay in seconds | |
| `loops` | Loop count (-1 = infinite) | |
| `loopType` | Restart / Yoyo / Incremental | `LoopType.cs` |
| `timeScale` | Per-tween multiplier | |
| `updateType` | Normal / Late / Fixed / Manual | `UpdateType.cs` |
| `isIndependentUpdate` | Uses `DOTween.unscaledTimeScale` | |

Query via `TweenExtensions.IsActive(tween) / IsPlaying / IsComplete / IsBackwards / IsTimeScaleIndependent / IsInitialized` (source: `TweenExtensions.cs:552,625,592,558,603,614`).

## Lifecycle commands

From `TweenExtensions.cs`:

| Command | Signature | Line |
|---------|-----------|------|
| `Play<T>` | `public static T Play<T>(this T t) where T : Tween` | 201 |
| `Pause<T>` | `public static T Pause<T>(this T t) where T : Tween` | 186 |
| `TogglePause` | `public static void TogglePause(this Tween t)` | 293 |
| `PlayForward` | `public static void PlayForward(this Tween t)` | 230 |
| `PlayBackwards` | `public static void PlayBackwards(this Tween t)` | 216 |
| `Flip` | `public static void Flip(this Tween t)` | 88 |
| `Rewind` | `public static void Rewind(this Tween t, bool includeDelay = true)` | 261 |
| `SmoothRewind` | `public static void SmoothRewind(this Tween t)` | 279 |
| `Restart` | `public static void Restart(this Tween t, bool includeDelay = true, float changeDelayTo = -1)` | 246 |
| `Complete` | `public static void Complete(this Tween t)` / `Complete(this Tween t, bool withCallbacks)` | 43 / 48 |
| `Kill` | `public static void Kill(this Tween t, bool complete = false)` | 144 |
| `Goto` | `public static void Goto(this Tween t, float to, bool andPlay = false)` | 119 |
| `GotoWithCallbacks` | `public static void GotoWithCallbacks(this Tween t, float to, bool andPlay = false)` | 125 |
| `ManualUpdate` | `public static void ManualUpdate(this Tween t, float deltaTime, float unscaledDeltaTime)` | 172 |
| `ForceInit` | `public static void ForceInit(this Tween t)` | 102 |

## State machine flow

```
 Created → (delay) → Running ──[complete]──> Completed ──[autoKill]──> Killed
    │         │         │                         │
    │         │         └─── Paused (Play to resume)
    │         └────────────── Skipped on Kill(complete:true) or Complete()
    └────────── Killed on SetAutoKill(false) = false and never Restart
```

Transitions to watch:
- `Kill(complete: false)` (default) — stops at current position, doesn't fire `OnComplete`.
- `Kill(complete: true)` — jumps to end value, fires `OnComplete` if `withCallbacks` is effectively true (callback-emitting kill is via `Complete()` + `Kill()`).
- `Complete(true)` — fires callbacks during jump to end. `Complete(false)` — silent.
- `Restart` — rewinds to 0 and plays. Does NOT un-kill a killed tween (autoKill=true case).
- `Rewind` — like Pause + Goto 0. `SmoothRewind` — animates back to 0.

## `autoKill` — the most common footgun

```csharp
// Default: tween auto-kills on completion.
transform.DOMove(target, 1f);
// After 1s, tween is killed. Below fails silently or throws (Safe Mode-dependent):
transform.Tween().Restart(); // ❌
```

```csharp
// Keep tween alive for replay
var tween = transform.DOMove(target, 1f).SetAutoKill(false).Pause();
// Later:
tween.PlayForward(); tween.PlayBackwards(); tween.Restart();
// When done, manual Kill:
tween.Kill();
```

`TweenSettingsExtensions.cs:39,49`:
```csharp
public static T SetAutoKill<T>(this T t) where T : Tween;                    // sets autoKill to true
public static T SetAutoKill<T>(this T t, bool autoKillOnCompletion) where T : Tween;
```

## Callbacks

```csharp
transform.DOMove(target, 1f)
    .OnStart(() => Debug.Log("start"))
    .OnPlay(() => Debug.Log("play"))
    .OnUpdate(() => Debug.Log("update"))           // every frame!
    .OnStepComplete(() => Debug.Log("loop step"))
    .OnComplete(() => Debug.Log("complete"))
    .OnKill(() => Debug.Log("kill"))
    .OnRewind(() => Debug.Log("rewind"))
    .OnPause(() => Debug.Log("pause"));
```

**Semantics**:
- `OnStart` — fires once when tween first gets its initial value (after delay).
- `OnPlay` — fires each time tween transitions from pause to playing.
- `OnUpdate` — every frame. AVOID for work that allocates — closure captures!
- `OnStepComplete` — each loop iteration end (not final). If `loops=3`, fires 3 times; `OnComplete` fires after the last.
- `OnComplete` — only after all loops finish. Does NOT fire on infinite loops (`loops = -1`).
- `OnKill` — fires once on Kill. Fires for both explicit and auto-kill.
- `OnRewind` — on Rewind and on each loop-restart (depending on `DOTween.rewindCallbackMode`).

**Critical**: `OnComplete` on `SetLoops(-1)` **never fires**. Use `OnStepComplete` if you need per-iteration work on infinite loops.

## `onUpdate` allocation trap

```csharp
transform.DOMove(target, 1f)
    .OnUpdate(() => _state.Progress = tween.ElapsedPercentage()); // ❌ closure allocs per frame? Actually once per tween, but...
```

The closure itself is allocated once when `OnUpdate` is called. But if you do any work per frame that allocates — string concatenation, lambda invocation with boxing — it compounds:

```csharp
transform.DOMove(target, 1f).OnUpdate(() => Debug.Log("pos " + transform.position)); // ❌ string alloc every frame
```

Move allocation-heavy work out of OnUpdate.

## `ManualUpdate` — driving tweens outside PlayerLoop

For deterministic replay or test runners:

```csharp
var tween = transform.DOMove(target, 1f).SetUpdate(UpdateType.Manual).Pause();
// In your own update loop:
tween.ManualUpdate(0.016f, 0.016f);
```

Used for Netcode-driven deterministic animation or offline rendering.

## `ForceInit`

```csharp
var tween = transform.DOMove(target, 1f).Pause();
tween.ForceInit(); // Compute start value NOW without waiting for first Play
```

Useful when you construct many tweens upfront and want to pay the init cost during loading, not during Play.

## Tween state checklist

- [ ] `SetAutoKill(false)` used ONLY when tween will be replayed — otherwise tween pool fills up.
- [ ] `OnComplete` not relied on for infinite-loop tweens (`SetLoops(-1)`); `OnStepComplete` instead.
- [ ] `OnUpdate` callback body does not allocate (no string concat, no boxing).
- [ ] `Kill(complete: true)` vs `Complete()` + `Kill()` is a deliberate choice based on whether callbacks should fire.
- [ ] `SetUpdate(UpdateType.Manual)` tweens have a clear driver calling `ManualUpdate` each frame/step.
- [ ] `ForceInit` used only when needed — it evaluates getters NOW, which may be wrong before object is positioned.

See [PITFALLS.md](./PITFALLS.md) for tween-lifecycle bugs.
