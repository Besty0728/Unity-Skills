---
name: unity-project
description: Read Unity project metadata and package lists
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Checking Unity version
- Detecting render pipeline
- Listing installed shaders/packages
- 查看 Unity 版本、判断渲染管线、列出已装 shader/包

# Project Skills

Project information and configuration.

## Operating Mode

`build_player` 为 `RiskLevel=high` 的实际出包操作，仅 Bypass 或 Allowlist 放行后可执行；`project_add_tag` 默认 FullAuto。其余 8 个查询 skill 均为 SemiAuto 只读操作。

> Player Settings、Build Settings、Layer 通过本模块只读获取；如需编辑，请使用 `editor_execute_menu` 打开 `Edit/Project Settings...` 或 `File/Build Settings...`。**注意 `editor_execute_menu` 自身是 auto-forbidden**（editor 模块，标 `MayTriggerReload = true`）：Approval 和 Auto 下都返 `MODE_FORBIDDEN` 且 grant 不解锁，仅 Bypass 或 Allowlist 命中可调。其他模式下只能改为指导用户手动打开这些窗口。

**DO NOT** (common hallucinations):
- `project_save` does not exist → use `scene_save` (scene module) or `editor_execute_menu` menuPath="File/Save" —— 两者都是 auto-forbidden（`RiskLevel="high"` / `MayTriggerReload`），仅 Bypass 或 Allowlist 可调
- `project_settings` does not exist → use specific skills: `project_get_render_pipeline`, `project_get_build_settings`, etc.
- `project_set_resolution` / `project_set_player_settings` do not exist → Player Settings are read-only via `project_get_player_settings`; to edit, open Project Settings via `editor_execute_menu` with `Edit/Project Settings...`（该 skill auto-forbidden，仅 Bypass 或 Allowlist 可调，否则请指导用户手动打开）
- `project_create` does not exist → projects are created via Unity Hub, not REST API

**Routing**:
- For graphics / quality / SRP configuration → use the `graphics` module
- For Layer/Tag management → `project_add_tag` (this module); Layers are read-only via `project_get_layers`（编辑需 `editor_execute_menu` → `Edit/Project Settings...`，而该 skill auto-forbidden，仅 Bypass 或 Allowlist 可调）
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
