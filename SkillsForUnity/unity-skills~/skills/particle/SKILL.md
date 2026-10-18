---
name: unity-particle
description: Create, inspect, configure and diagnose built-in Unity Particle Systems (Shuriken) - typed module patches, renderer, bursts, sub emitters, static analysis, batch and rollback. Not VFX Graph.
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Creating or tuning particle effects: fire, smoke, sparks, rain, snow, explosions, dust
- Changing emission rate, lifetime, size, colour or shape of a ParticleSystem; bursts; renderer material and sorting
- Adding motion or variation: forces, speed limit and drag, spin, orbiting, inherited emitter velocity, size / colour / rotation by speed, per-particle custom data for shaders
- Batch-editing many particle systems (lower the cost of all rain emitters, switch smoke to world space)
- Chaining systems with sub emitters (firework: rocket -> explosion -> sparks); finding why particles are invisible, pink or expensive
- 创建或调整粒子特效（火焰、烟雾、火花、雨雪、爆炸），修改发射率、生命周期、大小、颜色、形状、爆发与渲染器材质；子发射器；诊断粒子看不见或显示粉色的原因

# Unity Particle Skills

Built-in `ParticleSystem` (Shuriken) components in open scenes and prefab instances. **Visual Effect Graph is a different system and is out of scope**: none of these skills touches a `VisualEffect` or a `.vfx` asset.

> **BATCH-FIRST**: use the `*_batch` skills for 2+ systems; they validate every item first and are all-or-nothing.

## Operating Mode

- Reads (`particle_get_capabilities`, `particle_find_all`, `particle_get_info`, `particle_get_info_batch`, `particle_get_module`, `particle_get_renderer`, `particle_get_bursts`, `particle_get_sub_emitters`, `particle_analyze`) are `SemiAuto`: they run in all three modes without a grant.
- Writes (`particle_create*`, `particle_add_system*`, `particle_configure*`, `particle_set_renderer*`, `particle_set_bursts*`, `particle_set_sub_emitters*`) are `FullAuto`, risk `medium`, workflow-tracked: Approval needs a grant; Auto / Bypass run them directly.
- No skill here enters Play Mode, plays, simulates or deletes. Playback control is a later phase; to remove a system use `component_remove` (componentType `ParticleSystem`) or `gameobject_delete`.

## Guardrails

**DO NOT** (common hallucinations):
- `particle_set_emission_rate` / `particle_set_color` / `particle_set_size` do not exist → one `particle_configure` call patches any modules at once.
- Do not send module fields as top-level parameters → they go inside `configJson.modules.<module>`.
- Do not guess field names, units or ranges → `particle_get_capabilities` (with `module=main`, `renderer`, ...) lists them; `particle_get_module` shows the current values in the exact input shape.
- `main.startRotation*` are **radians**; `rotationOverLifetime.*` and `rotationBySpeed.*` are **radians per second** (the Inspector shows degrees for all of these); `velocityOverLifetime.orbitalX/Y/Z` are **radians per second** (shown as is); `shape.rotation`, `shape.angle`, `shape.arc` and `noise.rotationAmount` (per second) are **degrees**; `gravityModifier` is a multiplier of the project gravity, not a vector.
- `component_set_property` on a ParticleSystem module is not a substitute: module structs are native proxies and that path has no curve/gradient contract or validation.

**Routing**:
- Material creation and shader choice → `material` / `shader` modules; this module only references a material asset (never edits it).
- Saving a configured effect as a prefab → `prefab_create`; prefab assets are not edited here (instances get overrides).
- Moving / parenting / deleting the object → `gameobject` module.

> **Object targeting**: every single-object skill accepts `entityId` (string, preferred; the only id on Unity 6000.4+), `name`, `path` and `instanceId` (older Unity). An id that names nothing is an error, never a fallback to the name.

## Skills Overview

| Single object | Batch version | Purpose |
|---------------|---------------|---------|
| `particle_get_info` | `particle_get_info_batch` | Playback state, Main summary, every module's enabled flag, renderer summary |
| `particle_create` | `particle_create_batch` | New GameObject + ParticleSystem + renderer, optional initial configJson |
| `particle_add_system` | `particle_add_system_batch` | Add a system to an object that has none (never resets an existing one) |
| `particle_configure` | `particle_configure_batch` | Atomic partial patch of `system` and `modules` |
| `particle_set_renderer` | `particle_set_renderer_batch` | Renderer mode, material, mesh, sorting, alignment, display fields |
| `particle_set_bursts` | `particle_set_bursts_batch` | Replace the complete burst list |
| `particle_set_sub_emitters` | `particle_set_sub_emitters_batch` | Replace the complete sub-emitter list (cycles refused, also across a batch) |

**No batch needed**: `particle_get_capabilities` (contract of this Editor), `particle_find_all` (scene listing with paging), `particle_get_module` (one module, every field), `particle_get_renderer`, `particle_get_bursts`, `particle_get_sub_emitters`, `particle_analyze` (static diagnosis of one system, a root or the open scenes).

## Workflow

1. `particle_get_capabilities` once per session (`module=` to keep it small): fields, types, units, ranges, curve/gradient modes, limits.
2. Create or find: `particle_create` (new object, stopped, `playOnAwake=false`, pipeline default particle material, `rotX=-90` so the cone emits upwards) or `particle_find_all` / `particle_add_system`.
3. Configure with one `particle_configure` call (dryRun first under Approval), then `particle_set_renderer` and `particle_set_bursts`.
4. Verify with the returned `state` (read back from Unity) or `particle_get_module`; `adjustments` lists any value Unity clamped.
5. Chain systems with `particle_set_sub_emitters` (enable `modules.subEmitters` with `particle_configure`), and run `particle_analyze` when something is invisible, pink or expensive.

```json
{"name": "FireFX", "configJson": {"schemaVersion": 1, "modules": {
  "main": {"startLifetime": {"mode": "TwoConstants", "min": 0.7, "max": 1.2}, "startSpeed": 1.5,
           "startSize": 0.35, "startColor": {"mode": "Color", "color": {"r": 1, "g": 0.4, "b": 0.05, "a": 1}}, "maxParticles": 300},
  "emission": {"rateOverTime": 40},
  "shape": {"shapeType": "Cone", "angle": 12, "radius": 0.15}}}}
```

## Patch Rules

- `configJson` = `{"schemaVersion": 1, "system"?: {...}, "modules"?: {"<module>": {...}}}`; `particle_set_renderer` takes `{"schemaVersion": 1, "renderer": {...}}`; `burstsJson` is the complete burst array (`[]` clears it). Objects or JSON strings are both accepted.
- Omitted fields keep their value; `false` and `0` are applied. A curve, gradient, vector, colour or asset value replaces that property whole.
- Unknown modules/fields, duplicate keys, wrong JSON types, non-finite numbers, out-of-range values and illegal enum names reject the **whole** call with every problem listed under its JSON path (`configJson.modules.main.startLifetime.min`, `items[2].configJson...`); nothing is written.
- Writes never enable a module: values of a disabled module are stored and reported with `enabled: false` and a warning.
- Axis flags decide which fields apply: `startSize3D` (startSize vs startSizeX/Y/Z), `startRotation3D`, and `separateAxes` of `noise`, `sizeOverLifetime`, `sizeBySpeed`, `limitVelocityOverLifetime` (limit vs limitX/Y/Z) and the rotation modules (x and y only: z, the Inspector's "Angular Velocity", always applies). X/Y/Z triplets (also orbital, orbital offset and force) share one curve mode.
- Writes need Edit Mode and a **stopped** system: a playing or paused system (including the Scene view preview of a selected one) is refused, never stopped implicitly. Reads always answer.
- `main.stopAction` accepts only `None`; `simulationSpace` Custom, mesh/sprite shapes and every module `particle_get_capabilities` lists as not configurable are readable but not writable yet. The sub-emitter list is replaced with `particle_set_sub_emitters`; `modules.subEmitters.enabled` is a normal configure field. `externalForces` does not write its force-field list or influence mask yet.

Value shapes, field tables, references and recipes: [REFERENCE.md](./REFERENCE.md).

## Skills

### particle_get_capabilities
The particle contract of this Editor; `available: false` with `unavailableReason` when the built-in Particle System module is disabled.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `module` | string | No | null | One module id (`main`, `renderer`, `system`, ...); omit for all |

### particle_find_all
Systems in the open scenes in Hierarchy order (no assets, no Prefab Mode objects).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootEntityId` | string | No | null | Only under this object (also `rootName` / `rootPath` / `rootInstanceId`) |
| `includeInactive` | bool | No | true | Include inactive objects |
| `limit` | int | No | 100 | Page size, 1-500 |
| `offset` | int | No | 0 | Systems to skip |

**Returns**: `{total, count, offset, limit, truncated, systems: [{name, entityId, instanceId, path, scene, activeInHierarchy, isPlaying, isPaused, duration, loop, maxParticles, emissionEnabled, renderMode, material, isPrefabInstance}]}`

### particle_get_module
Every supported field of one module in the exact shape `configJson` accepts.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `module` | string | Yes | - | A module id from `particle_get_capabilities` (`main`, `shape`, `noise`, `system`, `renderer`, ...) |

### particle_create
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Particle System" | New object name |
| `x`, `y`, `z` | float | No | 0 | Position, local to the parent unless `space='world'` |
| `parentEntityId` | string | No | null | Parent (also `parentName` / `parentPath` / `parentInstanceId`); must exist |
| `rotX` | float | No | -90 | Euler degrees; -90 points the default cone up (`rotY`, `rotZ` default 0) |
| `space` | string | No | "local" | `local` or `world` |
| `configJson` | string | No | null | Optional initial configuration (same shape as `particle_configure`) |

**Returns**: `{name, entityId, instanceId, path, parentPath, position, localPosition, rotation, particleSystem: {type, entityId, instanceId}, renderer: {type, entityId, instanceId}, material, defaultsApplied, applied, state, playback, warnings, adjustments?}`

### particle_configure
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `configJson` | string | Yes | - | `{"schemaVersion": 1, "system"?, "modules"?}` partial patch |

**Returns**: `{name, entityId, instanceId, path, applied: ["modules.main.duration", ...], state: {system?, modules?}, warnings, adjustments?}`

### particle_configure_batch
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | `[{entityId|name|path|instanceId, configJson}]`, at most 100, one item per system |

**Returns**: the standard batch envelope `{success, totalItems, successCount, failCount, results, rolledBack?, revertedCount?}`; any invalid item means nothing is applied.

### particle_set_renderer
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `configJson` | string | Yes | - | `{"schemaVersion": 1, "renderer": {...}}` |

`material` / `mesh` are asset references: `"Assets/FX/Fire.mat"`, `{"guid": ...}`, or built-ins `{"assetPath": "Resources/unity_builtin_extra", "name": "Default-ParticleSystem"}` / `{"assetPath": "Library/unity default resources", "name": "Cube"}`. `renderMode: "Mesh"` needs a mesh.

### particle_set_bursts
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `burstsJson` | string | Yes | - | Complete list, at most 8: `[{time, count, cycleCount?, repeatInterval?, probability?}]`; `[]` clears |

`cycleCount` 0 repeats forever (Unity's encoding); `count` is a MinMaxCurve or a number.

### particle_get_sub_emitters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |

**Returns**: `{name, entityId, instanceId, path, enabled, count, subEmitters: [{system: {name, entityId, instanceId, path} | null, type, properties, emitProbability, missing?}], warnings}`

### particle_set_sub_emitters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `subEmittersJson` | string | Yes | - | Complete list, at most 16: `[{system, type, properties?, emitProbability?}]`; `[]` clears |

`system` is `{entityId}` (or `name` / `path` / `instanceId`, or a path/name string) of a ParticleSystem in the parent's scene; `type` is `Birth`, `Collision`, `Death`, `Trigger` or `Manual`; `properties` lists `InheritColor`, `InheritSize`, `InheritRotation`, `InheritLifetime`, `InheritDuration` (`[]` inherits nothing, `["InheritEverything"]` all five). The system itself, cycles (judged on the graph the whole call or batch leaves behind) and a repeated system+type are refused; a sub emitter outside the parent's hierarchy is only warned about, never reparented.

**Returns**: `{name, entityId, instanceId, path, enabled, count, subEmitters, warnings}`

### particle_analyze
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No | null | One system (or `name` / `path` / `instanceId`) |
| `rootEntityId` | string | No | null | Every system under this object (also `rootName` / `rootPath` / `rootInstanceId`); not with a single system |
| `includeInactive` | bool | No | true | Include inactive objects |
| `limit` | int | No | 100 | Findings returned, 1-500, most severe first |
| `thresholdsJson` | string | No | null | Overrides, e.g. `{"maxParticlesPerSystem": 5000}` |

Omit every locator to analyze the open scenes. Static checks only (no frame times, GPU cost or overdraw); nothing is changed.

**Returns**: `{scope, systemsAnalyzed, totalMaxParticles, findingCount, counts: {error, warning, info}, findings: [{code, severity, system, field, message, evidence?, fix?: {skill, reason, args?}}], truncated, thresholds}`

## Exact Signatures

Exact names, parameters, defaults and returns are defined by `GET /skills/schema?category=Particle` (or `unity_skills.get_skill_schema()`), not by this file; field-level contracts come from `particle_get_capabilities`.

## Common Errors

| Error | Trigger | Fix |
|---|---|---|
| `SEMANTIC_INVALID` | An invalid field value, unknown field, axis-flag conflict, mixed curve modes in a triplet, a sub-emitter cycle or self reference, a playing/paused system, or Play Mode. `problems` lists every path. | Fix each listed path (capabilities give ranges and enums); stop the system (Scene view Particle Effect overlay > Stop, or deselect it); exit Play Mode. |
| `TYPE_MISMATCH` | A JSON value of the wrong type (`"5"` for a number, `1` for an enum name). | Send the type capabilities name. |
| `INVALID_JSON` | Malformed configJson / burstsJson / subEmittersJson / thresholdsJson / items text, or a repeated key. | Send one well-formed JSON value with unique keys. |
| `TARGET_NOT_FOUND` | No such object, no ParticleSystem on it, a sub-emitter `system` that names nothing, or an asset reference that resolves to nothing. | `particle_find_all`, then retry with its `entityId`; `particle_add_system` first; check the asset path. |
| `MISSING_PACKAGE` | The project disabled the built-in Particle System module (`com.unity.modules.particlesystem`). | Enable it in Package Manager > Built-in (or `package_install` that id), wait for the reload, retry. |
| `INTERNAL` | Unity threw while applying an already validated write; the call was rolled back. | Report it; nothing was changed. |
