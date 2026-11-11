# Unity URP Skills

URP asset and renderer-feature management for Unity 2022.3+ (URP 14 and Unity 6 / URP 17). Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Configuring the URP asset
- Adding, editing or toggling renderer features
- Adjusting URP rendering settings

## Rules
- Call `urp_get_info` before `urp_add_renderer_feature`, and use only names from its `creatableRendererFeatures`.
- Resolve existing feature names and indexes with `urp_list_renderer_features` before `urp_set_renderer_feature_active` or `urp_remove_renderer_feature`.
- The creatable set depends on the version: Unity 2022.3 with URP 14 may expose a smaller set (for example `DecalRendererFeature`, `ScreenSpaceAmbientOcclusion`), Unity 6 with URP 17 a larger one. Do not assume `RenderObjects`, `FullScreenPassRendererFeature` or `ScreenSpaceReflectionRendererFeature` exist.
- Without the URP package every skill returns `{ error: "Universal Render Pipeline package … is not installed." }` (`RenderPipelineSkillsCommon.NoURP()`). This is a diagnostic payload, not a permission denial; call `project_get_render_pipeline` first.
- Do not instantiate arbitrary custom renderer features; this module covers only built-in ones.
- ShaderGraph is not in this module; see [shadergraph](../shadergraph/MODULE.md).

## Pitfalls
- Unity 2022 and Unity 6 do not expose the same built-in renderer features.

## Skills

### urp_get_info
Inspect the active URP asset and renderer layout.

### urp_set_asset_settings
Modify key URP asset settings such as HDR, MSAA, render scale, shadows and camera textures.

### urp_list_renderers
List renderer data assets on the active URP asset.

### urp_list_renderer_features
List renderer features on a specific renderer.

### urp_add_renderer_feature
Add a built-in renderer feature to a renderer.

### urp_remove_renderer_feature
Remove a renderer feature from a renderer.

### urp_set_renderer_feature_active
Enable or disable a renderer feature.
