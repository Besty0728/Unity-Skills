---
name: unityskills
description: Control Unity Editor via REST API. Create GameObjects, manage scenes, materials, prefabs, scripts. Use when user asks for Unity projects, scene manipulation, GameObject creation, material setup, UI building, lighting, animation.
---

# UnitySkills - Direct Unity Control

Control Unity Editor directly through a REST API. No MCP overhead - just simple HTTP calls.

## Quick Start

### 1. Start Unity REST Server
In Unity: **Window > UnitySkills > Start REST Server**

### 2. Call Skills from Python
```python
import requests

def call_skill(skill_name, **params):
    url = f"http://localhost:8080/skill/{skill_name}"
    response = requests.post(url, json=params)
    return response.json()

# Create objects
call_skill("gameobject_create", name="MyCube", primitiveType="Cube", x=0, y=1, z=0)

# Modify appearance (auto-detects render pipeline)
call_skill("material_set_color", name="MyCube", r=1, g=0, b=0)

# Query scene
info = call_skill("scene_get_info")
print(info["result"]["rootObjects"])
```

## 🎨 Render Pipeline Detection

UnitySkills auto-detects the project's render pipeline (Built-in/URP/HDRP):

```python
# Check current render pipeline
pipeline = call_skill("project_get_render_pipeline")
# Returns: { "pipeline": "URP", "shaderName": "Universal Render Pipeline/Lit", ... }

# Create material - auto-selects correct shader
call_skill("material_create", name="MyMat", path="Assets/Materials/MyMat.mat")

# Set color - auto-uses correct property (_Color or _BaseColor)
call_skill("material_set_color", name="MyObject", r=1, g=0, b=0)
```

| Pipeline | Default Shader | Color Property | Texture Property |
|----------|---------------|----------------|------------------|
| Built-in | Standard | `_Color` | `_MainTex` |
| URP | Universal Render Pipeline/Lit | `_BaseColor` | `_BaseMap` |
| HDRP | HDRP/Lit | `_BaseColor` | `_BaseColorMap` |

## Skills Categories

| Category | Skills | Description |
|----------|--------|-------------|
| **GameObject** | 7 | Create, delete, find, transform objects |
| **Component** | 5 | Add, remove, configure components |
| **Scene** | 6 | Scene management, screenshots |
| **Material** | 5 | Material/shader (smart pipeline detection) |
| **Prefab** | 4 | Prefab operations |
| **Asset** | 8 | Asset management |
| **Light** | 5 | Light creation and configuration |
| **Animator** | 8 | Animation controller management |
| **UI** | 10 | UI element creation |
| **Editor** | 11 | Editor control |
| **Console** | 5 | Debug and logging |
| **Script** | 4 | Script management |
| **Shader** | 3 | Shader operations |
| **Validation** | 7 | Project validation and cleanup |
| **Project** | 4 | Project info and pipeline detection |

---

## GameObject Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `gameobject_create` | Create a new GameObject | `name`, `primitiveType`, `x`, `y`, `z` |
| `gameobject_delete` | Delete a GameObject | `name`/`instanceId`/`path` |
| `gameobject_find` | Find GameObjects | `name`, `tag`, `layer`, `component`, `useRegex` |
| `gameobject_set_transform` | Set position/rotation/scale | `name`, `posX/Y/Z`, `rotX/Y/Z`, `scaleX/Y/Z` |
| `gameobject_set_parent` | Set parent-child relationship | `name`, `parentName` |
| `gameobject_set_active` | Enable/disable GameObject | `name`, `active` |
| `gameobject_get_info` | Get detailed info | `name`/`instanceId`/`path` |

**Primitive Types:** `Cube`, `Sphere`, `Capsule`, `Cylinder`, `Plane`, `Quad`, `Empty`/`None` (or omit for empty object)

---

## Component Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `component_add` | Add a component | `name`, `componentType` |
| `component_remove` | Remove a component | `name`, `componentType` |
| `component_list` | List all components | `name` |
| `component_set_property` | Set component property | `name`, `componentType`, `propertyName`, `value` |
| `component_get_properties` | Get all properties | `name`, `componentType` |

**Component Type Formats:** Simple name `"Rigidbody"` or full namespace `"UnityEngine.Rigidbody"` both work.

---

## Scene Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `scene_create` | Create new scene | `scenePath` |
| `scene_load` | Load a scene | `scenePath`, `additive` |
| `scene_save` | Save current scene | `scenePath` |
| `scene_get_info` | Get scene information | (none) |
| `scene_get_hierarchy` | Get scene hierarchy tree | `maxDepth` |
| `scene_screenshot` | Capture screenshot | `filename`, `width`, `height` |

---

## Material Skills (Smart Pipeline Detection)

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `material_create` | Create material (auto shader) | `name`, `shaderName`(optional), `savePath` |
| `material_set_color` | Set color (auto property) | `name`/`path`, `r`, `g`, `b`, `a` |
| `material_set_texture` | Set texture (auto property) | `name`/`path`, `texturePath` |
| `material_assign` | Assign to renderer | `name`, `materialPath` |
| `material_set_float` | Set float property | `name`/`path`, `propertyName`, `value` |

---

## Prefab Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `prefab_create` | Create prefab from GameObject | `gameObjectName`, `savePath` |
| `prefab_instantiate` | Instantiate prefab | `prefabPath`, `x`, `y`, `z`, `name` |
| `prefab_apply` | Apply prefab changes | `gameObjectName` |
| `prefab_unpack` | Unpack prefab instance | `gameObjectName`, `completely` |

---

## Asset Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `asset_import` | Import external asset | `sourcePath`, `destinationPath` |
| `asset_delete` | Delete an asset | `assetPath` |
| `asset_move` | Move/rename asset | `sourcePath`, `destinationPath` |
| `asset_duplicate` | Duplicate asset | `assetPath` |
| `asset_find` | Find assets | `searchFilter`, `limit` |
| `asset_create_folder` | Create folder | `folderPath` |
| `asset_refresh` | Refresh AssetDatabase | (none) |
| `asset_get_info` | Get asset information | `assetPath` |

---

## Light Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `light_create` | Create a new light | `name`, `lightType`, `x`, `y`, `z`, `r`, `g`, `b`, `intensity` |
| `light_set_properties` | Set light properties | `name`, `r`, `g`, `b`, `intensity`, `range`, `shadows` |
| `light_get_info` | Get light information | `name` |
| `light_find_all` | Find all lights | `lightType`, `limit` |
| `light_set_enabled` | Enable/disable light | `name`, `enabled` |

**Light Types:** `Directional`, `Point`, `Spot`, `Area`

---

## Animator Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `animator_create_controller` | Create Animator Controller | `name`, `folder` |
| `animator_add_parameter` | Add parameter | `controllerPath`, `paramName`, `paramType` |
| `animator_get_parameters` | Get all parameters | `controllerPath` |
| `animator_set_parameter` | Set parameter value | `name`, `paramName`, `paramType`, `floatValue`/`intValue`/`boolValue` |
| `animator_play` | Play animation state | `name`, `stateName`, `layer` |
| `animator_get_info` | Get Animator info | `name` |
| `animator_assign_controller` | Assign controller | `name`, `controllerPath` |
| `animator_list_states` | List states in controller | `controllerPath`, `layer` |

**Parameter Types:** `float`, `int`, `bool`, `trigger`

---

## UI Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `ui_create_canvas` | Create Canvas | `name`, `renderMode` |
| `ui_create_panel` | Create Panel | `name`, `parent`, `r`, `g`, `b`, `a` |
| `ui_create_button` | Create Button | `name`, `parent`, `text`, `width`, `height` |
| `ui_create_text` | Create Text | `name`, `parent`, `text`, `fontSize` |
| `ui_create_image` | Create Image | `name`, `parent`, `spritePath`, `width`, `height` |
| `ui_create_inputfield` | Create InputField | `name`, `parent`, `placeholder` |
| `ui_create_slider` | Create Slider | `name`, `parent`, `minValue`, `maxValue`, `value` |
| `ui_create_toggle` | Create Toggle | `name`, `parent`, `label`, `isOn` |
| `ui_set_text` | Set text content | `name`, `text` |
| `ui_find_all` | Find all UI elements | `uiType`, `limit` |

**Render Modes:** `ScreenSpaceOverlay`, `ScreenSpaceCamera`, `WorldSpace`

---

## Editor Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `editor_play` | Enter play mode | (none) |
| `editor_stop` | Exit play mode | (none) |
| `editor_pause` | Toggle pause | (none) |
| `editor_select` | Select GameObject | `gameObjectName`/`instanceId` |
| `editor_get_selection` | Get selected objects | (none) |
| `editor_undo` | Undo last action | (none) |
| `editor_redo` | Redo last action | (none) |
| `editor_get_state` | Get editor state | (none) |
| `editor_execute_menu` | Execute menu item | `menuPath` |
| `editor_get_tags` | Get all tags | (none) |
| `editor_get_layers` | Get all layers | (none) |

---

## Console Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `console_start_capture` | Start capturing logs | (none) |
| `console_stop_capture` | Stop capturing logs | (none) |
| `console_get_logs` | Get captured logs | `filter`, `limit` |
| `console_clear` | Clear console | (none) |
| `console_log` | Write log message | `message`, `type` |

---

## Script Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `script_create` | Create C# script | `scriptName`, `folder`, `template` |
| `script_read` | Read script content | `scriptPath` |
| `script_delete` | Delete script | `scriptPath` |
| `script_find_in_file` | Search in scripts | `pattern`, `folder`, `isRegex` |

---

## Shader Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `shader_create` | Create shader file | `shaderName`, `savePath`, `template` |
| `shader_read` | Read shader source | `shaderPath` |
| `shader_list` | List all shaders | `filter`, `limit` |

---

## Validation Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `validate_scene` | Validate scene for issues | `checkMissingScripts`, `checkMissingPrefabs`, `checkDuplicateNames` |
| `validate_find_missing_scripts` | Find missing scripts | `searchInPrefabs` |
| `validate_cleanup_empty_folders` | Clean empty folders | `rootPath`, `dryRun` |
| `validate_find_unused_assets` | Find unused assets | `assetType`, `limit` |
| `validate_texture_sizes` | Check texture sizes | `maxRecommendedSize`, `limit` |
| `validate_project_structure` | Get project overview | `rootPath`, `maxDepth` |
| `validate_fix_missing_scripts` | Remove missing scripts | `dryRun` |

---

## Project Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `project_get_info` | Get project information | (none) |
| `project_get_render_pipeline` | Get render pipeline type | (none) |
| `project_list_shaders` | List available shaders | `filter`, `limit` |
| `project_get_quality_settings` | Get quality settings | (none) |

---

## Workflow Examples

### Create a Simple Scene
```python
# Create ground
call_skill("gameobject_create", name="Ground", primitiveType="Plane", x=0, y=0, z=0)
call_skill("gameobject_set_transform", name="Ground", scaleX=10, scaleZ=10)

# Create player
call_skill("gameobject_create", name="Player", primitiveType="Cube", x=0, y=1, z=0)
call_skill("material_set_color", name="Player", r=0, g=0.5, b=1)

# Add lighting
call_skill("light_create", name="Sun", lightType="Directional", intensity=1)
call_skill("light_create", name="Fill", lightType="Point", x=5, y=3, z=5, intensity=0.5)
```

### Build UI Menu
```python
call_skill("ui_create_canvas", name="MainMenu")
call_skill("ui_create_text", name="Title", parent="MainMenu", text="My Game", fontSize=48)
call_skill("ui_create_button", name="PlayBtn", parent="MainMenu", text="Play")
call_skill("ui_create_button", name="QuitBtn", parent="MainMenu", text="Quit")
```

---

## Raw HTTP API

```bash
# List all skills
curl http://localhost:8080/skills

# Execute skill
curl -X POST http://localhost:8080/skill/gameobject_create \
  -H "Content-Type: application/json" \
  -d '{"name": "Cube1", "primitiveType": "Cube", "x": 1, "y": 2, "z": 3}'
```

## Response Format

```json
{
  "status": "success",
  "skill": "gameobject_create",
  "result": {
    "success": true,
    "name": "Cube1",
    "instanceId": 12345,
    "path": "/Cube1",
    "position": {"x": 1, "y": 2, "z": 3}
  }
}
```
