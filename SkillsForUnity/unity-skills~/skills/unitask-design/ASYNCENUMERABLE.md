---
name: unity-unitask-asyncenumerable
description: "UniTaskAsyncEnumerable LINQ-to-async operators, AsyncReactiveProperty, Channel<T>, IUniTaskAsyncEnumerable<T> lifecycle, EveryValueChanged, Publish, GroupBy, Merge, subscription/leak patterns."
type: reference
---

# UniTask AsyncEnumerable & Reactive

Sub-doc of [unitask-design](./SKILL.md). Covers UniTask's answer to IObservable / IAsyncEnumerable — the `IUniTaskAsyncEnumerable<T>` interface and the `UniTaskAsyncEnumerable` static factory.

## `IUniTaskAsyncEnumerable<T>`

Source: `IUniTaskAsyncEnumerable.cs`. The UniTask-native counterpart of `IAsyncEnumerable<T>`:

```csharp
public interface IUniTaskAsyncEnumerable<out T>
{
    IUniTaskAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default);
}

public interface IUniTaskAsyncEnumerator<out T> : IUniTaskAsyncDisposable
{
    T Current { get; }
    UniTask<bool> MoveNextAsync();
}
```

Consume with C#'s `await foreach`:

```csharp
await foreach (var value in SomeStream().WithCancellation(ct))
{
    Debug.Log(value);
}
```

The `WithCancellation(ct)` extension is essential — without it, the enumerator won't stop when `ct` fires.

## `UniTaskAsyncEnumerable` — the factory / LINQ surface

Static class at `UniTaskAsyncEnumerable.cs` with dozens of LINQ-style operators. The `Runtime/Linq/*.cs` directory implements each operator in its own file.

### Common factories

Unity-specific factories live under `Runtime/Linq/UnityExtensions/`:

| API | Signature & source |
|-----|--------------------|
| `EveryUpdate` | `EveryUpdate(PlayerLoopTiming updateTiming = PlayerLoopTiming.Update, bool cancelImmediately = false)` — `Linq/UnityExtensions/EveryUpdate.cs:7` |
| `EveryValueChanged` | `EveryValueChanged<TTarget, TProperty>(TTarget target, Func<TTarget, TProperty> propertySelector, PlayerLoopTiming monitorTiming = PlayerLoopTiming.Update, IEqualityComparer<TProperty> equalityComparer = null, bool cancelImmediately = false)` — `Linq/UnityExtensions/EveryValueChanged.cs:10` |
| `Timer` (single-shot) | `Timer(TimeSpan dueTime, PlayerLoopTiming updateTiming = Update, bool ignoreTimeScale = false, bool cancelImmediately = false)` — `Linq/UnityExtensions/Timer.cs:9` |
| `Timer` (periodic) | `Timer(TimeSpan dueTime, TimeSpan period, PlayerLoopTiming updateTiming = Update, bool ignoreTimeScale = false, bool cancelImmediately = false)` — `Linq/UnityExtensions/Timer.cs:14` |
| `TimerFrame` (single-shot) | `TimerFrame(int dueTimeFrameCount, PlayerLoopTiming updateTiming = Update, bool cancelImmediately = false)` — `Linq/UnityExtensions/Timer.cs:24` |
| `TimerFrame` (periodic) | `TimerFrame(int dueTimeFrameCount, int periodFrameCount, PlayerLoopTiming updateTiming = Update, bool cancelImmediately = false)` — `Linq/UnityExtensions/Timer.cs:34` |

Generic factories (`Runtime/Linq/*.cs`):

| API | Purpose | Source |
|-----|---------|--------|
| `UniTaskAsyncEnumerable.Return<T>(T value)` | Single-value stream | `Linq/Return.cs` |
| `UniTaskAsyncEnumerable.Repeat<T>(T value, int count)` | Fixed-count repeater | `Linq/Repeat.cs` |
| `UniTaskAsyncEnumerable.Range(int start, int count)` | Int range | `Linq/Range.cs` |
| `UniTaskAsyncEnumerable.Empty<T>()` | Empty stream | `Linq/Empty.cs` |
| `UniTaskAsyncEnumerable.Never<T>()` | Never-emits, never-completes stream | `Linq/Never.cs` |
| `UniTaskAsyncEnumerable.Throw<T>(Exception)` | Immediate-fault stream | `Linq/Throw.cs` |
| `UniTaskAsyncEnumerable.Create<T>(...)` | Build a custom stream | `Linq/Create.cs` |

### `EveryValueChanged` — the big one

```csharp
UniTaskAsyncEnumerable.EveryValueChanged(this, x => x.Health)
    .ForEachAsync(h => _hpBar.value = h, ct);
```

Semantics (from `Linq/UnityExtensions/EveryValueChanged.cs:10` + implementation):
- Polls the `propertySelector` once per `monitorTiming` slot (default `PlayerLoopTiming.Update`) — NOT push-based.
- Emits only when `equalityComparer.Equals(previous, current)` returns `false`. Default comparer is `UnityEqualityComparer.GetDefault<TProperty>()` (handles Unity fake-null for UnityEngine.Object types).
- When `target` is a `UnityEngine.Object` and becomes fake-null (destroyed), the stream terminates.

Combined with `this.GetCancellationTokenOnDestroy()`, leaks are rare.

### LINQ operators (partial list)

From `Runtime/Linq/*.cs`:
- Filtering: `Where`, `Skip`, `Take`, `DistinctUntilChanged`, `TakeWhile`, `SkipWhile`, `TakeUntilCanceled`, `SkipUntilCanceled`
- Projection: `Select`, `SelectMany`, `Cast`, `OfType`
- Aggregation: `Aggregate`, `Average`, `Count`, `Sum`, `Min`, `Max`, `First`, `Last`, `Single`, `Any`, `All`, `Contains`
- Combining: `Merge`, `Concat`, `CombineLatest`, `SelectMany`
- Buffering: `Buffer`, `Pairwise`
- Sharing: `Publish`, `Queue`
- Timing: `Throttle`, `Debounce`
- Materialization: `ToArray`, `ToList`, `ToDictionary`, `ToHashSet`, `ToObservable`

Each operator is a separate file in `Linq/` — browse the folder to see the full list.

### Consumption patterns

```csharp
// For-each (most common)
await stream.ForEachAsync(x => Debug.Log(x), ct);

// With index
await stream.ForEachAsync((x, i) => Debug.Log($"{i}: {x}"), ct);

// Subscribe — returns IDisposable for push-style consumption.
// Overloads (`Linq/Subscribe.cs`):
//   Subscribe(onNext)
//   Subscribe(onNext, onError)           // `Action<Exception>` 2nd arg
//   Subscribe(onNext, onCompleted)       // `Action` 2nd arg (disambiguated from onError)
//   Subscribe(IObserver<TSource> observer) // covers all three via IObserver
// There is NO public 3-arg Subscribe(onNext, onError, onCompleted) overload — use IObserver if you need all three.
using var sub = stream.Subscribe(x => Debug.Log(x), ex => Debug.LogError(ex));
```

## `Channel<T>` — producer/consumer queue

Source: `Channel.cs`. Static factory and abstract base classes:

```csharp
// Channel.cs:7-12
public static class Channel
{
    public static Channel<T> CreateSingleConsumerUnbounded<T>();
}

// Channel.cs:24
public abstract class Channel<T> : Channel<T, T> { }

// Channel.cs:15
public abstract class Channel<TWrite, TRead>
{
    public ChannelReader<TRead>  Reader { get; protected set; }
    public ChannelWriter<TWrite> Writer { get; protected set; }
}
```

Reader / Writer APIs (`Channel.cs:28-73`):

```csharp
public abstract class ChannelReader<T>
{
    public abstract bool TryRead(out T item);
    public abstract UniTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default);
    public abstract UniTask Completion { get; }
    public virtual  UniTask<T> ReadAsync(CancellationToken cancellationToken = default);
    public abstract IUniTaskAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken = default);
}

public abstract class ChannelWriter<T>
{
    public abstract bool TryWrite(T item);
    public abstract bool TryComplete(Exception error = null);
    public void Complete(Exception error = null); // throws ChannelClosedException on already-closed
}

public partial class ChannelClosedException : InvalidOperationException { }
```

The 2.5.10 source ships `SingleConsumerUnboundedChannel<T>` (`Channel.cs:90`) as the only built-in implementation — a single-consumer, unbounded MPSC queue.

```csharp
var channel = Channel.CreateSingleConsumerUnbounded<int>();

// Producer (any thread)
channel.Writer.TryWrite(42);
channel.Writer.TryComplete();

// Consumer
await foreach (var item in channel.Reader.ReadAllAsync().WithCancellation(ct))
{
    Debug.Log(item);
}
```

Use when producers and consumers have independent rates and you want backpressure or buffering.

## `AsyncReactiveProperty<T>` — reactive value

Source: `AsyncReactiveProperty.cs`. Implements `IAsyncReactiveProperty<T>` and `IDisposable`:

```csharp
// AsyncReactiveProperty.cs:6-16
public interface IReadOnlyAsyncReactiveProperty<T> : IUniTaskAsyncEnumerable<T>
{
    T Value { get; }
    IUniTaskAsyncEnumerable<T> WithoutCurrent();
    UniTask<T> WaitAsync(CancellationToken cancellationToken = default);
}

public interface IAsyncReactiveProperty<T> : IReadOnlyAsyncReactiveProperty<T>
{
    new T Value { get; set; }
}

// AsyncReactiveProperty.cs:18-60
[Serializable]
public class AsyncReactiveProperty<T> : IAsyncReactiveProperty<T>, IDisposable
{
    public AsyncReactiveProperty(T value);
    public T Value { get; set; }  // setter fires subscribers
    public IUniTaskAsyncEnumerable<T> WithoutCurrent();  // skip initial value
    public UniTask<T> WaitAsync(CancellationToken cancellationToken = default);
    public void Dispose();  // ends stream + releases subscribers
    public static implicit operator T(AsyncReactiveProperty<T> value);
}
```

```csharp
public class Health
{
    public AsyncReactiveProperty<int> Value { get; } = new(100);
}

// Subscribe to changes (includes initial value)
await enemy.Health.Value.ForEachAsync(h => _hpBar.value = h, ct);

// Subscribe to changes (skip initial value — only react to mutations)
await enemy.Health.Value.WithoutCurrent().ForEachAsync(h => _hpBar.value = h, ct);

// Wait for a value change
int next = await enemy.Health.Value.WaitAsync(ct);

// Synchronously read/write
int current = enemy.Health.Value.Value;
enemy.Health.Value.Value = 75; // fires subscribers
```

Dispose when no longer needed — subscribers are released (`Dispose` calls `triggerEvent.SetCompleted()` at `AsyncReactiveProperty.cs:57-60`).

## Lifecycle gotchas

### 1. Forgot `WithCancellation`

```csharp
// ❌ Never terminates when ct fires
await foreach (var x in stream)
{
    if (ct.IsCancellationRequested) break; // only works AFTER current iteration emits
}

// ✅ Terminates reactively
await foreach (var x in stream.WithCancellation(ct))
{
    /* ... */
}
```

### 2. Forgot to dispose `AsyncReactiveProperty`

`AsyncReactiveProperty<T>` holds strong references to subscribers until `.Dispose()` — long-lived properties with frequent short-lived subscribers leak.

### 3. `Subscribe` without tracking the returned `IDisposable`

`.Subscribe(...)` returns an `IDisposable`. If you don't track it, the subscription lives until the stream completes (which may be never for `EveryUpdate` / `EveryValueChanged`).

```csharp
// ✅ Use .AddTo(ct) to auto-dispose
stream.Subscribe(x => HandleValue(x)).AddTo(ct);
```

### 4. `EveryUpdate` leaks PlayerLoop registration

`EveryUpdate` adds itself to UniTask's PlayerLoop pump. Without a cancellation token, it runs forever.

```csharp
// ✅ Always scope
UniTaskAsyncEnumerable.EveryUpdate()
    .ForEachAsync(_ => Tick(), this.GetCancellationTokenOnDestroy())
    .Forget();
```

## AsyncEnumerable checklist

- [ ] `await foreach` calls use `.WithCancellation(ct)` so cancellation terminates the loop.
- [ ] `Subscribe` return values are tracked (either `using var sub = ...` or `.AddTo(ct)`).
- [ ] `EveryValueChanged` / `EveryUpdate` subscriptions are scoped to a lifetime token.
- [ ] `AsyncReactiveProperty<T>` instances are disposed in `OnDestroy` / `Dispose`.
- [ ] `Channel<T>.Writer.TryComplete()` is called when production ends so consumers finish.

See [PITFALLS.md](./PITFALLS.md) for concrete leak and cancellation examples.
