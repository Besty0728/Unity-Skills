---
name: unity-unitask-playerloop
description: "PlayerLoopTiming (16 values 2020.2+), UniTask.Yield / NextFrame / Delay / DelayFrame / WaitForEndOfFrame / WaitForFixedUpdate, DelayType, frame-ordering semantics and coroutine interop."
type: reference
---

# UniTask PlayerLoop & Timing

Sub-doc of [unitask-design](./SKILL.md). Everything here maps to `PlayerLoopHelper.cs`, `UniTask.Delay.cs`, and Unity's player loop order.

## `PlayerLoopTiming` — full enum

Defined in `PlayerLoopHelper.cs:71-99`. On Unity 2020.2+ there are **16** values (below). On older Unity there are **14** — `TimeUpdate` / `LastTimeUpdate` are omitted (see `#if UNITY_2020_2_OR_NEWER` guard at `PlayerLoopHelper.cs:94-98`).

| Value | Name | Phase |
|:-----:|------|-------|
| 0 | `Initialization` | Start of frame |
| 1 | `LastInitialization` | End of Initialization phase |
| 2 | `EarlyUpdate` | Input/UI events |
| 3 | `LastEarlyUpdate` | |
| 4 | `FixedUpdate` | Physics step |
| 5 | `LastFixedUpdate` | |
| 6 | `PreUpdate` | |
| 7 | `LastPreUpdate` | |
| 8 | `Update` | **default** for `UniTask.Yield` / `Delay` |
| 9 | `LastUpdate` | |
| 10 | `PreLateUpdate` | |
| 11 | `LastPreLateUpdate` | |
| 12 | `PostLateUpdate` | |
| 13 | `LastPostLateUpdate` | Runs AFTER WaitForEndOfFrame equivalent |
| 14 | `TimeUpdate` *(2020.2+)* | |
| 15 | `LastTimeUpdate` *(2020.2+)* | |

Bitmask flags: `InjectPlayerLoopTimings` in `PlayerLoopHelper.cs:101-172` defines the same values as power-of-two bit flags for selective injection.

## `UniTask.Yield` family

```csharp
// UniTask.Delay.cs:24-44
public static YieldAwaitable Yield();                                         // default: PlayerLoopTiming.Update
public static YieldAwaitable Yield(PlayerLoopTiming timing);
public static UniTask       Yield(CancellationToken, bool cancelImmediately = false);
public static UniTask       Yield(PlayerLoopTiming, CancellationToken, bool cancelImmediately = false);
```

Use `YieldAwaitable` overloads for hot paths (parameterless and `timing`-only) — they allocate less than the CancellationToken overloads which need to create a promise.

```csharp
await UniTask.Yield();                             // next Update
await UniTask.Yield(PlayerLoopTiming.FixedUpdate); // next FixedUpdate
await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate); // end of frame
```

## `UniTask.NextFrame`

```csharp
// UniTask.Delay.cs:49-76
public static UniTask NextFrame();
public static UniTask NextFrame(PlayerLoopTiming timing);
public static UniTask NextFrame(CancellationToken, bool cancelImmediately = false);
public static UniTask NextFrame(PlayerLoopTiming, CancellationToken, bool cancelImmediately = false);
```

`Yield` may resolve on the SAME frame when awaited from an earlier phase than the target timing. `NextFrame` guarantees the continuation runs on a subsequent frame.

**Rule of thumb**:
- Need to split work across frames? → `NextFrame`
- Need to yield back to a specific phase (same or later frame)? → `Yield(timing)`

## `WaitForEndOfFrame` / `WaitForFixedUpdate`

```csharp
// UniTask.Delay.cs:91-133
#if UNITY_2023_1_OR_NEWER
public static async UniTask WaitForEndOfFrame(CancellationToken cancellationToken = default);
#endif
public static UniTask WaitForEndOfFrame(MonoBehaviour coroutineRunner);
public static UniTask WaitForEndOfFrame(MonoBehaviour coroutineRunner, CancellationToken, bool cancelImmediately = false);

public static UniTask WaitForFixedUpdate();
public static UniTask WaitForFixedUpdate(CancellationToken, bool cancelImmediately = false);
```

- **Before Unity 2023.1**: `WaitForEndOfFrame` requires a `MonoBehaviour` to host an internal coroutine (Unity's native `WaitForEndOfFrame` is only usable from coroutines). The overload without `coroutineRunner` does NOT exist — compile error on older Unity.
- **Unity 2023.1+**: A parameterless overload exists (`#if UNITY_2023_1_OR_NEWER`, `UniTask.Delay.cs:78-89`). It uses `WaitForEndOfFrameAsync()` under the hood.
- On all versions you can substitute `UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate)` for end-of-frame semantics if you don't need strict Unity EOF timing (rendered-before-present).

## `Delay` / `DelayFrame`

```csharp
// UniTask.Delay.cs:137-168
public static UniTask DelayFrame(int delayFrameCount, PlayerLoopTiming delayTiming = PlayerLoopTiming.Update, CancellationToken = default, bool cancelImmediately = false);

// Legacy overload — mixes "unscaled" intent into a bool:
public static UniTask Delay(int millisecondsDelay, bool ignoreTimeScale = false, PlayerLoopTiming = Update, CancellationToken = default, bool cancelImmediately = false);
public static UniTask Delay(TimeSpan delayTimeSpan, bool ignoreTimeScale = false, PlayerLoopTiming = Update, CancellationToken = default, bool cancelImmediately = false);

// Preferred overload — explicit DelayType:
public static UniTask Delay(int millisecondsDelay, DelayType delayType, PlayerLoopTiming = Update, CancellationToken = default, bool cancelImmediately = false);
public static UniTask Delay(TimeSpan delayTimeSpan, DelayType delayType, PlayerLoopTiming = Update, CancellationToken = default, bool cancelImmediately = false);
```

### `DelayType` values (`UniTask.Delay.cs:12-20`)

| Value | Semantics |
|-------|-----------|
| `DeltaTime` | Uses `Time.deltaTime` — pauses with `Time.timeScale = 0` |
| `UnscaledDeltaTime` | Uses `Time.unscaledDeltaTime` — ignores `timeScale` |
| `Realtime` | Uses `Stopwatch.GetTimestamp()` — highest-accuracy wall clock |

`ignoreTimeScale = true` from the legacy overload maps to `DelayType.UnscaledDeltaTime`. For new code, prefer the explicit `DelayType` overload — it is unambiguous and is what Cysharp recommends.

### `cancelImmediately` parameter

Default is `false`: cancellation is checked on the next timing slot (e.g. next Update). Passing `true` registers a callback on the `CancellationToken` that schedules the cancellation immediately on the current thread — slightly more expensive but tears down the delay without waiting for the next frame.

## Coroutine interop: frame-order surprises

Unity's legacy `yield return null` resolves at `Update` phase (roughly `PlayerLoopTiming.Update`). Mixed code that does:

```csharp
// In a Coroutine
yield return null; // Update
// In parallel UniTask
await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
```

The Coroutine resumes first. If your UniTask expects state set by the Coroutine, that's fine. If the Coroutine expects state set by the UniTask, you'll see a one-frame lag.

## `WaitForEndOfFrame` gotcha on WebGL

Even with Unity 2023.1+, `WaitForEndOfFrameAsync` on WebGL resolves at a similar phase to `LastPostLateUpdate`, because WebGL does not have a post-render phase exposed to managed code. Treat the two as equivalent when targeting WebGL.

## Choosing the right API

| Intent | Best API |
|--------|----------|
| Back off one frame | `UniTask.NextFrame()` |
| Back off N frames | `UniTask.DelayFrame(N)` |
| Wait X seconds, respect `Time.timeScale` | `UniTask.Delay(ms, DelayType.DeltaTime)` |
| Wait X seconds, ignore `Time.timeScale` | `UniTask.Delay(ms, DelayType.UnscaledDeltaTime)` |
| Sync with physics | `UniTask.WaitForFixedUpdate()` |
| Sync with end of frame (post-render) | `UniTask.WaitForEndOfFrame(this)` (<2023.1) or `UniTask.WaitForEndOfFrame()` (2023.1+) |
| Yield to let UI catch up | `UniTask.Yield(PlayerLoopTiming.PreLateUpdate)` |

See [PITFALLS.md](./PITFALLS.md) for common timing bugs.
