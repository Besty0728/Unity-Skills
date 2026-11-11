# Unity Particle Skills

Built-in `ParticleSystem` (Shuriken) components in open scenes and prefab instances. **Visual Effect Graph is a different system and is out of scope**: nothing here touches a `VisualEffect` or a `.vfx` asset. Parameters come from the schema (`GET /skills/schema?category=Particle&wire=v2`) and `particle_get_capabilities` (fields, units, ranges); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Value encodings, recipes and every module's field table: [REFERENCE.md](REFERENCE.md).

## When to use
- Creating or tuning effects: fire, smoke, sparks, rain, snow, explosions, dust
- Changing emission rate, lifetime, size, colour, shape, bursts, renderer material or sorting
- Adding motion or variation: forces, speed limit and drag, spin, orbit, inherited velocity, size, colour or rotation by speed, custom data for shaders
- Batch-editing many systems, or chaining systems with sub emitters (rocket to explosion to sparks)
- Diagnosing invisible, pink or expensive particles
- Previewing an effect: play, pause, stop, fast-forward, or emit now

## Rules
- **Targets**: every single-object skill accepts `entityId` (preferred; the only id on Unity 6000.4+), `name`, `path` and `instanceId` (older Unity). An id that names nothing is an error, never a fallback to the name.
- **Workflow**:
  1. `particle_get_capabilities` once per session (`module=` narrows it): fields, types, units, ranges, curve and gradient modes, limits.
  2. Create or find: `particle_create` (stopped, `playOnAwake=false`, default particle material, `rotX=-90` so the cone emits up), or `particle_find_all` / `particle_add_system`.
  3. Configure with one `particle_configure` call, then `particle_set_renderer` and `particle_set_bursts`.
  4. Verify with the returned `state` or `particle_get_module`; `adjustments` lists values Unity clamped.
  5. Chain with `particle_set_sub_emitters` (enable `modules.subEmitters` with `particle_configure`); run `particle_analyze` when something is invisible, pink or expensive.
  6. Preview with `particle_simulate` (`seconds`, `restart`); it returns `time`, `particleCount` and `isPaused`. Run `particle_stop` before the next write.
- **Patch shape**: `configJson` is `{"schemaVersion": 1, "system"?: {...}, "modules"?: {"<module>": {...}}}`; `particle_set_renderer` takes `{"schemaVersion": 1, "renderer": {...}}`; `burstsJson` is the complete array (`[]` clears it). Omitted fields keep their value; `false` and `0` apply. A curve, gradient, vector, colour or asset value replaces that property whole.
- Unknown fields, duplicate keys, wrong JSON types, non-finite or out-of-range numbers and bad enum names reject the **whole** call; every problem is listed under its JSON path. Nothing is written.
- Writes never enable a module: values of a disabled module are stored and reported with `enabled: false`.
- Axis flags decide which fields apply: `startSize3D`, `startRotation3D`, and `separateAxes` of `noise`, `sizeOverLifetime`, `sizeBySpeed`, `limitVelocityOverLifetime` and the rotation modules (z, the Inspector's "Angular Velocity", always applies). X/Y/Z triplets share one curve mode.
- Writes need Edit Mode and a **stopped** system; a playing or paused system (including the Scene view preview of a selected one) is refused, never stopped implicitly. Reads always answer.
- Unsupported in this version and readable only: `main.stopAction` other than `None`, `simulationSpace` Custom, mesh and sprite shapes, and modules `particle_get_capabilities` lists as not configurable. `externalForces` does not write its force-field list or influence mask. The sub-emitter list is replaced by `particle_set_sub_emitters`.
- **Playback** (`particle_play`, `particle_pause`, `particle_stop`, `particle_simulate`, `particle_emit`) is `FullAuto`, low risk and not workflow-tracked: runtime state only, `undoable: false` in every result. It works in Edit Mode (a preview) and Play Mode, and never enters or leaves Play Mode. No skill here deletes; remove a system with `component_remove` (`ParticleSystem`) or `gameobject_delete`.
- **Batch playback** validates every item, runs nothing if one is invalid, then runs the items in order without rollback; `executedCount` says how many ran. Resend only the failed items.
- `withChildren=false` by default; `true` reaches every child system (a dryRun lists them). Two batch items may not reach the same system.
- `particle_simulate` steps in whole `Time.fixedDeltaTime` steps (`fixedTimeStep=false` for variable steps). Budgets: 300 steps and 10 systems per call, 100000 summed `maxParticles`. Above the inline limit send `runAsync=true` and poll `GET /jobs/{id}`; `job_cancel` stops it. A fixed `randomSeed` repeats only in the same Editor.
- Refused playback setups: `main.stopAction` other than `None`, collision messages, trigger callbacks, and a running script implementing `OnParticleUpdateJobScheduled` (sub emitters count). `particle_pause` is never refused for these.
- Material creation and shader choice belong to `material` and `shader`; this module only references a material asset. Saving an effect as a prefab is `prefab_create`. Moving, parenting or deleting the object is the `gameobject` module.

## Pitfalls
- In Edit Mode, Unity advances a playing system only while the Scene view previews it, so `particle_play` sets flags, not time; `particle_simulate` moves time by an exact amount.
- `main.startRotation*` are radians. `rotationOverLifetime.*` and `rotationBySpeed.*` are radians per second. `velocityOverLifetime.orbitalX/Y/Z` are radians per second. `shape.rotation`, `shape.angle`, `shape.arc` and `noise.rotationAmount` are degrees. `gravityModifier` is a multiplier, not a vector.
- Do not resend `particle_emit` after a timeout: the first call may have emitted. Read `particleCount` with `particle_get_info` first.
- Do not put playback steps in `POST /skills/batch?mode=transactional`; that is refused with `400` before anything runs.
- `component_set_property` on a ParticleSystem module bypasses the curve and gradient contract and validation: use `particle_configure`.

## Skills Overview

| Single object | Batch version | Purpose |
|---------------|---------------|---------|
| `particle_get_info` | `particle_get_info_batch` | Playback state, Main summary, each module's enabled flag, renderer summary |
| `particle_create` | `particle_create_batch` | New GameObject with a ParticleSystem and renderer, optional `configJson` |
| `particle_add_system` | `particle_add_system_batch` | Add a system to an object that has none (never resets an existing one) |
| `particle_configure` | `particle_configure_batch` | Atomic partial patch of `system` and `modules` |
| `particle_set_renderer` | `particle_set_renderer_batch` | Renderer mode, material, mesh, sorting, alignment, display fields |
| `particle_set_bursts` | `particle_set_bursts_batch` | Replace the complete burst list |
| `particle_set_sub_emitters` | `particle_set_sub_emitters_batch` | Replace the complete sub-emitter list (cycles refused, also across a batch) |
| `particle_play` | `particle_play_batch` | Play (`withChildren` for child systems) |
| `particle_pause` | `particle_pause_batch` | Pause at the current time, keeping particles |
| `particle_stop` | `particle_stop_batch` | Stop; `StopEmittingAndClear` (default) also removes every particle |
| `particle_simulate` | `particle_simulate_batch` | Fast-forward N seconds in fixed steps, then pause; `runAsync` for long runs |
| `particle_emit` | `particle_emit_batch` | Emit N particles now (not idempotent) |

No batch needed: `particle_get_capabilities`, `particle_find_all` (scene listing with paging), `particle_get_module`, `particle_get_renderer`, `particle_get_bursts`, `particle_get_sub_emitters`, and `particle_analyze` (static diagnosis of one system, a root or the open scenes).

Per-skill parameter tables and return shapes: `reference/<skill_name>.md`.

**DO NOT** (common hallucinations):
- `particle_set_emission_rate`, `particle_set_color` and `particle_set_size` do not exist: one `particle_configure` call patches any modules.
- Module fields are not top-level parameters: they go inside `configJson.modules.<module>`.
- Field names, units and ranges are not guesses: `particle_get_capabilities` (`module=main`, `renderer`, ...) lists them.
- `particle_clear`, `particle_restart` and `particle_preview` do not exist: use `particle_stop` (clears by default) or `particle_simulate` with `restart=true`.
- `editor_undo` and `workflow_undo_task` do not undo playback.

## Common Errors

| Error | Trigger | Fix |
|---|---|---|
| `SEMANTIC_INVALID` | Invalid field value, unknown field, axis-flag conflict, mixed curve modes in a triplet, sub-emitter cycle or self reference, a playing or paused system, or Play Mode. `problems` lists each path. | Fix each path (capabilities give ranges and enums); stop the system; exit Play Mode. |
| `TYPE_MISMATCH` | JSON value of the wrong type (`"5"` for a number, `1` for an enum name). | Send the type capabilities name. |
| `INVALID_JSON` | Malformed `configJson`, `burstsJson`, `subEmittersJson`, `thresholdsJson` or `items`, or a repeated key. | Send one well-formed JSON value with unique keys. |
| `TARGET_NOT_FOUND` | No such object, no ParticleSystem on it, a sub-emitter `system` that names nothing, or an asset reference that resolves to nothing. | `particle_find_all`, then retry with `entityId`; `particle_add_system` first; check the asset path. |
| `MISSING_PACKAGE` | The built-in Particle System module (`com.unity.modules.particlesystem`) is disabled. | Tell the user; enabling it is their call. |
| `INTERNAL` | Unity threw while applying a validated write; rolled back. | Report it; nothing changed. |
| `SEMANTIC_INVALID` (playback) | A refused setup, a budget (steps, systems, particles, emit count), the inline work limit, a system another simulation drives, or two batch items reaching one system. | Change the setup, split the call, send `runAsync=true`, or wait for or cancel the job. |
| `400` from `/skills/batch?mode=transactional` | A playback step in a transactional batch. | Run playback outside the transaction. |

Transport codes → [error codes](../../references/protocol-error-codes.md).
