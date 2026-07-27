---
name: unity-editor
description: Control and observe the Unity Editor — read persistent scene/file changes, enter/exit/pause/step play mode, inspect live GameObject runtime state, select objects, undo/redo, and execute menu items. Use after the user edited Unity while the AI was away, when file watching reports changes, or when driving Editor state. 控制并观察 Unity 编辑器(读取持久化场景/文件变更、进入/退出/暂停/单帧步进 play mode、检查运行时状态、选中对象、撤销/重做、执行菜单项);当用户在 AI 离开期间修改了 Unity、文件监控发现变化、或需要操控编辑器状态时使用。
---

# Unity Editor Skills

Observe and control the Unity Editor without parsing scene YAML.

## Operating Mode

- **Approval**：本模块 Mixed —— `editor_get_changes` / `editor_get_selection` / `editor_get_context` / `editor_get_state` / `editor_get_tags` / `editor_get_layers` / `editor_playmode_inspect` 标 `SkillMode.SemiAuto`，可直接执行；其余 `editor_select` / `editor_undo` / `editor_redo` / `editor_execute_menu` / `editor_playmode_step` 默认 FullAuto，Approval 模式下需 grant。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`editor_play` / `editor_play_capture` / `editor_stop` / `editor_pause`（标 `MayEnterPlayMode = true`）。这些在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

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
| `editor_play_capture` | Observe runtime errors, optionally screenshot, then exit |
| `editor_stop` | Exit play mode |
| `editor_pause` | Toggle pause |
| `editor_playmode_step` | Advance Play Mode by N frames (async job) |
| `editor_playmode_inspect` | Inspect a GameObject's live runtime state (transform, component fields) |
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

### editor_play_capture
Enter Play Mode, observe errors for `durationSeconds` (default 10, range 1–300), optionally capture the Game View, then exit. Returns a Job whose result includes `healthy`, error aggregates, `stoppedEarly`, and `screenshotPath`.

### editor_stop
Exit play mode.

**Returns**: `{success, mode}` — `mode="stopped"`.

### editor_pause
Toggle pause state.

**Returns**: `{success, paused}` — `paused` is the new boolean state.

### editor_playmode_step
Advance Play Mode forward by `frames` (1–100, default 1) using `EditorApplication.Step`; Unity automatically enters paused state as part of stepping. Requires Play Mode to already be active — call `editor_play` or `editor_play_capture` first, otherwise this returns a structured error (`error` + `hint` + `suggestedSkills: ["editor_play", "editor_play_capture"]`). Only one step job may be in flight at a time; a second call while one is still running returns an error naming the active `jobId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `frames` | int | No | 1 | Frames to advance, clamped to 1–100 |

**Runs as an async job, not synchronously**: `EditorApplication.Step()` only lands on a later Editor tick, so the skill issues one `Step()` call at a time, confirms it landed by watching `Time.frameCount` advance, and only then issues the next — back-to-back `Step()` calls without that confirmation are not reliable. The call itself returns immediately: `{success, status: "accepted", jobId, framesRequested}`.

Poll `job_status` with the returned `jobId` until `status="completed"`; its `details` then contains `{framesRequested, framesCompleted, frameCount, isPaused}`. **Do not use `job_wait`** — like `playmode`/`play_capture`, this job's progress depends on `EditorApplication.update` ticks, and `job_wait`'s blocking loop runs on the same main thread those ticks come from, so it cannot observe progress; it will just spend its full timeout doing nothing. If Play Mode exits or Unity fails to advance a frame within 10s, the job fails with `failed_exited_play_mode` / `failed_step_timeout`.

### editor_playmode_inspect
Inspect a GameObject's live runtime state: transform (position/rotation/scale), `activeSelf`/`activeInHierarchy`, and — when `componentType` is given — that component's public fields and properties (reuses the same reflection helper as `component_get_properties`, not reimplemented). Works during Play Mode, including while paused, and also in Edit Mode, where it returns editor-time values — check `isPlaying`/`isPaused` in the response to know which state the values reflect.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Object path |
| `componentType` | string | No | Component type name; when set, also returns its public fields/properties |

*One identifier required

**Returns**: `{success, gameObject, entityId, instanceId, path, activeSelf, activeInHierarchy, transform: {position, localPosition, rotation, localScale}, isPlaying, isPaused, component}`. `component` is `null` when `componentType` is omitted, an error object when the type isn't found or isn't attached, or `{gameObject, component, fullTypeName, properties, fields}` (same shape as `component_get_properties`) otherwise.

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

# Step through Play Mode frame-by-frame and assert state changed
import time

# Enter Play Mode with a generous observation window so there's time left to step
capture = unity_skills.call_skill("editor_play_capture", durationSeconds=30)
while True:
    capture_status = unity_skills.call_skill("job_status", jobId=capture["jobId"])
    if capture_status["currentStage"] not in ("entering_play_mode", "domain_reload_recovery"):
        break  # Play Mode has been entered; capture_status["currentStage"] == "observing"
    time.sleep(0.2)

before = unity_skills.call_skill("editor_playmode_inspect", name="Player")["transform"]["position"]

step = unity_skills.call_skill("editor_playmode_step", frames=3)
while True:
    step_status = unity_skills.call_skill("job_status", jobId=step["jobId"])  # not job_wait, see above
    if step_status["status"] in ("completed", "failed"):
        break
    time.sleep(0.1)

after = unity_skills.call_skill("editor_playmode_inspect", name="Player")["transform"]["position"]
assert after != before, "Player did not move after 3 frames"
print(step_status["details"]["frameCount"], step_status["details"]["isPaused"])
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
