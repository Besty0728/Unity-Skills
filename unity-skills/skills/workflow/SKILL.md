# Workflow Skills

Persistent history and rollback system for AI operations ("Time Machine").
Allows tagging tasks, snapshotting objects before modification, and reverting specific tasks even after Editor restarts.

## Skills

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

### `workflow_revert_task`
Revert changes from a specific task. Restores snapshotted objects to their original state.
**Parameters:**
- `taskId` (string): The UUID of the task to revert.

**Returns:** `{ success: true, taskId: "..." }`

### `workflow_delete_task`
Delete a task record from history (does *not* revert changes, just removes the record).
**Parameters:**
- `taskId` (string): The UUID of the task to delete.

**Returns:** `{ success: true }`

## Usage Pattern

```python
# 1. Start Task
unity_skills.call_skill("workflow_task_start", tag="Adjust Player Speed", description="Set speed to 10")

# 2. Snapshot target object(s)
unity_skills.call_skill("workflow_snapshot_object", name="Player")

# 3. Perform modifications
unity_skills.call_skill("component_set_property", name="Player", componentType="PlayerController", propertyName="speed", value=10)

# 4. End Task
unity_skills.call_skill("workflow_task_end")
```
