---
name: unity-editor
description: Control Unity Editor state - play mode, selection, undo, and menu commands via REST API
---

# Unity Editor Skills

Control the Unity Editor itself - enter play mode, manage selection, undo/redo, and execute menu items.

## Capabilities

- Enter/exit play mode
- Pause/resume playback
- Select GameObjects
- Undo/redo operations
- Get editor state
- Execute menu commands
- Query tags and layers

## Skills Reference

| Skill | Description |
|-------|-------------|
| `editor_play` | Enter play mode |
| `editor_stop` | Exit play mode |
| `editor_pause` | Toggle pause |
| `editor_select` | Select GameObject |
| `editor_get_selection` | Get selected objects |
| `editor_undo` | Undo last action |
| `editor_redo` | Redo last action |
| `editor_get_state` | Get editor state |
| `editor_execute_menu` | Execute menu item |
| `editor_get_tags` | Get all tags |
| `editor_get_layers` | Get all layers |

## Parameters

### editor_select

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `gameObjectName` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |

*One identifier required

### editor_execute_menu

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `menuPath` | string | Yes | Menu item path |

## Common Menu Paths

| Menu Path | Action |
|-----------|--------|
| `File/Save` | Save current scene |
| `File/Build Settings...` | Open build settings |
| `Edit/Play` | Toggle play mode |
| `GameObject/Create Empty` | Create empty object |
| `Window/General/Console` | Open console |
| `Assets/Refresh` | Refresh assets |

## Example Usage

```python
import unity_skills

# Check editor state
state = unity_skills.call_skill("editor_get_state")
print(f"Is Playing: {state['result']['isPlaying']}")
print(f"Is Paused: {state['result']['isPaused']}")

# Enter play mode
unity_skills.call_skill("editor_play")

# Pause the game
unity_skills.call_skill("editor_pause")

# Resume
unity_skills.call_skill("editor_pause")  # Toggle

# Stop play mode
unity_skills.call_skill("editor_stop")

# Select an object
unity_skills.call_skill("editor_select",
    gameObjectName="Player"
)

# Get current selection
selection = unity_skills.call_skill("editor_get_selection")
for obj in selection['result']['selectedObjects']:
    print(f"Selected: {obj['name']}")

# Undo last action
unity_skills.call_skill("editor_undo")

# Redo
unity_skills.call_skill("editor_redo")

# Execute menu command
unity_skills.call_skill("editor_execute_menu",
    menuPath="File/Save"
)

# Get available tags
tags = unity_skills.call_skill("editor_get_tags")
print(f"Tags: {tags['result']['tags']}")

# Get available layers
layers = unity_skills.call_skill("editor_get_layers")
```

## Response Format

### editor_get_state Response

```json
{
  "status": "success",
  "skill": "editor_get_state",
  "result": {
    "success": true,
    "isPlaying": false,
    "isPaused": false,
    "isCompiling": false,
    "platform": "StandaloneWindows64"
  }
}
```

### editor_get_selection Response

```json
{
  "result": {
    "success": true,
    "selectedCount": 2,
    "selectedObjects": [
      {"name": "Player", "instanceId": 12345},
      {"name": "Camera", "instanceId": 12346}
    ]
  }
}
```

## Best Practices

1. Check editor state before play mode operations
2. Don't modify scene during play mode (changes lost)
3. Use undo for safe experimentation
4. Select objects before batch operations
5. Menu commands must match exact paths
