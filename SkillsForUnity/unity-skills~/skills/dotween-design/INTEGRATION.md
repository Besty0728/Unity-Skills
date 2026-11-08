---
name: unity-dotween-integration
description: "DOTween + UniTask (ToUniTask, TweenCancelBehaviour, AttachExternalCancellation), DOTween + Coroutine (WaitForCompletion YieldInstruction), DOTween + Addressables/prefab lifetime, DOTween in deterministic Netcode loops."
type: reference
---

# DOTween Integration

Sub-doc of [dotween-design](./SKILL.md). Covers how DOTween interoperates with UniTask, Coroutines, Addressables/YooAsset-loaded assets, and Netcode-driven deterministic replay.

## UniTask bridge — the preferred async path

The UniTask package ships `External/DOTween/DOTweenAsyncExtensions.cs`, gated by `#if UNITASK_DOTWEEN_SUPPORT` (auto-set when both packages are present).

### `TweenCancelBehaviour` (UniTask `DOTweenAsyncExtensions.cs:14-27`)

| Value | Behavior on cancellation |
|-------|---------------------------|
| `Kill` | Kill the tween; awaiter does NOT throw OCE |
| `KillWithCompleteCallback` | Kill + fire `OnComplete`; awaiter does NOT throw |
| `Complete` | Jump to end; awaiter does NOT throw |
| `CompleteWithSequenceCallback` | Jump to end, fire Sequence callbacks; awaiter does NOT throw |
| `CancelAwait` | Leave tween alive; awaiter DOES throw `OperationCanceledException` |
| `KillAndCancelAwait` | Kill + await throws |
| `KillWithCompleteCallbackAndCancelAwait` | Kill, fire OnComplete, await throws |
| `CompleteAndCancelAwait` | Jump to end, await throws |
| `CompleteWithSequenceCallbackAndCancelAwait` | Jump + seq callback, await throws |

### API surface

```csharp
// DOTweenAsyncExtensions.cs:54
public static UniTask ToUniTask(this Tween tween,
    TweenCancelBehaviour tweenCancelBehaviour = TweenCancelBehaviour.Kill,
    CancellationToken cancellationToken = default);

public static UniTask WithCancellation(this Tween tween, CancellationToken cancellationToken);

public static UniTask AwaitForComplete(this Tween tween, TweenCancelBehaviour, CancellationToken);
public static UniTask AwaitForPause(this Tween tween, ...);
public static UniTask AwaitForPlay(this Tween tween, ...);
public static UniTask AwaitForRewind(this Tween tween, ...);
public static UniTask AwaitForStepComplete(this Tween tween, ...);
public static UniTask AwaitForKill(this Tween tween, ...);
```

### Recommended recipe

```csharp
public async UniTask FadeInThenShake(CancellationToken ct)
{
    await _image.DOFade(1f, 0.3f)
        .ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, ct);

    await transform.DOShakePosition(0.2f, 0.1f)
        .ToUniTask(TweenCancelBehaviour.Complete, ct);
}
```

- `KillAndCancelAwait` for the fade: if cancelled, kill the fade AND unwind our async method.
- `Complete` for the shake: if cancelled (e.g. user skipped), complete the shake (so position is at final value) and continue.

### Why `ToUniTask` over `AsyncWaitForCompletion`

| Aspect | `tween.AsyncWaitForCompletion()` | `tween.ToUniTask(...)` |
|--------|----------------------------------|------------------------|
| Return type | `Task` (heap alloc) | `UniTask` (struct, zero alloc) |
| Cancellation | No CancellationToken parameter | Full CT support via TweenCancelBehaviour |
| Module dependency | Requires `DOTweenModuleUnityVersion` | Requires UniTask + UNITASK_DOTWEEN_SUPPORT |
| Polling loop | `while (t.active && !t.IsComplete()) await Task.Yield();` — spins every frame | Event-driven via internal promise |
| GC | Allocates Task per call | Zero |

**For new code: always `ToUniTask`.** `AsyncWaitForCompletion` is legacy.

## Coroutine bridge

`TweenExtensions.cs:357,372,387,403,419,435`:

```csharp
public static YieldInstruction WaitForCompletion(this Tween t);
public static YieldInstruction WaitForRewind(this Tween t);
public static YieldInstruction WaitForKill(this Tween t);
public static YieldInstruction WaitForElapsedLoops(this Tween t, int elapsedLoops);
public static YieldInstruction WaitForPosition(this Tween t, float position);
public static Coroutine        WaitForStart(this Tween t);
```

```csharp
IEnumerator Routine()
{
    yield return transform.DOMove(target, 1f).WaitForCompletion();
    yield return new WaitForSeconds(0.5f);
    yield return transform.DORotate(a, 1f).WaitForKill();
}
```

Under the hood, `WaitForCompletion()` returns a `CustomYieldInstruction` that checks tween state each frame. Cheap but not zero-alloc (small heap alloc per call).

## Addressables / YooAsset-loaded prefab tween lifetime

When tween target is a GameObject instantiated from a loaded bundle, the tween MUST end before the bundle unloads — or the tween hits a destroyed Unity object.

```csharp
// ❌ Bundle release can race with tween
var handle = Addressables.InstantiateAsync("Effect");
var go = await handle.Task;
go.transform.DOMove(target, 5f); // 5 seconds
await Task.Delay(1000);
Addressables.ReleaseInstance(go); // bundle unloads while tween still runs — tween targets destroyed GO
```

```csharp
// ✅ Link tween lifetime to GameObject, kill before release
var handle = Addressables.InstantiateAsync("Effect");
var go = await handle.Task;
go.transform.DOMove(target, 5f).SetLink(go, LinkBehaviour.KillOnDestroy);
await Task.Delay(1000);
Addressables.ReleaseInstance(go); // SetLink kills tween, then release is safe
```

Alternatively:

```csharp
// ✅ await the tween before release
await go.transform.DOMove(target, 5f).ToUniTask(TweenCancelBehaviour.Kill);
Addressables.ReleaseInstance(go);
```

## Netcode deterministic replay

DOTween's default `UpdateType.Normal` uses frame-local `Time.deltaTime`, which differs across clients. For replicated animation:

1. Use `UpdateType.Manual` + explicit `ManualUpdate(deltaTime, unscaledDeltaTime)` driven from the server's authoritative tick:

```csharp
var tween = transform.DOMove(target, 1f)
    .SetUpdate(UpdateType.Manual)
    .Pause();

// Each server tick
void OnServerTick(float tickDelta)
{
    tween.ManualUpdate(tickDelta, tickDelta);
}
```

2. Serialize key tween parameters (target, duration, start time) and RECREATE the tween on each client. Don't try to sync tween STATE over the wire.

## Audio sync

```csharp
var seq = DOTween.Sequence()
    .Append(transform.DOMove(a, 1f))
    .AppendCallback(() => audioSource.PlayOneShot(clip))
    .Append(transform.DOMove(b, 1f));
```

If audio MUST hit exact visual key frames, consider `SetUpdate(UpdateType.Manual)` driven by audio time:

```csharp
var audioStartDsp = AudioSettings.dspTime;
var tween = sequence.SetUpdate(UpdateType.Manual).Pause();

void Update()
{
    double elapsed = AudioSettings.dspTime - audioStartDsp;
    tween.Goto((float)elapsed, andPlay: false);
}
```

## UI Animation framework coexistence

### DOTween + Unity Animator

Animator drives `transform.position` each frame. DOTween does the same. **Last write wins** — Animator after DOTween if both modify same property in the same update slot.

Fix: tween a different property chain (e.g., tween a value that feeds into Animator parameter via `OnUpdate`).

### DOTween + Unity UI Toolkit

USS transitions and DOTween both animate VisualElement properties. Prefer one or the other per property. DOTween is better for complex sequences; USS transitions for simple hover/focus state changes.

## Checklist

- [ ] `tween.ToUniTask(TweenCancelBehaviour, ct)` used in async code — NOT `AsyncWaitForCompletion`.
- [ ] `TweenCancelBehaviour` explicitly chosen: `Kill` for "abort", `Complete` for "skip to end", `...AndCancelAwait` for "also unwind our async method".
- [ ] Addressables / YooAsset-instantiated tween targets use `SetLink(go, KillOnDestroy)` OR are awaited to completion before release.
- [ ] Netcode-replicated animation uses `UpdateType.Manual` driven by server tick OR reconstructs tween on each client.
- [ ] Animator + DOTween on same property avoided (last-writer-wins).
- [ ] Coroutine-based legacy code uses `.WaitForCompletion()` yield — acceptable but not zero-alloc.

See [PITFALLS.md](./PITFALLS.md) for integration bugs.
