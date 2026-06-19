---
name: unity-urp
description: Manage the Universal Render Pipeline (URP) — URP assets, the renderer, and renderer features. Use when configuring the URP asset, adding or editing renderer features, or adjusting URP rendering settings, even if the user just says "URP配置" or "渲染特性". 管理通用渲染管线(URP:URP 资产、渲染器、渲染器特性);当用户要配置 URP 资产、添加或编辑渲染器特性、或调整 URP 渲染设置时使用。
---

# URP Skills

URP-specific asset and renderer feature management for Unity 2022.3+ (URP 14 and Unity 6 / URP 17).

## Operating Mode

- Query skills (`urp_get_info`, `urp_list_renderers`, `urp_list_renderer_features`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- Mutating skills (`urp_set_asset_settings`, `urp_add_renderer_feature`, `urp_set_renderer_feature_active`) are `SkillMode.FullAuto` — under **Approval** they need user grant (grant triggers one server-side execute returning the result); under **Auto** / **Bypass** they execute directly.
- `urp_remove_renderer_feature` carries `SkillOperation.Delete` and is **auto-forbidden** in Approval / Auto modes (NeverInSemi). Only **Bypass** or the user-managed **Allowlist** can run it.

## URP Package Stub

This module is compiled against `com.unity.render-pipelines.universal` (`URP`). When URP is not installed, **every** skill returns a stub `{ error: "Universal Render Pipeline package … is not installed." }` (`RenderPipelineSkillsCommon.NoURP()`). The stub is a diagnostic payload, not a permission denial — it does **not** require grant and is **not** treated as NeverInSemi. Call `project_get_render_pipeline` first when you see this error.

## Guardrails

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
