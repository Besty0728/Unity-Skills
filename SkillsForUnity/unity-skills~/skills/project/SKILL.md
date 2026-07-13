---
name: unity-project
description: Read Unity project information — project metadata such as Unity version and render pipeline, plus lists of shaders and UPM packages. Use when checking the Unity version, detecting the render pipeline, or listing installed shaders/packages, even if the user just says "项目信息" or "什么版本". 读取 Unity 项目信息(项目元数据如 Unity 版本与渲染管线,以及 shader 与 UPM 包列表);当用户要查看 Unity 版本、判断渲染管线、或列出已装 shader/包时使用。
---

# Project Skills

Project information and configuration.

## Operating Mode

`build_player` 为 `RiskLevel=high` 的实际出包操作，仅 Bypass 或 Allowlist 放行后可执行；`project_add_tag` 默认 FullAuto。其余 8 个查询 skill 均为 SemiAuto 只读操作。

> Player Settings、Build Settings、Layer 通过本模块只读获取；如需编辑，请使用 `editor_execute_menu` 打开 `Edit/Project Settings...` 或 `File/Build Settings...`（菜单本身在 editor 模块为 SemiAuto，可直接执行）。

**DO NOT** (common hallucinations):
- `project_save` does not exist → use `scene_save` (scene module) or `editor_execute_menu` menuPath="File/Save"
- `project_settings` does not exist → use specific skills: `project_get_render_pipeline`, `project_get_build_settings`, etc.
- `project_set_resolution` / `project_set_player_settings` do not exist → Player Settings are read-only via `project_get_player_settings`; to edit, open Project Settings via `editor_execute_menu` with `Edit/Project Settings...`
- `project_create` does not exist → projects are created via Unity Hub, not REST API

**Routing**:
- For graphics / quality / SRP configuration → use the `graphics` module
- For Layer/Tag management → `project_add_tag` (this module); Layers are read-only via `project_get_layers` (edit via `editor_execute_menu` → `Edit/Project Settings...`)
- For inspecting build settings → `project_get_build_settings`; for producing a player → `build_player`

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

### `build_player`
Build a player through `BuildPipeline.BuildPlayer` and return immediately with an asynchronous Job.

**Parameters:** `outputPath?`, `target?`, `scenes?`, `development=false`, `overwrite=false`. Output must remain inside the project and outside Unity-managed folders.

**Returns:** `{success, status:"accepted", jobId, kind, platform, outputPath, scenes}`. Poll `/jobs/{id}`; the final result contains the BuildReport summary.

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
