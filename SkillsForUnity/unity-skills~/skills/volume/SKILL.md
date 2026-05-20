---
name: unity-volume
description: "SRP Volume framework management. Use when users want to create VolumeProfiles, create global/local Volumes, add/remove VolumeComponents, or set override parameters. Triggers: volume, volume profile, post-processing profile, override, VolumeComponent, 后处理配置, Volume, VolumeProfile."
---

# Volume Skills

Shared SRP Volume framework skills for Unity 2022.3+.

## Guardrails

**Mode**: Mixed — query skills marked SkillMode.SemiAuto; mutators are SkillMode.FullAuto (need grant under Approval)

> Some skills (Delete / PlayMode / Reload / high-risk) are auto-forbidden in Approval/Auto modes — only Bypass can run them.

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
