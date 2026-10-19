# DC4 — Ballistic Launcher Design Notes

Status 2026-10-19: redesigned for live grading (`track = "dev"`, `tier = "challenge"`, `caseVersion = 1`, `minutes = 45`, `needs_focus = True`, `requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}`).

## Why the redesign

The real `component_invoke_method` returns a GameObject only as `{entityId, instanceId, name, type}`, so launch telemetry cannot ride in `returnValue`; and the flattened-cylinder pads stopped a projectile about 0.7 m from the pad centre. Flight is now measured in the scene by a fixture-owned gauge script, and the pads carry no collider.

## Fixture (`SkillsForUnity/Evaluation~/pack/tasks/DC4/task.py`)

- Seeded `gravityY in {-12, -14, -15, -16}` via `physics_set_gravity` (prior vector in `handles["prevGravity"]`, idempotent teardown); projectile prefab (Sphere, Rigidbody `mass in {1.5, 2.0, 2.5, 3.0}`, `drag in {0.35, 0.45, 0.55}`); `Range > Battery > Muzzle`, decoy `Stand > Muzzle`, `Deck > 3 Cylinder pads` at distinct heights and distances.
- Each pad has its `CapsuleCollider` removed, so projectiles fly through the pad centre.
- A gauge script (`RangeGauge` / `FlightMeter` / `PassRecorder` + `_<word>_<run>`, drawn after all other seeded values) is written under `A/Scripts`, and one gauge component sits on every pad with `origin` (muzzle world position), `mass` and `damping` (the prefab's values) set via `component_set_serialized_property` (comma form `x,y,z` for the Vector3).
- In `FixedUpdate` the gauge tracks every Rigidbody in the scene: `closest` (min segment distance from the body's path to the pad), `bodies` (distinct bodies seen), `offOrigin` (bodies first seen more than 0.15 m from `origin`), `altered` (bodies that are kinematic, have no gravity or differ from `mass`/`damping`). The C# branches on `UNITY_6000_4_OR_NEWER` (`FindObjectsByType` overloads) and `UNITY_6000_0_OR_NEWER` (`linearDamping`).
- C0 records the gauge script sha and each pad's gauge settings.

## Prompt

R6 only: each call spawns exactly one projectile and, within 2.5 s of its launch, its free flight passes within 0.25 m of that call's `target.position` (the targets have no colliders). R1-R5, R7 unchanged.

## Assertions (`eval/pack/private/DC4.py`)

- D1-D3, D6-D8 as before. D4 also compares each pad's gauge settings with C0; D5 also requires the gauge script sha unchanged.
- Scenario: `wait_frames 2`; per pad: read gauge, `Fire(pad)`, `wait_seconds 2.6`, read gauge; then gravity read and the `isPlaying` assert.
- `gauge_ok`: gauge script sha and the pad settings seen in the baseline reads equal C0.
- D9 (binary): scenario passed, seeded gravity, no cheating source, `gauge_ok`, three distinct returned object ids, final reading `bodies == 3`, `altered == 0`, `offOrigin == 0`.
- D10 (fraction of 3): shot i hits when the D9 global conditions hold, the baseline before shot i has `closest > 0.25`, and the reading after it has `closest <= 0.25`, `bodies == i`, `altered == 0`, `offOrigin == 0` (a projectile spawned off the muzzle or with changed Rigidbody settings is no valid flight).
- No trajectory maths in the verifier; the discrete solver and integrator live in `eval/tests/runner/fake_dc4.py`.

## Solutions and negatives (`eval/pack/solutions/DC4.py`)

Reference: discrete FixedUpdate solver (`factor = clamp01(1 - drag * dt)`, 60 steps) with `AddForce(v0, ForceMode.VelocityChange)`; every variant reads drag through `#if UNITY_6000_0_OR_NEWER` (`linearDamping`) / `drag`. Alternatives: `impulse_with_mass`, `save_run_scene`.

| Negative | Fails |
|---|---|
| no_drag, wrong_gravity, fixed_angle, first_target_only, local_space_muzzle | D10 |
| wrong_forcemode | D10 |
| strip_rigidbody_values, spawn_at_pad | D9, D10 |
| on_decoy_stand | D3, D9, D10 |
| field_renamed | D1, D3 (Fire still launches from the renamed field) |
| modified_gravity | D4, D6, D9, D10 (the gravity write is a journaled scope violation) |
| compile_error | D2, D3, D9, D10 |
| edit_gauge | D5, D9, D10 |
| extra_script | D5 |
| wrong_report | D8 |
| outside_root | D6 |
| direct_file | D7 |

## Offline model

`FakeEditorDC4` integrates 130 fixed steps per `Fire` call (v += g*dt; v *= factor; x += v*dt) and updates every pad gauge as the C# does. The live selftest on a URP editor remains the gate (PhysX damping convention, gauge ordering against the spawn).
