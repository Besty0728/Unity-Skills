---
name: unity-unitask-cancellation
description: "UniTask cancellation model — CancellationToken lifetimes, GetCancellationTokenOnDestroy (3 overloads: MonoBehaviour/GameObject/Component), AttachExternalCancellation, WaitUntilCanceled, CancelAfterSlim, AddTo, OperationCanceledException flow, non-MonoBehaviour token management."
type: reference
---

# UniTask Cancellation

Sub-doc of [unitask-design](./SKILL.md). Cancellation is the part of UniTask most often done wrong. The source lives in `CancellationTokenExtensions.cs`, `CancellationTokenSourceExtensions.cs`, and `Triggers/AsyncTriggerExtensions.cs`.

## The core model

UniTask uses standard `System.Threading.CancellationToken` / `CancellationTokenSource`. When a token is canceled:

1. If a UniTask method observes it via `token.ThrowIfCancellationRequested()` or uses it in an API that does (`Delay`, `WaitUntil`, etc.), it throws `OperationCanceledException`.
2. `OperationCanceledException` propagates up the `async` stack like any exception.
3. The final `await` in the chain sees the exception. Callers choose to catch, rethrow, or suppress.

UniTask does NOT automatically cascade cancellation into child tasks. If you call `await ChildAsync()` without passing the token, the child runs to completion even after the parent's token is canceled.

## `CancellationTokenOnDestroy` — MonoBehaviour integration

Defined in `Triggers/AsyncTriggerExtensions.cs:14,22,28`:

```csharp
public static CancellationToken GetCancellationTokenOnDestroy(this MonoBehaviour monoBehaviour);
public static CancellationToken GetCancellationTokenOnDestroy(this GameObject gameObject);
public static CancellationToken GetCancellationTokenOnDestroy(this Component component);
```

Implementation detail: the extension attaches a hidden `AsyncDestroyTrigger` component to the GameObject that signals cancellation on `OnDestroy`. The trigger is cached so repeated calls return the same token.

```csharp
public class Enemy : MonoBehaviour
{
    async UniTaskVoid Start()
    {
        // Token is canceled when this GameObject is destroyed
        var ct = this.GetCancellationTokenOnDestroy();
        await RoamAsync(ct);
    }
}
```

**Non-MonoBehaviour classes do NOT have this extension** — `this.GetCancellationTokenOnDestroy()` on a plain C# class is a compile error. Pattern for plain classes:

```csharp
public class EnemyAI : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;

    public async UniTask RunAsync()
    {
        await UniTask.Delay(1000, cancellationToken: _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
```

## Passing tokens through the call chain

**Rule**: Every `async UniTask` method that eventually calls a cancellation-aware primitive (`Delay`, `WaitUntil`, `AsyncOperation.ToUniTask`, etc.) must accept a `CancellationToken` parameter and pass it down.

```csharp
// ❌ Token is not forwarded — Delay ignores cancellation
async UniTask BadChain(CancellationToken ct)
{
    await UniTask.Delay(1000); // no ct!
}

// ✅ Forward explicitly
async UniTask GoodChain(CancellationToken ct)
{
    await UniTask.Delay(1000, cancellationToken: ct);
    await SomeOtherAsync(ct);
}
```

`ct.ThrowIfCancellationRequested()` after every yield is idiomatic defensive practice:

```csharp
while (!ct.IsCancellationRequested)
{
    await UniTask.Yield(ct);
    DoWork();
}
ct.ThrowIfCancellationRequested();
```

## `AttachExternalCancellation` — wrap an uncancelable UniTask

For third-party UniTask returns that don't accept a token:

```csharp
// LibraryMethod returns UniTask without a token parameter
await thirdParty.LibraryMethodAsync().AttachExternalCancellation(ct);
```

This wraps the original UniTask and throws `OperationCanceledException` on the outer `await` if the token fires before the inner task completes. The inner task continues running to completion in the background — `AttachExternalCancellation` does NOT cancel the underlying operation, it only cancels the *wait*.

## `SuppressCancellationThrow` — cancel-aware return

Instead of throwing, return a `UniTask<bool>` indicating whether cancellation happened:

```csharp
bool isCanceled = await SomeAsync(ct).SuppressCancellationThrow();
if (isCanceled) { /* handle */ }
```

For `UniTask<T>`, the return is `UniTask<(bool IsCanceled, T Result)>`. Source: `UniTask.cs:68-74` and the `IsCanceledSource` type.

## `WaitUntilCanceled` — turn token into awaitable

```csharp
// CancellationTokenExtensions.cs:80-83
public static CancellationTokenAwaitable WaitUntilCanceled(this CancellationToken ct);
```

```csharp
// Run forever, complete when token fires
await ct.WaitUntilCanceled();
Debug.Log("Shutting down");
```

## `CancelAfterSlim` — low-alloc timeout

Source: `CancellationTokenSourceExtensions.cs:22,27`:

```csharp
public static IDisposable CancelAfterSlim(this CancellationTokenSource cts,
    int millisecondsDelay,
    DelayType delayType = DelayType.DeltaTime,
    PlayerLoopTiming delayTiming = PlayerLoopTiming.Update);

public static IDisposable CancelAfterSlim(this CancellationTokenSource cts,
    TimeSpan delayTimeSpan,
    DelayType delayType = DelayType.DeltaTime,
    PlayerLoopTiming delayTiming = PlayerLoopTiming.Update);
```

Both overloads return an `IDisposable` you can dispose to cancel the pending timer (e.g., when the work finished before the timeout). Internally routes through `PlayerLoopTimer.StartNew`, so it uses UniTask's PlayerLoop pump — zero `System.Threading.Timer` allocation.

```csharp
using var cts = new CancellationTokenSource();
var timer = cts.CancelAfterSlim(TimeSpan.FromSeconds(5));
try
{
    await DoLongWorkAsync(cts.Token);
    timer.Dispose(); // cancel the timer if work finished in time
}
catch (OperationCanceledException) { /* timeout fired */ }
```

Equivalent intent to `cts.CancelAfter(5000)` but uses UniTask's PlayerLoop-based timer instead of `System.Threading.Timer`.

## `RegisterRaiseCancelOnDestroy` — tie CTS to a GameObject

`CancellationTokenSourceExtensions.cs:32,37`:

```csharp
public static void RegisterRaiseCancelOnDestroy(this CancellationTokenSource cts, Component component);
public static void RegisterRaiseCancelOnDestroy(this CancellationTokenSource cts, GameObject gameObject);
```

Attaches an `AsyncDestroyTrigger` to the GameObject so its `OnDestroy` calls `cts.Cancel()`. Useful when you own a CTS (for composed work) but want Unity destruction to feed into it automatically.

## `AddTo` — dispose on cancel

```csharp
// CancellationTokenExtensions.cs:129-132
public static CancellationTokenRegistration AddTo(this IDisposable disposable, CancellationToken cancellationToken);
```

```csharp
// Dispose the subscription when ct is canceled
someDisposable.AddTo(ct);
```

Useful for releasing `IAsyncDisposable` wrappers, `IUniTaskAsyncEnumerable<T>` subscriptions, or UniRx-style resources.

## `ToCancellationToken` — UniTask → token

```csharp
// CancellationTokenExtensions.cs:14-47
public static CancellationToken ToCancellationToken(this UniTask task);
public static CancellationToken ToCancellationToken(this UniTask task, CancellationToken linkToken);
public static CancellationToken ToCancellationToken<T>(this UniTask<T> task);
public static CancellationToken ToCancellationToken<T>(this UniTask<T> task, CancellationToken linkToken);
```

Creates a token that cancels when the UniTask completes (success, fault, or cancel). Useful for "cancel when X finishes" patterns.

## `CreateLinkedTokenSource` — combine tokens

Standard BCL API (`CancellationTokenSource.CreateLinkedTokenSource`) composes well:

```csharp
using var linked = CancellationTokenSource.CreateLinkedTokenSource(
    this.GetCancellationTokenOnDestroy(),
    externalCts.Token);
await DoWorkAsync(linked.Token);
```

## Lifecycle checklist

- [ ] Every `async UniTask` method that calls a cancellation-aware primitive accepts `CancellationToken` and forwards it.
- [ ] Non-MonoBehaviour classes that need destruction-aware cancellation own their own `CancellationTokenSource` and dispose it.
- [ ] `CancellationTokenSource` created inside a method is disposed in `finally` (or `using var`).
- [ ] Caller-side handling of `OperationCanceledException` is intentional — catch and log (for UI) vs. let propagate (for fire-and-forget).
- [ ] Timeouts use `CancelAfterSlim` (UniTask-native) rather than `CancellationTokenSource.CancelAfter` (which allocates a `System.Threading.Timer`).
- [ ] External disposables are tied to tokens via `.AddTo(ct)` when lifetime is token-scoped.

## Common pitfalls (full list in [PITFALLS.md](./PITFALLS.md))

- Forgetting to pass the token — child operation outlives parent cancellation.
- Calling `GetCancellationTokenOnDestroy()` on a plain C# class — compile error.
- Disposing a CTS that has outstanding registrations without a try/finally — ObjectDisposedException on cancel.
- Canceling a CTS twice — second `Cancel()` is a no-op but `Dispose()` on an already-disposed CTS throws.
