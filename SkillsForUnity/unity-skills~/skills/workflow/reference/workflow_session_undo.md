### workflow_session_undo
Undo all changes made during a specific session (conversation-level undo).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sessionId | string | No | null | The UUID of the session to undo. If not provided, undoes the most recent session |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

Restores the session's steps the same way `workflow_restore` does, so the response is the restore response plus `sessionId`.

**Returns:** `{ sessionId, to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`
