---
name: unity-patterns
description: "Unity design pattern selector. Use when users want advice on ScriptableObject, events, state machines, object pools, observer, or other patterns. Triggers: design pattern, which pattern, ScriptableObject, event channel, observer, state machine, object pool, singleton, service locator, 设计模式, 用什么模式, 事件系统, 状态机, 对象池, 观察者模式, 该用什么模式."
---

# Unity Pattern Selector

Use this skill to decide whether a pattern is justified. Do not recommend every pattern at once.

## Guardrails

**Mode**: Documentation only — guidance for AI under all three operating modes; no REST skills to gate.

- Recommend at most 1-3 patterns, and explain why simpler options are not enough.

## Pattern Guide

- `ScriptableObject`
  - Use for authored config, shared static data, event channels, and reusable data assets.
  - Avoid as the default home for per-run mutable gameplay state.

- `C# events / delegates`
  - Use for one-to-many notifications with clear ownership and unsubscribe points.
  - Avoid for imperative flows that need ordering, return values, or complex debugging.

- `Global event bus / observer hub`
  - Use sparingly and only when many systems truly need broad decoupled notifications.
  - Avoid as the default answer to coupling. It often hides ownership and makes debugging harder.

- `Interfaces`
  - Use when multiple implementations or clearer dependency boundaries are needed.
  - Avoid adding interfaces around every class without a real seam.

- `State machine`
  - Use for actors with mutually exclusive states and explicit transitions.
  - Avoid when a few booleans or a small command flow is enough.

- `Object pool`
  - Use for frequent spawn/despawn of bullets, VFX, enemies, UI items.
  - Avoid for rare objects or when lifetime is simple.

- `Service layer`
  - Use for a small number of cross-scene systems with explicit bootstrap and interfaces.
  - Avoid turning everything into hidden singletons or service locators.

- `Generics / custom attributes`
  - Use when they remove repeated boilerplate with clear type safety or editor metadata value.
  - Avoid when they make gameplay code harder to read than duplicated simple code.

## Decision Lab: Same Goal, Multiple Implementations

The Pattern Guide above answers "which pattern?". For most non-trivial design decisions, the better exercise is: pick the 2-3 plausible implementations, put them side by side, and compare on switch cost, query cost, code complexity, and failure modes. Below is a worked example; use it as the shape for other decisions rather than as the answer.

### Worked example: "Pause AI on 100 enemies for a cutscene"

| Implementation | Switch cost (enter/exit cutscene) | Per-frame cost while paused | Code complexity | Failure modes |
|----------------|-----------------------------------|----------------------------|-----------------|---------------|
| **Field flag**: each `EnemyAI.Update` starts with `if (_paused) return;`; manager sets the flag | O(N) writes, no allocations | N branch tests + dispatch overhead per frame | Lowest — one bool, one guard clause | Silent bugs when a dev adds a new `Update` and forgets the guard |
| **`enabled = false`**: manager disables the `MonoBehaviour` on every enemy | O(N) writes, no allocations | Zero — Unity skips disabled `Behaviours` in its message list | Low, but state spread across components | OnDisable / OnEnable side effects (coroutines stop, listeners unsubscribe) may fire unexpectedly |
| **Subset list**: manager keeps `_active` / `_frozen` lists; no per-enemy Update — manager ticks only `_active` | O(N) list move on switch | Proportional to `_active` count only — perfect early-cull | Highest — one source of truth for ownership, requires managed ticking | Adding a new `Update` on enemies reintroduces per-frame cost; the list is easy to desync if other code respawns enemies directly |

### How to use

- Pick the **smallest** scale where the trade-off matters. At N=5 all three are indistinguishable; at N=5000 only the subset list stays flat.
- State what *gets worse* under each option, not only what improves. A table with only upsides is a sign you haven't thought about it yet.
- The ECS lesson is that the same three shapes (value change, structural change, enableable) show up everywhere — the labels transfer even when the framework does not. *Source: `Dots101/Entities101/Assets/HelloCube/13. StateChange/SetStateSystem.cs:42-80` — one system dispatches to three implementations behind `config.Mode`, each with measurably different cost in the profiler.*

### When to skip this exercise

If one option is obviously bounded (N ≤ 10, state changes once per session, or the code runs once at startup), a single paragraph "we picked X because it's simplest" is enough. Decision Lab is for choices that will outlive the current task.

## Output Format

- Recommended pattern(s)
- Why they fit this case
- Why not the simpler alternative
- Minimal implementation boundary
- Known tradeoffs
