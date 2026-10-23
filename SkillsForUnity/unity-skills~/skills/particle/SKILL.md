---
name: unity-particle
description: Create, inspect, configure, diagnose and preview built-in Unity Particle Systems (Shuriken) - typed module patches, renderer, bursts, sub emitters, static analysis, play/pause/stop/simulate/emit, batch and rollback. Not VFX Graph.
---

## Triggers
- Creating or tuning particle effects: fire, smoke, sparks, rain, snow, explosions, dust
- Changing emission rate, lifetime, size, colour or shape of a ParticleSystem; bursts; renderer material and sorting
- Adding motion or variation: forces, speed limit and drag, spin, orbiting, inherited emitter velocity, size / colour / rotation by speed, per-particle custom data for shaders
- Batch-editing many particle systems (lower the cost of all rain emitters, switch smoke to world space)
- Chaining systems with sub emitters (firework: rocket -> explosion -> sparks); finding why particles are invisible, pink or expensive
- Previewing an effect: play, pause, stop or clear it, fast-forward it by N seconds, emit particles now
- 创建或调整粒子特效（火焰、烟雾、火花、雨雪、爆炸），修改发射率、生命周期、大小、颜色、形状、爆发与渲染器材质；子发射器；诊断粒子看不见或显示粉色的原因；播放、暂停、停止、模拟预览与即时发射

# Unity Particle Skills

Built-in `ParticleSystem` (Shuriken) components in open scenes and prefab instances. **Visual Effect Graph is a different system and is out of scope**: none of these skills touches a `VisualEffect` or a `.vfx` asset. Parameters come from the schema (recommend `includeSchema=true` or `/skills/schema?category=Particle`), which notes the non-obvious ones — never from a skill's name. Use the `*_batch` skills for 2+ systems; they validate every item first and are all-or-nothing (playback batches excepted, see Playback).

## Operating Mode

- Reads (`particle_get_capabilities`, `particle_find_all`, `particle_get_info`, `particle_get_info_batch`, `particle_get_module`, `particle_get_renderer`, `particle_get_bursts`, `particle_get_sub_emitters`, `particle_analyze`) are `SemiAuto`: they run in all three modes without a grant.
- Writes (`particle_create*`, `particle_add_system*`, `particle_configure*`, `particle_set_renderer*`, `particle_set_bursts*`, `particle_set_sub_emitters*`) are `FullAuto`, risk `medium`, workflow-tracked: Approval needs a grant; Auto / Bypass run them directly.
- Playback (`particle_play*`, `particle_pause*`, `particle_stop*`, `particle_simulate*`, `particle_emit*`) is `FullAuto`, risk `low`, **not** workflow-tracked: it changes runtime state only (playback, time, live particles), which is never saved and which no undo restores. It works in Edit Mode (a preview) and in Play Mode alike and never enters or leaves Play Mode.
- No skill here deletes; to remove a system use `component_remove` (componentType `ParticleSystem`) or `gameobject_delete`.

## Shared rules

Value encodings (MinMaxCurve, gradient, burst, sub emitter, asset references), units, recipes, troubleshooting, the analysis checks and every module's field table: [REFERENCE.md](REFERENCE.md).

- **Targets**: every single-object skill accepts `entityId` (string, preferred; the only id on Unity 6000.4+), `name`, `path` and `instanceId` (older Unity). An id that names nothing is an error, never a fallback to the name.
- **Routing**: material creation and shader choice → `material` / `shader` modules (this module only references a material asset, never edits it); saving a configured effect as a prefab → `prefab_create` (prefab assets are not edited here, instances get overrides); moving / parenting / deleting the object → `gameobject` module.
- **Workflow**:
  1. `particle_get_capabilities` once per session (`module=` to keep it small): fields, types, units, ranges, curve/gradient modes, limits.
  2. Create or find: `particle_create` (new object, stopped, `playOnAwake=false`, pipeline default particle material, `rotX=-90` so the cone emits upwards) or `particle_find_all` / `particle_add_system`.
  3. Configure with one `particle_configure` call (dryRun first under Approval), then `particle_set_renderer` and `particle_set_bursts`.
  4. Verify with the returned `state` (read back from Unity) or `particle_get_module`; `adjustments` lists any value Unity clamped.
  5. Chain systems with `particle_set_sub_emitters` (enable `modules.subEmitters` with `particle_configure`); run `particle_analyze` when something is invisible, pink or expensive.
  6. Preview with `particle_simulate` (`seconds`, `restart`): it returns `time`, `particleCount`, `isPaused` read back. `particle_stop` before the next write: writes refuse a playing or paused system.

**Patch rules**
- `configJson` = `{"schemaVersion": 1, "system"?: {...}, "modules"?: {"<module>": {...}}}`; `particle_set_renderer` takes `{"schemaVersion": 1, "renderer": {...}}`; `burstsJson` is the complete burst array (`[]` clears it). Objects or JSON strings are both accepted.
- Omitted fields keep their value; `false` and `0` are applied. A curve, gradient, vector, colour or asset value replaces that property whole.
- Unknown modules/fields, duplicate keys, wrong JSON types, non-finite numbers, out-of-range values and illegal enum names reject the **whole** call with every problem listed under its JSON path (`configJson.modules.main.startLifetime.min`, `items[2].configJson...`); nothing is written.
- Writes never enable a module: values of a disabled module are stored and reported with `enabled: false` and a warning.
- Axis flags decide which fields apply: `startSize3D` (startSize vs startSizeX/Y/Z), `startRotation3D`, and `separateAxes` of `noise`, `sizeOverLifetime`, `sizeBySpeed`, `limitVelocityOverLifetime` (limit vs limitX/Y/Z) and the rotation modules (x and y only: z, the Inspector's "Angular Velocity", always applies). X/Y/Z triplets (also orbital, orbital offset and force) share one curve mode.
- Writes need Edit Mode and a **stopped** system: a playing or paused system (including the Scene view preview of a selected one) is refused, never stopped implicitly; `particle_stop` stops it. Reads always answer.
- `main.stopAction` accepts only `None`; `simulationSpace` Custom, mesh/sprite shapes and every module `particle_get_capabilities` lists as not configurable are readable but not writable yet. The sub-emitter list is replaced with `particle_set_sub_emitters`; `modules.subEmitters.enabled` is a normal configure field. `externalForces` does not write its force-field list or influence mask yet.

**Playback rules**
- Runtime state only: `undoable: false` in every result. A batch validates every item, runs **nothing** if one is invalid, then runs the items in order without a rollback: `executedCount` says how many ran; after a run-time failure resend only the failed items.
- Refused in this version (the call changes nothing): a reached system with `main.stopAction` other than `None`, collision messages, trigger callbacks, or a running script implementing `OnParticleUpdateJobScheduled` (sub-emitters count). `particle_pause` is never refused for these.
- `withChildren=false` by default; `true` reaches every child ParticleSystem (a dryRun lists them). Two items may not reach the same system.
- Edit Mode: Unity advances a playing system only while the Scene view previews it, so `particle_play` changes the flags, not the time; `particle_simulate` moves it by an exact time.
- `particle_simulate`: whole steps of `Time.fixedDeltaTime` (`fixedTimeStep=false`: variable steps); budgets 300 steps and 10 systems per simulation, 100000 summed `maxParticles` per call; above the inline work limit send `runAsync=true` and poll `GET /jobs/{id}` (result in `resultData`; `job_cancel` stops it where it is). A fixed `randomSeed` and fixed steps repeat in the same Editor only.

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
| `particle_play` | `particle_play_batch` | Play (`withChildren` for child systems) |
| `particle_pause` | `particle_pause_batch` | Pause at the current time, keeping the particles |
| `particle_stop` | `particle_stop_batch` | Stop; `StopEmittingAndClear` (default) also removes every particle |
| `particle_simulate` | `particle_simulate_batch` | Fast-forward N seconds in fixed steps, then paused; `runAsync` for long runs |
| `particle_emit` | `particle_emit_batch` | Emit N particles now (not idempotent) |

**No batch needed**: `particle_get_capabilities` (contract of this Editor), `particle_find_all` (scene listing with paging), `particle_get_module` (one module, every field), `particle_get_renderer`, `particle_get_bursts`, `particle_get_sub_emitters`, `particle_analyze` (static diagnosis of one system, a root or the open scenes).

Per-skill tables, item fields and return shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `particle_set_emission_rate` / `particle_set_color` / `particle_set_size` do not exist → one `particle_configure` call patches any modules at once.
- Do not send module fields as top-level parameters → they go inside `configJson.modules.<module>`.
- Do not guess field names, units or ranges → `particle_get_capabilities` (with `module=main`, `renderer`, ...) lists them; `particle_get_module` shows the current values in the exact input shape.
- `main.startRotation*` are **radians**; `rotationOverLifetime.*` and `rotationBySpeed.*` are **radians per second** (the Inspector shows degrees for all of these); `velocityOverLifetime.orbitalX/Y/Z` are **radians per second** (shown as is); `shape.rotation`, `shape.angle`, `shape.arc` and `noise.rotationAmount` (per second) are **degrees**; `gravityModifier` is a multiplier of the project gravity, not a vector.
- `component_set_property` on a ParticleSystem module is not a substitute: module structs are native proxies and that path has no curve/gradient contract or validation.
- `particle_clear` / `particle_restart` / `particle_preview` do not exist → `particle_stop` (`stopBehavior` `StopEmittingAndClear`) clears; `particle_simulate` (`restart=true`) restarts and fast-forwards.
- Do not expect `editor_undo` / `workflow_undo_task` to undo playback, and do not put playback steps in `POST /skills/batch?mode=transactional` (refused with `400` before anything runs).
- Do not resend `particle_emit` after a timeout: the first call may have emitted. Read `particleCount` (`particle_get_info`) first.

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
| `SEMANTIC_INVALID` (playback) | A refused setup (stopAction, collision/trigger messages, a particle job script), a budget (steps, systems, particles, emit count), the inline work limit, a system another simulation job still drives, or two batch items reaching one system. | Follow the message: change the setup, split the call, send `runAsync=true`, or wait for / cancel the job. |
| `400` from `/skills/batch?mode=transactional` | A playback step in a transactional batch. | Run playback outside the transaction (plain `/skills/batch` or single calls). |
