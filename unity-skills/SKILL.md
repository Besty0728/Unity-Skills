---
name: unity-skills
description: "Control Unity Editor via REST API. Create and manage GameObjects, components, scenes, materials, prefabs, lights, UI elements, scripts, and more. Provides 100+ automation tools for Unity development."
---

# Unity Editor Control Skill

You are an expert Unity developer. This skill enables you to directly control Unity Editor through a REST API.

## Prerequisites

1. Unity Editor must be running with the UnitySkills package installed
2. REST server must be started: **Window > UnitySkills > Start Server**
3. Server endpoint: `http://localhost:8090`

## Quick Start

```python
import unity_skills

# Create objects
unity_skills.call_skill("gameobject_create", name="MyCube", primitiveType="Cube", x=0, y=1, z=0)
unity_skills.call_skill("gameobject_create", name="MySphere", primitiveType="Sphere", x=2, y=1, z=0)

# Modify appearance
unity_skills.call_skill("material_set_color", name="MyCube", r=1, g=0, b=0)  # Red

# Query scene
info = unity_skills.call_skill("scene_get_info")
print(info["result"]["rootObjects"])

# Delete objects
unity_skills.call_skill("gameobject_delete", name="MyCube")
```

## ⚠️ Important: Script Creation & Domain Reload

When creating C# scripts with `script_create`, Unity recompiles all scripts (Domain Reload).
The server temporarily stops during compilation and auto-restarts.

```python
# After creating a script, wait for Unity to recompile
result = unity_skills.call_skill('script_create', name='MyScript', template='MonoBehaviour')
if result.get('success'):
    unity_skills.wait_for_unity(timeout=10)  # Wait for server to come back
```

## Skills Categories

| Category | Description |
|----------|-------------|
| [GameObject](#gameobject-skills) | Create, delete, find, transform objects |
| [Component](#component-skills) | Add, remove, configure components |
| [Scene](#scene-skills) | Scene management |
| [Material](#material-skills) | Material and shader operations |
| [Prefab](#prefab-skills) | Prefab operations |
| [Asset](#asset-skills) | Asset management |
| [Light](#light-skills) | Light creation and configuration |
| [Animator](#animator-skills) | Animation controller management |
| [UI](#ui-skills) | UI element creation |
| [Editor](#editor-skills) | Editor control |
| [Console](#console-skills) | Debug and logging |
| [Script](#script-skills) | Script management |
| [Shader](#shader-skills) | Shader operations |
| [Validation](#validation-skills) | Project validation and cleanup |

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

### Primitive Types
- `Cube`, `Sphere`, `Capsule`, `Cylinder`, `Plane`, `Quad` - 3D primitives
- `Empty`, `None`, or omit - Creates an empty GameObject

### Example: Create a Cube
```python
unity_skills.call_skill("gameobject_create", 
    name="Player", 
    primitiveType="Cube", 
    x=0, y=1, z=0
)
```

### Example: Create an Empty GameObject
```python
# All three methods work:
unity_skills.call_skill("gameobject_create", name="Container", primitiveType="Empty")
unity_skills.call_skill("gameobject_create", name="Container", primitiveType="None")
unity_skills.call_skill("gameobject_create", name="Container")  # primitiveType omitted
```

---

## Component Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `component_add` | Add a component | `name`, `componentType` |
| `component_remove` | Remove a component | `name`, `componentType` |
| `component_list` | List all components | `name` |
| `component_set_property` | Set component property | `name`, `componentType`, `propertyName`, `value` |
| `component_get_properties` | Get all properties | `name`, `componentType` |

### Component Type Formats
All these formats are supported:
- Simple name: `"Rigidbody"`, `"MeshRenderer"`, `"Light"`
- Full namespace: `"UnityEngine.Rigidbody"`, `"UnityEngine.UI.Image"`
- TMPro components: `"TextMeshProUGUI"`, `"TMPro.TextMeshProUGUI"`

### Example: Add Rigidbody
```python
# All formats work:
unity_skills.call_skill("component_add", name="Player", componentType="Rigidbody")
unity_skills.call_skill("component_add", name="Player", componentType="UnityEngine.Rigidbody")
```

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

## Material Skills

| Skill | Description | Key Parameters |
|-------|-------------|----------------|
| `material_create` | Create a new material | `name`, `shaderName`, `savePath` |
| `material_set_color` | Set material color | `name`/`path`, `r`, `g`, `b`, `a` |
| `material_set_texture` | Set material texture | `name`/`path`, `texturePath` |
| `material_assign` | Assign material to renderer | `name`, `materialPath` |
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

### Light Types
- `Directional` - Sun-like parallel light
- `Point` - Omnidirectional light
- `Spot` - Cone-shaped light
- `Area` - Rectangle light (baked only)

### Example: Create Point Light
```python
unity_skills.call_skill("light_create",
    name="TorchLight",
    lightType="Point",
    x=0, y=3, z=0,
    r=1, g=0.8, b=0.4,
    intensity=2,
    range=10,
    shadows="soft"
)
```

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

### Parameter Types
- `float` - Floating point value
- `int` - Integer value
- `bool` - Boolean value
- `trigger` - One-shot trigger

### Example: Create Animation Controller
```python
# Create controller
unity_skills.call_skill("animator_create_controller",
    name="PlayerController",
    folder="Assets/Animations"
)

# Add parameters
unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="Speed",
    paramType="float"
)
```

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

### Render Modes
- `ScreenSpaceOverlay` - UI rendered on top
- `ScreenSpaceCamera` - UI rendered by camera
- `WorldSpace` - UI in 3D world

### Example: Create Simple Menu
```python
# Create canvas
unity_skills.call_skill("ui_create_canvas", name="MainMenu")

# Create title
unity_skills.call_skill("ui_create_text",
    name="Title",
    parent="MainMenu",
    text="My Game",
    fontSize=48
)

# Create start button
unity_skills.call_skill("ui_create_button",
    name="StartButton",
    parent="MainMenu",
    text="Start Game",
    width=200,
    height=50
)
```

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

### Example: Validate Project
```python
# Check scene for issues
result = unity_skills.call_skill("validate_scene",
    checkMissingScripts=True,
    checkMissingPrefabs=True,
    checkDuplicateNames=True
)

# Clean up empty folders (dry run first)
result = unity_skills.call_skill("validate_cleanup_empty_folders",
    rootPath="Assets",
    dryRun=True
)
```

---

## Raw HTTP API

```bash
# List all skills
curl http://localhost:8090/skills

# Execute skill
curl -X POST http://localhost:8090/skill/gameobject_create \
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

## Adding Custom Skills

In Unity, create a C# file:

```csharp
using UnitySkills;

public static class MyCustomSkills
{
    [UnitySkill("my_skill", "Description for AI")]
    public static object MySkill(string param1, float param2 = 0)
    {
        // Your logic here
        return new { success = true, message = "Done!" };
    }
}
```

Restart the REST server to discover new skills.

## Python Client Reference

```python
import unity_skills

# Generic skill call
result = unity_skills.call_skill("skill_name", param1="value", param2=123)

# Health check
if unity_skills.health():
    print("Unity is running")

# List all skills
skills = unity_skills.get_skills()
```

## CLI Usage

```bash
# List skills
python unity_skills.py --list

# Call skill
python unity_skills.py gameobject_create name=MyCube primitiveType=Cube x=1 y=2 z=3
```
