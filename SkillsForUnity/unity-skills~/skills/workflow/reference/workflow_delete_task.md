### workflow_delete_task
Delete a task from history (does not revert changes, just removes the record).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | The `s<seq>` id or UUID of the task to delete |

**Returns:** `{ success, deletedId }`
