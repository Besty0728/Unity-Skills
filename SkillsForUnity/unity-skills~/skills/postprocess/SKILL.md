---
name: unity-postprocess
description: Configure modern SRP post-processing on URP/HDRP VolumeProfiles — add and tune effects like bloom, tonemapping, and color grading. Use when setting up post-processing, adding effects to a VolumeProfile, or tuning the look of a URP/HDRP scene, even if the user just says "后处理" or "加个泛光". 在 URP/HDRP VolumeProfile 上配置现代 SRP 后处理(添加与调校泛光、色调映射、调色等效果);当用户要搭建后处理、向 VolumeProfile 添加效果、或调整 URP/HDRP 场景观感时使用。
---

# PostProcess Skills

Modern URP / HDRP post-processing skills built on top of the SRP Volume framework. For Volume container / profile CRUD (`volume_profile_create`, `volume_create`, etc.), use the `volume` module.

## Operating Mode

- Query skills (`postprocess_list_effects`, `postprocess_get_effect`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- Mutating skills (`postprocess_add_effect`, `postprocess_set_parameter`, `postprocess_set_bloom`, `postprocess_set_depth_of_field`, `postprocess_set_tonemapping`, `postprocess_set_vignette`, `postprocess_set_color_adjustments`) are `SkillMode.FullAuto` — under **Approval** they need user grant (grant triggers one server-side execute returning the result); under **Auto** / **Bypass** they execute directly.
- `postprocess_remove_effect` carries `SkillOperation.Delete` and is **auto-forbidden** in Approval / Auto modes (NeverInSemi). Only **Bypass** or the user-managed **Allowlist** can run it.

## SRP Package Stub

This module is compiled against `com.unity.render-pipelines.core` (`SRP_CORE`). When neither URP nor HDRP is installed (no SRP Core), **every** skill returns a stub `{ error: "Scriptable Render Pipeline Core package … is not installed." }` (`RenderPipelineSkillsCommon.NoSRP()`). The stub is a diagnostic payload, not a permission denial — it does **not** require grant and is **not** treated as NeverInSemi.

## Guardrails

**DO NOT**:
- Use this module for PPv2 / `com.unity.postprocessing`
- Use this module for general Volume container/profile management; use `volume`

**Runtime-first rules**:
- Always call `postprocess_list_effects` before assuming an effect exists on the active pipeline
- Use `postprocess_get_effect` or `volume_get_component` to inspect real parameter names before setting generic parameters
- Prefer the dedicated high-frequency skills (`postprocess_set_bloom`, `postprocess_set_depth_of_field`, etc.) over guessing generic parameter names
- Treat URP and HDRP parameter surfaces as similar-but-not-identical; do not reuse names blindly across pipelines

## Skills

### `postprocess_list_effects`
List modern SRP post-processing effects supported by the active pipeline.

### `postprocess_add_effect`
Add a post-processing effect override to a VolumeProfile.

### `postprocess_remove_effect`
Remove a post-processing effect override from a VolumeProfile.

### `postprocess_get_effect`
Inspect a post-processing effect override.

### `postprocess_set_parameter`
Set one parameter on a post-processing effect override.

### `postprocess_set_bloom`
Configure Bloom.

### `postprocess_set_depth_of_field`
Configure Depth Of Field.

### `postprocess_set_tonemapping`
Configure Tonemapping.

### `postprocess_set_vignette`
Configure Vignette.

### `postprocess_set_color_adjustments`
Configure Color Adjustments.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
