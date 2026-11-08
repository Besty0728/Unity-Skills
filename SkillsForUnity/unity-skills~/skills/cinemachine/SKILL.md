---
name: unity-cinemachine
description: Set up Cinemachine Virtual Cameras
---

# Unity Cinemachine Skills

Create and tune Cinemachine Virtual Cameras, camera managers, target groups, extensions, impulse and the brain. Parameters come from the schema (`GET /skills/schema?category=Cinemachine&wire=v2`), which notes the non-obvious ones. Execution, dryRun and verification rules: root [SKILL.md](../../SKILL.md).

## When to use
- Creating or tuning Cinemachine cameras
- Configuring follow, look-at and noise
- Building cinematic camera behavior

## Rules
- Gate: `cinemachine_inspect_vcam`, `cinemachine_list_components` and `cinemachine_get_brain_info` are read-only and run under Approval. Every other skill is `FullAuto` and needs a grant.
- Delete-class skills are `MODE_FORBIDDEN` under Approval and Auto; only Bypass or an Allowlist hit runs them: `cinemachine_set_component`, `cinemachine_target_group_remove_member`, `cinemachine_remove_extension`. The gate is static, from the attribute flags, so it ignores the arguments: every `cinemachine_set_component` call is blocked, including `componentType="None"` and a switch to another component of the same stage ([operating mode](../../references/protocol-operating-mode.md)).
- `cinemachine_set_component` removes the stage's current pipeline component before it adds the requested one. `"None"` only removes it, and the same type is a no-op.
- Package: needs `com.unity.cinemachine` (2.x or 3.x). Without it every skill returns `MISSING_PACKAGE`; check with the `package_*` skills first.
- 2.x vs 3.x: a reflection adapter (`CinemachineAdapter` / `CinemachineSkills`) bridges both, so skills work on both. CM2 uses `CinemachineVirtualCamera`, CM3 `CinemachineCamera`; skills handle this. CM3 versions before `3.0.0-pre.5` changed core camera APIs and may fail from API drift. In compatibility code, use `Priority.Value` for CM3 priority access.
- Probe first: run `cinemachine_inspect_vcam` to see the fields the installed version exposes before choosing `propertyName`; `cinemachine_list_components` lists the component names.
- VCam targeting: `vcamName`, `instanceId`, `path` or `entityId` (preferred; the only id on Unity 6000.4+), at least one. Skills that address several objects prefix each identifier set (`group*`, `target*`, `vcam*`, `spline*`, `mixer*`, `child*`, `camera*`, `sequencer*`, `source*`).
- `CinemachineBrain` is added to the Main Camera on the first VCam creation; there is no add-brain skill.
- Higher priority wins activation; `cinemachine_set_active` forces SOLO by setting the highest priority.
- `cinemachine_configure_body` and `cinemachine_configure_aim` apply only the fields of the active component and fail with `warnings` listing the rest when none matches.
- Routing: basic Game Camera operations and the Scene View camera go to the [camera](../camera/SKILL.md) module; camera animation sequences go to [timeline](../timeline/SKILL.md) with a Cinemachine track.

## Pitfalls
- `cinemachine_create` does not exist: use `cinemachine_create_vcam` for virtual cameras.
- `cinemachine_set_target`, `cinemachine_set_follow` and `cinemachine_set_lookat` do not exist: use `cinemachine_set_targets`, which sets Follow and LookAt together.
- `cinemachine_add_brain` does not exist.
- Do not pick `CinemachineVirtualCamera` or `CinemachineCamera` by version; the skills handle both.
- `SEMANTIC_INVALID` (invalid `mode`, blend style, unresolved animator or bounding shape): fix per `validValues` or the message; nothing was written.

## Skills Overview

- VCam lifecycle and state: `cinemachine_create_vcam`, `cinemachine_create_freelook`, `cinemachine_inspect_vcam`, `cinemachine_list_components`, `cinemachine_set_active`, `cinemachine_set_priority`, `cinemachine_set_targets` (Follow and LookAt together).
- Properties and pipeline: `cinemachine_set_vcam_property`, `cinemachine_set_component`, `cinemachine_add_component` (deprecated), `cinemachine_set_lens`, `cinemachine_set_noise`, `cinemachine_set_spline`, `cinemachine_configure_body`, `cinemachine_configure_aim`.
- Extensions and impulse: `cinemachine_add_extension`, `cinemachine_remove_extension`, `cinemachine_configure_extension`, `cinemachine_impulse_generate`, `cinemachine_configure_impulse_source`.
- Brain: `cinemachine_get_brain_info`, `cinemachine_set_blend`, `cinemachine_set_brain`.
- Target groups: `cinemachine_create_target_group`, `cinemachine_target_group_add_member`, `cinemachine_target_group_remove_member`.
- Camera managers: `cinemachine_create_mixing_camera`, `cinemachine_mixing_camera_set_weight`, `cinemachine_create_clear_shot`, `cinemachine_create_state_driven_camera`, `cinemachine_state_driven_camera_add_instruction`, `cinemachine_create_sequencer`, `cinemachine_sequencer_add_instruction`, `cinemachine_configure_camera_manager`.

No batch variants exist in this module. Per-skill tables, item fields and return shapes: `reference/<skill_name>.md`, rarely needed because the schema notes the parameters.
