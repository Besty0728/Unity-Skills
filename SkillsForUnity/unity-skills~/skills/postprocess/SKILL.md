---
name: unity-postprocess
description: Configure modern SRP post-processing on URP/HDRP
---

# Unity PostProcess Skills

Modern URP and HDRP post-processing on SRP Volume profiles. Parameters come from the schema (`GET /skills/schema?category=PostProcess&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Volume container and profile management (`volume_profile_create`, `volume_create`, ...) belongs to the [volume](../volume/SKILL.md) module.

## When to use
- Setting up post-processing or tuning the look of a URP or HDRP scene
- Adding or adjusting an effect override on a VolumeProfile

## Rules
- Call `postprocess_list_effects` before assuming an effect exists on the active pipeline.
- Inspect real parameter names with `postprocess_get_effect` (or `volume_get_component`) before using the generic `postprocess_set_parameter`; prefer the dedicated `postprocess_set_*` skills.
- URP and HDRP parameter surfaces are similar but not identical; do not reuse names across pipelines.
- Query skills are `SemiAuto`. The `postprocess_add_effect`, `postprocess_set_*` and `postprocess_set_parameter` writers are `FullAuto`: under Approval they need a grant.
- `postprocess_remove_effect` is a Delete operation: refused under Approval and Auto; only Bypass or an allowlist runs it.
- Without SRP Core installed, every skill returns `{ error: "Scriptable Render Pipeline Core package (com.unity.render-pipelines.core) is not installed..." }`. That is a diagnostic answer, not a permission denial, and needs no grant.

## Pitfalls
- This module is not for PPv2 (`com.unity.postprocessing`).
- Volume container and profile CRUD belong to the `volume` module, not here.

## Skills

### `postprocess_list_effects`
List the post-processing effects the active pipeline supports.
**Parameters:** None.

### `postprocess_add_effect`
Add an effect override to a VolumeProfile.
**Parameters:** `profilePath`, `effectType`, `overrides` (bool, default true).

### `postprocess_remove_effect`
Remove an effect override from a VolumeProfile.
**Parameters:** `profilePath`, `effectType`.

### `postprocess_get_effect`
Inspect an effect override on a VolumeProfile.
**Parameters:** `profilePath`, `effectType`.

### `postprocess_set_parameter`
Set one parameter on an effect override.
**Parameters:** `profilePath`, `effectType`, `parameterName`, `value`, `overrideState` (bool, default true).

### `postprocess_set_bloom`
Configure Bloom.
**Parameters:** `profilePath`, `intensity?`, `threshold?`, `scatter?`, `tint?`.

### `postprocess_set_depth_of_field`
Configure Depth of Field.
**Parameters:** `profilePath`, `mode?`, `focusDistance?`, `gaussianStart?`, `gaussianEnd?`.

### `postprocess_set_tonemapping`
Configure Tonemapping.
**Parameters:** `profilePath`, `mode?`.

### `postprocess_set_vignette`
Configure Vignette.
**Parameters:** `profilePath`, `intensity?`, `smoothness?`, `color?`, `center?`, `rounded?`.

### `postprocess_set_color_adjustments`
Configure Color Adjustments.
**Parameters:** `profilePath`, `postExposure?`, `contrast?`, `colorFilter?`, `hueShift?`, `saturation?`.

