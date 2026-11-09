---
name: unity-project
description: Read Unity project metadata and package lists
---

# Unity Project Skills

Project information and configuration. Parameters come from the schema (`GET /skills/schema?category=Project&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Checking the Unity version or the render pipeline
- Listing installed packages, shaders, layers or tags
- Reading build or Player Settings
- Adding a custom tag or layer, or starting a player build

## Rules
- Eight query skills are `SemiAuto` read-only and run in every mode.
- `project_add_tag` and `project_add_layer` are `FullAuto`: under Approval they need a grant.
- `build_player` is high risk and has no dryRun: it runs only in Bypass or when allowlisted. It returns at once with a `jobId`; poll `GET /jobs/{id}` for the BuildReport summary. Output must stay inside the project directory.
- Player Settings and Build Settings are read-only here. To edit them, open the window with `editor_execute_menu` (`Edit/Project Settings...` or `File/Build Settings...`). That skill is refused under Approval and Auto, so under those modes tell the user to open the window.

## Routing
- Graphics, quality or SRP configuration → `graphics` module.
- Layers can be read (`project_get_layers`) and defined (`project_add_layer`); tags can be added (`project_add_tag`).
- Build settings → `project_get_build_settings`; producing a player → `build_player`.
- Installed packages with their versions → `package_list`; the manifest entries → `project_get_packages`.

## Skills

### `project_get_info`
Project information, including render pipeline, Unity version and settings.
**Parameters:** None.

### `project_get_render_pipeline`
Current render pipeline type and recommended shaders.
**Parameters:** None.

### `project_list_shaders`
List the shaders available in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| filter | string | No | null | Filter by name |
| limit | int | No | 50 | Maximum results |

### `project_get_build_settings`
Build settings: platform and scenes.
**Parameters:** None.

**Returns:** `{ success, activeBuildTarget, buildTargetGroup, sceneCount, scenes }`

### `build_player`
Build a player through `BuildPipeline.BuildPlayer`; returns at once with an async job.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| outputPath | string | No | null | Output location, inside the project and outside Unity-managed folders |
| target | string | No | null | BuildTarget enum name or `macOS`, `windows64`, `linux64`, `android`, `webgl`, `iOS` |
| scenes | string[] | No | null | Scene paths; defaults to the Build Settings scenes |
| development | bool | No | false | Development build |
| overwrite | bool | No | false | Overwrite existing output |

**Returns:** `{ success, status: "accepted", jobId, kind, platform, outputPath, scenes }`

### `project_get_packages`
Installed UPM packages from the project manifest.
**Parameters:** None.

**Returns:** `{ success, manifest }`

### `project_get_layers`
All Layer definitions.
**Parameters:** None.

**Returns:** `{ success, count, layers }`

### `project_get_tags`
All Tag definitions.
**Parameters:** None.

**Returns:** `{ success, count, tags }`

### `project_add_tag`
Add a custom Tag.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tagName | string | Yes | - | Tag name to add |

**Returns:** `{ success, tag }`

### `project_add_layer`
Define a named Layer in Tags & Layers. Slots 0-7 are built in, so only slots 8-31 can be used.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| layerName | string | Yes | - | Layer name to define |
| index | int | No | -1 | Slot 8-31; -1 uses the first empty user slot |

**Returns:** `{ success, layer, index }`

### `project_get_player_settings`
Player Settings.
**Parameters:** None.

**Returns:** `{ success, productName, companyName, bundleVersion, defaultScreenWidth, defaultScreenHeight, fullscreen, apiCompatibility, scriptingBackend }`

**DO NOT** (common hallucinations):
- `project_save` does not exist: use `scene_save`, or `editor_execute_menu` with `File/Save`. Both are refused under Approval and Auto.
- `project_settings`, `project_set_resolution` and `project_set_player_settings` do not exist: Player Settings are read-only here (`project_get_player_settings`).
- `project_create` does not exist: projects are created in Unity Hub.
