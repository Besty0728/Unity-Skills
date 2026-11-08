---
name: unity-dotween-lifetime
description: "Tween lifetime management — SetAutoKill, SetLink, SetRecyclable, SetId, SetTarget, DOTween.Kill/KillAll grouping, Safe Mode semantics, tween pool capacity, tween ID types (object/string/int)."
type: reference
---

# DOTween Lifetime & Ownership

Sub-doc of [dotween-design](./SKILL.md). This is where most DOTween bugs live: tweens running on destroyed targets, tween pool exhaustion, tweens surviving scene load.

## `SetTarget` — grouping by owner

`TweenSettingsExtensions.cs:116`:

```csharp
public static T SetTarget<T>(this T t, object target) where T : Tween;
```

**Shortcut extensions set target automatically** (`transform.DOMove(...)` → target = transform). `DOTween.To(getter, setter, ...)` does NOT — manual `.SetTarget(yourObject)` required for later `DOTween.Kill(target)` to work.

Usage:

```csharp
// Kill all tweens on this GameObject
DOTween.Kill(gameObject);

// Kill all tweens matching a specific target
DOTween.Kill(myCustomObject);

// Kill all tweens with additional filter
DOTween.KillAll(false, gameObject); // exclude tweens targeting gameObject
```

## `SetId` — grouping by tag

`TweenSettingsExtensions.cs:59,69,79`:

```csharp
public static T SetId<T>(this T t, object objectId) where T : Tween;
public static T SetId<T>(this T t, string stringId) where T : Tween;   // typed for perf
public static T SetId<T>(this T t, int intId) where T : Tween;         // typed for perf
```

Three typed overloads avoid boxing the ID. Use string or int IDs for GC-sensitive paths.

```csharp
transform.DOMove(a, 1f).SetId("intro");
otherTransform.DORotate(b, 1f).SetId("intro");

// Kill all tweens tagged "intro"
DOTween.Kill("intro");
```

## `SetLink` — bind lifecycle to GameObject

`TweenSettingsExtensions.cs:91,103`:

```csharp
public static T SetLink<T>(this T t, GameObject gameObject) where T : Tween;
public static T SetLink<T>(this T t, GameObject gameObject, LinkBehaviour behaviour) where T : Tween;
```

`LinkBehaviour` (source: `LinkBehaviour.cs:11-35` — **11 public values**):

| Value | Effect |
|-------|--------|
| `PauseOnDisable` | Pause tween when link target is disabled |
| `PauseOnDisablePlayOnEnable` | Pause on disable, Play on enable |
| `PauseOnDisableRestartOnEnable` | Pause on disable, Restart on enable |
| `PlayOnEnable` | Play on enable |
| `RestartOnEnable` | Restart on enable |
| `KillOnDisable` | Kill tween when target is disabled |
| `KillOnDestroy` | Kill when target destroyed (becomes NULL). **Always active even if another behaviour is chosen** |
| `CompleteOnDisable` | Complete tween (jump to end) when target is disabled |
| `CompleteAndKillOnDisable` | Complete + kill on disable |
| `RewindOnDisable` | Rewind (delay excluded) when target is disabled |
| `RewindAndKillOnDisable` | Rewind + kill on disable |

**Key fact** (`LinkBehaviour.cs:25-26`): `KillOnDestroy` behavior applies automatically — destroying the linked GameObject kills the tween regardless of which `LinkBehaviour` you picked.

```csharp
// Common: tween on UI that should stop if panel is closed
image.DOFade(1f, 0.3f).SetLink(gameObject, LinkBehaviour.KillOnDisable);
// Destruction of gameObject also kills it (KillOnDestroy is always active).
```

**Without `SetLink`, destroyed targets are handled by Safe Mode** — tween is caught on exception, logged, and killed. Safe Mode is OK for shipping but you should still prefer `SetLink` for explicit ownership.

## `SetAutoKill` — survive completion?

`TweenSettingsExtensions.cs:39,49`:

```csharp
public static T SetAutoKill<T>(this T t) where T : Tween;                  // autoKill = true
public static T SetAutoKill<T>(this T t, bool autoKillOnCompletion) where T : Tween;
```

Default **true**: tween is killed on completion → cannot be replayed.

```csharp
// Replayable tween (e.g. idle animation loop trigger)
var idle = transform.DOScale(1.1f, 0.5f)
    .SetEase(Ease.InOutSine)
    .SetLoops(-1, LoopType.Yoyo)
    .SetAutoKill(false);

// Start/stop
idle.Play(); idle.Pause(); idle.Restart();

// Explicit cleanup when done forever
idle.Kill();
```

**Always pair `SetAutoKill(false)` with a clear explicit `.Kill()` path** — else tween pool leaks.

## `SetRecyclable` — tween instance pooling

`TweenSettingsExtensions.cs:237,246`:

```csharp
public static T SetRecyclable<T>(this T t) where T : Tween;
public static T SetRecyclable<T>(this T t, bool recyclable) where T : Tween;
```

Controls whether killed tween objects return to DOTween's internal pool or are discarded. Default comes from `DOTween.defaultRecyclable`.

**Recycling = faster allocation but more confusion**: when recycled, a Tween reference in user code may point to a now-reused instance for a different target. If you cache tween references, set `SetRecyclable(false)`.

## `DOTween.Kill` / `DOTween.KillAll`

`DOTween.cs:864,872,884,892`:

```csharp
public static int KillAll(bool complete = false);
public static int KillAll(bool complete, params object[] idsOrTargetsToExclude);
public static int Kill(object targetOrId, bool complete = false);
public static int Kill(object target, object id, bool complete = false);
```

Return value: number of tweens killed (useful for debugging).

```csharp
// Kill on scene exit
DOTween.KillAll();

// Kill everything EXCEPT UI tweens
DOTween.KillAll(false, "ui");

// Kill only tweens on this target
DOTween.Kill(gameObject);

// Kill tweens on this target with this ID
DOTween.Kill(gameObject, "fade-in");
```

`complete: true` → jumps each killed tween to end value (fires `OnComplete` if withCallbacks via Tween.Complete(true) semantics).

## Safe Mode — destroyed target protection

`DOTween.cs:49,51`:

```csharp
public static bool useSafeMode = true;
public static SafeModeLogBehaviour safeModeLogBehaviour = SafeModeLogBehaviour.Warning;
```

When Safe Mode is ON and a tween's target is destroyed mid-tween:
1. The tween's next update throws MissingReferenceException / NullReferenceException.
2. Safe Mode catches the exception.
3. The tween is killed internally.
4. Depending on `safeModeLogBehaviour`:
   - `None` → no log
   - `Warning` (default) → `Debug.LogWarning("DOTween reports:...")`
   - `Error` → `Debug.LogError(...)`
5. For nested tweens inside a Sequence, `nestedTweenFailureBehaviour` applies.

**Trade-off**:
- Safe Mode ON: robustness at small CPU cost.
- Safe Mode OFF: crashes immediately on missing targets — forces you to fix with `SetLink` / `Kill` / proper lifetime scoping.

Recommended: OFF during development bug hunts, ON in release.

## Tween pool capacity

`DOTween.cs:301`:

```csharp
public static void SetTweensCapacity(int tweenersCapacity, int sequencesCapacity);
```

Defaults: **200 Tweeners + 50 Sequences**. Exceeding either triggers a runtime expansion with a warning log — one-time hiccup but visible in Profiler.

```csharp
// For a particle-heavy scene with 500 simultaneous tweens
DOTween.SetTweensCapacity(1000, 100);
```

Call AFTER `DOTween.Init(...)` or pass via `Init().SetCapacity(...)` chain.

Symptom of capacity miss: `Max Tweens reached: capacity has been automatically increased` in console at first frame of a scene.

## `DOTween.Clear`

`DOTween.cs:311`:

```csharp
public static void Clear(bool destroy = false);
```

- `false` (default): kill all tweens, reset pool, keep driver GameObject.
- `true`: also destroy `[DOTween]` GameObject. Next tween creation will re-init and re-create the driver.

Use `Clear(true)` in Test Runner teardown to guarantee a clean state across tests.

## `ClearCachedTweens`

`DOTween.cs:353`:

```csharp
public static void ClearCachedTweens();
```

Shrinks the pool back to capacity, discarding pooled-but-unused tweens. Useful for memory-sensitive mobile builds between large scene transitions.

## Lifetime checklist

- [ ] Every long-running / infinite tween has **either** `SetLink(gameObject, ...)` **or** a manual `Kill()` path.
- [ ] `SetAutoKill(false)` tweens have a matching explicit `.Kill()` call in teardown.
- [ ] `DOTween.To(...)` custom-tweens explicitly `.SetTarget(yourObject)` for kill grouping.
- [ ] `SetId` used for scene-scoped groups (intro, cutscene, UI pop-in) — killable in bulk.
- [ ] `DOTween.KillAll()` on scene teardown OR scene-specific `SetId` groups for selective kills.
- [ ] `SetTweensCapacity` sized for the hottest scene's concurrent count.
- [ ] Safe Mode stays ON in release; deliberately OFF for bug hunts.
- [ ] Test Runner tests call `DOTween.Clear(true)` in `[TearDown]`.

See [PITFALLS.md](./PITFALLS.md) for lifetime bugs (dangling tweens on pooled GameObjects, Safe Mode hiding NREs, pool exhaustion).
