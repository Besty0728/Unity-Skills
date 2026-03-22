---
name: unity-async
description: "Unity async and lifecycle strategy advisor. Use when users want to choose between Update, coroutine, UniTask, timers, or handle cleanup/cancellation. Triggers: async, coroutine, UniTask, await, Update vs coroutine, timer, lifecycle, IDisposable, cancellation, 异步, 协程, 生命周期, 用协程还是UniTask, 异步怎么写, 定时器, 取消操作."
---

# Unity Async Strategy

Use this skill when the user is deciding how runtime work should be scheduled or cleaned up.

## Guardrails

**Mode**: Both (Semi-Auto + Full-Auto) — advisory only, no REST skills

- Do not recommend `UniTask` just because it looks more advanced than coroutine.
- Prefer the simplest scheduling model that fits the use case.

## Decision Ladder

1. First ask whether the task needs per-frame work at all.
2. If not, prefer events, callbacks, or explicit method calls.
3. If a short Unity-bound sequence is needed, prefer coroutine.
4. Recommend `UniTask` only when:
   - the project already uses it, or
   - the user explicitly wants it and accepts the dependency.
5. Use `Update` only for true continuous simulation, polling, or input loops that cannot be event-driven.

## Specific Guidance

- Avoid many unrelated `Update` methods if a more event-driven flow works.
- Cache references used in hot paths.
- Always define lifecycle ownership:
  - who starts the work
  - who cancels or stops it
  - when it is cleaned up
- In `MonoBehaviour`, prefer `OnEnable` / `OnDisable` / `OnDestroy` for subscribe-unsubscribe symmetry.
- Use `IDisposable` mainly for pure C# lifetimes, temporary subscriptions, or scope-based cleanup helpers, not as a cargo-cult replacement for Unity lifecycle methods.

## Output Format

- Recommended scheduling model
- Why it fits
- Lifecycle / cancellation owner
- Hot-path risks
- Why the heavier alternative is unnecessary, if applicable
