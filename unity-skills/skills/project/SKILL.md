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
