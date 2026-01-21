---
name: unity-scene
description: Manage Unity scenes - create, load, save, and query scene information via REST API
---

# Unity Scene Skills

Control Unity scenes - the containers that hold all your GameObjects.

## Capabilities

- Create new scenes
- Load existing scenes (single or additive)
- Save current scene
- Get scene information (name, path, objects)
- Get full scene hierarchy tree
- Capture screenshots

## Skills Reference

| Skill | Description |
|-------|-------------|
| `scene_create` | Create a new scene |
| `scene_load` | Load a scene |
| `scene_save` | Save current scene |
| `scene_get_info` | Get scene information |
| `scene_get_hierarchy` | Get hierarchy tree |
| `scene_screenshot` | Capture screenshot |

## Parameters

### scene_create

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scenePath` | string | Yes | Path for new scene (e.g., "Assets/Scenes/MyScene.unity") |

### scene_load

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scenePath` | string | Yes | - | Scene asset path |
| `additive` | bool | No | false | Load additively (keep current scene) |

### scene_save

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scenePath` | string | No | Save path (null = save current) |

### scene_get_info

No parameters - returns current scene information.

### scene_get_hierarchy

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 10 | Maximum hierarchy depth |

### scene_screenshot

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filename` | string | No | "screenshot.png" | Output filename |
| `width` | int | No | 1920 | Image width |
| `height` | int | No | 1080 | Image height |

## Example Usage

```python
import unity_skills

# Create a new scene
unity_skills.call_skill("scene_create",
    scenePath="Assets/Scenes/Level1.unity"
)

# Load an existing scene
unity_skills.call_skill("scene_load",
    scenePath="Assets/Scenes/MainMenu.unity"
)

# Load scene additively (multi-scene)
unity_skills.call_skill("scene_load",
    scenePath="Assets/Scenes/UI.unity",
    additive=True
)

# Get current scene info
info = unity_skills.call_skill("scene_get_info")
print(f"Scene: {info['result']['name']}")
print(f"Objects: {info['result']['rootObjectCount']}")

# Get full hierarchy
hierarchy = unity_skills.call_skill("scene_get_hierarchy",
    maxDepth=5
)

# Save scene
unity_skills.call_skill("scene_save")

# Take screenshot
unity_skills.call_skill("scene_screenshot",
    filename="preview.png",
    width=1920,
    height=1080
)
```

## Response Format

### scene_get_info Response

```json
{
  "status": "success",
  "skill": "scene_get_info",
  "result": {
    "success": true,
    "name": "SampleScene",
    "path": "Assets/Scenes/SampleScene.unity",
    "isDirty": false,
    "rootObjectCount": 5,
    "rootObjects": [
      "Main Camera",
      "Directional Light",
      "Player",
      "Environment",
      "UI Canvas"
    ]
  }
}
```

### scene_get_hierarchy Response

```json
{
  "status": "success",
  "skill": "scene_get_hierarchy",
  "result": {
    "success": true,
    "hierarchy": [
      {
        "name": "Player",
        "instanceId": 12345,
        "children": [
          {"name": "Model", "instanceId": 12346, "children": []},
          {"name": "Weapon", "instanceId": 12347, "children": []}
        ]
      }
    ]
  }
}
```

## Best Practices

1. Always save before loading a new scene
2. Use additive loading for UI overlays
3. Keep scene hierarchy organized with empty parent objects
4. Use scene_get_info to verify scene state
5. Screenshots are saved to project root by default
