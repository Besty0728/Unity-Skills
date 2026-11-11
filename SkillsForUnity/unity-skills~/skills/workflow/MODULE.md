# Unity Workflow Skills

Time Machine for AI operations: restoring a checkpoint undoes every tracked step after it, even across an Editor restart. Parameters: `GET /skills/schema?category=Workflow&wire=v2`.

## When to use
- Undoing a restore, task, session or recent steps
- Naming a checkpoint before risky changes

## Restore Protocol

1. Call `workflow_restore {"to":"last"}` (or a `c<n>` id or label). Without a conflict, that is the whole job.
2. On `WORKFLOW_CONFLICT`, ask the user in one sentence which changes to keep; default to keeping them.
3. Resend with `"conflicts":"keep"`, or with the `conflicts[].id` values the user chose to overwrite. Any conflict not in your list is kept.
4. `?mode=dryRun` previews a restore without applying it.
5. To undo a restore, call `workflow_restore {"to":"<guard>"}` with the `guard` from its response, or `workflow_redo_task`, which reverts only that restore.

## Checkpoints

- **Automatic**: a write that follows at least 30 s of idle time from its agent gets a checkpoint. Compile and reload time is not idle. The History tab switch turns them off; explicit ones still work.
- **`_checkpoint`**: a body key on any write or on `/skills/batch` (its first recorded step): `{"_checkpoint":"before lighting", ...}`. A failed call leaves no checkpoint.
- **`workflow_checkpoint {label, at?}`**: without `at`, adds a checkpoint before the caller's next step; with `at:"c343"`, renames one.

## Restoring

`workflow_restore {to, agents?, conflicts?, scope?}`. `to` is a `c<n>` (back to that checkpoint), an `s<n>` (back to before that step), a label (the newest checkpoint with it), or `last`. `agents` is `self` (default) or `all`. `scope` limits it (see below).

Returns `to`, `guard`, `restored`, `deleted`, `recreated`, `failed`, `skipped`, `coverage`, `undo`, `compileTriggered`, and the optional `merged`, `failedItems` and `skippedItems`; see [`workflow_restore`](reference/workflow_restore.md).

Errors: `WORKFLOW_CONFLICT` (nothing changed; `conflicts` lists each id, target, who and what), `NOT_FOUND` (with `recentCheckpoints`), `SEMANTIC_INVALID` (Play Mode, a bad `agents`, `conflicts` or `scope` value), `COMPILING` (retry after the compile).

An open scene's `.unity` is not rewritten; the scene is marked dirty, and saving applies the restore.

`?mode=dryRun` returns the preview in `changes`. `workflow_undo_task`, `workflow_redo_task` and `workflow_session_undo` share this engine and the same `conflicts` parameter.

## Undoing One Step and Scope

`workflow_undo_task {"taskId":"s342"}` reverts one step. Later steps stay, even on the same object: the undo merges property by property.

`scope` keeps some targets: `entity:<id>`, object names or paths (trailing `/` adds children), `Assets/` paths (trailing `/` = folder), or setting keys (trailing `.` = prefix). Example: `{"to":"c343","scope":"Player/,Assets/Art/"}`. A scoped restore marks no steps `(undone)`. An item matching nothing returns `SEMANTIC_INVALID` with the targets. The dryRun preview lists `danglingReferences` for deleted objects still in use.

## Known Limitations

- Conflict detection reads the change journal (about 22,000 entries). Once older entries rotate out, `coverage` is `partial` or `unknown` and older changes are not checked.
- Never-saved scene objects use session-local ids; they do not survive an Editor restart and restore as `failed`.
- External side effects (for example Package Manager operations) cannot be rolled back.

## Skills Overview

| Skill | Purpose |
|-------|---------|
| [`workflow_task_start`](reference/workflow_task_start.md) | Start a tracked task |
| [`workflow_task_end`](reference/workflow_task_end.md) | End and save the task |
| [`workflow_checkpoint`](reference/workflow_checkpoint.md) | Name or rename a checkpoint |
| [`workflow_snapshot_object`](reference/workflow_snapshot_object.md) | Snapshot an object (grant) |
| [`workflow_snapshot_created`](reference/workflow_snapshot_created.md) | Record a created object (grant) |
| [`workflow_restore`](reference/workflow_restore.md) | Restore to a checkpoint or step |
| [`workflow_undo_task`](reference/workflow_undo_task.md) | Undo one step or group |
| [`workflow_redo_task`](reference/workflow_redo_task.md) | Redo a restore |
| [`workflow_undone_list`](reference/workflow_undone_list.md) | List redoable restores |
| [`workflow_revert_task`](reference/workflow_revert_task.md) | Deprecated; use `workflow_undo_task` |
| [`workflow_list`](reference/workflow_list.md) | List tasks and checkpoints |
| [`workflow_delete_task`](reference/workflow_delete_task.md) | Remove a task record (NeverInSemi) |
| [`workflow_clear_history`](reference/workflow_clear_history.md) | Clear all history (NeverInSemi) |
| [`history_undo`](reference/history_undo.md) | Undo the last operation(s) |
| [`history_redo`](reference/history_redo.md) | Redo the last undone operation(s) |
| [`history_get_current`](reference/history_get_current.md) | Get the current undo group |
| [`workflow_session_start`](reference/workflow_session_start.md) | Start a session |
| [`workflow_session_end`](reference/workflow_session_end.md) | End and save the session |
| [`workflow_session_undo`](reference/workflow_session_undo.md) | Undo a whole session |
| [`workflow_session_list`](reference/workflow_session_list.md) | List sessions |
| [`workflow_session_status`](reference/workflow_session_status.md) | Get session status |
| [`workflow_plan`](reference/workflow_plan.md) | Build a combined plan |
| [`bookmark_set`](reference/bookmark_set.md) | Save selection and Scene view (grant) |
| [`bookmark_goto`](reference/bookmark_goto.md) | Restore selection and Scene view (grant) |
| [`bookmark_list`](reference/bookmark_list.md) | List bookmarks |
| [`bookmark_delete`](reference/bookmark_delete.md) | Delete a bookmark (NeverInSemi) |

## Pitfalls
- `workflow_save`, `workflow_rollback` and `workflow_create` do not exist. Use `workflow_task_end`, `workflow_restore` and `workflow_task_start`.
- A single simple undo is `editor_undo`.
