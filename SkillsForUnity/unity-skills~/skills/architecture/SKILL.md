---
name: unity-architecture
description: Advise on Unity gameplay and system architecture
---

# Unity Architecture Advisor

Documentation only: no REST skills. Use before generating many gameplay scripts or when the user asks for a cleaner architecture.

## When to use
- Planning code organization
- Splitting responsibilities
- Reducing coupling
- Choosing a refactor direction

## Workflow
1. Identify scope: prototype, small game, or long-lived project.
2. Define the core loop and the minimum runtime systems needed.
3. Recommend the smallest architecture that fits the scope.
4. Separate: scene/bootstrap layer, gameplay/domain logic, data/config assets, view/presentation layer.
5. Call out what should stay simple now and what is worth abstracting.

## Output format
- Project tier: prototype / small-game / long-lived
- Recommended modules: 3-7, each with a one-line responsibility
- Scene/bootstrap plan: where composition and initialization happen
- Data ownership: what belongs in scene objects, ScriptableObjects, or pure C# classes
- Communication rules: direct refs, interfaces, events, or commands
- Performance risks: only the hot paths that matter
- Do now / skip now

## Default guidance
- Prefer thin `MonoBehaviour` scripts as composition bridges.
- Put reusable gameplay rules in plain C# classes when possible.
- Use `ScriptableObject` for authored config and shared static data, not as a default dump for runtime state.
- Keep dependencies explicit. Avoid hidden global state unless project size justifies a small service layer.
- Favor simple module boundaries over framework-heavy architecture.

## Explicit execution order and entry guards
Most "random" gameplay bugs trace back to two silent assumptions: that scripts run in a predictable order, and that `Update` runs only when its data is ready. Neither holds unless you make it so.

### Make startup order explicit
Do not rely on Script Execution Order panels or accidental `Awake` ordering. In order of preference:
- **One Bootstrap script** as the single entry point. It owns the initialization sequence and calls `sub.Init()` on the managers it creates. Only this script has `[DefaultExecutionOrder(-10000)]`.
- **Pull, don't push**: a subscriber that needs data calls `source.GetValue()` on demand or subscribes to an event. A publisher that pushes into others during its own `Awake` creates hidden order dependencies.
- **Reserve `[DefaultExecutionOrder(n)]`** for load-bearing singletons only (Bootstrap, InputRouter, SceneController). If more than 3-4 scripts need it, the architecture is wrong, not the ordering.

The ECS analogue, `[UpdateBefore(typeof(BarSystem))]` / `[UpdateAfter]`, works because the framework validates contradictions at sort time. MonoBehaviour code gets no such check, so be conservative. *Source: `EntitiesSamples/Docs/systems.md:32`: "if the ordering attributes of a group's children create a contradiction, an exception is thrown".*

### Make update preconditions explicit
Every `Update` / `LateUpdate` / `FixedUpdate` should open with guard clauses that return early when the system is not ready. Cheapest check first:
```csharp
void Update() {
    if (!_isInitialized) return;       // construction-time
    if (_dataSource == null) return;   // dependency-level
    if (_paused) return;               // gameplay-state
    // real work here
}
```
A missing guard separates "doesn't run yet" (safe) from "runs with stale or null data and silently corrupts state" (a debugging nightmare). The ECS equivalent is `state.RequireForUpdate<Config>()` in `OnCreate`, which turns the precondition into a system-level invariant. *Source: `Dots101/Entities101/Assets/HelloCube/3. Prefabs/SpawnSystem.cs:17-19`.*

## Pitfalls
- Do not start from a giant reusable framework unless the project truly needs it.
- Do not add layers just to satisfy textbook SOLID wording.
- Prefer a small architecture that can grow over an impressive one that slows iteration.

## Related advisory modules
- Pattern choice: [patterns](../patterns/SKILL.md)
- Async / Update / UniTask decisions: [async](../async/SKILL.md)
- Inspector-facing field design: [inspector](../inspector/SKILL.md)
- Script quality review: [scriptdesign](../scriptdesign/SKILL.md)
