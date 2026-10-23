---
name: unity-cinemachine
description: Set up Cinemachine Virtual Cameras
---

## Triggers
- Creating or tuning Cinemachine cameras
- Configuring follow/look-at/noise
- Building cinematic camera behavior
- 创建或调校 Cinemachine 相机、设置跟随/注视/噪声、构建运镜效果

# Cinemachine Skills

Create and tune Cinemachine Virtual Cameras, camera managers, target groups, extensions, impulse and the brain. Parameters come from the schema (recommend `includeSchema=true` or `/skills/schema?names=`), which notes the non-obvious ones — never from a skill's name. Execution, dryRun and verification rules: root [SKILL.md](../../SKILL.md).

## Operating Mode
`cinemachine_inspect_vcam`, `cinemachine_list_components` and `cinemachine_get_brain_info` are `SemiAuto` (read-only) and run directly under Approval; every other skill (create, set, configure, ...) is `FullAuto` and needs a user grant, after which the server runs it in one step and returns the result. Under Auto / Bypass, skills not on the forbidden list run directly.

`cinemachine_set_component` (replaces or removes a pipeline component), `cinemachine_target_group_remove_member` and `cinemachine_remove_extension` are `Operation.Delete`, hence NeverInSemi: `MODE_FORBIDDEN` under Approval/Auto, run only under Bypass or an Allowlist hit → [operating mode](../../references/protocol-operating-mode.md).

## Shared rules

- **Package**: needs `com.unity.cinemachine` (CM 2.x or 3.x). Without it every skill returns the stub `{ error = "Cinemachine is not installed. Install Cinemachine 2.x or 3.x via Package Manager." }` (`MISSING_PACKAGE`); check the install state with the `package_*` skills first.
- **2.x vs 3.x**: a runtime reflection adapter (`CinemachineAdapter` / `CinemachineSkills`) bridges the two APIs, so skills work on both. CM2 uses `CinemachineVirtualCamera`, CM3 `CinemachineCamera` (and `CinemachineComponentBase` was renamed); skills handle this automatically. CM3 previews before `3.0.0-pre.5` changed core camera APIs significantly, are outside the support baseline and may fail from API drift. When writing compatibility code, CM3 priority access should use `Priority.Value` as the lowest common API.
- **Probe before setting**: because of that fragility, run `cinemachine_inspect_vcam` first to see the fields the installed version exposes, then pick `propertyName`; `cinemachine_list_components` lists the available component names.
- **VCam targeting**: VCam skills take `vcamName`, `instanceId`, `path` or `entityId` (preferred; the only id on Unity 6000.4+); give at least one. Skills that address several objects prefix each identifier set (`group*`, `target*`, `vcam*`, `spline*`, `mixer*`, `child*`, `camera*`, `sequencer*`, `source*`).
- **Brain**: `CinemachineBrain` is added to the Main Camera automatically on the first VCam creation — there is no add-brain skill.
- **Priority**: higher priority wins activation; `cinemachine_set_active` forces SOLO by setting the highest priority.
- **Pipeline stages**: Body / Aim / Noise components are switched with `cinemachine_set_component`; `cinemachine_configure_body` and `cinemachine_configure_aim` apply only the fields matching the active component and fail with `warnings` listing the rest when none matches.

## Skills Overview

- VCam lifecycle and state: `cinemachine_create_vcam`, `cinemachine_create_freelook`, `cinemachine_inspect_vcam`, `cinemachine_list_components`, `cinemachine_set_active`, `cinemachine_set_priority`, `cinemachine_set_targets` (Follow and LookAt together).
- Properties and pipeline: `cinemachine_set_vcam_property`, `cinemachine_set_component`, `cinemachine_add_component` (deprecated), `cinemachine_set_lens`, `cinemachine_set_noise`, `cinemachine_set_spline`, `cinemachine_configure_body`, `cinemachine_configure_aim`.
- Extensions and impulse: `cinemachine_add_extension`, `cinemachine_remove_extension`, `cinemachine_configure_extension`, `cinemachine_impulse_generate`, `cinemachine_configure_impulse_source`.
- Brain: `cinemachine_get_brain_info`, `cinemachine_set_blend`, `cinemachine_set_brain`.
- Target groups: `cinemachine_create_target_group`, `cinemachine_target_group_add_member`, `cinemachine_target_group_remove_member`.
- Camera managers: `cinemachine_create_mixing_camera`, `cinemachine_mixing_camera_set_weight`, `cinemachine_create_clear_shot`, `cinemachine_create_state_driven_camera`, `cinemachine_state_driven_camera_add_instruction`, `cinemachine_create_sequencer`, `cinemachine_sequencer_add_instruction`, `cinemachine_configure_camera_manager`.

No batch variants exist in this module.

Per-skill tables, item fields and return shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `cinemachine_create` does not exist → use `cinemachine_create_vcam` for virtual cameras
- `cinemachine_set_target` / `cinemachine_set_follow` / `cinemachine_set_lookat` do not exist → use `cinemachine_set_targets` (sets both Follow and LookAt in one call)
- `cinemachine_add_brain` does not exist → CinemachineBrain is auto-added to Main Camera on first VCam creation
- `CinemachineVirtualCamera` (2.x) vs `CinemachineCamera` (3.x) → do not pick by version; skills handle this automatically

**Routing**:
- Basic Game Camera operations → `camera` module
- Scene View camera → `camera` module's `camera_set_transform` / `camera_look_at`
- Camera animation sequences → `timeline` module with a Cinemachine track

## Exact Signatures

Names, parameters, defaults and required flags come from `GET /skills/schema?category=Cinemachine` (or `?names=a,b`), not this file.

## Common Errors

| Error | Trigger → fix |
|---|---|
| `MISSING_PACKAGE` | Cinemachine not installed → install 2.x or 3.x via `package_*`. |
| `MODE_FORBIDDEN` | A Delete-class skill under Approval/Auto → needs Bypass or an Allowlist hit. |
| `SEMANTIC_INVALID` | Invalid `mode` / blend style / unresolved animator or bounding shape → fix per `validValues` / the message; nothing was written. |

Transport codes (`COMPILING`, `RATE_LIMIT`, ...) → [error codes](../../references/protocol-error-codes.md).
