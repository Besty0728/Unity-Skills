# Unity Script Design Review

Documentation only: no REST skills. Use before creating gameplay scripts, or after scripts are generated and need a design pass.

## When to use
- Reviewing code quality
- Untangling tightly coupled scripts
- Planning a refactor for maintainability

## Review checklist
- Responsibility: does the script have one clear job?
- Role: should it be a `MonoBehaviour`, `ScriptableObject`, or plain C# class?
- Coupling: are dependencies explicit instead of hidden globals or deep scene lookups?
- Communication: direct reference, interface call, or event?
- Performance: unnecessary `Update`, repeated `Find`, avoidable allocation, or reflection in hot paths?
- Lifecycle: are subscriptions, timers, and async work cleaned up clearly?
- Inspector UX: are serialized fields private, grouped, and explained?
- Testability: can the core logic move into a plain C# class?
- Naming: do class and field names explain intent without cryptic abbreviations?

## Data lifecycle boundary
The checklist asks where a *class* lives. Ask the same question of every *field*. Each piece of state has one of three lifecycles, and putting a field on the wrong one causes most "it broke when the designer tweaked a value" and "unit tests are flaky" reports.

| Lifecycle | When the value is decided | Where it belongs | Typical idiom |
|-----------|--------------------------|------------------|---------------|
| **Authoring-time** | By a designer in the Editor, before Play | `ScriptableObject` asset, or `[SerializeField] private` on a prefab | Immutable at runtime; read via `_config.Speed` |
| **Composition-time** | Once per scene/instance, at `Awake`/`Start` | `private` field, assigned from `GetComponent` / `GetComponentInChildren` / ctor arg | Cached reference, no per-frame lookup |
| **Runtime-mutable** | Every frame or on gameplay events | `private` backing field + `public` read-only property + event | `public float Health { get; private set; }` + `OnHealthChanged` |

Typical assignments:
- Weapon damage / fire rate / clip size: authoring-time (ScriptableObject, so balance can be hot-swapped).
- Enemy AI's current target `Transform`: composition-time if set once at spawn, runtime-mutable if re-targeted each frame.
- Player current HP: runtime-mutable with an event. Never `public float hp;`.
- Reference to `Rigidbody` or `Animator` on the same GameObject: composition-time, cached in `Awake`.
- Level music track: authoring-time via a ScriptableObject level descriptor.
- "Is in combat" flag: runtime-mutable, but usually derived from other state, so review whether it should be a field at all.

Why the separation matters: mixing the lifecycles turns a clean class into a god object. A `MonoBehaviour` whose `public float speed` is edited by both the Inspector and a power-up script has two owners and no invariant. The ECS baking pipeline makes this a hard boundary (Authoring -> Baker -> System), and the discipline transfers: if you would not mix an Authoring component with runtime write-back in ECS, do not mix them in a MonoBehaviour. *Source: `EntitiesSamples/Docs/baking.md:5-16`.*

## Output format
- Keep: what is already good
- Simplify: what should stay straightforward
- Refactor: the highest-value structural change
- Performance notes: only real hotspots, not theoretical micro-optimizations
- Maintainability notes: naming, ownership, coupling, editor usability

## Pitfalls
- Prefer descriptive names over local shorthand.
- Do not "optimize" readability away for imagined productivity gains.
- Do not recommend complex patterns when a smaller refactor fixes the real problem.
