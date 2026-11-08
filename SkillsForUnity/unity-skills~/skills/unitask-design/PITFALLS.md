---
name: unity-unitask-pitfalls
description: "30 concrete UniTask hallucination and runtime pitfalls — double await, forgotten Forget, wrong PlayerLoopTiming, WebGL ThreadPool crashes, tracker leaks, AsyncEnumerable subscription leaks, coroutine/tween bridge edge cases."
type: reference
---

# UniTask Pitfalls

Sub-doc of [unitask-design](./SKILL.md). Every item below is a real pattern that breaks in production. Read this before reviewing a PR that touches async code.

Format: ❌ wrong → ✅ right, with a short WHY.

---

### 1. Awaiting the same UniTask variable twice

```csharp
var t = LoadAsync();
await t;
await t; // ❌ InvalidOperationException: Already continuation registered
```

```csharp
var t = LoadAsync().Preserve();
await t;
await t; // ✅ Preserve memoizes the source
```

**Why**: `UniTask` is a struct wrapping `(IUniTaskSource, token)`. After the first await, the source is returned to a pool and the token is stale. Source: [UniTask.cs:34-113](./BASICS.md#struct-trap-single-await-semantics).

---

### 2. Returning `UniTask` and neither awaiting nor calling `.Forget()`

```csharp
void Start() { DoLater(); } // ❌ exception swallowed
async UniTask DoLater() { await UniTask.Delay(1000); throw new Exception(); }
```

```csharp
void Start() { DoLater().Forget(); } // ✅ exception goes to UnobservedTaskException
```

**Why**: `async UniTask` fire-and-forget silently drops results. `UniTask Analyzer` warns; make sure analyzers are enabled in CI.

---

### 3. Using `async void` instead of `async UniTaskVoid`

```csharp
async void Fire() { await UniTask.Yield(); } // ❌ exceptions are unobservable; harder to track
```

```csharp
async UniTaskVoid Fire() { await UniTask.Yield(); }
void Start() => Fire().Forget(); // ✅
```

**Why**: `async void` exceptions unwind on the SynchronizationContext — on Unity that's the main thread, but exceptions bypass UniTaskScheduler's handler.

---

### 4. Forgetting to pass `CancellationToken` through the call chain

```csharp
async UniTask Outer(CancellationToken ct)
{
    await Inner(); // ❌ Inner has no token — outlives cancellation
}
```

```csharp
async UniTask Outer(CancellationToken ct)
{
    await Inner(ct); // ✅
}
```

**Why**: UniTask does NOT auto-propagate cancellation. See [CANCELLATION.md](./CANCELLATION.md).

---

### 5. `this.GetCancellationTokenOnDestroy()` on a plain C# class

```csharp
public class Service // not a MonoBehaviour
{
    public async UniTask Run()
    {
        var ct = this.GetCancellationTokenOnDestroy(); // ❌ compile error
    }
}
```

```csharp
public class Service : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    public async UniTask Run() { await UniTask.Delay(1000, cancellationToken: _cts.Token); }
    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}
```

**Why**: The extension exists only for `MonoBehaviour / GameObject / Component` at `Triggers/AsyncTriggerExtensions.cs:14,22,28`.

---

### 6. `UniTask.Delay(0)` expecting same-frame yield

```csharp
await UniTask.Delay(0); // ❌ still passes through PlayerLoop — one frame delay
```

```csharp
await UniTask.Yield(); // ✅ explicit single yield
```

**Why**: `Delay(0)` allocates a NextFramePromise and pumps through PlayerLoop. Semantically close but not identical to `Yield`.

---

### 7. Wrong `PlayerLoopTiming` for physics work

```csharp
await UniTask.Yield(PlayerLoopTiming.Update);
rigidbody.AddForce(v); // ❌ applied in Update, not FixedUpdate — stutter
```

```csharp
await UniTask.Yield(PlayerLoopTiming.FixedUpdate);
rigidbody.AddForce(v); // ✅
```

**Why**: Physics integrates on `FixedUpdate`. Force applied in `Update` gets integrated on the NEXT FixedUpdate — fine for most cases but wrong if you're coordinating with multi-step physics.

---

### 8. `WaitForEndOfFrame` without `coroutineRunner` on Unity < 2023.1

```csharp
await UniTask.WaitForEndOfFrame(); // ❌ compile error on 2022.3
```

```csharp
await UniTask.WaitForEndOfFrame(this); // ✅ pre-2023.1
await UniTask.WaitForEndOfFrame();     // ✅ 2023.1+
```

**Why**: The parameterless overload is gated by `#if UNITY_2023_1_OR_NEWER` at `UniTask.Delay.cs:78-89`.

---

### 9. `UniTask.Run` / `SwitchToThreadPool` on WebGL

```csharp
await UniTask.RunOnThreadPool(() => Compute()); // ❌ NotSupportedException on WebGL
```

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
    var result = Compute();
#else
    var result = await UniTask.RunOnThreadPool(() => Compute());
#endif
```

**Why**: WebGL is single-threaded. Source: `UniTask.Threading.cs:57`.

---

### 10. Accessing Unity API after `SwitchToThreadPool`

```csharp
await UniTask.SwitchToThreadPool();
var pos = transform.position; // ❌ UnityException: get_position can only be called from the main thread
```

```csharp
await UniTask.SwitchToThreadPool();
var data = ExpensiveCompute();
await UniTask.SwitchToMainThread();
transform.position = data.Result; // ✅
```

---

### 11. Yielding a UniTask from a Coroutine

```csharp
IEnumerator Legacy()
{
    yield return SomeUniTask(); // ❌ Coroutine doesn't understand UniTask
}
```

```csharp
IEnumerator Legacy()
{
    yield return SomeUniTask().ToCoroutine();
}
```

**Why**: UniTask is not `IEnumerator` — must be adapted. See [CONVERSION.md](./CONVERSION.md).

---

### 12. `WaitUntil` with a predicate that never yields to PlayerLoop

```csharp
await UniTask.WaitUntil(() => _flag); // ❌ if _flag is flipped from async context on WebGL without yielding, deadlock
```

```csharp
await UniTask.WaitUntil(() => _flag);
// or ensure the setter runs on a PlayerLoop-pumped path
```

**Why**: `WaitUntil` polls on every `PlayerLoopTiming.Update`. WebGL has only one thread, so predicate + setter must share the pump.

---

### 13. Tree-shaking kills `Preserve()` users

```csharp
var t = LoadAsync();
await UniTask.WhenAll(Use1(t), Use2(t)); // ❌ each WhenAll takes ownership — second await fails
```

```csharp
var t = LoadAsync().Preserve();
await UniTask.WhenAll(Use1(t), Use2(t)); // ✅
```

---

### 14. `AsyncReactiveProperty` left undisposed

```csharp
var hp = new AsyncReactiveProperty<int>(100);
hp.ForEachAsync(v => _bar.value = v, ct).Forget();
// ❌ hp never disposed — subscribers reference leaked
```

```csharp
using var hp = new AsyncReactiveProperty<int>(100);
// ...
```

---

### 15. `.Subscribe()` return value ignored

```csharp
stream.Subscribe(x => Handle(x)); // ❌ runs until stream completes — may be never
```

```csharp
stream.Subscribe(x => Handle(x)).AddTo(ct); // ✅ disposes on cancel
```

---

### 16. `UniTaskTracker` window shipped in release builds

The `UniTask Tracker` editor window (Window → UniTask → Tracker) relies on `TaskTracker.cs` which wraps every UniTask in a linked-list entry. `TaskTracker.EnableTracking = false` by default in release — ensure it stays false and not toggled at runtime in shipped builds.

---

### 17. `UniTask.Void` with a method that throws before first `await`

```csharp
UniTask.Void(async () => { throw new Exception("boom"); }); // ❌ thrown synchronously, goes to UnobservedTaskException
```

`UniTask.Void` routes through `.Forget()`, so exceptions DO go through the scheduler's handler — but the caller is not notified. Use only for deliberately isolated work.

---

### 18. DOTween tween via `AsyncWaitForCompletion` instead of `.ToUniTask()`

```csharp
await tween.AsyncWaitForCompletion(); // ❌ returns Task — allocates
```

```csharp
await tween.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, ct); // ✅ UniTask-native
```

**Why**: `AsyncWaitForCompletion` lives in DOTween's Module file (`DOTweenModuleUnityVersion.cs:216`). The UniTask bridge is `External/DOTween/DOTweenAsyncExtensions.cs:54`.

---

### 19. `SendWebRequest().ToUniTask()` with `null` progress when caller expected progress

```csharp
await UnityWebRequest.Get(url).SendWebRequest().ToUniTask(); // ❌ no progress
```

```csharp
await UnityWebRequest.Get(url).SendWebRequest().ToUniTask(
    progress: Progress.Create<float>(p => _bar.value = p));
```

---

### 20. Mixing `try/finally` cleanup with cancellation

```csharp
try
{
    await UniTask.Delay(10000, cancellationToken: ct);
    File.WriteAllText(path, "done");
}
finally
{
    // ❌ runs on cancel too — writes partial state
    File.WriteAllText(path, "done"); // wrong copy!
}
```

Always check token state in finally:

```csharp
finally
{
    if (!ct.IsCancellationRequested) File.WriteAllText(path, "done");
}
```

---

### 21. Test using real `UniTask.Delay` blocks test runner

```csharp
[Test]
public async Task MyTest()
{
    await UniTask.Delay(5000); // ❌ actually waits 5s
}
```

```csharp
[Test]
public async Task MyTest()
{
    await UniTask.DelayFrame(1); // or use EditMode-friendly mock clock
}
```

---

### 22. PlayerLoop not registered after Enter Play Mode (fast mode)

With Enter Play Mode Options → Reload Domain: Off, UniTask's static state may not re-initialize. Source: `PlayerLoopHelper.cs` registers via `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`. Ensure this attribute is present on your custom PlayerLoop registrations too.

---

### 23. Custom `SynchronizationContext` fighting `UniTaskSynchronizationContext`

If your code calls `SynchronizationContext.SetSynchronizationContext(mine)` before UniTask initializes, UniTask's continuations may run on the wrong context. UniTask sets its own on startup — don't override it.

---

### 24. `async void` handler attached to `Button.onClick`

```csharp
button.onClick.AddListener(async () => await DoWork()); // ❌ async void semantics
```

```csharp
button.onClick.AddListener(() => DoWork().Forget());
// or
button.OnClickAsAsyncEnumerable().ForEachAsync(_ => DoWorkHandler(), ct).Forget();
```

---

### 25. Chain-`.Forget()`ing a UniTask you wanted to await

```csharp
await LoadAsync().Forget(); // ❌ .Forget returns void — compile error
```

```csharp
await LoadAsync(); // ✅
// or
LoadAsync().Forget(); // fire and forget
```

---

### 26. Deep `async UniTask` stack traces missing frames

UniTask's struct-based state machine trims stack traces more aggressively than `Task`. Enable `DEBUG_SYMBOLS` on the UniTask assembly in development builds to preserve more frames. Source: `Internal/DiagnosticsExtensions.cs`.

---

### 27. Addressables / YooAsset adapter version drift

`External/Addressables/AddressablesAsyncExtensions.cs:3` is gated by `#if UNITASK_ADDRESSABLE_SUPPORT` (singular `ADDRESSABLE`, NOT `ADDRESSABLES`). The asmdef Version Defines must pick up the Addressables package — if your project uses a forked or custom-named package, the define is not set and the extension disappears silently.

---

### 28. `GetCancellationTokenOnDestroy` vs `GetAsyncDestroyToken`

Both exist and return the same token in practice, but the `GetAsyncDestroyToken` naming appears in older docs. Use `GetCancellationTokenOnDestroy` — that's the name in current source at `AsyncTriggerExtensions.cs:14`.

---

### 29. Using UniTask inside a Job System `IJob`

```csharp
public struct MyJob : IJob
{
    public async UniTask Execute() { ... } // ❌ Burst / Jobs require pure struct, no async state machine
}
```

Jobs must be synchronous, unmanaged-compatible structs. UniTask is a managed async primitive — incompatible. Bridge via a MonoBehaviour that schedules jobs and awaits `JobHandle.Complete()`.

---

### 30. `UniTask.FromException` fires UnobservedTaskException handler

```csharp
UniTask.FromException(ex).Forget(); // ❌ still routes to UnobservedTaskException because no one awaited
```

If you want exception handling, await or pass a handler to `Forget(ex => ...)`.

---

## How to use this list

- **Code review**: Open a PR diff and scan for any of these patterns.
- **CI**: Configure the `UniTask.Analyzer` Roslyn analyzer to catch #2, #3, #24 automatically.
- **Onboarding**: New team members read this list once before writing their first `async UniTask` method.

When one of these bites you anyway, add the reproduction to your project's `docs/async-incidents.md` with a link back to the rule above.
