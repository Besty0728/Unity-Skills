# Unity Volume Skills

Shared SRP Volume framework skills for Unity 2022.3+, for URP and HDRP through SRP Core. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Setting up volumes
- Creating or loading a VolumeProfile
- Adding global or local volumes

## Rules
- Without SRP Core every skill returns `{ error: "Scriptable Render Pipeline Core package … is not installed." }` (`RenderPipelineSkillsCommon.NoSRP()`). This is a diagnostic payload, not a permission denial; inspect `project_get_render_pipeline` first.
- Call `volume_list_component_types` before assuming a component type exists on the active pipeline.
- After adding or creating a component, call `volume_get_component` and use the parameter names it returns instead of names from memory.
- `volume_set_parameter_batch` expects `items` as a JSON array string.
- For high-level post-processing effects such as Bloom, DOF or Tonemapping, prefer the [postprocess](../postprocess/MODULE.md) module. This module covers Volume container and profile CRUD.

## Skills

### volume_profile_create
Create a VolumeProfile asset.

### volume_create
Create a global or local Volume GameObject.

### volume_set_profile
Assign or replace the profile on an existing Volume.

### volume_list_component_types
List the explicitly supported VolumeComponent types for the active SRP pipeline.

### volume_add_component
Add a VolumeComponent override to a VolumeProfile.

### volume_remove_component
Remove a VolumeComponent override from a VolumeProfile.

### volume_get_component
Inspect a VolumeComponent override and its parameters.

### volume_set_parameter
Set one override parameter on a VolumeComponent.

### volume_set_parameter_batch
Set multiple override parameters on one VolumeComponent.
