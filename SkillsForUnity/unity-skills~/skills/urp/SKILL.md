---
name: unity-urp
description: "URP asset and renderer feature management. Use when users want to inspect or modify the active URP asset, list renderers, or add/remove built-in renderer features. Triggers: URP asset, renderer feature, renderer data, SSAO, fullscreen pass, ScreenSpaceReflection, URP设置, 渲染器特性."
---

# URP Skills

URP-specific asset and renderer feature management for Unity 2022.3+.

## Guardrails

**Mode**: Mixed — query skills marked SkillMode.SemiAuto; mutators are SkillMode.FullAuto (need grant under Approval)

> Some skills (Delete / PlayMode / Reload / high-risk) are auto-forbidden in Approval/Auto modes — only Bypass can run them.

**DO NOT**:
- Use this module for ShaderGraph
- Assume arbitrary custom renderer features are safe to instantiate
- Assume Unity 2022 and Unity 6 expose the same built-in renderer features

**Runtime-first rules**:
- Always call `urp_get_info` before `urp_add_renderer_feature`
- Only use names returned by `urp_get_info.creatableRendererFeatures`
- Do not hardcode `RenderObjects`, `FullScreenPassRendererFeature`, `ScreenSpaceReflectionRendererFeature`, etc. as universally available
- Use `urp_list_renderer_features` to resolve existing feature names/indexes before calling `urp_set_renderer_feature_active` or `urp_remove_renderer_feature`

**Validated behavior**:
- Unity 2022.3 + URP 14 real environment may expose a smaller creatable set, e.g. `DecalRendererFeature` and `ScreenSpaceAmbientOcclusion`
- Unity 6 + URP 17 real environment may expose a larger set

## Skills

### `urp_get_info`
Inspect the active URP asset and renderer layout.

### `urp_set_asset_settings`
Modify key URP asset settings like HDR, MSAA, render scale, shadows, and camera textures.

### `urp_list_renderers`
List renderer data assets on the active URP asset.

### `urp_list_renderer_features`
List renderer features on a specific renderer.

### `urp_add_renderer_feature`
Add a safe built-in renderer feature to a renderer.

### `urp_remove_renderer_feature`
Remove a renderer feature from a renderer.

### `urp_set_renderer_feature_active`
Enable or disable a renderer feature.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
