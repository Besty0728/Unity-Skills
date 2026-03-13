---
name: unity-project
description: "Project information. Use when users want to get project settings, quality settings, or shader lists. Triggers: project, settings, quality, build, configuration, Unity项目, Unity设置, Unity质量, Unity构建."
---

# Project Skills

Project information and configuration.

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

### `project_get_quality_settings`
Get current quality settings.
**Parameters:** None.

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

### `project_set_quality_level`
Switch quality level by index or name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| level | int | No | -1 | Quality level index |
| levelName | string | No | null | Quality level name |

**Returns:** `{ success, level, name }`
