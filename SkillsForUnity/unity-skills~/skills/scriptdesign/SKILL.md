---
name: unity-scriptdesign
description: Advises on Unity gameplay script quality — code review, reducing coupling, improving maintainability, and refactoring. Use when reviewing code quality, untangling tightly-coupled scripts, or planning a refactor for maintainability, even if the user just says "看看我代码" or "代码有点乱". 为 Unity 游戏脚本质量提供建议(代码审查、降低耦合、提升可维护性、重构);当用户要审查代码质量、理顺高耦合脚本、或为可维护性规划重构时使用。
---

# Unity Script Design Review

Use this skill before creating gameplay scripts, or after scripts are generated and need a design pass.

## Review Checklist

- Responsibility: does the script have one clear job?
- Role: should it really be a `MonoBehaviour`, `ScriptableObject`, or plain C# class?
- Coupling: are dependencies explicit instead of hidden globals or deep scene lookups?
- Communication: should this be a direct reference, interface call, or event?
- Performance: is there unnecessary `Update`, repeated `Find`, avoidable allocation, or reflection in hot paths?
- Lifecycle: are subscriptions, timers, and async work cleaned up clearly?
- Inspector UX: are serialized fields private, grouped, and explained?
- Testability: can the core logic move into a plain C# class?
- Naming: do class and field names explain intent without cryptic abbreviations?

## Data Lifecycle Boundary

The Review Checklist above asks "where does this *class* live". Ask the same question for every *field*. Every piece of state has one of three lifecycles, and putting a field on the wrong one is the most common cause of "why did this break when the designer tweaked a value" and "why are my unit tests flaky".

| Lifecycle | When the value is decided | Where it belongs | Typical idiom |
|-----------|--------------------------|------------------|---------------|
| **Authoring-time** | By a designer in the Editor, before Play | `ScriptableObject` asset, or `[SerializeField] private` on a prefab | Immutable at runtime; read via `_config.Speed` |
| **Composition-time** | Once per scene/instance, at `Awake`/`Start` | `private` field, assigned from `GetComponent` / `GetComponentInChildren` / ctor arg | Cached reference, no per-frame lookup |
| **Runtime-mutable** | Every frame or on gameplay events | `private` backing field + `public` read-only property + event | Exposed via `public float Health { get; private set; }` + `OnHealthChanged` |

### Typical assignments

- Weapon damage / fire rate / clip size → **Authoring-time** (ScriptableObject so balance can be hot-swapped).
- Enemy AI's current target `Transform` → **Composition-time** if set once at spawn, **Runtime-mutable** if re-targeted each frame.
- Player current HP → **Runtime-mutable** with event. Never `public float hp;`.
- Reference to `Rigidbody`/`Animator` on the same GameObject → **Composition-time**, cached in `Awake`.
- Level music track → **Authoring-time** via ScriptableObject level descriptor.
- "Is in combat" flag → **Runtime-mutable**, but usually derived from other state — review whether it should be a field at all.

### Why the separation matters

Mixing the three lifecycles is what turns a clean class into a god object. A `MonoBehaviour` whose `public float speed` is edited by both the Inspector **and** a power-up script has two owners and no invariant; a bug in either path corrupts the other. The ECS baking pipeline makes this distinction a hard architectural boundary (Authoring → Baker → System), and the discipline transfers directly: if you would not mix an Authoring component with runtime write-back in ECS, do not mix them in a MonoBehaviour either. *Source: `EntitiesSamples/Docs/baking.md:5-16`.*

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Prefer descriptive names over local shorthand.
- Do not “optimize” readability away for imagined productivity gains.
- Do not recommend complex patterns if a smaller refactor fixes the real problem.

## Output Format

- Keep: what is already good
- Simplify: what should stay straightforward
- Refactor: the highest-value structural change
- Performance notes: only real hotspots, not theoretical micro-optimizations
- Maintainability notes: naming, ownership, coupling, editor usability
