---
name: unity-unitask-triggers
description: "AsyncTriggerBase and MonoBehaviour lifecycle-to-UniTask extensions — GetAsyncStartTrigger / OnDestroyAsync / OnCollisionEnterAsync / OnClickAsync / OnTriggerEnterAsync, AsyncTriggerExtensions, GetCancellationTokenOnDestroy internals."
type: reference
---

# UniTask Triggers

Sub-doc of [unitask-design](./SKILL.md). Triggers let you `await` Unity lifecycle events and UI callbacks as UniTasks / AsyncEnumerables. Source lives in `Runtime/Triggers/*.cs`.

## Core types

| Type | Purpose | Source |
|------|---------|--------|
| `AsyncTriggerBase<T>` | Abstract base: MonoBehaviour that holds pending awaiters and signals them | `AsyncTriggerBase.cs` |
| `AsyncAwakeTrigger` | Fires once on `Awake` | `AsyncAwakeTrigger.cs` |
| `AsyncStartTrigger` | Fires once on `Start` | `AsyncStartTrigger.cs` |
| `AsyncDestroyTrigger` | Fires on `OnDestroy`; used by `GetCancellationTokenOnDestroy` | `AsyncDestroyTrigger.cs` |
| `MonoBehaviourMessagesTriggers` (partial) | Update / LateUpdate / FixedUpdate / OnEnable / OnDisable / OnCollision* / OnTrigger* / OnMouse* etc. | `MonoBehaviourMessagesTriggers.cs` |
| `AsyncTriggerExtensions` | Static extensions exposing `GetAsync*Trigger()` on MonoBehaviour / GameObject / Component | `AsyncTriggerExtensions.cs` |

## Pattern 1 — `GetCancellationTokenOnDestroy`

Simplest usage. The extension lazy-adds an `AsyncDestroyTrigger` and returns a `CancellationToken`:

```csharp
// AsyncTriggerExtensions.cs:14,22,28
public static CancellationToken GetCancellationTokenOnDestroy(this MonoBehaviour monoBehaviour);
public static CancellationToken GetCancellationTokenOnDestroy(this GameObject gameObject);
public static CancellationToken GetCancellationTokenOnDestroy(this Component component);
```

```csharp
public class Enemy : MonoBehaviour
{
    async UniTaskVoid Start()
    {
        await WanderAsync(this.GetCancellationTokenOnDestroy());
    }
}
```

Destroy the GameObject → token fires → `WanderAsync` sees `OperationCanceledException` and tears down cleanly.

## Pattern 2 — `OnDestroyAsync` (awaitable lifecycle)

```csharp
await this.OnDestroyAsync();
```

Completes when the current MonoBehaviour is destroyed. Useful for cleanup fibers:

```csharp
async UniTaskVoid ManageResource()
{
    var resource = Allocate();
    await this.OnDestroyAsync();
    resource.Release(); // runs after OnDestroy
}
```

## Pattern 3 — `GetAsync*Trigger()` + `OnCollisionEnterAsync` etc.

Each lifecycle message has a dedicated trigger accessor. `MonoBehaviourMessagesTriggers.cs` provides:

- `GetAsyncUpdateTrigger()` → `IUniTaskAsyncEnumerable<AsyncUnit>` ticking every Update
- `GetAsyncLateUpdateTrigger()`
- `GetAsyncFixedUpdateTrigger()`
- `GetAsyncEnableTrigger()`, `GetAsyncDisableTrigger()`
- `GetAsyncCollisionEnterTrigger()` → `IUniTaskAsyncEnumerable<Collision>`
- `GetAsyncCollisionStayTrigger()`, `GetAsyncCollisionExitTrigger()`
- `GetAsyncTriggerEnterTrigger()` → `IUniTaskAsyncEnumerable<Collider>`
- `GetAsyncTriggerStayTrigger()`, `GetAsyncTriggerExitTrigger()`
- Same for 2D: `GetAsyncCollision2DEnterTrigger()`, `GetAsyncTrigger2DEnterTrigger()`, etc.
- `GetAsyncMouseDownTrigger()`, `GetAsyncMouseUpTrigger()`, etc.

Plus convenience single-shot awaits:
- `await this.GetAsyncCollisionEnterTrigger().OnCollisionEnterAsync();` — fires once per call
- Or consume as stream:
  ```csharp
  await foreach (var collision in this.GetAsyncCollisionEnterTrigger().WithCancellation(ct))
  {
      HandleHit(collision);
  }
  ```

## Pattern 4 — UI events (uGUI)

`UnityAsyncExtensions.uGUI.cs` adds async sugar for UI controls:

- `button.OnClickAsync()` — awaits a single click
- `toggle.OnValueChangedAsync()` → `UniTask<bool>`
- `slider.OnValueChangedAsync()` → `UniTask<float>`
- `inputField.OnValueChangedAsync()` → `UniTask<string>`

Stream variants via `OnClickAsAsyncEnumerable` etc.

```csharp
// Wait for the next click
await button.OnClickAsync(ct);

// Process every click
await foreach (var _ in button.OnClickAsAsyncEnumerable().WithCancellation(ct))
{
    HandleClick();
}
```

## How triggers manage lifetime

Each `AsyncTriggerBase<T>` derivative is a `MonoBehaviour` added as a **hidden component** (`hideFlags = HideFlags.DontSave`) to the target GameObject on first access. It's re-used across subsequent calls. On `OnDestroy`, the trigger cancels all pending awaiters and tears down its enumerator registry.

**Implication**: `GetAsync*Trigger()` lazy-adds a component. If you never destroy the GameObject, the trigger stays around. Normally harmless, but if you're profiling component counts, expect a `AsyncDestroyTrigger` / `AsyncCollisionEnterTrigger` etc. on any GO that uses UniTask triggers.

## Null checks and fake-null

`Component`-typed extensions handle Unity's fake-null semantics gracefully. Calling `GetCancellationTokenOnDestroy()` on a destroyed GameObject returns an already-canceled token (does not throw).

## Triggers checklist

- [ ] Lifecycle cancellation for MonoBehaviours uses `this.GetCancellationTokenOnDestroy()` rather than a hand-rolled CTS.
- [ ] Plain C# classes create and dispose their own `CancellationTokenSource`.
- [ ] `OnClickAsync` / `OnCollisionEnterAsync` single-shot awaits accept a cancellation token.
- [ ] Stream-style trigger consumption uses `.WithCancellation(ct)` on the AsyncEnumerable.
- [ ] Triggers on pooled GameObjects are reused safely across activate/deactivate cycles (they re-bind when the GO is active).

See [PITFALLS.md](./PITFALLS.md) for trigger-related bugs (stale triggers after pooling, fake-null edge cases).
