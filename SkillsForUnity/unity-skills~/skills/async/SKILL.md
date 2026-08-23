---
name: unity-async
description: Advise on Unity async and lifecycle strategy
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Choosing Update/coroutine/UniTask/timer
- Handling cancellation and cleanup
- 在 Update/协程/UniTask/定时器间取舍、处理取消与清理

# Unity Async Strategy

> **Scope**: this module is about **async code you write into the Unity runtime** — `Update`, coroutines, UniTask, cancellation and lifecycle ownership. It is not about the REST protocol's own async jobs. For `jobId` / polling / `job_status` / `GET /jobs/{id}`, see [batch](../batch/SKILL.md).

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
