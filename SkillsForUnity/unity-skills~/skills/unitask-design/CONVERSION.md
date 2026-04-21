---
name: unity-unitask-conversion
description: "UniTask interop — AsyncOperation.ToUniTask, UnityWebRequest.SendWebRequest().ToUniTask, IEnumerator.ToUniTask, Task.AsUniTask, UniTask.AsTask, UniTask.ToCoroutine. When each conversion is correct, when it leaks progress callbacks or throws."
type: reference
---

# UniTask Conversion & Interop

Sub-doc of [unitask-design](./SKILL.md). Covers every bridge between UniTask and Unity's legacy `AsyncOperation` / `Coroutine`, and between UniTask and `System.Threading.Tasks.Task`.

## `AsyncOperation.ToUniTask`

Source: `UnityAsyncExtensions.cs:37` (base overload) and typed overloads for Unity's AsyncOperation subtypes. Base signature:

```csharp
public static UniTask ToUniTask(this AsyncOperation asyncOperation,
    IProgress<float> progress = null,
    PlayerLoopTiming timing = PlayerLoopTiming.Update,
    CancellationToken cancellationToken = default(CancellationToken),
    bool cancelImmediately = false);
```

Typed overloads (each has a matching `WithCancellation` variant) for every Unity AsyncOperation subtype:

| Overload | Returns | Source line |
|----------|---------|:----:|
| `ResourceRequest.ToUniTask(...)` | `UniTask<UnityEngine.Object>` | `UnityAsyncExtensions.cs:263` |
| `AssetBundleRequest.ToUniTask(...)` | `UniTask<UnityEngine.Object>` | `UnityAsyncExtensions.cs:498` |
| `AssetBundleCreateRequest.ToUniTask(...)` | `UniTask<AssetBundle>` | `UnityAsyncExtensions.cs:734` |
| `UnityWebRequestAsyncOperation.ToUniTask(...)` | `UniTask<UnityWebRequest>` | `UnityAsyncExtensions.cs:970` |

```csharp
// ✅ Idiomatic
var bundle = await AssetBundle.LoadFromFileAsync(path).ToUniTask(
    progress: Progress.Create<float>(p => _progressBar.value = p),
    cancellationToken: this.GetCancellationTokenOnDestroy());
```

### Why not just `await operation`?

UniTask provides a GetAwaiter extension on `AsyncOperation`, so `await op` compiles. But:
- Progress reporting is NOT wired up — you'd have to subscribe `op.completed += ...` manually.
- Cancellation is NOT wired up — the operation runs to completion regardless.
- Result extraction is NOT typed — you have to cast the operation afterwards.

Always prefer `.ToUniTask()` unless you explicitly need the untyped, no-progress, no-cancel behavior.

## `UnityWebRequest` — the full pattern

```csharp
using var request = UnityWebRequest.Get(url);
try
{
    await request.SendWebRequest().ToUniTask(
        progress: Progress.Create<float>(p => Debug.Log($"DL: {p:P0}")),
        cancellationToken: ct);
}
catch (UnityWebRequestException ex)
{
    // Networking error — inspect ex.Result / ex.Error / ex.ResponseCode
    Debug.LogError(ex);
}
```

`UnityWebRequestException` is defined in its own file at `UnityWebRequestException.cs:9` (NOT `Internal/UnityWebRequestExtensions.cs`), gated by:
```csharp
#if ENABLE_UNITYWEBREQUEST && (!UNITY_2019_1_OR_NEWER || UNITASK_WEBREQUEST_SUPPORT)
```

### Exception fields (`UnityWebRequestException.cs:11-21`)

| Property | Type | Notes |
|----------|------|-------|
| `UnityWebRequest` | `UnityWebRequest` | The failed request object itself |
| `Result` | `UnityWebRequest.Result` | 2020.2+ only — `ConnectionError / DataProcessingError / ProtocolError` |
| `IsNetworkError` | `bool` | Pre-2020.2 only |
| `IsHttpError` | `bool` | Pre-2020.2 only |
| `Error` | `string` | `UnityWebRequest.error` |
| `Text` | `string` | Downloaded body if `DownloadHandlerBuffer` was used |
| `ResponseCode` | `long` | HTTP status code |
| `ResponseHeaders` | `Dictionary<string, string>` | Response headers dict |

The exception is thrown internally at multiple sites in `UnityAsyncExtensions.cs:978 / 1008 / 1018 / 1165 / 1203` whenever `UnityWebRequestResultExtensions.IsError(request)` returns `true` (see `Internal/UnityWebRequestExtensions.cs:14`).

A plain `await request.SendWebRequest()` (without `.ToUniTask()`) does NOT throw — you have to check `request.result` manually.

## `Coroutine` → UniTask

```csharp
// UnityAsyncExtensions.MonoBehaviour.cs
public static UniTask ToUniTask(this Coroutine coroutine);
// Wraps a started Coroutine (already returned by StartCoroutine).

// EnumeratorAsyncExtensions.cs
public static async UniTask ToUniTask(this IEnumerator enumerator,
    MonoBehaviour coroutineRunner);
public static async UniTask ToUniTask(this IEnumerator enumerator,
    PlayerLoopTiming timing = PlayerLoopTiming.Update,
    CancellationToken cancellationToken = default);
```

```csharp
// Start your legacy coroutine, then await its completion via UniTask
var handle = StartCoroutine(LegacyRoutine());
await handle.ToUniTask(); // waits until LegacyRoutine finishes

// Or skip StartCoroutine entirely and run an IEnumerator on the UniTask scheduler
await LegacyRoutine().ToUniTask();
```

The `IEnumerator.ToUniTask()` overload WITHOUT a `MonoBehaviour coroutineRunner` drives the IEnumerator through UniTask's PlayerLoop pump. Some Unity yield instructions (`WaitForEndOfFrame`, `WaitForSeconds`) require a real MonoBehaviour host — use the overload with `coroutineRunner` for those.

## UniTask → Coroutine (`.ToCoroutine()`)

```csharp
// UniTask.cs (partial)
public Coroutine ToCoroutine(Action<Exception> exceptionHandler = null, bool returnToMainThread = true);
```

Converts a UniTask into a Coroutine that can be yielded from legacy code:

```csharp
IEnumerator Legacy()
{
    yield return NewUniTaskWorkflow().ToCoroutine();
}
```

**Requires a MonoBehaviour host** — the Coroutine will be associated with UniTask's internal driver MonoBehaviour. If you need the Coroutine to live on YOUR MonoBehaviour, wrap differently:

```csharp
IEnumerator Legacy()
{
    var task = NewUniTaskWorkflow();
    while (task.Status == UniTaskStatus.Pending) yield return null;
    if (task.Status == UniTaskStatus.Faulted) throw task.GetAwaiter().GetResult();
}
```

## `Task` ↔ `UniTask`

```csharp
// UniTaskExtensions.cs:17,47
public static UniTask<T> AsUniTask<T>(this Task<T> task, bool useCurrentSynchronizationContext = true);
public static UniTask    AsUniTask   (this Task task, bool useCurrentSynchronizationContext = true);

public static Task<T> AsTask<T>(this UniTask<T> task);
public static Task    AsTask   (this UniTask task);
```

Round-tripping a Task through UniTask:
- `AsUniTask(useCurrentSynchronizationContext: true)` — continuations run on the captured `SynchronizationContext` (Unity main thread if called from there).
- `AsUniTask(false)` — continuations run wherever `Task.ContinueWith` chose (thread pool on most platforms, throws on WebGL).

**Rule**: When converting from Task, pass `true` (the default) unless you have a specific reason to run off-main-thread.

## `IObservable<T>` ↔ UniTask (UniRx interop)

`UniTaskObservableExtensions.cs`:
```csharp
public static UniTask<T> ToUniTask<T>(this IObservable<T> source, bool useFirstValue = false);
public static IObservable<T> ToObservable<T>(this UniTask<T> task);
```

- `useFirstValue: true` — UniTask resolves with the FIRST emitted value (then unsubscribes).
- `useFirstValue: false` — UniTask resolves with the LAST emitted value when the observable completes.

Useful for bridging between UniRx / R3 observables and UniTask.

## `Task.Run` replacement: `UniTask.RunOnThreadPool` / `UniTask.Run`

```csharp
// UniTask.Run.cs
public static UniTask RunOnThreadPool(Action action, bool configureAwait = true, CancellationToken cancellationToken = default);
public static UniTask<T> RunOnThreadPool<T>(Func<T> func, ...);
```

Equivalent to `Task.Run` but UniTask-native. **Throws `NotSupportedException` on WebGL.** Guard:

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
    result = ExpensiveComputation();
#else
    result = await UniTask.RunOnThreadPool(() => ExpensiveComputation(), cancellationToken: ct);
#endif
```

## Progress reporting with `Progress.Create<T>`

Source: `Progress.cs`:

```csharp
// Progress.cs:12
public static IProgress<T> Create<T>(Action<T> handler);

// Progress.cs:18 — default `comparer` uses UnityEqualityComparer.GetDefault<T>() (2018.3+) or EqualityComparer<T>.Default
public static IProgress<T> CreateOnlyValueChanged<T>(Action<T> handler, IEqualityComparer<T> comparer = null);
```

`CreateOnlyValueChanged` deduplicates consecutive equal values — handy for UI progress bars where you don't want to refresh every frame with the same value.

`handler == null` returns a `NullProgress<T>.Instance` singleton (no-op).

```csharp
var progress = Progress.CreateOnlyValueChanged<float>(p =>
    _slider.value = p);
await op.ToUniTask(progress: progress);
```

## Conversion checklist

- [ ] `AsyncOperation`-typed APIs use `.ToUniTask()` with explicit `cancellationToken` and optional `progress`.
- [ ] `UnityWebRequest` callers `try/catch UnityWebRequestException` for network errors.
- [ ] Coroutines converted to UniTask via `coroutine.ToUniTask()` are started with `StartCoroutine` on a live MonoBehaviour.
- [ ] `Task`-typed results use `.AsUniTask()` with the default `useCurrentSynchronizationContext: true` unless off-main-thread is required.
- [ ] `UniTask.RunOnThreadPool` / `UniTask.Run` is guarded with `#if !UNITY_WEBGL` on WebGL-targeted code.
- [ ] Progress callbacks use `Progress.Create` / `CreateOnlyValueChanged` rather than raw closures to avoid per-frame GC.

See [PITFALLS.md](./PITFALLS.md) for interop bugs (double-progress-callback, WebGL ThreadPool crash, etc.).
