### workflow_task_start
Start a new persistent workflow task to track changes for undo. Call workflow_task_end when done.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tag | string | Yes | - | Short label for the task (e.g., "Create NPC") |
| description | string | No | "" | Detailed description or prompt |

**Returns:** `{ success, taskId, checkpoint, message }`. `checkpoint` is the task's checkpoint id (`c<n>`), labelled with the tag.
