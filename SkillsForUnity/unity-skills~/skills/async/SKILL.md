---
name: unity-async
description: Advises on Unity async and lifecycle strategy — choosing among Update, coroutines, UniTask, and timers, plus cleanup and cancellation. Use when deciding how to write async code, choosing between coroutine and UniTask, scheduling timers, or handling cancellation and cleanup, even if the user just asks "异步怎么写" or "用协程还是UniTask". 为 Unity 异步与生命周期策略提供建议(在 Update、协程、UniTask、定时器间取舍,以及清理与取消);当用户要决定异步代码怎么写、在协程与 UniTask 间选择、调度定时器或处理取消与清理时使用。
---

# Unity Async Strategy

Use this skill when the user is deciding how runtime work should be scheduled or cleaned up.

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

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
