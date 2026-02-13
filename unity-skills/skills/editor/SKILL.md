---
name: unity-editor
description: "Unity Editor control. Use when users want to enter play mode, select objects, undo/redo, or execute menu commands. Triggers: play, stop, pause, select, undo, redo, menu, editor, Unity编辑器, Unity播放, Unity撤销, Unity选择."
---

# Unity Editor Skills

Control the Unity Editor itself - enter play mode, manage selection, undo/redo, and execute menu items.

## Skills Overview

| Skill | Description |
|-------|-------------|
| `editor_play` | Enter play mode |
| `editor_stop` | Exit play mode |
| `editor_pause` | Toggle pause |
| `editor_select` | Select GameObject |
| `editor_get_selection` | Get selected objects |
| `editor_get_context` | Get full editor context (selection, assets, scene) |
| `editor_undo` | Undo last action |
| `editor_redo` | Redo last action |
| `editor_get_state` | Get editor state |
| `editor_execute_menu` | Execute menu item |
| `editor_get_tags` | Get all tags |
| `editor_get_layers` | Get all layers |
| `editor_set_pause_on_error` | Pause play mode on error |

---

## Skills

### editor_play
Enter play mode.

### editor_stop
Exit play mode.

### editor_pause
Toggle pause state.

### editor_select
Select a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `gameObjectName` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |

*One identifier required

### editor_get_selection
Get currently selected objects.

**Returns**: `{success, count, objects: [{name, instanceId}]}`

### editor_get_context
Get full editor context including selection, assets, and scene info.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeComponents` | bool | No | false | Include component list |
| `includeChildren` | bool | No | false | Include children info |

**Returns**:
- `selectedGameObjects`: Objects in Hierarchy (instanceId, path, tag, layer)
- `selectedAssets`: Assets in Project window (GUID, path, type, isFolder)
- `activeScene`: Current scene info (name, path, isDirty)
- `focusedWindow`: Name of focused editor window
- `isPlaying`, `isCompiling`: Editor state

### editor_undo
Undo the last action.

### editor_redo
Redo the last undone action.

### editor_get_state
Get current editor state.

**Returns**: `{success, isPlaying, isPaused, isCompiling, platform}`

### editor_execute_menu
Execute a menu command.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `menuPath` | string | Yes | Menu item path |

**Common Menu Paths**:
| Menu Path | Action |
|-----------|--------|
| `File/Save` | Save current scene |
| `File/Build Settings...` | Open build settings |
| `Edit/Play` | Toggle play mode |
| `GameObject/Create Empty` | Create empty object |
| `Window/General/Console` | Open console |
| `Assets/Refresh` | Refresh assets |

### editor_get_tags
Get all available tags.

**Returns**: `{success, tags: [string]}`

### editor_get_layers
Get all available layers.

**Returns**: `{success, layers: [{index, name}]}`

### editor_set_pause_on_error
Configure whether to pause play mode when an error occurs.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `enabled` | bool | No | true | Enable pause on error |

---

## Example Usage

```python
import unity_skills

# Check editor state before operations
state = unity_skills.call_skill("editor_get_state")
if state['isCompiling']:
    print("Wait for compilation to finish")

# Get full context (useful for understanding current state)
context = unity_skills.call_skill("editor_get_context", includeComponents=True)
for obj in context['selectedGameObjects']:
    print(f"Selected: {obj['name']} (ID: {obj['instanceId']})")

# Select and operate on object
unity_skills.call_skill("editor_select", gameObjectName="Player")
selection = unity_skills.call_skill("editor_get_selection")

# Safe experimentation with undo
unity_skills.call_skill("gameobject_delete", name="TestObject")
unity_skills.call_skill("editor_undo")  # Restore if needed

# Execute menu command
unity_skills.call_skill("editor_execute_menu", menuPath="File/Save")
```

## Best Practices

1. Check editor state before play mode operations
2. Don't modify scene during play mode (changes lost)
3. Use undo for safe experimentation
4. Use `editor_get_context` to get instanceId for batch operations
5. Menu commands must match exact paths
