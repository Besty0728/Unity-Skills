# Workflow Skills

Persistent history and rollback system for AI operations ("Time Machine").
Allows tagging tasks, snapshotting objects before modification, and undoing specific tasks even after Editor restarts.

**NEW: Session-level undo** - Group all changes from a conversation and undo them together.

## Session Management (Conversation-Level Undo)

### `workflow_session_start`
Start a new session (conversation-level). All changes will be tracked and can be undone together.
**Call this at the beginning of each conversation.**
**Parameters:**
- `tag` (string, optional): Label for the session.

**Returns:** `{ success: true, sessionId: "uuid..." }`

### `workflow_session_end`
End the current session and save all tracked changes.
**Call this at the end of each conversation.**
**Parameters:** None.

**Returns:** `{ success: true, sessionId: "..." }`

### `workflow_session_undo`
Undo all changes made during a specific session (conversation-level undo).
**Parameters:**
- `sessionId` (string, optional): The UUID of the session to undo. If not provided, undoes the most recent session.

**Returns:** `{ success: true, sessionId: "...", message: "Session changes undone successfully" }`

### `workflow_session_list`
List all recorded sessions (conversation-level history).
**Parameters:** None.

**Returns:**
```json
{
  "success": true,
  "count": 3,
  "currentSessionId": "...",
  "sessions": [
    { "sessionId": "...", "taskCount": 2, "totalChanges": 15, "startTime": "...", "endTime": "...", "tags": ["..."] }
  ]
}
```

### `workflow_session_status`
Get the current session status.
**Parameters:** None.

**Returns:** `{ hasActiveSession: true, currentSessionId: "...", isRecording: true, snapshotCount: 5 }`

## Task-Level Skills

### `workflow_task_start`
Start a new persistent workflow task/session.
**Parameters:**
- `tag` (string): Short label for the task (e.g., "Create NPC").
- `description` (string, optional): Detailed description or prompt.

**Returns:** `{ success: true, taskId: "uuid..." }`

### `workflow_task_end`
End the current persistent workflow task and save to disk.
**Parameters:** None.

**Returns:** `{ success: true, taskId: "...", snapshotCount: 5 }`

### `workflow_snapshot_object`
Manually snapshot an object's state *before* you modify it.
**Call this BEFORE `component_set_property`, `gameobject_set_transform`, etc.**
**Parameters:**
- `name` (string, optional): Name of the Game Object.
- `instanceId` (int, optional): Instance ID of the object (preferred).

**Returns:** `{ success: true, objectName: "Cube", type: "GameObject" }`

### `workflow_snapshot_created`
Record a newly created object for undo tracking. Use this when you create objects via methods that don't automatically record (e.g., custom scripts).
**Note:** `component_add` and `gameobject_create` automatically record created objects, so you typically don't need to call this manually.
**Parameters:**
- `name` (string, optional): Name of the Game Object.
- `instanceId` (int, optional): Instance ID of the object (preferred).

**Returns:** `{ success: true, objectName: "Cube", type: "Rigidbody" }`

### `workflow_list`
List persistent workflow history.
**Parameters:** None.

**Returns:**
```json
{
  "success": true,
  "history": [
    { "id": "...", "tag": "Fix Light", "time": "14:30:00", "changes": 2 }
  ]
}
```

### `workflow_undo_task`
Undo changes from a specific task. Restores snapshotted objects to their original state and deletes objects created during the task.
**Parameters:**
- `taskId` (string): The UUID of the task to undo.

**Returns:** `{ success: true, taskId: "..." }`

### `workflow_revert_task` (deprecated)
Alias for `workflow_undo_task`. Use `workflow_undo_task` instead.
**Parameters:**
- `taskId` (string): The UUID of the task to undo.

**Returns:** `{ success: true, taskId: "..." }`

### `workflow_delete_task`
Delete a task record from history (does *not* undo changes, just removes the record).
**Parameters:**
- `taskId` (string): The UUID of the task to delete.

**Returns:** `{ success: true }`

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
