# Particle Reference

Detail behind [SKILL.md](./SKILL.md): payload shapes, value types, references, limits, recipes and troubleshooting. The authoritative field list of the running Editor is `particle_get_capabilities` (`module=` scopes it); the tables at the end are a snapshot of it.

## Payloads

| Skill | Parameter | Shape |
|---|---|---|
| `particle_configure`, `particle_create`, `particle_add_system` | `configJson` | `{"schemaVersion": 1, "system"?: {field: value}, "modules"?: {"<module>": {field: value}}}` |
| `particle_set_renderer` | `configJson` | `{"schemaVersion": 1, "renderer": {field: value}}` |
| `particle_set_bursts` | `burstsJson` | `[Burst, ...]`, the complete list (at most 8); `[]` clears it |
| `*_batch` | `items` | `[{...the single skill's parameters...}]`, at most 100 items, one item per target, unknown item keys rejected |

- `schemaVersion` is required and must be `1`. An empty `particle_configure` / `particle_set_renderer` patch is rejected; `particle_create` / `particle_add_system` may omit `configJson`.
- A payload may be a JSON object/array or a string holding one. Every key must be unique (a repeated key is rejected rather than "last one wins").
- `system` holds ParticleSystem properties (`useAutoRandomSeed`, `randomSeed`), not Main module fields. `randomSeed` needs `"useAutoRandomSeed": false` in the same patch (Unity would switch it off as a side effect).

## Responses

- `applied`: the field paths written, in application order (`modules.main.duration`, `renderer.material`, ...).
- `state`: every written field **read back from Unity**, plus each touched module's `enabled`.
- `warnings`: values that were stored but have no effect (disabled module, angle on a Sphere, stretch fields outside renderMode Stretch, burst after the duration, ...).
- `adjustments` (only when non-empty): `[{path, valueRequested, valueApplied}]` for values Unity changed on write (it clamps some fields silently, e.g. a start lifetime below 0.0001).
- Errors list every problem: `problems: [{path, error, errorCode}]`. Over REST a rejected request comes back as `SEMANTIC_INVALID` with `details.semanticErrors` naming the same paths, and dryRun reports them under `validation.semanticErrors`.

## Value Types

### MinMaxCurve

| Mode | Shape |
|---|---|
| Constant | `{"mode": "Constant", "constant": 1.5}`, or the bare number `1.5` |
| TwoConstants | `{"mode": "TwoConstants", "min": 0.5, "max": 2}` (min <= max) |
| Curve | `{"mode": "Curve", "multiplier": 1, "curve": AnimationCurve}` |
| TwoCurves | `{"mode": "TwoCurves", "multiplier": 1, "minCurve": AnimationCurve, "maxCurve": AnimationCurve}` |

A field's min/max apply to the constants; curve key values are any finite number scaled by `multiplier`. Some fields accept fewer modes, as the Inspector does: `main.startDelay` constants only, `sizeOverLifetime` curves only.

### AnimationCurve and Keyframe

```json
{"preWrapMode": "ClampForever", "postWrapMode": "ClampForever",
 "keys": [{"time": 0, "value": 0, "inTangent": 0, "outTangent": 1, "inWeight": 0.333, "outWeight": 0.333,
           "weightedMode": "None", "leftTangentMode": "Free", "rightTangentMode": "Free", "broken": false}]}
```

- 1-64 keys, sorted by time (unsorted input is sorted), no two keys at one time. Key times are normalized 0-1 for every P0 field (lifetime, duration or noise input; `keyTime` in capabilities says which).
- Defaults when omitted: tangents 0, weights 1/3, `weightedMode` None, tangent modes Free, `broken` false; wrap modes ClampForever.
- A `Free` side uses the given tangent. `Auto`, `ClampedAuto` and `Linear` sides are recomputed by Unity from the neighbouring keys. A `Constant` side is a step: its tangent is infinite, so it carries **no** tangent value (omit it). Unity marks a key `broken` when either side is Linear or Constant.
- Readback is the canonical form above and can be sent back unchanged.

### MinMaxGradient and Gradient

| Mode | Shape |
|---|---|
| Color | `{"mode": "Color", "color": Color}`, or a bare Color object |
| TwoColors | `{"mode": "TwoColors", "minColor": Color, "maxColor": Color}` |
| Gradient | `{"mode": "Gradient", "gradient": Gradient}` |
| TwoGradients | `{"mode": "TwoGradients", "minGradient": Gradient, "maxGradient": Gradient}` |
| RandomColor | `{"mode": "RandomColor", "gradient": Gradient}` |

```json
{"mode": "Blend", "colorSpace": "Uninitialized",
 "colorKeys": [{"time": 0, "color": {"r": 1, "g": 0.6, "b": 0.1}}, {"time": 1, "color": {"r": 0.2, "g": 0.2, "b": 0.2}}],
 "alphaKeys": [{"time": 0, "alpha": 0}, {"time": 0.2, "alpha": 1}, {"time": 1, "alpha": 0}]}
```

- `mode`: Blend, Fixed or PerceptualBlend. 1-8 colour keys and 1-8 alpha keys (Unity's limit), times 0-1, no repeats.
- Colour keys have **no alpha** (Unity discards it); opacity goes in `alphaKeys`. Unity stores key times in steps of 1/65535, so a time reads back within 0.00002.
- `colorOverLifetime.color` accepts Gradient and TwoGradients only.

### Color, Vector

- Color `{"r", "g", "b", "a"?}`: linear floats as Unity stores them, no gamma/linear conversion; r/g/b >= 0 and may exceed 1 (HDR); `a` 0-1, default 1.
- Vector3 `{"x", "y", "z"}` and Vector2 `{"x", "y"}`: every component required.

### Asset references (renderer `material`, `mesh`)

- Project asset: `"Assets/FX/Fire.mat"`, `{"assetPath": "Assets/FX/Fire.mat"}` or `{"guid": "..."}`; add `"name"` to pick one sub-asset when a file holds several of the type (meshes in a model).
- Built-in assets: `{"assetPath": "Resources/unity_builtin_extra", "name": "Default-ParticleSystem"}`, `{"assetPath": "Library/unity default resources", "name": "Cube"}` (Sphere, Capsule, Cylinder, Plane, Quad, ...).
- `null` clears the field. Readback adds `guid`, `type` and `builtin`, and the whole object is accepted back as input.
- The renderer uses the **shared** material: a material is referenced, never instanced or edited. Material and shader choice follow the project's render pipeline (`material` / `shader` modules); `particle_create` assigns the pipeline's default particle material.

### Burst

`{"time": 0.5, "count": MinMaxCurve, "cycleCount": 1, "repeatInterval": 0.01, "probability": 1}`

- `time` seconds >= 0 (a burst after the duration never fires and is warned about); `count` >= 0 particles per cycle; `cycleCount` >= 0 where **0 repeats forever** (Unity's encoding), default 1; `repeatInterval` > 0 seconds, default 0.01; `probability` 0-1, default 1.

## Units

| Quantity | Unit |
|---|---|
| durations, lifetimes, delays, burst times | seconds |
| `main.startRotation*` | **radians** (the Inspector shows degrees) |
| `shape.angle`, `shape.arc`, `shape.rotation` | **degrees** |
| `noise.rotationAmount` | degrees/second |
| sizes, radii, positions, speeds | Unity units (per second for speeds) |
| `main.gravityModifier` | multiplier of the project gravity (Physics or Physics2D per `gravitySource`), not a vector |
| `renderer.minParticleSize` / `maxParticleSize` | fraction of the viewport |

## Preconditions and Rollback

- Writes run in Edit Mode on objects of open scenes (prefab instances get instance overrides; prefab assets and Prefab Mode objects are refused) and need a stopped system. A selected system plays in the Scene view preview: press Stop in the Particle Effect overlay or deselect it.
- One write = one Undo step (`editor_undo` / Ctrl+Z restores it) and one workflow task: `workflow_undo_task` restores every field, curve, gradient, burst and the renderer material, also after a scene reload. `particle_create` registers the created object; `particle_add_system` registers both the system and the renderer Unity adds with it.
- Batches validate every item before writing anything; if an item fails while applying, every item already applied is rolled back (`rolledBack: true`). Inside `POST /skills/batch?mode=transactional` a later failing step rolls back particle steps the same way; playback steps are refused there (see Playback).
- `?diff=1` reports the changed ParticleSystem / ParticleSystemRenderer fields.

## Recipes

These are starting points, not finished art: the look also depends on texture, shader, colour space and post-processing.

**Fire** (on `particle_create`): main `startLifetime` TwoConstants 0.7-1.2, `startSpeed` 1.5, `startSize` 0.35, `startColor` Color (1, 0.4, 0.05), `simulationSpace` World; emission `rateOverTime` 40; shape Cone angle 12 radius 0.15; colorOverLifetime Gradient (orange -> dark grey, alpha 0 -> 1 -> 0); sizeOverLifetime Curve 1 -> 0.2.

**Smoke**: main `startLifetime` TwoConstants 3-5, `startSpeed` 0.5, `startSize` TwoConstants 0.8-1.5, `startRotation` TwoConstants 0-6.28 (radians); emission 8; colorOverLifetime grey with alpha 0 -> 0.5 -> 0; sizeOverLifetime Curve 0.5 -> 1.5; noise enabled strength 0.3 frequency 0.4.

**Rain**: main `startLifetime` 1.2, `startSpeed` 12, `startSize` 0.05, `maxParticles` 3000, `simulationSpace` World; emission 800; shape Box (scale 20, 1, 20); renderer `renderMode` Stretch, `lengthScale` 4.

**Explosion**: main `loop` false, `duration` 1, `startLifetime` TwoConstants 0.3-0.8, `startSpeed` TwoConstants 4-10; emission `rateOverTime` 0 with `particle_set_bursts` `[{"time": 0, "count": {"mode": "TwoConstants", "min": 40, "max": 60}}]`; shape Sphere radius 0.2.

**Lower the cost of every rain emitter**: `particle_find_all` (root = the weather group) -> `particle_configure_batch` items setting `main.maxParticles` and `emission.rateOverTime` -> dryRun -> execute; any invalid item rejects the whole batch.

## Troubleshooting

| Symptom | Check |
|---|---|
| Nothing visible | `renderer.enabled`, a `material` (null renders nothing), `emission.enabled` and a rate or burst, `startSize` / `startLifetime` > 0, alpha of `startColor` / colour over lifetime, the camera's culling mask. |
| Write refused "is playing" | The system plays (Scene view preview of a selected system, or a script). Stop it there or deselect it; writes never stop it themselves. |
| `startSizeY` refused | Send `"startSize3D": true` in the same patch; with it off only `startSize` applies. |
| "must share one curve mode" | Velocity x/y/z (and other X/Y/Z triplets) take one mode together; send all three. |
| A value came back different | See `adjustments`: Unity clamped it; the `state` shows what it kept. |
| Pink / magenta particles | The material's shader does not match the render pipeline; pick a pipeline particle shader with the `material` module. |

## Playback

`particle_play`, `particle_pause`, `particle_stop`, `particle_simulate`, `particle_emit` and their batches drive the live state of a system: playback flags, time and particles. None of it is serialized, so:

- Nothing is saved with the scene and nothing reaches the Undo stack or the workflow (`undoable: false`; `TracksWorkflow` is false on purpose); reopening the scene starts every system stopped again. `particle_stop` with `StopEmittingAndClear` brings a system back to stopped and empty.
- `POST /skills/batch?mode=transactional` refuses a batch with a playback step (`400 SEMANTIC_INVALID`, `details.violations`) before anything runs: a rollback could not undo it. Plain `/skills/batch` runs them.
- The skills never enter or leave Play Mode. In Edit Mode they drive a preview: Unity advances a playing system only while the Scene view previews it (a selected system), so `particle_play` sets the flags and `particle_simulate` moves time. In Play Mode they drive the running system and your scripts keep running (a script may react to the change).
- A system selected in the Editor is driven by the Scene view's Particle Effect preview, which can override a call; the result warns about it.

**Results** read every value back from Unity: `playback` `{isPlaying, isPaused, isStopped, isEmitting, particleCount, time, totalTime}` of the target and `systems` with the same for every reached system (target first). Simulate also reports `time`, `totalTime`, `particleCount` and `isPaused` at the top: Unity leaves a simulated system **paused**, not playing.

**Refused in this version** (`SEMANTIC_INVALID`, nothing changes), checked on every system the call reaches and on their sub-emitters:

| Setup | Why | Refused for |
|---|---|---|
| `main.stopAction` Disable / Destroy / Callback | When the system stops Unity deactivates or destroys the GameObject, or calls `OnParticleSystemStopped` | play, simulate, emit, stop |
| `collision.sendCollisionMessages` | `OnParticleCollision` on scripts of the system and of every collider hit | play, simulate, emit |
| A trigger action set to `Callback` | `OnParticleTrigger` on the system's scripts | play, simulate, emit |
| A running script implementing `OnParticleUpdateJobScheduled` | Called while the system updates (in Edit Mode only `[ExecuteAlways]` / `[ExecuteInEditMode]` scripts run) | play, simulate, emit |

`particle_pause` runs no user code and is never refused for these. To preview such a system, set `main.stopAction` to `None` with `particle_configure` (`workflow_undo_task` restores it) or change the others in the Inspector.

**`withChildren`** (play, pause, stop, simulate) is `false` by default; `true` adds every ParticleSystem below the target, inactive ones included, in Hierarchy order. A dryRun lists them under `changes.modify`. A batch refuses two items that reach the same system (their order would decide the result).

**Simulate**

- `seconds` rounds to whole steps of `Time.fixedDeltaTime` (`fixedTimeStep=true`, default; 0.02 s unless the project changed it). `fixedTimeStep=false` simulates the exact time, which Unity splits into updates of at most `Time.maximumParticleDeltaTime`; the step budget counts those. `steps` reports the steps taken.
- `restart=true` starts from time 0; `restart=false` continues from the current state (a paused system goes on where it is), so a long preview can be taken in parts.
- A fixed random seed (`particle_configure` `system.useAutoRandomSeed: false` + `randomSeed`) and fixed steps repeat the same particles in the same Editor and project; nothing promises identical trajectories across Unity versions, physics settings or render pipelines.

| Limit (product limit, not a Unity limit) | Value | Exceeded |
|---|---|---|
| Steps per simulation (one call or item) | 300 | `SEMANTIC_INVALID` on `seconds` |
| Systems per simulation (target + children) | 10 | `SEMANTIC_INVALID` on `target` |
| Summed `maxParticles` per call | 100000 | `SEMANTIC_INVALID` |
| Steps x systems per call | 3000 | `SEMANTIC_INVALID` on `items` |
| Inline work (steps x summed `maxParticles`) | 3000000 | needs `runAsync=true` |
| Systems reached by a play / pause / stop item | 50 | `SEMANTIC_INVALID` on `target` |
| Particles per `particle_emit` / per call | 10000 / 100000 | `SEMANTIC_INVALID` on `count` / `items` |

`particle_get_capabilities` reports them under `limits.playback`. A dryRun of `particle_simulate` reports `steps`, `particleBudget`, `estimatedWork` and `runsAs` under `playback`.

**Simulation jobs** (`runAsync=true`): the call validates everything and answers at once `{status: "accepted", jobId, kind: "particle_simulate", waitUrl, steps, ...}`. The job advances a slice per editor tick (about 10 ms of main-thread time), so `/health` and `/jobs` keep answering. Poll `GET /jobs/{id}` (or `job_status`): when `status` is `completed`, `resultData` holds the result an inline call returns (the batch envelope for `particle_simulate_batch`). It fails honestly, with `doneSteps` / `totalSteps`, when a domain reload or Editor restart ends it (`interrupted`, never resumed), when Play Mode changes, or when something else plays, stops or moves the system between slices. `job_cancel` stops it; the systems stay paused where it stopped. Other playback calls on a system a job still simulates are refused until it ends; the configuration skills refuse it anyway (it is paused).

**Emit**: `count` 1-10000 particles at once with the system's current settings (no per-particle `EmitParams`). `emitted` is what Unity kept (at most `main.maxParticles` alive), `particleCount` the count after. Emitting is not idempotent: after a timeout or a dropped connection the call may have run, so read `particleCount` with `particle_get_info` before sending it again, and never resend a whole `particle_emit_batch`.

**Batches** validate every item through the single skill's checks plus the cross-item ones (overlap, per-call budgets), and run **nothing** when any item is invalid (each valid item reports `executed: false`). Then the items run in order through the standard batch envelope with `atomic: false`: an item that fails at run time is reported failed, the ones that ran stay as they are (`executedCount`, `undoable: false`). Resend only the failed items.

**Preview a fire effect**: `particle_simulate {entityId, seconds: 1}` -> check `particleCount` and the `systems` states -> `particle_get_info` or a screenshot -> `particle_stop {entityId}` before the next `particle_configure`.

## Field Tables

Snapshot of `particle_get_capabilities` (schemaVersion 1, Unity 6000.3 and 2022.3 report the same table). Ranges are
inclusive unless marked `>`; a MinMaxCurve's range applies to its constants. Modules marked read-only report their
`enabled` flag only and refuse writes until P1.

### Module `system`

`system` - Properties of the ParticleSystem itself (not of a module).

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `useAutoRandomSeed` | bool |  |  | Pick a new random seed every time the system plays. |
| `randomSeed` | uint |  | >= 0 | Fixed seed used while useAutoRandomSeed is false; send useAutoRandomSeed:false with it. |

### Module `main`

`modules.main` - Main module: duration, looping, start values, simulation space, limits.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `duration` | float | seconds | >= 0.05, <= 100000 | Length of one system cycle. |
| `loop` | bool |  |  | Restart the cycle when duration ends. |
| `prewarm` | bool |  |  | Start as if one full cycle already ran (looping systems only). |
| `startDelay` | MinMaxCurve (Constant, TwoConstants) | seconds | >= 0 | Delay before emission starts. |
| `startLifetime` | MinMaxCurve | seconds | > 0 | Initial lifetime of each particle. |
| `startSpeed` | MinMaxCurve | units/second |  | Initial speed along the emission direction (negative moves backwards). |
| `startSize3D` | bool |  |  | Use startSizeX/Y/Z instead of startSize. |
| `startSize` | MinMaxCurve | units | >= 0 | Initial size (applies while startSize3D is false; Unity stores it as startSizeX). |
| `startSizeX` | MinMaxCurve | units | >= 0 | Initial size along X (startSize3D only). |
| `startSizeY` | MinMaxCurve | units | >= 0 | Initial size along Y (startSize3D only). |
| `startSizeZ` | MinMaxCurve | units | >= 0 | Initial size along Z (startSize3D only). |
| `startRotation3D` | bool |  |  | Use startRotationX/Y/Z instead of startRotation. |
| `startRotation` | MinMaxCurve | radians |  | Initial rotation in RADIANS (the Inspector shows degrees); applies while startRotation3D is false, stored as startRotationZ. |
| `startRotationX` | MinMaxCurve | radians |  | Initial rotation around X in radians (startRotation3D only). |
| `startRotationY` | MinMaxCurve | radians |  | Initial rotation around Y in radians (startRotation3D only). |
| `startRotationZ` | MinMaxCurve | radians |  | Initial rotation around Z in radians (startRotation3D only). |
| `flipRotation` | float | ratio | >= 0, <= 1 | Fraction of particles that spin the opposite way. |
| `startColor` | MinMaxGradient | linear RGBA (HDR allowed) |  | Initial colour of each particle. |
| `gravitySource` | enum: Physics3D / Physics2D |  |  | Which physics gravity gravityModifier scales. |
| `gravityModifier` | MinMaxCurve | multiplier of the project gravity |  | Scale of Physics.gravity (Physics2D.gravity with gravitySource Physics2D); not a gravity vector. |
| `simulationSpace` | enum: Local / World (readable: all) |  |  | Space particles live in after emission. Custom (needs a Transform reference) is readable but not writable in this version. |
| `simulationSpeed` | float | multiplier | >= 0, <= 100 | Playback speed of the whole system. |
| `useUnscaledTime` | bool |  |  | Ignore Time.timeScale. |
| `scalingMode` | enum: Hierarchy / Local / Shape |  |  | How Transform scale affects the system. |
| `playOnAwake` | bool |  |  | Start playing when the object is created in Play Mode. |
| `emitterVelocityMode` | enum: Transform / Rigidbody / Custom |  |  | Where the emitter's velocity (for inherit velocity and emission over distance) comes from. |
| `emitterVelocity` | Vector3 | units/second |  | Emitter velocity used when emitterVelocityMode is Custom. |
| `maxParticles` | int | particles | >= 0 | Most particles alive at once; emission stops at the limit. |
| `stopAction` | enum: None (readable: all) |  |  | What happens when the system stops. Only None is writable: Disable/Destroy/Callback deactivate or destroy objects or run scripts. |
| `cullingMode` | enum: Automatic / PauseAndCatchup / Pause / AlwaysSimulate |  |  | Behaviour while off screen. |
| `ringBufferMode` | enum: Disabled / PauseUntilReplaced / LoopUntilReplaced |  |  | Keep particles alive until replaced when maxParticles is reached. |
| `ringBufferLoopRange` | Vector2 | normalized lifetime | >= 0, <= 1 | Lifetime range looped by ringBufferMode LoopUntilReplaced (x <= y). |

Axis flag `startSize3D`: false -> `startSize`; true -> `startSizeX`, `startSizeY`, `startSizeZ`.

Axis flag `startRotation3D`: false -> `startRotation`; true -> `startRotationX`, `startRotationY`, `startRotationZ`.
One curve mode for `startSizeX`, `startSizeY`, `startSizeZ` (when `startSize3D` is true).
One curve mode for `startRotationX`, `startRotationY`, `startRotationZ` (when `startRotation3D` is true).

### Module `emission`

`modules.emission` - Emission: rate over time / distance. Bursts are a list with their own skills.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `enabled` | bool |  |  | Whether the module is enabled. |
| `rateOverTime` | MinMaxCurve | particles/second | >= 0 | Particles emitted per second. |
| `rateOverDistance` | MinMaxCurve | particles/unit | >= 0 | Particles emitted per unit the emitter moves. |
| `burstCount` | int |  |  | **read-only**: Bursts are written with particle_set_bursts. Number of bursts. |

### Module `shape`

`modules.shape` - Shape: emission volume or surface and its local transform. Mesh/sprite/texture shapes arrive in P1.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `enabled` | bool |  |  | Whether the module is enabled. |
| `shapeType` | enum: Sphere / Hemisphere / Cone / ConeVolume / Box / BoxShell / BoxEdge / Circle / SingleSidedEdge / Donut / Rectangle (readable: all) |  |  | Emitter shape. Mesh, MeshRenderer, SkinnedMeshRenderer, Sprite and SpriteRenderer need object references (P1) and are readable only. |
| `angle` | float | degrees | >= 0, <= 90 | Cone opening angle (Cone, ConeVolume). |
| `radius` | float | units | >= 0.0001 | Shape radius (Sphere, Hemisphere, Cone, ConeVolume, Circle, SingleSidedEdge, Donut). |
| `radiusThickness` | float | ratio | >= 0, <= 1 | 0 emits from the surface, 1 from the whole volume. |
| `arc` | float | degrees | >= 0, <= 360 | Portion of the circle that emits (Cone, ConeVolume, Circle, Donut). |
| `arcMode` | enum: Random / Loop / PingPong / BurstSpread |  |  | How emission positions move around the arc. |
| `arcSpread` | float | ratio | >= 0, <= 1 | Discrete arc intervals emission is snapped to (0 = continuous). |
| `length` | float | units | >= 0 | Cone length (ConeVolume). |
| `donutRadius` | float | units | >= 0.0001 | Thickness of the ring (Donut). |
| `position` | Vector3 | units |  | Shape offset in the system's local space. |
| `rotation` | Vector3 | degrees |  | Shape rotation as Euler angles in DEGREES (unlike main.startRotation). |
| `scale` | Vector3 | multiplier |  | Shape scale. |
| `alignToDirection` | bool |  |  | Orient particles along their emission direction. |
| `randomDirectionAmount` | float | ratio | >= 0, <= 1 | Blend towards a random direction. |
| `sphericalDirectionAmount` | float | ratio | >= 0, <= 1 | Blend towards a direction away from the shape centre. |
| `randomPositionAmount` | float | units | >= 0 | Random offset added to the emission position. |

### Module `velocityOverLifetime`

`modules.velocityOverLifetime` - Velocity over Lifetime: linear velocity curves, space, radial velocity and speed modifier. Orbital velocity arrives in P1.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `enabled` | bool |  |  | Whether the module is enabled. |
| `x` | MinMaxCurve | units/second |  | Velocity along X (x, y and z must share one mode). |
| `y` | MinMaxCurve | units/second |  | Velocity along Y. |
| `z` | MinMaxCurve | units/second |  | Velocity along Z. |
| `space` | enum: Local / World (readable: all) |  |  | Space of x/y/z. |
| `radial` | MinMaxCurve | units/second |  | Velocity away from the system centre. |
| `speedModifier` | MinMaxCurve | multiplier |  | Speed multiplier that keeps the direction. |
One curve mode for `x`, `y`, `z`.

### Module `limitVelocityOverLifetime` (read-only, P1)

`modules.limitVelocityOverLifetime` - Limit Velocity over Lifetime: per-axis speed limit, dampen, drag.

### Module `inheritVelocity` (read-only, P1)

`modules.inheritVelocity` - Inherit Velocity: how much emitter velocity particles inherit.

### Module `lifetimeByEmitterSpeed` (read-only, P1)

`modules.lifetimeByEmitterSpeed` - Lifetime by Emitter Speed: start lifetime mapped from emitter speed.

### Module `forceOverLifetime` (read-only, P1)

`modules.forceOverLifetime` - Force over Lifetime: force curves, space, randomize.

### Module `colorOverLifetime`

`modules.colorOverLifetime` - Color over Lifetime: a gradient multiplied with the start colour.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `enabled` | bool |  |  | Whether the module is enabled. |
| `color` | MinMaxGradient (Gradient, TwoGradients) | linear RGBA (HDR allowed) |  | Colour and alpha over the particle lifetime (Gradient or TwoGradients, as in the Inspector). |

### Module `colorBySpeed` (read-only, P1)

`modules.colorBySpeed` - Color by Speed: speed range and gradient.

### Module `sizeOverLifetime`

`modules.sizeOverLifetime` - Size over Lifetime: size multiplier curves.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `enabled` | bool |  |  | Whether the module is enabled. |
| `separateAxes` | bool |  |  | Use x/y/z instead of size. |
| `size` | MinMaxCurve (Curve, TwoCurves) | multiplier of the start size |  | Size multiplier (while separateAxes is false; stored as x). |
| `x` | MinMaxCurve (Curve, TwoCurves) | multiplier of the start size |  | Size multiplier along X (separateAxes only). |
| `y` | MinMaxCurve (Curve, TwoCurves) | multiplier of the start size |  | Size multiplier along Y (separateAxes only). |
| `z` | MinMaxCurve (Curve, TwoCurves) | multiplier of the start size |  | Size multiplier along Z (separateAxes only). |

Axis flag `separateAxes`: false -> `size`; true -> `x`, `y`, `z`.
One curve mode for `x`, `y`, `z` (when `separateAxes` is true).

### Module `sizeBySpeed` (read-only, P1)

`modules.sizeBySpeed` - Size by Speed: speed range and size curves.

### Module `rotationOverLifetime` (read-only, P1)

`modules.rotationOverLifetime` - Rotation over Lifetime: per-axis angular velocity.

### Module `rotationBySpeed` (read-only, P1)

`modules.rotationBySpeed` - Rotation by Speed: speed range and angular velocity.

### Module `externalForces` (read-only, P1)

`modules.externalForces` - External Forces: multiplier and force fields.

### Module `noise`

`modules.noise` - Noise: turbulent movement, with optional effect on rotation and size.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `enabled` | bool |  |  | Whether the module is enabled. |
| `separateAxes` | bool |  |  | Use strengthX/Y/Z and remapX/Y/Z instead of strength and remap. |
| `strength` | MinMaxCurve | units |  | Noise strength (while separateAxes is false; stored as strengthX). |
| `strengthX` | MinMaxCurve | units |  | Noise strength on X (separateAxes only). |
| `strengthY` | MinMaxCurve | units |  | Noise strength on Y (separateAxes only). |
| `strengthZ` | MinMaxCurve | units |  | Noise strength on Z (separateAxes only). |
| `frequency` | float | 1/units | >= 0.0001 | Noise frequency: low is smooth, high is erratic. |
| `scrollSpeed` | MinMaxCurve | noise field scroll per second |  | Moves the noise field over time. |
| `damping` | bool |  |  | Scale strength with frequency. |
| `octaveCount` | int | octaves | >= 1, <= 4 | Layers of noise combined. |
| `octaveMultiplier` | float | ratio | >= 0, <= 1 | Strength factor of each further octave. |
| `octaveScale` | float | multiplier | >= 1, <= 4 | Frequency factor of each further octave. |
| `quality` | enum: Low / Medium / High |  |  | Noise quality (1D/2D/3D). |
| `remapEnabled` | bool |  |  | Remap the final noise values with remap / remapX/Y/Z. |
| `remap` | MinMaxCurve | noise value |  | Remap curve (while separateAxes is false; stored as remapX). |
| `remapX` | MinMaxCurve | noise value |  | Remap curve on X (separateAxes only). |
| `remapY` | MinMaxCurve | noise value |  | Remap curve on Y (separateAxes only). |
| `remapZ` | MinMaxCurve | noise value |  | Remap curve on Z (separateAxes only). |
| `positionAmount` | MinMaxCurve | multiplier |  | How much the noise moves particles. |
| `rotationAmount` | MinMaxCurve | degrees/second |  | How much the noise rotates particles. |
| `sizeAmount` | MinMaxCurve | multiplier |  | How much the noise scales particles. |

Axis flag `separateAxes`: false -> `strength`, `remap`; true -> `strengthX`, `strengthY`, `strengthZ`, `remapX`, `remapY`, `remapZ`.
One curve mode for `strengthX`, `strengthY`, `strengthZ` (when `separateAxes` is true).
One curve mode for `remapX`, `remapY`, `remapZ` (when `separateAxes` is true).

### Module `collision` (read-only, P1)

`modules.collision` - Collision: world/planes, bounce, dampen, lifetime loss.

### Module `trigger` (read-only, P1)

`modules.trigger` - Triggers: collider list and enter/exit behaviour.

### Module `subEmitters` (read-only, P1)

`modules.subEmitters` - Sub Emitters: child systems spawned on events (particle_get_sub_emitters in P1).

### Module `textureSheetAnimation` (read-only, P1)

`modules.textureSheetAnimation` - Texture Sheet Animation: grid/sprites and frame curves.

### Module `lights` (read-only, P1)

`modules.lights` - Lights: light reference, ratio, range and intensity.

### Module `trails` (read-only, P1)

`modules.trails` - Trails: mode, ratio, lifetime, width and colour.

### Module `customData` (read-only, P1)

`modules.customData` - Custom Data: Custom1/Custom2 vector or colour streams.

### Module `renderer`

`renderer` - The ParticleSystemRenderer component: mode, material, sorting, alignment and display parameters.

| Field | Type | Unit | Range | Notes |
|---|---|---|---|---|
| `enabled` | bool |  |  | Whether the renderer draws. |
| `renderMode` | enum: Billboard / Stretch / HorizontalBillboard / VerticalBillboard / Mesh / None |  |  | How particles are drawn. Mesh needs a mesh. |
| `material` | AssetReference (Material, nullable) |  |  | Shared material asset (never an implicit instance); the material asset itself is not modified. null clears it. |
| `mesh` | AssetReference (Mesh, nullable) |  |  | Mesh drawn per particle in renderMode Mesh (a mesh list arrives in P1). null clears it. |
| `sortMode` | enum: None / Distance / OldestInFront / YoungestInFront / Depth / DistanceReverse / DepthReverse |  |  | Particle draw order within the system. |
| `sortingFudge` | float | units |  | Bias of the system in transparent sorting; lower draws later (on top). |
| `sortingLayerName` | sortingLayerName |  |  | Sorting layer (must exist in Tags and Layers). |
| `sortingOrder` | int | order | >= -32768, <= 32767 | Order within the sorting layer. |
| `alignment` | enum: View / World / Local / Facing / Velocity |  |  | Billboard facing: View, World, Local, Facing or Velocity. |
| `pivot` | Vector3 | particle sizes |  | Pivot offset in multiples of the particle size. |
| `flip` | Vector3 | ratio | >= 0, <= 1 | Fraction of particles flipped on each axis. |
| `allowRoll` | bool |  |  | Let camera-facing particles roll with the camera. |
| `minParticleSize` | float | fraction of the viewport | >= 0 | Smallest on-screen size. |
| `maxParticleSize` | float | fraction of the viewport | >= 0 | Largest on-screen size. |
| `normalDirection` | float | ratio | >= 0, <= 1 | Billboard lighting normal: 1 faces the camera, 0 points away from the centre. |
| `cameraVelocityScale` | float | multiplier |  | Stretch from camera movement (renderMode Stretch). |
| `velocityScale` | float | multiplier |  | Stretch proportional to speed (renderMode Stretch). |
| `lengthScale` | float | multiplier |  | Stretch proportional to size (renderMode Stretch). |
| `freeformStretching` | bool |  |  | Stretch without keeping the camera-facing orientation (renderMode Stretch). |
| `rotateWithStretchDirection` | bool |  |  | Rotate stretched particles with their direction (renderMode Stretch). |
| `shadowCastingMode` | enum: Off / On / TwoSided / ShadowsOnly |  |  | Whether particles cast shadows. |
| `receiveShadows` | bool |  |  | Whether particles receive shadows. |
| `shadowBias` | float | particle sizes | >= 0 | Shadow offset along the light direction. |
| `maskInteraction` | enum: None / VisibleInsideMask / VisibleOutsideMask |  |  | Interaction with Sprite Masks. |
