---
name: unity-scene
description: "Unity scene management. Use when users want to create, load, save scenes, or get scene hierarchy. Triggers: scene, load scene, save scene, hierarchy, screenshot, Unity场景, Unity加载场景, Unity保存场景, Unity截图."
---

# Unity Scene Skills

Control Unity scenes - the containers that hold all your GameObjects.

## Skills Overview

| Skill | Description |
|-------|-------------|
| `scene_create` | Create a new scene |
| `scene_load` | Load a scene |
| `scene_save` | Save current scene |
| `scene_get_info` | Get scene information |
| `scene_get_hierarchy` | Get hierarchy tree |
| `scene_screenshot` | Capture screenshot |
| `scene_get_loaded` | Get all loaded scenes |
| `scene_unload` | Unload an additive scene |
| `scene_set_active` | Set active scene |
| `scene_summarize` | Get scene summary |

---

## Skills

### scene_create
Create a new scene.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scenePath` | string | Yes | Path for new scene (e.g., "Assets/Scenes/MyScene.unity") |

### scene_load
Load a scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scenePath` | string | Yes | - | Scene asset path |
| `additive` | bool | No | false | Load additively (keep current scene) |

### scene_save
Save the current scene.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scenePath` | string | No | Save path (null = save current) |

### scene_get_info
Get current scene information.

No parameters.

**Returns**: `{success, name, path, isDirty, rootObjectCount, rootObjects: [name]}`

### scene_get_hierarchy
Get full scene hierarchy tree.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 10 | Maximum hierarchy depth |

**Returns**: `{success, hierarchy: [{name, instanceId, children: [...]}]}`

### scene_screenshot
Capture a screenshot.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filename` | string | No | "screenshot.png" | Output filename |
| `width` | int | No | 1920 | Image width |
| `height` | int | No | 1080 | Image height |

### scene_get_loaded
Get list of all currently loaded scenes.

No parameters.

**Returns**: `{success, scenes: [{name, path, isActive, isDirty}]}`

### scene_unload
Unload a loaded scene (additive).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sceneName` | string | Yes | Scene name to unload |

### scene_set_active
Set the active scene (for multi-scene editing).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sceneName` | string | Yes | Scene name to set active |

### scene_summarize
Get a structured summary of the current scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeComponentStats` | bool | No | true | Include component statistics |
| `topComponentsLimit` | int | No | 10 | Max components to list |

**Returns**: `{success, objectCount, componentStats, hierarchyDepth}`

---

## Example Usage

```python
import unity_skills

# Create a new scene
unity_skills.call_skill("scene_create", scenePath="Assets/Scenes/Level1.unity")

# Load an existing scene
unity_skills.call_skill("scene_load", scenePath="Assets/Scenes/MainMenu.unity")

# Load scene additively (multi-scene)
unity_skills.call_skill("scene_load", scenePath="Assets/Scenes/UI.unity", additive=True)

# Get current scene info
info = unity_skills.call_skill("scene_get_info")
print(f"Scene: {info['name']}, Objects: {info['rootObjectCount']}")

# Get full hierarchy (useful for understanding scene structure)
hierarchy = unity_skills.call_skill("scene_get_hierarchy", maxDepth=5)

# Save scene
unity_skills.call_skill("scene_save")

# Take screenshot
unity_skills.call_skill("scene_screenshot", filename="preview.png", width=1920, height=1080)
```

## Best Practices

1. Always save before loading a new scene
2. Use additive loading for UI overlays
3. Keep scene hierarchy organized with empty parent objects
4. Use `scene_get_info` to verify scene state
5. Screenshots are saved to project root by default
