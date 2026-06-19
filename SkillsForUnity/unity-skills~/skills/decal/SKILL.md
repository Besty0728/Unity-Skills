---
name: unity-decal
description: Create and configure URP Decal Projectors plus DecalRendererFeature setup — project decals onto surfaces and wire the renderer feature. Use when adding decals in URP, configuring a Decal Projector, or enabling the Decal Renderer Feature, even if the user just says "贴花" or "decal". 创建与配置 URP Decal Projector 并设置 DecalRendererFeature(将贴花投射到表面、接入渲染器特性);当用户要在 URP 中添加贴花、配置 Decal Projector 或启用 Decal Renderer Feature 时使用。
---

# Decal Skills

URP Decal Projector creation and configuration (URP only; HDRP decal APIs are not covered here).

## Operating Mode

- Query skills (`decal_get_info`, `decal_find_all`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- Mutating skills (`decal_create`, `decal_set_properties`, `decal_set_properties_batch`, `decal_ensure_renderer_feature`) are `SkillMode.FullAuto` — under **Approval** they need user grant (grant triggers one server-side execute returning the result); under **Auto** / **Bypass** they execute directly.
- `decal_delete` carries `SkillOperation.Delete` and is **auto-forbidden** in Approval / Auto modes (NeverInSemi). Only **Bypass** or the user-managed **Allowlist** can run it.

## URP Package Stub

This module is compiled against `com.unity.render-pipelines.universal` (`URP`). When URP is not installed, **every** skill returns a stub `{ error: "Universal Render Pipeline package … is not installed." }` (`RenderPipelineSkillsCommon.NoURP()`). The stub is a diagnostic payload, not a permission denial — it does **not** require grant and is **not** treated as NeverInSemi.

## Guardrails

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
