---
name: unity-async
description: Advise on Unity async and lifecycle strategy
---

# Unity Async Strategy

Documentation only: no REST skills.

Scope: this module covers async code written into the Unity runtime (`Update`, coroutines, UniTask, cancellation, lifecycle ownership). It does not cover the REST protocol's own async jobs; for `jobId` polling see [batch](../batch/SKILL.md).

## When to use
- Deciding how runtime work should be scheduled or cleaned up

## Decision ladder
1. Ask first whether the task needs per-frame work at all.
2. If not, prefer events, callbacks, or explicit method calls.
3. For a short Unity-bound sequence, prefer a coroutine.
4. Recommend `UniTask` only when the project already uses it, or the user explicitly wants it and accepts the dependency.
5. Use `Update` only for true continuous simulation, polling, or input loops that cannot be event-driven.

## Specific guidance
- Avoid many unrelated `Update` methods when an event-driven flow works.
- Cache references used in hot paths.
- Always define lifecycle ownership: who starts the work, who cancels or stops it, when it is cleaned up.
- In `MonoBehaviour`, prefer `OnEnable` / `OnDisable` / `OnDestroy` for subscribe/unsubscribe symmetry.
- Use `IDisposable` mainly for pure C# lifetimes, temporary subscriptions, or scope-based cleanup, not as a replacement for Unity lifecycle methods.

## Output format
- Recommended scheduling model
- Why it fits
- Lifecycle / cancellation owner
- Hot-path risks
- Why the heavier alternative is unnecessary, if applicable

## Pitfalls
- Do not recommend `UniTask` just because it looks more advanced than a coroutine.
- Prefer the simplest scheduling model that fits the use case.
