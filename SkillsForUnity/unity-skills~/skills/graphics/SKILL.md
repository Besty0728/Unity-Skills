---
name: unity-graphics
description: Manage project-wide GraphicsSettings and QualitySettings
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Configuring project graphics/quality settings
- Assigning an SRP asset
- Adjusting quality levels
- 配置工程图形/画质设置、指定 SRP 资产、调整质量等级

# Graphics Skills

Project-wide graphics and quality settings (GraphicsSettings + QualitySettings) for Unity 2022.3+. Works in Built-in, URP and HDRP — does not depend on any SRP package being installed.

## Operating Mode

- Query skills (`graphics_get_overview`, `graphics_get_quality_settings`, `graphics_get_render_pipeline_assets`, `graphics_list_always_included_shaders`, `graphics_get_shader_stripping`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- Mutating skills (`graphics_set_quality_level`, `graphics_set_default_render_pipeline`, `graphics_set_quality_render_pipeline`, `graphics_add_always_included_shader`, `graphics_remove_always_included_shader`, `graphics_set_shader_stripping`) are `SkillMode.FullAuto` — under **Approval** they need user grant (grant triggers one server-side execute returning the result); under **Auto** / **Bypass** they execute directly.
- This module contains **no** Delete / PlayMode / Reload / high-risk skills (no NeverInSemi). `graphics_remove_always_included_shader` is a list mutation, not a `SkillOperation.Delete`.

## Guardrails

**Routing**:
- For current render pipeline detection only: `project_get_render_pipeline`
- For SRP/quality configuration: use this module, not `project_*`

## Skills

### `graphics_get_overview`
Get a graphics/quality/render-pipeline summary.

### `graphics_get_quality_settings`
List quality levels and their render pipeline overrides.

### `graphics_set_quality_level`
Switch the active quality level.

### `graphics_get_render_pipeline_assets`
List default/current/per-quality render pipeline assets.

### `graphics_set_default_render_pipeline`
Set or clear the default SRP asset.

### `graphics_set_quality_render_pipeline`
Assign or clear the SRP asset for a specific quality level.

### `graphics_list_always_included_shaders`
List shaders in Always Included Shaders.

### `graphics_add_always_included_shader`
Add a shader to Always Included Shaders.

### `graphics_remove_always_included_shader`
Remove a shader from Always Included Shaders.

### `graphics_get_shader_stripping`
Inspect shader stripping configuration in GraphicsSettings.

### `graphics_set_shader_stripping`
Modify shader stripping configuration in GraphicsSettings.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
