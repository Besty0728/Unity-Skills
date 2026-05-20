---
name: unity-project
description: "Project information. Use when users want to get project info, packages, player settings, layers, tags, or shader lists. Triggers: project, settings, build, configuration, Unity项目, Unity设置, Unity构建."
---

# Project Skills

Project information and configuration.

## Guardrails

**Mode**: SkillMode.SemiAuto (most skills usable in Approval mode)

**DO NOT** (common hallucinations):
- `project_save` does not exist → use `scene_save` (scene module) or `editor_execute_menu` menuPath="File/Save"
- `project_settings` does not exist → use specific skills: `project_get_render_pipeline`, `project_get_build_settings`, etc.
- `project_set_resolution` / `project_set_player_settings` do not exist → Player Settings are read-only via `project_get_player_settings`; to edit, open Project Settings via `editor_execute_menu` with `Edit/Project Settings...`
- `project_create` does not exist → projects are created via Unity Hub, not REST API

**Routing**:
- For graphics / quality / SRP configuration → use the `graphics` module
- For Layer/Tag management → `project_add_tag` (this module); Layers are read-only via `project_get_layers` (edit via `editor_execute_menu` → `Edit/Project Settings...`)
- For build settings → `project_get_build_settings` (read-only; use `editor_execute_menu` → `File/Build Settings...` to edit)

## Skills

### `project_get_info`
Get project information including render pipeline, Unity version, and settings.
**Parameters:** None.

### `project_get_render_pipeline`
Get current render pipeline type and recommended shaders.
**Parameters:** None.

### `project_list_shaders`
List all available shaders in the project.
**Parameters:**
- `filter` (string, optional): Filter by name.
- `limit` (int, optional): Max results (default 50).

### `project_get_build_settings`
Get build settings (platform, scenes).

**Parameters:** None.

**Returns:** `{ success, activeBuildTarget, buildTargetGroup, sceneCount, scenes }`

### `project_get_packages`
List installed UPM packages.

**Parameters:** None.

**Returns:** `{ success, manifest }`

### `project_get_layers`
Get all Layer definitions.

**Parameters:** None.

**Returns:** `{ success, count, layers }`

### `project_get_tags`
Get all Tag definitions.

**Parameters:** None.

**Returns:** `{ success, count, tags }`

### `project_add_tag`
Add a custom Tag.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tagName | string | Yes | - | The tag name to add |

**Returns:** `{ success, tag }`

### `project_get_player_settings`
Get Player Settings.

**Parameters:** None.

**Returns:** `{ success, productName, companyName, bundleVersion, defaultScreenWidth, defaultScreenHeight, fullscreen, apiCompatibility, scriptingBackend }`

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
