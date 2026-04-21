---
name: unity-unitask-composition
description: "UniTask composition operators — WhenAll, WhenAny, WhenEach, Forget, ContinueWith, timeout patterns, RetryOnError; zero-alloc combinator semantics and how they differ from Task.WhenAll/WhenAny."
type: reference
---

# UniTask Composition

Sub-doc of [unitask-design](./SKILL.md). Covers every combinator that lets you run UniTasks in parallel or chained.

## `UniTask.WhenAll`

Defined in `UniTask.WhenAll.cs:12,22,31,41`:

```csharp
public static UniTask<T[]> WhenAll<T>(params UniTask<T>[] tasks);
public static UniTask<T[]> WhenAll<T>(IEnumerable<UniTask<T>> tasks);
public static UniTask      WhenAll   (params UniTask[] tasks);
public static UniTask      WhenAll   (IEnumerable<UniTask> tasks);
```

Semantics match `Task.WhenAll`:
- Completes when all tasks complete.
- If any task faults, the returned UniTask faults with an `AggregateException` (or the single inner exception depending on version).
- If any task is canceled, the returned UniTask is canceled.

**Critical**: `WhenAll` stores the tasks in an array internally. Awaiting each task is a ONE-TIME operation (`UniTask` is a struct with single-use semantics). You cannot pass the same `UniTask` variable twice to the array — use `.Preserve()` if you need to wait on the same result in multiple `WhenAll` calls.

### Generated overloads — heterogeneous tuples

`UniTask.WhenAll.Generated.cs` provides overloads that return tuples:

```csharp
(int a, string b) = await UniTask.WhenAll(
    IntAsync(),
    StringAsync()
);
```

Up to 10 heterogeneous tasks in one tuple-returning call. Source: `UniTask.WhenAll.Generated.cs`.

## `UniTask.WhenAny`

```csharp
// UniTask.WhenAny.cs
public static UniTask<(int winArgumentIndex, T result)> WhenAny<T>(params UniTask<T>[] tasks);
public static UniTask<(int winArgumentIndex, T result)> WhenAny<T>(IEnumerable<UniTask<T>> tasks);
public static UniTask<int>                              WhenAny   (params UniTask[] tasks);
public static UniTask<int>                              WhenAny   (IEnumerable<UniTask> tasks);
```

Returns the **index** of the winner along with the result (for typed overloads). Unlike `Task.WhenAny`, it does NOT return the winning UniTask itself — awaiting that UniTask again would fail due to struct semantics.

```csharp
var (winIndex, result) = await UniTask.WhenAny(
    FetchFromServer(),
    UniTask.Delay(5000, cancellationToken: ct).ContinueWith(() => "timeout")
);
```

**Losers keep running.** WhenAny does NOT cancel non-winning tasks. If you need them canceled, compose with a linked `CancellationTokenSource`:

```csharp
using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
var (winIndex, result) = await UniTask.WhenAny(
    FetchA(cts.Token),
    FetchB(cts.Token)
);
cts.Cancel(); // kill the loser
```

## `UniTask.WhenEach`

`UniTask.WhenEach.cs` — newer addition (2.5+). Returns `IUniTaskAsyncEnumerable<WhenEachResult<T>>` yielding each task as it completes, with index and result:

```csharp
await foreach (var result in UniTask.WhenEach(taskA, taskB, taskC))
{
    Debug.Log($"Task #{result.Index} finished: {result.Result}");
}
```

Useful for "process each as it arrives" patterns that used to require `WhenAny` + list removal loops.

## `.ContinueWith` — chaining without re-await

```csharp
// UniTaskExtensions.cs
public static UniTask<TR> ContinueWith<T, TR>(this UniTask<T> task, Func<T, TR> continuation);
public static UniTask<TR> ContinueWith<T, TR>(this UniTask<T> task, Func<T, UniTask<TR>> continuation);
public static UniTask     ContinueWith        (this UniTask task, Action continuation);
// ... etc
```

Zero-alloc linear chain without needing `async UniTask<T> Wrap() { return await inner; }`. Faults and cancellations propagate.

## `.Timeout` / `.TimeoutWithoutException`

```csharp
// UniTaskExtensions.cs
public static UniTask<T> Timeout<T>(this UniTask<T> task, TimeSpan timeout, DelayType delayType = DelayType.DeltaTime, PlayerLoopTiming timeoutCheckTiming = PlayerLoopTiming.Update, CancellationTokenSource taskCancellationTokenSource = null);
public static UniTask<(bool IsTimeout, T Result)> TimeoutWithoutException<T>(this UniTask<T> task, TimeSpan timeout, ...);
```

```csharp
// Throws TimeoutException if > 5s
var result = await LongWork().Timeout(TimeSpan.FromSeconds(5));

// Non-throwing variant
var (isTimeout, result) = await LongWork().TimeoutWithoutException(TimeSpan.FromSeconds(5));
```

**Gotcha**: `Timeout` does NOT cancel the underlying task by default — it just gives up waiting. To cancel, pass the `taskCancellationTokenSource` so timeout triggers `.Cancel()` on it.

## `.AttachExternalCancellation`

Covered in [CANCELLATION.md](./CANCELLATION.md). Wraps an uncancelable UniTask so the await respects a new token.

## Fire-and-forget: `.Forget()`

```csharp
// UniTask.cs (partial)
public void Forget();
public void Forget(Action<Exception> exceptionHandler, bool handleExceptionOnMainThread = true);
```

`.Forget()` ensures the async state machine runs but explicitly drops the result. Unhandled exceptions go to `UniTaskScheduler.UnobservedTaskException` unless a handler is provided.

```csharp
LongRunningSideEffect().Forget();

// With custom handler
LongRunningSideEffect().Forget(ex => Debug.LogError(ex));
```

## `Preserve()` — reusable UniTask

See [BASICS.md](./BASICS.md). Call `.Preserve()` once to obtain a UniTask that can be awaited multiple times. Required when the same result must be shared across multiple `WhenAll`s or multiple consumers.

```csharp
UniTask<Config> configTask = LoadConfig().Preserve();
await UniTask.WhenAll(
    UseConfig1(configTask),
    UseConfig2(configTask)
);
```

## Retry patterns

UniTask ships no built-in `Retry` — implement with a loop:

```csharp
async UniTask<T> RetryAsync<T>(Func<UniTask<T>> factory, int maxAttempts, TimeSpan backoff, CancellationToken ct)
{
    for (int attempt = 0; ; attempt++)
    {
        try
        {
            return await factory();
        }
        catch (Exception ex) when (!(ex is OperationCanceledException) && attempt < maxAttempts - 1)
        {
            await UniTask.Delay(backoff, cancellationToken: ct);
        }
    }
}
```

Pass `factory` (not a UniTask value) because each retry needs a fresh UniTask — struct semantics forbid re-awaiting.

## Composition checklist

- [ ] Tasks passed to `WhenAll` / `WhenAny` are fresh UniTasks, not stored variables awaited twice.
- [ ] Losers in `WhenAny` are explicitly canceled (or their cost is acceptable).
- [ ] `.Timeout` callers understand it doesn't cancel the inner task unless `taskCancellationTokenSource` is supplied.
- [ ] Reusable results use `.Preserve()` ONCE, then the returned UniTask is shared.
- [ ] Retry loops recreate the UniTask from a factory rather than awaiting a stored value.

See [PITFALLS.md](./PITFALLS.md) for composition-related bugs.
