# Unity Graphics Skills

Project-wide graphics and quality settings (GraphicsSettings and QualitySettings) for Unity 2022.3+. Works in Built-in, URP and HDRP, and does not depend on any SRP package being installed. Parameters come from the schema (`GET /skills/schema?category=Graphics&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Configuring project graphics and quality settings
- Assigning an SRP asset
- Adjusting quality levels

## Rules
- Query skills (`graphics_get_overview`, `graphics_get_quality_settings`, `graphics_get_render_pipeline_assets`, `graphics_list_always_included_shaders`, `graphics_get_shader_stripping`) are read-only and run under Approval.
- Mutating skills (`graphics_set_quality_level`, `graphics_set_default_render_pipeline`, `graphics_set_quality_render_pipeline`, `graphics_add_always_included_shader`, `graphics_remove_always_included_shader`, `graphics_set_shader_stripping`) need a grant under Approval. `graphics_remove_always_included_shader` edits a list; it is not delete-class.
- For the detected render pipeline only, use `project_get_render_pipeline`. SRP and quality configuration belongs to this module.

## Skills

### graphics_get_overview
Get a graphics/quality/render-pipeline summary.

### graphics_get_quality_settings
List quality levels and their render pipeline overrides.

### graphics_set_quality_level
Switch the active quality level.

### graphics_get_render_pipeline_assets
List default/current/per-quality render pipeline assets.

### graphics_set_default_render_pipeline
Set or clear the default SRP asset.

### graphics_set_quality_render_pipeline
Assign or clear the SRP asset for a specific quality level.

### graphics_list_always_included_shaders
List shaders in Always Included Shaders.

### graphics_add_always_included_shader
Add a shader to Always Included Shaders.

### graphics_remove_always_included_shader
Remove a shader from Always Included Shaders.

### graphics_get_shader_stripping
Inspect shader stripping configuration in GraphicsSettings.

### graphics_set_shader_stripping
Modify shader stripping configuration in GraphicsSettings.
