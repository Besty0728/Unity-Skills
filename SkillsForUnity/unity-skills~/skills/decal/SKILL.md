---
name: unity-decal
description: "URP Decal Projector management. Use when users want to create, inspect, configure, batch edit, or validate DecalProjectors and DecalRendererFeature setup. Triggers: decal, decal projector, URP decal, DecalRendererFeature, 贴花, DecalProjector."
---

# Decal Skills

URP decal projector creation and configuration.

## Guardrails

**Mode**: Mixed — query skills marked SkillMode.SemiAuto; mutators are SkillMode.FullAuto (need grant under Approval)

> Some skills (Delete / PlayMode / Reload / high-risk) are auto-forbidden in Approval/Auto modes — only Bypass can run them.

**Routing**:
- For renderer feature management in general: `urp`
- For DecalProjector scene operations: this module

**Runtime-first rules**:
- Call `decal_ensure_renderer_feature` before assuming the current URP renderer is decal-ready
- Use `decal_get_info` / `decal_find_all` to discover real projector state before editing
- `decal_set_properties_batch` expects `items` to be a JSON array string
- This module targets the URP Decal workflow first; do not assume HDRP decal APIs are covered here

## Skills

### `decal_create`
Create a Decal Projector.

### `decal_get_info`
Inspect a Decal Projector.

### `decal_set_properties`
Modify Decal Projector properties.

### `decal_find_all`
List Decal Projectors in the scene.

### `decal_delete`
Delete a Decal Projector GameObject.

### `decal_set_properties_batch`
Batch-edit Decal Projectors.

### `decal_ensure_renderer_feature`
Ensure the target URP renderer has a DecalRendererFeature.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
