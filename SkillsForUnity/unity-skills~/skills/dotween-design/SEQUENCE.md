---
name: unity-dotween-sequence
description: "DOTween.Sequence() composition — Append vs Join vs Insert vs Prepend vs AppendInterval vs PrependInterval vs AppendCallback vs InsertCallback vs JoinCallback, nested sequences, Sequence.SetLoops, timing math, parent Sequence autoKill semantics."
type: reference
---

# DOTween Sequences

Sub-doc of [dotween-design](./SKILL.md). Sequences are where tweens get composed in time. Most confusion is about Append vs Join vs Insert vs Prepend.

## Creating a Sequence

`DOTween.cs:772,786`:

```csharp
public static Sequence Sequence();
public static Sequence Sequence(object target);   // sets target for later Kill(target)
```

```csharp
var seq = DOTween.Sequence();
```

## Composition APIs

All from `TweenSettingsExtensions.cs`:

| API | Line | Effect |
|-----|:----:|--------|
| `Append(this Sequence s, Tween t)` | 499 | Add tween at current END of sequence |
| `Prepend(this Sequence s, Tween t)` | 508 | Add tween at time 0; pushes everything else later |
| `Join(this Sequence s, Tween t)` | 517 | Add tween at the START of the previously appended tween (parallel with previous) |
| `Insert(this Sequence s, float atPosition, Tween t)` | 528 | Add tween at absolute time `atPosition` within sequence |
| `AppendInterval(this Sequence s, float interval)` | 538 | Add empty gap at end |
| `PrependInterval(this Sequence s, float interval)` | 547 | Add empty gap at time 0; pushes everything later |
| `AppendCallback(this Sequence s, TweenCallback callback)` | 557 | Fire callback at end |
| `PrependCallback(this Sequence s, TweenCallback callback)` | 568 | Fire callback at time 0 |
| `JoinCallback(this Sequence s, TweenCallback callback)` | 580 | Fire callback at time of LAST appended tween start |
| `InsertCallback(this Sequence s, float atPosition, TweenCallback callback)` | 593 | Fire callback at absolute time `atPosition` |

## Timing semantics (visual)

Sequence starts at time 0 and grows as you Append. `lastTweenInsertTime` tracks the start of the most-recently-appended tween (Sequence.cs:25).

```
Sequence timeline (X = tween playing, . = empty, | = marker)

Append(t1, dur=2):      [XX______]       duration = 2
Append(t2, dur=1):      [XX_X____]       duration = 3
Join(t3,   dur=2):      [XX_XX___]       t3 starts where t2 started (time 2), runs in parallel with t2
Insert(0.5, t4, dur=1): [XXX_XX__]       t4 at absolute time 0.5
AppendInterval(2):      [XXX_XX___.]     insert gap after last append
```

### Append vs Join — the classic confusion

```csharp
// ❌ Wanted parallel animations, got sequential
DOTween.Sequence()
    .Append(transform.DOMove(a, 1f))
    .Append(transform.DORotate(b, 1f)); // runs AFTER move, not alongside
```

```csharp
// ✅ Append the first, Join subsequent parallel ones
DOTween.Sequence()
    .Append(transform.DOMove(a, 1f))
    .Join(transform.DORotate(b, 1f));   // parallel
```

### `Insert` at absolute positions

```csharp
var seq = DOTween.Sequence();
seq.Insert(0f, transform.DOMove(start, 2f));
seq.Insert(1f, transform.DOScale(big, 1f)); // starts at t=1 regardless of previous Append state
```

## Nested Sequences

A Sequence is itself a Tween, so you can nest:

```csharp
var inner = DOTween.Sequence()
    .Append(target.DOMove(a, 1f))
    .Append(target.DOMove(b, 1f));

var outer = DOTween.Sequence()
    .Append(inner)
    .Append(target.DORotate(Vector3.zero, 1f));
```

**Safe Mode nuance** (`DOTween.cs:54`):
```csharp
public static NestedTweenFailureBehaviour nestedTweenFailureBehaviour = NestedTweenFailureBehaviour.TryToPreserveSequence;
```

If an inner tween fails (target destroyed), the parent Sequence's behavior depends on this setting. `TryToPreserveSequence` (default) removes the broken tween and keeps the sequence running. `KillWholeSequence` propagates the failure.

## Sequence with `SetLoops`

```csharp
var seq = DOTween.Sequence()
    .Append(target.DOMoveX(1, 1f))
    .Append(target.DOMoveX(0, 1f))
    .SetLoops(-1, LoopType.Restart);
```

Works on the Sequence as a whole. Important:
- Internal tweens' own `SetLoops` is UNUSED when inside a Sequence — only Sequence-level loops apply.
- `SetAutoKill(false)` on the SEQUENCE is what matters for replay, not on the inner tweens.
- Mixing `autoKill` settings between inner tweens and parent Sequence is a known source of bugs — keep inner tweens as transient and control lifecycle on the Sequence.

## `JoinCallback` timing

Unlike `Join(tween)` which starts parallel at the last appended tween's START, `JoinCallback` fires at the START of the last appended tween:

```csharp
DOTween.Sequence()
    .Append(transform.DOMove(a, 2f))      // t=0..2
    .AppendInterval(1f)                   // t=2..3
    .Append(transform.DOMove(b, 2f))      // t=3..5
    .JoinCallback(() => Debug.Log("b started"))  // fires at t=3
    .InsertCallback(4f, () => Debug.Log("halfway")); // fires at t=4
```

## `Prepend` vs `PrependInterval`

```csharp
// Sequence starts at time 0 with existing [A]
// Prepend(B, dur=1): B runs at t=0..1, A now runs at t=1..
// PrependInterval(1): empty gap at t=0..1, A now runs at t=1..
```

`Prepend` shifts everything right by the prepended tween's duration. `PrependInterval` shifts by the interval.

## Sequence + `SetDelay`

`TweenSettingsExtensions.cs:755`:

```csharp
public static T SetDelay<T>(this T t, float delay, bool asPrependedIntervalIfSequence) where T : Tween;
```

For a Sequence, `SetDelay(d, asPrependedIntervalIfSequence: true)` is equivalent to `PrependInterval(d)`. `false` adds the delay as a pre-play offset that inner tweens can't see — rarely what you want.

**Preferred**: Use `PrependInterval` explicitly for clarity.

## Sequence-level callbacks

Sequence inherits all `OnStart / OnPlay / OnStepComplete / OnComplete / OnKill / OnRewind` hooks from Tween. They fire for the Sequence as a whole. Inner tween callbacks fire independently when each inner tween hits its own state.

```csharp
DOTween.Sequence()
    .Append(tweenA.OnComplete(() => Debug.Log("A done")))
    .Append(tweenB.OnComplete(() => Debug.Log("B done")))
    .OnComplete(() => Debug.Log("sequence done"));
// Output order: "A done", "B done", "sequence done"
```

## Sequence checklist

- [ ] Parallel tweens use `Join`, not a second `Append`.
- [ ] `Insert(atPosition, ...)` used when absolute timing matters; `Append / Join` for relative.
- [ ] Infinite-loop Sequences do not rely on `OnComplete` — use `OnStepComplete`.
- [ ] Inner tweens inside a looped Sequence do NOT set their own `SetLoops` (unused).
- [ ] Sequence-level `SetAutoKill(false)` used for replayable Sequences; inner tweens transient.
- [ ] `nestedTweenFailureBehaviour` chosen deliberately when parent sequence depends on inner completion semantics.

See [PITFALLS.md](./PITFALLS.md) for sequence bugs (Append-when-should-Join, infinite-loop OnComplete, etc.).
