---
name: unity-patterns
description: Advise on choosing Unity design patterns
---

# Unity Pattern Selector

Documentation only: no REST skills. Use to decide whether a pattern is justified. Do not recommend every pattern at once.

## When to use
- Deciding which pattern fits a problem
- Structuring decoupled systems
- Choosing event, state-machine or pool approaches

## Pattern guide
- `ScriptableObject`
  - Use for authored config, shared static data, event channels, and reusable data assets.
  - Avoid as the default home for per-run mutable gameplay state.
- `C# events / delegates`
  - Use for one-to-many notifications with clear ownership and unsubscribe points.
  - Avoid for imperative flows that need ordering, return values, or complex debugging.
- `Global event bus / observer hub`
  - Use sparingly, only when many systems truly need broad decoupled notifications.
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

## Decision lab: same goal, multiple implementations
For a non-trivial design decision, pick the 2-3 plausible implementations, put them side by side, and compare switch cost, query cost, code complexity and failure modes. Worked example below; use it as the shape for other decisions, not as the answer.

### Worked example: "Pause AI on 100 enemies for a cutscene"

| Implementation | Switch cost (enter/exit cutscene) | Per-frame cost while paused | Code complexity | Failure modes |
|----------------|-----------------------------------|----------------------------|-----------------|---------------|
| **Field flag**: each `EnemyAI.Update` starts with `if (_paused) return;`; manager sets the flag | O(N) writes, no allocations | N branch tests + dispatch overhead per frame | Lowest: one bool, one guard clause | Silent bugs when a dev adds a new `Update` and forgets the guard |
| **`enabled = false`**: manager disables the `MonoBehaviour` on every enemy | O(N) writes, no allocations | Zero: Unity skips disabled `Behaviours` in its message list | Low, but state spread across components | OnDisable / OnEnable side effects (coroutines stop, listeners unsubscribe) may fire unexpectedly |
| **Subset list**: manager keeps `_active` / `_frozen` lists; no per-enemy Update, manager ticks only `_active` | O(N) list move on switch | Proportional to `_active` count only: early cull | Highest: one source of truth for ownership, requires managed ticking | Adding a new `Update` on enemies reintroduces per-frame cost; the list desyncs easily if other code respawns enemies directly |

How to use:
- Pick the **smallest** scale where the trade-off matters. At N=5 all three are indistinguishable; at N=5000 only the subset list stays flat.
- State what *gets worse* under each option, not only what improves.
- The same three shapes (value change, structural change, enableable) recur in ECS; the labels transfer even when the framework does not. *Source: `Dots101/Entities101/Assets/HelloCube/13. StateChange/SetStateSystem.cs:42-80`.*

Skip this exercise when one option is obviously bounded (N <= 10, state changes once per session, or the code runs once at startup). A single sentence saying "we picked X because it's simplest" is enough.

## Output format
- Recommended pattern(s), at most 1-3
- Why they fit this case
- Why not the simpler alternative
- Minimal implementation boundary
- Known tradeoffs

## Pitfalls
- Explain why simpler options are not enough before recommending a pattern.
