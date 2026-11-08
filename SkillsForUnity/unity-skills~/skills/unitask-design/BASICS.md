---
name: unity-unitask-basics
description: "UniTask struct semantics, UniTaskVoid, zero-alloc state machine, AsyncMethodBuilder, Preserve(), Status/UniTaskStatus — foundations that diverge from System.Threading.Tasks.Task."
type: reference
---

# UniTask Basics

Sub-doc of [unitask-design](./SKILL.md). Read alongside the source; every API shape below is backed by an explicit `file:line`.

## Why UniTask exists

`System.Threading.Tasks.Task` allocates per-await (state machine box + `Task` object + continuation wrapper). On Unity's single-threaded game loop, with thousands of async operations per frame, this allocation storm creates GC pauses. UniTask rewrites the async plumbing so that:

1. The task itself is a **struct** (`readonly partial struct UniTask` in `UniTask.cs:34`).
2. The continuation source is pooled (`IUniTaskSource` recycled through `StatePool`).
3. The async state machine uses `AsyncUniTaskMethodBuilder` (not `AsyncTaskMethodBuilder`) via the `[AsyncMethodBuilder(typeof(AsyncUniTaskMethodBuilder))]` attribute on `UniTask` — see `UniTask.cs:32`.

## Core types at a glance

| Type | Kind | Purpose | Source |
|------|------|---------|--------|
| `UniTask` | `readonly partial struct` | Zero-alloc awaitable, no result | `UniTask.cs:34` |
| `UniTask<T>` | `readonly partial struct` | Zero-alloc awaitable, result `T` | `UniTask.cs` (partial T file) |
| `UniTaskVoid` | `readonly struct` | Fire-and-forget, `async UniTaskVoid` lets you `await` inside without allocating a UniTask | `UniTaskVoid.cs:11-17` |
| `UniTaskCompletionSource` | `class` | Manual completion (like `TaskCompletionSource`) | `UniTaskCompletionSource.cs:573` |
| `UniTaskCompletionSource<T>` | `class` | Manual completion with result | `UniTaskCompletionSource.cs:754` |
| `IUniTaskSource` / `IUniTaskSource<T>` | interface | Internal backing source for pooling | `IUniTaskSource.cs` |
| `UniTaskStatus` | enum | `Pending / Succeeded / Faulted / Canceled` | `UniTask.cs` (partial) |

## `UniTask` vs `Task`

```csharp
// Task: allocates on every async call (state machine box + Task + continuation)
async Task<int> ComputeTask() => await Task.Yield().ContinueWith(_ => 42);

// UniTask: zero alloc in steady state
async UniTask<int> ComputeUniTask()
{
    await UniTask.Yield();
    return 42;
}
```

Qualitative comparison (verified from `UniTask.cs` / `UniTaskCompletionSource.cs` design, no benchmark numbers because the exact cost depends on runtime / platform / release build flags):

| API | `async Task<int>` | `async UniTask<int>` |
|-----|-------------------|----------------------|
| `Yield + return` | Allocates the Task state-machine box + continuation | Zero allocation in steady state (state-machine box reused via `AsyncUniTaskMethodBuilder`) |
| `Delay(10)` | Allocates a Task + `System.Threading.Timer` | Zero allocation — timer is a pooled `IUniTaskSource` on the PlayerLoop pump |
| `WhenAll(n tasks)` | Allocates `n` Task boxes + combined Task | Zero allocation via pooled `WhenAllPromise` (`UniTask.WhenAll.cs`) |

For real numbers on your target platform, profile with the Unity Profiler. UniTask ships a `UniTask Tracker` window (Window → UniTask Tracker) that lists every pending task.

## Struct trap: single-await semantics

A `UniTask` value wraps `(IUniTaskSource source, short token)`. Once awaited, the source is returned to the pool and the token becomes stale. Awaiting it again throws:

```
System.InvalidOperationException: Already continuation registered, can not await twice or get Status after await.
```

**How to detect**:
```csharp
var t = SomeAsync();       // creates UniTask with (source, token=42)
await t;                   // OK: source fetched for token 42, then recycled
await t;                   // THROWS: token 42 no longer valid, source reused for other task
```

**Fix 1** — `Preserve()` (memoize into a reusable wrapper):
```csharp
var t = SomeAsync().Preserve();
await t; // OK
await t; // OK — MemoizeSource re-serves the same result
```
Source: `UniTask.cs:103-113` — `public UniTask Preserve()` wraps `source` in `MemoizeSource`.

**Fix 2** — run each time:
```csharp
async UniTask<int> Compute() { await UniTask.Yield(); return 42; }
// call Compute() twice, not await the same returned UniTask twice
```

## `UniTaskVoid`: fire-and-forget that can `await`

```csharp
// ❌ Does not compile — async void cannot return UniTask
async void BadFireAndForget() { await UniTask.Delay(100); }

// ✅ Idiomatic fire-and-forget
async UniTaskVoid DoLater()
{
    await UniTask.Delay(100);
    Debug.Log("later");
}

void Start() => DoLater().Forget(); // must call Forget()
```

`UniTaskVoid` has a single method: `Forget()` (no-op) — `UniTaskVoid.cs:14-16`. Its purpose is to satisfy the C# async state machine contract while flagging intent.

## `Forget()`, `AttachExternalCancellation`, `SuppressCancellationThrow`

`.Forget()` takes an optional `UniTaskScheduler` delegate for custom unobserved-exception handling. Without it, exceptions surface on `UniTaskScheduler.UnobservedTaskException` (static event at `UniTaskScheduler.cs:13`).

```csharp
// Route exceptions through a custom handler
DoLater().Forget(ex => Debug.LogError($"Fire-and-forget failed: {ex}"));

// Attach an external CancellationToken (wraps the call in a cancelable UniTask)
await DoWorkAsync().AttachExternalCancellation(token);

// Do not throw OperationCanceledException on cancellation — return bool instead
var (isCanceled, result) = await DoWorkAsync().SuppressCancellationThrow();
```

## `UniTaskCompletionSource` — manual completion

Counterpart to `TaskCompletionSource`. Three setter methods per generic/non-generic flavor:

| Method | Returns | Source |
|--------|---------|--------|
| `TrySetResult()` / `TrySetResult(T)` | `bool` (false if already completed) | `UniTaskCompletionSource.cs:610,792` |
| `TrySetCanceled(CancellationToken = default)` | `bool` | `UniTaskCompletionSource.cs:616,801` |
| `TrySetException(Exception)` | `bool` | `UniTaskCompletionSource.cs:625,810` |

Once a setter returns `true`, subsequent setter calls return `false` — they never throw, so a defensive check is safe:

```csharp
var promise = new UniTaskCompletionSource<int>();
someEvent += result => promise.TrySetResult(result);
var value = await promise.Task;
```

## `UniTaskStatus`

Check status without awaiting:
```csharp
if (task.Status == UniTaskStatus.Succeeded) { /* done */ }
```
Values: `Pending / Succeeded / Faulted / Canceled`. `UniTask.Status` getter at `UniTask.cs:47-56` delegates to the backing `IUniTaskSource.GetStatus(token)`.

## Checklist when writing a new `async UniTask*` method

- [ ] Return type is `UniTask`, `UniTask<T>`, or `UniTaskVoid` — never `async void` with a UniTask body.
- [ ] The caller either `await`s the UniTask or calls `.Forget()` (lint with `UniTask Analyzer`).
- [ ] A `CancellationToken` is accepted as the LAST parameter (or first after `this`) when the method is long-running.
- [ ] The returned `UniTask` is NOT stored in a field and awaited twice — use `.Preserve()` if it must be reused.
- [ ] Any `CancellationTokenSource` created inside is disposed in `finally`.

See [PITFALLS.md](./PITFALLS.md) for the full list of things that trip up first-time UniTask users.
