---
name: unity-volume
description: Work with the SRP Volume framework — create/load VolumeProfile assets and create global/local Volume GameObjects with components. Use when setting up volumes, creating or loading a VolumeProfile, or adding global/local volumes to a scene, even if the user just says "Volume" or "体积". 使用 SRP Volume 框架(创建/加载 VolumeProfile 资产、创建全局/局部 Volume GameObject 及组件);当用户要搭建 Volume、创建或加载 VolumeProfile、或向场景添加全局/局部 Volume 时使用。
---

# Volume Skills

Shared SRP Volume framework skills for Unity 2022.3+ — works in URP and HDRP via SRP Core.

## Operating Mode

- Query skills (`volume_list_component_types`, `volume_get_component`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- Mutating skills (`volume_profile_create`, `volume_create`, `volume_set_profile`, `volume_add_component`, `volume_set_parameter`, `volume_set_parameter_batch`) are `SkillMode.FullAuto` — under **Approval** they need user grant (grant triggers one server-side execute returning the result); under **Auto** / **Bypass** they execute directly.
- `volume_remove_component` carries `SkillOperation.Delete` and is **auto-forbidden** in Approval / Auto modes (NeverInSemi). Only **Bypass** or the user-managed **Allowlist** can run it.

## SRP Package Stub

This module is compiled against `com.unity.render-pipelines.core` (`SRP_CORE`). When neither URP nor HDRP is installed (no SRP Core), **every** skill returns a stub `{ error: "Scriptable Render Pipeline Core package … is not installed." }` (`RenderPipelineSkillsCommon.NoSRP()`). The stub is a diagnostic payload, not a permission denial — it does **not** require grant and is **not** treated as NeverInSemi. Inspect `project_get_render_pipeline` first when you see this error.

## Guardrails

**Routing**:
- For Volume container/profile CRUD: use this module
- For high-level modern post-processing effects like Bloom/DOF/Tonemapping: prefer `postprocess`

**Runtime-first rules**:
- Always call `volume_list_component_types` before assuming a component type exists on the active pipeline
- Use `volume_get_component` after add/create to inspect the actual parameter names before writing values
- Prefer exact parameter names returned by the live component data instead of guessing from memory
- `volume_set_parameter_batch` expects `items` to be a JSON array string

## Skills

### `volume_profile_create`
Create a VolumeProfile asset.

### `volume_create`
Create a global or local Volume GameObject.

### `volume_set_profile`
Assign or replace the profile on an existing Volume.

### `volume_list_component_types`
List explicit supported VolumeComponent types for the active SRP pipeline.

### `volume_add_component`
Add a VolumeComponent override to a VolumeProfile.

### `volume_remove_component`
Remove a VolumeComponent override from a VolumeProfile.

### `volume_get_component`
Inspect a VolumeComponent override and its parameters.

### `volume_set_parameter`
Set one override parameter on a VolumeComponent.

### `volume_set_parameter_batch`
Set multiple override parameters on one VolumeComponent.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
