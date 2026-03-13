---
name: unity-workflow
description: "Operation history and rollback. Use when users want to track changes, create snapshots, or undo operations. Triggers: undo, redo, snapshot, rollback, history, revert, session, 工作流, Unity快照, Unity回滚, Unity撤销."
---

# Workflow Skills

Persistent history and rollback system for AI operations ("Time Machine").
Allows tagging tasks, snapshotting objects before modification, and undoing specific tasks even after Editor restarts.

**NEW: Session-level undo** - Group all changes from a conversation and undo them together.

## Bookmark Skills

### `bookmark_set`
Save current selection and scene view position as a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Name for the bookmark |
| note | string | No | null | Optional note for the bookmark |

**Returns:** `{ success, bookmark, selectedCount, hasSceneView, note }`

### `bookmark_goto`
Restore selection and scene view from a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Name of the bookmark to restore |

**Returns:** `{ success, bookmark, restoredSelection, note }`

### `bookmark_list`
List all saved bookmarks.

No parameters.

**Returns:** `{ success, count, bookmarks: [{ name, selectedCount, hasSceneView, note, createdAt }] }`

### `bookmark_delete`
Delete a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Name of the bookmark to delete |

**Returns:** `{ success, deleted }`

## History Skills

### `history_undo`
Undo the last operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of undo steps to perform |

**Returns:** `{ success, undoneSteps }`

### `history_redo`
Redo the last undone operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of redo steps to perform |

**Returns:** `{ success, redoneSteps }`

### `history_get_current`
Get the name of the current undo group.

No parameters.

**Returns:** `{ success, currentGroup, groupIndex }`

## Session Management (Conversation-Level Undo)

### `workflow_session_start`
Start a new session (conversation-level). All changes will be tracked and can be undone together.
**Call this at the beginning of each conversation.**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tag | string | No | null | Label for the session |

**Returns:** `{ success, sessionId, message }`

### `workflow_session_end`
End the current session and save all tracked changes.
**Call this at the end of each conversation.**

No parameters.

**Returns:** `{ success, sessionId, message }`

### `workflow_session_undo`
Undo all changes made during a specific session (conversation-level undo).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sessionId | string | No | null | The UUID of the session to undo. If not provided, undoes the most recent session |

**Returns:** `{ success, sessionId, message }`

### `workflow_session_list`
List all recorded sessions (conversation-level history).

No parameters.

**Returns:** `{ success, count, currentSessionId, sessions: [{ sessionId, taskCount, totalChanges, startTime, endTime, tags }] }`

### `workflow_session_status`
Get the current session status.

No parameters.

**Returns:** `{ success, hasActiveSession, currentSessionId, isRecording, currentTaskId, currentTaskTag, currentTaskDescription, snapshotCount }`

## Task-Level Skills

### `workflow_task_start`
Start a new persistent workflow task to track changes for undo. Call workflow_task_end when done.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tag | string | Yes | - | Short label for the task (e.g., "Create NPC") |
| description | string | No | "" | Detailed description or prompt |

**Returns:** `{ success, taskId, message }`

### `workflow_task_end`
End the current workflow task and save it. Requires an active task (call workflow_task_start first).

No parameters.

**Returns:** `{ success, taskId, snapshotCount, message }`

### `workflow_snapshot_object`
Manually snapshot an object's state before modification. Requires an active task (call workflow_task_start first).
**Call this BEFORE `component_set_property`, `gameobject_set_transform`, etc.**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the Game Object |
| instanceId | int | No | 0 | Instance ID of the object (preferred) |

**Returns:** `{ success, objectName, type }`

### `workflow_snapshot_created`
Record a newly created object for undo tracking. Requires an active task (call workflow_task_start first).
**Note:** `component_add` and `gameobject_create` automatically record created objects, so you typically don't need to call this manually.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the Game Object |
| instanceId | int | No | 0 | Instance ID of the object (preferred) |

**Returns:** `{ success, objectName, type }`

### `workflow_list`
List persistent workflow history.

No parameters.

**Returns:** `{ success, count, history: [{ id, tag, description, time, changes }] }`

### `workflow_undo_task`
Undo changes from a specific task (restore to previous state). The undone task is saved and can be redone later.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | The UUID of the task to undo |

**Returns:** `{ success, taskId }`

### `workflow_redo_task`
Redo a previously undone task (restore changes).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | No | null | The UUID of the task to redo. If not provided, redoes the most recently undone task |

**Returns:** `{ success, taskId }`

### `workflow_undone_list`
List all undone tasks that can be redone.

No parameters.

**Returns:** `{ success, count, undoneStack: [{ id, tag, description, time, changes }] }`

### `workflow_revert_task`
**(deprecated)** Alias for `workflow_undo_task`. Use `workflow_undo_task` instead.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | The UUID of the task to undo |

**Returns:** `{ success, taskId }`

### `workflow_delete_task`
Delete a task from history (does not revert changes, just removes the record).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | The UUID of the task to delete |

**Returns:** `{ success, deletedId }`

## Recommended Usage Pattern

### Session-Level (Conversation Undo)

```python
# At the START of each conversation
unity_skills.call_skill("workflow_session_start", tag="Build Player Character")

# ... perform multiple operations ...
unity_skills.call_skill("gameobject_create", name="Player", primitiveType="Capsule")
unity_skills.call_skill("component_add", name="Player", componentType="Rigidbody")
unity_skills.call_skill("component_add", name="Player", componentType="CapsuleCollider")
unity_skills.call_skill("material_create", name="PlayerMaterial", shaderName="Standard")

# At the END of the conversation
unity_skills.call_skill("workflow_session_end")

# Later: Undo the ENTIRE conversation
result = unity_skills.call_skill("workflow_session_list")
session_id = result['sessions'][0]['sessionId']
unity_skills.call_skill("workflow_session_undo", sessionId=session_id)
```

### Task-Level (Fine-Grained Undo)

```python
# 1. Start Task
unity_skills.call_skill("workflow_task_start", tag="Adjust Player Speed", description="Set speed to 10")

# 2. Snapshot target object(s) before modification
unity_skills.call_skill("workflow_snapshot_object", name="Player")

# 3. Perform modifications
unity_skills.call_skill("component_set_property", name="Player", componentType="PlayerController", propertyName="speed", value=10)

# 4. End Task
unity_skills.call_skill("workflow_task_end")
```

## Auto-Tracked Operations

The following operations are **automatically tracked** for undo when a session/task is active:

- `gameobject_create` / `gameobject_create_batch`
- `gameobject_duplicate` / `gameobject_duplicate_batch`
- `component_add` / `component_add_batch`
- `ui_create_*` (canvas, button, text, image, etc.)
- `light_create`
- `prefab_instantiate` / `prefab_instantiate_batch`
- `material_create` / `material_duplicate`
- `terrain_create`
- `cinemachine_create_vcam`

For **modification operations**, the system auto-snapshots target objects before changes when possible.
