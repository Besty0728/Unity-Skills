---
name: unity-architecture
description: Advises on Unity gameplay and system architecture — module boundaries, decoupling, scene composition, SOLID structure, refactor direction. Use when planning how to organize code, splitting responsibilities, reducing coupling, or choosing a refactor direction, before writing structural code. 为 Unity 游戏与系统架构提供建议(模块边界、解耦、场景组织、SOLID、重构方向);当用户要规划代码结构、划分职责、降低耦合或决定重构方向时使用。
---

# Unity Architecture Advisor

Use this before generating lots of gameplay scripts or when the user asks for a cleaner architecture.

## Workflow

1. Identify scope: prototype, small game, or long-lived project.
2. Define the core loop and the minimum runtime systems needed.
3. Recommend the smallest architecture that fits the scope.
4. Separate:
   - scene/bootstrap layer
   - gameplay/domain logic
   - data/config assets
   - view/presentation layer
5. Call out what should stay simple now vs what is worth abstracting.

## Output Format

When using this skill, structure the advice as:

- Project tier: prototype / small-game / long-lived
- Recommended modules: 3-7 modules with one-line responsibilities
- Scene/bootstrap plan: where composition and initialization happen
- Data ownership: what belongs in scene objects, ScriptableObjects, or pure C# classes
- Communication rules: direct refs, interfaces, events, or commands
- Performance risks: only the hot paths that matter
- Do now / skip now: avoid over-engineering

## Default Guidance

- Prefer thin `MonoBehaviour` scripts as composition bridges.
- Put reusable gameplay rules in plain C# classes when possible.
- Use `ScriptableObject` for authored config and shared static data, not as a default dump for runtime state.
- Keep dependencies explicit. Avoid hidden global state unless the project size clearly justifies a small service layer.
- Favor simple module boundaries over framework-heavy architecture.

## Explicit Execution Order and Entry Guards

Most "random" gameplay bugs trace back to two silent assumptions: that scripts run in a predictable order, and that `Update` runs only when its data is ready. Neither is true unless you make them so.

### Make startup order explicit

Do not rely on Script Execution Order panels or accidental `Awake` ordering. Prefer these patterns in order of preference:

- **One Bootstrap script** as the single entry point. It owns the initialization sequence and calls `sub.Init()` on the managers it creates. Only this one script has `[DefaultExecutionOrder(-10000)]`.
- **Pull, don't push**: a subscriber that needs data calls `source.GetValue()` on demand or subscribes to an event. A publisher that pushes into others during its own `Awake` creates hidden order dependencies.
- **Reserve `[DefaultExecutionOrder(n)]`** for load-bearing singletons only (Bootstrap, InputRouter, SceneController). If more than 3-4 scripts need it, the architecture is wrong, not the ordering.

The analogous ECS pattern — `[UpdateBefore(typeof(BarSystem))]` / `[UpdateAfter]` — works because the framework validates contradictions at sort time. In MonoBehaviour code the compiler won't catch them; be conservative. *Source: `EntitiesSamples/Docs/systems.md:32` — "if the ordering attributes of a group's children create a contradiction, an exception is thrown".*

### Make update preconditions explicit

Every `Update` / `LateUpdate` / `FixedUpdate` should open with guard clauses that early-return when the system is not ready. Prefer the cheapest check first:
```csharp
void Update() {
    if (!_isInitialized) return;       // construction-time
    if (_dataSource == null) return;   // dependency-level
    if (_paused) return;               // gameplay-state
    // real work here
}
```
A missing guard is the difference between "doesn't run yet" (safe) and "runs with stale/null data and silently corrupts state" (debug nightmare). The ECS equivalent is `state.RequireForUpdate<Config>()` in `OnCreate`, which turns the precondition into a system-level invariant. *Source: `Dots101/Entities101/Assets/HelloCube/3. Prefabs/SpawnSystem.cs:17-19` — the system does not update unless a `Spawner` entity exists.*

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Do not start from a giant reusable framework unless the project truly needs it.
- Do not add layers just to satisfy textbook SOLID wording.
- Prefer a small architecture that can grow, not an impressive one that slows iteration.

## Load Related Advisory Modules When Needed

- Pattern choice: see [`../patterns/SKILL.md`](../patterns/SKILL.md)
- Async / Update / UniTask decisions: see [`../async/SKILL.md`](../async/SKILL.md)
- Inspector-facing field design: see [`../inspector/SKILL.md`](../inspector/SKILL.md)
- Script quality review: see [`../scriptdesign/SKILL.md`](../scriptdesign/SKILL.md)
