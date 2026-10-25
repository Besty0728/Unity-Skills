### workflow_redo_task
Undo a restore (from `workflow_restore` or `workflow_undo_task`): only the targets that restore changed go back, and steps after it stay. To go back to the moment before the restore instead, discarding everything since, call `workflow_restore {"to":"<guard>"}`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | No | null | The restore's `s<seq>`, its guard `c<n>`, or a step it reverted. Omitted = your newest undoable restore |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

**Returns:** `{ to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`. No undoable restore: `errorCode: NOT_FOUND`.
