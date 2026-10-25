### workflow_undo_task
Undo one step or group: its targets go back to their state before it. Later steps on other targets stay. Each undo is recorded as a restore with a guard checkpoint, so `workflow_redo_task` can take it back.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | `s<seq>` or the task UUID (one step); a group id (`g<n>` / `b<n>`, every step of the group); or `c<n>` (every step of the group of the step at seq n) |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

**Returns:** `{ taskId, to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`, plus `merged` when targets were merged and `failedItems` when something failed. A failed item makes the call report `failed > 0`; nothing is split. Unknown id: `errorCode: NOT_FOUND`. A later step on another property of the same target is kept and merged; only a property changed by both is a conflict, which refuses with `WORKFLOW_CONFLICT` until it is answered (see Conflicts under `workflow_restore`).

**Surface profile:** `workflow_undo_task` / `workflow_redo_task` / `workflow_revert_task` / `workflow_session_undo` inspect every snapshot the target task(s) would restore. If any snapshot would author a category the active profile withdraws — a scene GameObject, or a `.unity` / `.mat` asset — the whole call is refused with `SURFACE_EXCLUDED` (`surfaceProfile`/`category`/`operation`/`manualDoc`/`userControlled`/`hint` at the top level, not nested under `details`) rather than partially applied. Snapshots of scripts, prefabs, textures, ScriptableObjects, and Editor/project settings are unaffected — those stay undoable under every profile.
