---
name: unity-editor
description: Control and observe the Unity Editor — read persistent scene/file changes, enter/exit/pause play mode, select objects, undo/redo, and execute menu items. Use after the user edited Unity while the AI was away, when file watching reports changes, or when driving Editor state. 控制并观察 Unity 编辑器(读取持久化场景/文件变更、进入/退出/暂停 play mode、选中对象、撤销/重做、执行菜单项);当用户在 AI 离开期间修改了 Unity、文件监控发现变化、或需要操控编辑器状态时使用。
---

# Unity Editor Skills

Observe and control the Unity Editor without parsing scene YAML.

## Operating Mode

- **Approval**：本模块 Mixed —— `editor_get_changes` / `editor_get_selection` / `editor_get_context` / `editor_get_state` / `editor_get_tags` / `editor_get_layers` 标 `SkillMode.SemiAuto`，可直接执行；其余 `editor_select` / `editor_undo` / `editor_redo` / `editor_execute_menu` 默认 FullAuto，Approval 模式下需 grant。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`editor_play` / `editor_stop` / `editor_pause`（标 `MayEnterPlayMode = true`，进出 PlayMode 会丢失运行时改动）。这些在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

**DO NOT** (common hallucinations):
- `editor_run` does not exist → use `editor_play` to enter play mode
- `editor_compile` / `editor_recompile` do not exist → use `debug_force_recompile`
- `editor_save` does not exist → use `editor_execute_menu` with menuPath `"File/Save"`
- `editor_execute_menu` requires exact menu path — typos cause silent failure

**Routing**:
- For compilation check → use `debug` module's `debug_check_compilation`
- For console errors → use `debug` module's `debug_get_errors`
- For scene save → `scene_save` (scene module) or `editor_execute_menu` menuPath="File/Save"
- When file watching reports changes, or the AI resumes after the user edited Unity → call `editor_get_changes` before reading `.unity` YAML
- If `editor_get_changes.dropped=true` → its 500-entry retention window was exceeded; rebuild context with `scene_context` / `scene_diff`

## Skills Overview

| Skill | Description |
|-------|-------------|
| `editor_play` | Enter play mode |
| `editor_stop` | Exit play mode |
| `editor_pause` | Toggle pause |
| `editor_select` | Select GameObject |
| `editor_get_selection` | Get selected objects |
| `editor_get_context` | Get full editor context (selection, assets, scene) |
| `editor_get_changes` | Read persistent scene/file changes by cursor |
| `editor_undo` | Undo last action |
| `editor_redo` | Redo last action |
| `editor_get_state` | Get editor state |
| `editor_execute_menu` | Execute menu item |
| `editor_get_tags` | Get all tags |
| `editor_get_layers` | Get all layers |
| `console_set_pause_on_error` | Pause play mode on error (console module) |

---

## Skills

### editor_play
Enter play mode. Warning: any unsaved scene changes made during Play mode will be lost when exiting.

**Returns**: `{success, mode, jobId}` — `mode="playing"`, `jobId` returned from `AsyncJobService` so callers can poll `entering_play_mode` completion.

### editor_stop
Exit play mode.

**Returns**: `{success, mode}` — `mode="stopped"`.

### editor_pause
Toggle pause state.

**Returns**: `{success, paused}` — `paused` is the new boolean state.

### editor_select
Select a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Object path |

*One identifier required

### editor_get_selection
Get currently selected objects.

**Returns**: `{count, objects: [{name, instanceId}]}`

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

### editor_get_changes
Read the persistent change journal at `Library/UnitySkills/editor_changes.jsonl`. It captures scene object/component/property summaries plus imported, deleted, and moved asset paths across Domain Reloads. It never parses or returns raw `.unity` YAML.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `since` | long | No | `0` | Previous response cursor; `0` reads retained history |
| `types` | string | No | `all` | Comma-separated `scene`, `file`, `undo`, or `lifecycle` |
| `source` | string | No | `all` | `editor`/`manual`, `rest`, or `all` |
| `limit` | int | No | `100` | Newest entries to return, 1-500 |

**Returns:** `{hasChanges, cursor, oldestSeq, dropped, truncated, changes}`. Save `cursor` for the next call. `dropped=true` means older changes fell outside the 500-entry journal, so rebuild full scene context.

### editor_undo
Undo the last action.

### editor_redo
Redo the last undone action.

### editor_get_state
Get current editor state.

**Returns**: `{isPlaying, isPaused, isCompiling, timeSinceStartup, unityVersion, platform}`

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

**Returns**: `{tags: [string]}`

### editor_get_layers
Get all available layers.

**Returns**: `{layers: [{index, name}]}`

### Pause On Error
Pause-on-error is provided by the console module, not the editor module.

Use `console_set_pause_on_error` from [console/SKILL.md](/E:/CodeSpace/Unity-Skills/SkillsForUnity/unity-skills~/skills/console/SKILL.md).

---

## Example Usage

```python
import unity_skills

# Check editor state before operations
state = unity_skills.call_skill("editor_get_state")
if state['isCompiling']:
    print("Wait for compilation to finish")

# On resume or after file-watch notification, inspect semantic changes first.
changes = unity_skills.call_skill("editor_get_changes", since=last_cursor)
last_cursor = changes["cursor"]

# Get full context (useful for understanding current state)
context = unity_skills.call_skill("editor_get_context", includeComponents=True)
for obj in context['selectedGameObjects']:
    print(f"Selected: {obj['name']} (ID: {obj['instanceId']})")

# Select and operate on object
unity_skills.call_skill("editor_select", name="Player")
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
4. On resume, call `editor_get_changes` before reading scene files
5. Use `editor_get_context` to get instanceId for batch operations
6. Menu commands must match exact paths

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
