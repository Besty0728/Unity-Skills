---
name: unity-graphics
description: "Graphics and quality settings for SRP-era Unity projects. Use when users want GraphicsSettings, QualitySettings, default SRP, Always Included Shaders, or shader stripping. Triggers: graphics settings, quality settings, SRP asset, always included shaders, shader stripping, 图形设置, 质量设置, 渲染管线设置."
---

# Graphics Skills

Graphics and quality settings management for Unity 2022.3+ projects.

## Guardrails

**Mode**: Mixed — query skills marked SkillMode.SemiAuto; mutators are SkillMode.FullAuto (need grant under Approval)

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
