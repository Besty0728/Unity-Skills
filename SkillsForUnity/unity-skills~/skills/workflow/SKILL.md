---
name: unity-workflow
description: Persistent operation history and orchestration
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Undoing a restore, a task, a session, or a few recent steps
- Naming a checkpoint before risky changes
- Rolling back
- 撤销、回退到检查点、高危改动前打点

# Workflow Skills

Time Machine for AI operations. Every tracked write is a step; a checkpoint names a point in that history, and restoring a checkpoint undoes everything after it, even after an Editor restart.

## Restore Protocol

1. Call `workflow_restore {"to":"last"}` (or a `c<n>` id or a label from `workflow_list`). With no conflict, that one call is the whole job.
2. On `WORKFLOW_CONFLICT` (the user or another agent changed the target since), ask the user in one sentence which changes to keep. Default to keeping them.
3. Resend with `"conflicts":"keep"`, or with the `conflicts[].id` values the user chose to overwrite. Any conflict not in your list is kept.
4. `?mode=dryRun` previews a restore without applying it. It is optional unless `dryRunPolicy` is on.
5. To undo a restore, call `workflow_restore {"to":"<guard>"}` with the `guard` from its response, or `workflow_redo_task`, which reverts only that restore.

## workflow_list Rows

```
s344 14:36:10 light_set_properties Sun (2)
c343 14:35:58 auto
s338 14:28:11 gameobject_create_batch x12 (12) (undone)
```

- `s<n>` is a step: `id time skill target (changes)`, plus `@agent` when another agent made it, `xN` for a batch, and `(undone)` when a restore reverted it.
- `c<n>` is a checkpoint: `auto`, or its label in quotes.
- `view:"checkpoints"` lists one row per checkpoint; pass the answer's `more` as `before` to page.

## Checkpoints

- **Automatic**: a write that follows at least 30 s of idle time from its agent gets a checkpoint. Compile and reload time is not idle. The switch in the History tab turns automatic checkpoints off; explicit ones still work.
- **`_checkpoint`**: a body key on any write (`POST /skill/<name>`) or on `/skills/batch` (its first recorded step): `{"_checkpoint":"before lighting", ...}`. A failed call leaves no checkpoint.
- **`workflow_checkpoint {label, at?}`**: without `at`, adds a checkpoint before the caller's next step; with `at:"c343"`, renames one (for example, to name an auto checkpoint).

## Restoring

`workflow_restore {to, agents?, conflicts?, scope?}`. `to` is a `c<n>` (back to that checkpoint), an `s<n>` (back to before that step), a label (the newest checkpoint with it), or `last`. `agents` is `self` (default) or `all`. `scope` limits it (see below).

Returns `to`, `guard`, `restored`, `deleted`, `recreated`, `failed`, `skipped`, `coverage`, `undo`, and `compileTriggered` (with `jobId` / `waitUrl` when scripts recompile), plus `merged`, `failedItems` and `skippedItems` when present.

Errors: `WORKFLOW_CONFLICT` (nothing changed; `conflicts` lists each id, target, who and what), `NOT_FOUND` (with `recentCheckpoints`), `SEMANTIC_INVALID` (Play Mode, a bad `agents`, `conflicts` or `scope` value), `COMPILING` (retry after the compile).

An open scene's `.unity` is not rewritten; the scene is marked dirty, and saving applies the restore.

`?mode=dryRun` returns the preview in `changes`. `workflow_undo_task`, `workflow_redo_task` and `workflow_session_undo` share this engine and the same `conflicts` parameter.

## Undoing One Step and Scope

`workflow_undo_task {"taskId":"s342"}` reverts one step. Later steps stay, even on the same object: the undo merges property by property.

`scope` keeps some targets: `entity:<id>`, object names or paths (trailing `/` adds children), `Assets/` paths (trailing `/` = folder), or setting keys (trailing `.` = prefix). Example: `{"to":"c343","scope":"Player/,Assets/Art/"}`. A scoped restore marks no steps `(undone)`. An item matching nothing returns `SEMANTIC_INVALID` with the targets. The dryRun preview lists `danglingReferences` for deleted objects still in use.

## Known Limitations

- Conflict detection reads the change journal: about 22,000 entries (11 segments of 2,000). Once older entries rotate out, `coverage` is `partial` or `unknown` and the older changes are not checked.
- Objects in a never-saved scene are tracked by session-local ids. They do not survive an Editor restart, so a restore reports them as `failed`.
- External side effects (Package Manager operations, etc.) cannot be rolled back.

## Skills

- Tracking: `workflow_task_start`, `workflow_task_end`, `workflow_checkpoint`, `workflow_snapshot_object`, `workflow_snapshot_created`
- Restore and undo: `workflow_restore`, `workflow_undo_task`, `workflow_redo_task`, `workflow_undone_list`, `workflow_revert_task` (deprecated)
- History: `workflow_list`, `workflow_delete_task`, `workflow_clear_history`, `history_undo`, `history_redo`, `history_get_current`
- Sessions: `workflow_session_start`, `workflow_session_end`, `workflow_session_undo`, `workflow_session_list`, `workflow_session_status`
- Planning: `workflow_plan`
- Bookmarks: `bookmark_set`, `bookmark_goto`, `bookmark_list`, `bookmark_delete`

Details are in `reference/<skill_name>.md`.

Read-only skills run under Approval. `bookmark_set`, `bookmark_goto`, `workflow_snapshot_object` and `workflow_snapshot_created` need a grant. `bookmark_delete`, `workflow_delete_task` and `workflow_clear_history` are NeverInSemi (Bypass or allowlist only).

**DO NOT** call `workflow_save` (use `workflow_task_end`), `workflow_rollback` (use `workflow_restore`) or `workflow_create` (use `workflow_task_start`); none of them exists. A single simple undo is `editor_undo`.
