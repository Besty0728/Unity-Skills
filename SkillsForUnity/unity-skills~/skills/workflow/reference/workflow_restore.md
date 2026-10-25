### workflow_restore
Puts the scene and project assets back to a checkpoint. Only the caller's steps from that point on are reverted unless `agents` is `all`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| to | string | Yes | - | A `c<n>` id, an `s<n>` step, `last` (the caller's newest checkpoint) or a checkpoint label |
| agents | string | No | `self` | `self` or `all` |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

**Returns:** `{ to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`, plus `failedItems` (up to 20 `name (type): reason` strings) when something failed, and `skippedItems` (up to 20 `target (kept: <by> change <id>)` strings) when a kept conflict skipped work. `guard` is the checkpoint taken before the restore; `undo` is the call that reverts this restore (`workflow_restore {"to":"<guard>"}`). Ctrl+Z only reverts the scene, not the assets, so use this call to undo a restore. Script changes start a compile job (`jobId`, `waitUrl`). Play Mode returns `SEMANTIC_INVALID`; an unknown position returns `NOT_FOUND` with `recentCheckpoints`.

**Conflicts.** A restore refuses to overwrite changes made since the checkpoint that it did not make itself: a person's edit in the Inspector or the scene, or another agent's step on the same target. Such a refusal is `WORKFLOW_CONFLICT`, with top-level `conflicts` (each `{id, target, by, what, at}`, plus `confidence: "logged"` for journal entries) and `coverage` (`full`, `partial` when rotation dropped part of the journal, or `unknown`). Nothing changed and no guard was taken. Protocol: ask the user in one sentence, default to keeping their changes, then retry with `conflicts:"keep"` (every conflicting target is skipped and listed in `skippedItems`), or with the ids the user wants overwritten. A conflict that is not listed is kept. `conflicts` takes effect the same way for `workflow_undo_task`, `workflow_redo_task` and `workflow_session_undo`.

**Preview (dryRun).** `POST /skill/workflow_restore?mode=dryRun` applies nothing and returns the preview in `changes`: `to`, `label` (when `to` is a checkpoint), `agent`, `steps`, `plan` (`modify`, `delete`, `recreate`, `move`, `settings`), `items` (up to 20 `name (type) action` strings, then `moreItems`), `conflicts` (the same ids the refusal lists), `coverage`, `unresolvable` (`{target, reason}`, such as a scene that is not open) and `mayTriggerReload`. The preview is optional unless `dryRunPolicy` is on. `workflow_undo_task`, `workflow_redo_task` and `workflow_session_undo` preview the same way.
