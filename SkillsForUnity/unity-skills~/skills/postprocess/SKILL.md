---
name: unity-postprocess
description: "Modern SRP post-processing effect management for Unity 2022.3+. Use when users want Bloom, Depth Of Field, Tonemapping, Vignette, or Color Adjustments on URP/HDRP VolumeProfiles. Triggers: bloom, dof, tonemapping, vignette, color adjustments, modern post-processing, 后处理, 景深, 泛光."
---

# PostProcess Skills

Modern URP/HDRP post-processing skills built on top of the Volume framework.

## Guardrails

**Mode**: SkillMode.FullAuto (default — requires grant under Approval mode)

> Some skills (Delete / PlayMode / Reload / high-risk) are auto-forbidden in Approval/Auto modes — only Bypass can run them.

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
