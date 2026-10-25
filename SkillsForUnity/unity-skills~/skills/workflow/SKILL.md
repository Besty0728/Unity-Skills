---
name: unity-workflow
description: Persistent operation history and orchestration
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Undoing a whole task or session
- Snapshotting before risky changes
- Planning/previewing batch operations
- Rolling back
- 撤销整个任务或会话、高危改动前快照、规划/预览批量操作、回滚

# Workflow Skills

Persistent history and rollback system for AI operations ("Time Machine").
Allows tagging tasks, snapshotting objects before modification, and undoing specific tasks even after Editor restarts.

**NEW: Session-level undo** - Group all changes from a conversation and undo them together.

## Operating Mode

- **Approval**：本模块大部分 skill 标 `SkillMode.SemiAuto`（bookmark / history / task / session 系列里的纯读查询 + `workflow_plan`，后者 ReadOnly=true 仅生成聚合计划），可直接执行。有副作用的 skill (`bookmark_set` / `bookmark_goto` / `workflow_snapshot_object` / `workflow_snapshot_created` / `batch_retry_failed`) 走默认 `SkillMode.FullAuto`，需 grant。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`bookmark_delete` / `workflow_delete_task`（标 Operation.Delete，删除书签/任务记录）、`workflow_clear_history`（Operation.Delete + RiskLevel=high，清空全部历史+redo栈+文件存储，不可逆）。这些在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

> 注意：`workflow_undo_task` / `workflow_session_undo` 不是 Delete operation（标的是 Modify/Execute），它们能在 Approval/Auto 直接撤销已记录任务。

**Surface profile:** `workflow_undo_task` / `workflow_redo_task` / `workflow_revert_task` / `workflow_session_undo` inspect every snapshot the target task(s) would restore. If any snapshot would author a category the active profile withdraws — a scene GameObject, or a `.unity` / `.mat` asset — the whole call is refused with `SURFACE_EXCLUDED` (`surfaceProfile`/`category`/`operation`/`manualDoc`/`userControlled`/`hint` at the top level, not nested under `details`) rather than partially applied. Snapshots of scripts, prefabs, textures, ScriptableObjects, and Editor/project settings are unaffected — those stay undoable under every profile.

**DO NOT** (common hallucinations):
- `workflow_save` does not exist → use `workflow_task_end` to end and save a task
- `workflow_rollback` does not exist → use `workflow_undo_task` (by taskId) or `workflow_session_undo` (by sessionId)
- `workflow_create` does not exist → use `workflow_task_start`
- `workflow_revert_task` is deprecated → use `workflow_undo_task`

**Routing**:
- For simple undo/redo (1 step) → `editor_undo` / `editor_redo` (editor module)
- For multi-step undo → `history_undo` with `steps` parameter (this module)
- For conversation-level undo → `workflow_session_undo` (this module)

## Bookmark Skills

### `bookmark_set`
Save current selection and scene view position as a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Name for the bookmark |
| note | string | No | null | Optional note for the bookmark |

**Returns:** `{ success, bookmark, selectedCount, hasSceneView, note }`

### `bookmark_goto`
Restore selection and scene view from a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Name of the bookmark to restore |

**Returns:** `{ success, bookmark, restoredSelection, note }`

### `bookmark_list`
List all saved bookmarks.

No parameters.

**Returns:** `{ success, count, bookmarks: [{ name, selectedCount, hasSceneView, note, createdAt }] }`

### `bookmark_delete`
Delete a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Name of the bookmark to delete |

**Returns:** `{ success, deleted }`

## History Skills

### `history_undo`
Undo the last operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of undo steps to perform |

**Returns:** `{ success, undoneSteps }`

### `history_redo`
Redo the last undone operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of redo steps to perform |

**Returns:** `{ success, redoneSteps }`

### `history_get_current`
Get the name of the current undo group.

No parameters.

**Returns:** `{ success, currentGroup, groupIndex }`

## Planning And Batch Governance

### `workflow_plan`
Generate a combined execution plan for multiple skills on the server side.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `skillsJson` | string | Yes | - | JSON array of `{ "name": "...", "params": { ... } }` entries |

**Returns:** `{ totalSteps, totalRisk, steps, dependencies, warnings, mayDisconnect }`

### `batch_query_assets`
Query project assets with filters that are useful before batch cleanup or migration work.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchFilter` | string | No | - | Extra `AssetDatabase.FindAssets` filter text |
| `folder` | string | No | `Assets` | Search root |
| `typeFilter` | string | No | - | Asset type filter such as `t:Material` or `Prefab` |
| `namePattern` | string | No | - | Regex applied to file name without extension; ignores case unless `caseSensitive` |
| `labelFilter` | string | No | - | Asset label filter such as `l:Addressable` |
| `maxResults` | int | No | `200` | Max assets returned |
| `caseSensitive` | bool | No | `false` | `true` makes `namePattern` match case exactly |

**Returns:** `{ count, totalMatched, truncated, summary, assets }` (`totalMatched` counts after the `namePattern` filter; `truncated` = `maxResults` cut the list)

### `batch_retry_failed`
Retry only the failed items from an earlier batch execution report. This now reuses the original operation context stored in the report.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `reportId` | string | Yes | - | Source report ID from `batch_report_get` / `batch_report_list` |
| `runAsync` | bool | No | `true` | Return a `jobId` immediately or wait for completion |
| `chunkSize` | int | No | `100` | Chunk size for retry execution |

**Returns:** `{ status, jobId?, retryCount, originalReportId, reportId? }`

## Session Management (Conversation-Level Undo)

### `workflow_session_start`
Start a new session (conversation-level). All changes will be tracked and can be undone together.
**Call this at the beginning of each conversation.**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tag | string | No | null | Label for the session |

**Returns:** `{ success, sessionId, message }`

### `workflow_session_end`
End the current session and save all tracked changes.
**Call this at the end of each conversation.**

No parameters.

**Returns:** `{ success, sessionId, message }`

### `workflow_session_undo`
Undo all changes made during a specific session (conversation-level undo).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sessionId | string | No | null | The UUID of the session to undo. If not provided, undoes the most recent session |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

Restores the session's steps the same way `workflow_restore` does, so the response is the restore response plus `sessionId`.

**Returns:** `{ sessionId, to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`

### `workflow_session_list`
List all recorded sessions (conversation-level history).

No parameters.

**Returns:** `{ success, count, currentSessionId, sessions: [{ sessionId, taskCount, totalChanges, startTime, endTime, tags }] }`

### `workflow_session_status`
Get the current session status.

No parameters.

**Returns:** `{ success, hasActiveSession, currentSessionId, isRecording, currentTaskId, currentTaskTag, currentTaskDescription, snapshotCount }`

## Task-Level Skills

### `workflow_task_start`
Start a new persistent workflow task to track changes for undo. Call workflow_task_end when done.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| tag | string | Yes | - | Short label for the task (e.g., "Create NPC") |
| description | string | No | "" | Detailed description or prompt |

**Returns:** `{ success, taskId, checkpoint, message }`. `checkpoint` is the task's checkpoint id (`c<n>`), labelled with the tag.

### `workflow_checkpoint`
Names a point in the undo history. Without `at` it adds a checkpoint before the caller's next recorded step; with `at` it renames an existing checkpoint.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| label | string | Yes | - | The name, e.g. `"before lighting"` |
| at | string | No | null | A `c<n>` id from `workflow_list` to rename instead of adding one |

**Returns:** `{ id, label }`. An unknown `at` returns `NOT_FOUND`.

Any write skill also takes a `_checkpoint` body key (`{"_checkpoint":"before lighting", ...}`), which names the checkpoint before that call's step. A failed call leaves no checkpoint. A `/skills/batch` body takes the same key for its first recorded step.

### `workflow_restore`
Puts the scene and project assets back to a checkpoint. Only the caller's steps from that point on are reverted unless `agents` is `all`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| to | string | Yes | - | A `c<n>` id, an `s<n>` step, `last` (the caller's newest checkpoint) or a checkpoint label |
| agents | string | No | `self` | `self` or `all` |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

**Returns:** `{ to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`, plus `failedItems` (up to 20 `name (type): reason` strings) when something failed, and `skippedItems` (up to 20 `target (kept: <by> change <id>)` strings) when a kept conflict skipped work. `guard` is the checkpoint taken before the restore; `undo` is the call that reverts this restore (`workflow_restore {"to":"<guard>"}`). Ctrl+Z only reverts the scene, not the assets, so use this call to undo a restore. Script changes start a compile job (`jobId`, `waitUrl`). Play Mode returns `SEMANTIC_INVALID`; an unknown position returns `NOT_FOUND` with `recentCheckpoints`.

**Conflicts.** A restore refuses to overwrite changes made since the checkpoint that it did not make itself: a person's edit in the Inspector or the scene, or another agent's step on the same target. Such a refusal is `WORKFLOW_CONFLICT`, with top-level `conflicts` (each `{id, target, by, what, at}`, plus `confidence: "logged"` for journal entries) and `coverage` (`full`, `partial` when rotation dropped part of the journal, or `unknown`). Nothing changed and no guard was taken. Protocol: ask the user in one sentence, default to keeping their changes, then retry with `conflicts:"keep"` (every conflicting target is skipped and listed in `skippedItems`), or with the ids the user wants overwritten. A conflict that is not listed is kept. `conflicts` takes effect the same way for `workflow_undo_task`, `workflow_redo_task` and `workflow_session_undo`.

**Preview (dryRun).** `POST /skill/workflow_restore?mode=dryRun` applies nothing and returns the preview in `changes`: `to`, `label` (when `to` is a checkpoint), `agent`, `steps`, `plan` (`modify`, `delete`, `recreate`, `move`, `settings`), `items` (up to 20 `name (type) action` strings, then `moreItems`), `conflicts` (the same ids the refusal lists), `coverage`, `unresolvable` (`{target, reason}`, such as a scene that is not open) and `mayTriggerReload`. The preview is optional unless `dryRunPolicy` is on. `workflow_undo_task`, `workflow_redo_task` and `workflow_session_undo` preview the same way.

### `workflow_task_end`
End the current workflow task and save it. Requires an active task (call workflow_task_start first).

No parameters.

**Returns:** `{ success, taskId, snapshotCount, message }`

### `workflow_snapshot_object`
Manually snapshot an object's state before modification. Requires an active task (call workflow_task_start first).
**Call this BEFORE `component_set_property`, `gameobject_set_transform`, etc.**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the Game Object |
| instanceId | int | No | 0 | Instance ID of the object (preferred) |

**Returns:** `{ success, objectName, type }`

### `workflow_snapshot_created`
Record a newly created object for undo tracking. Requires an active task (call workflow_task_start first).
**Note:** `component_add` and `gameobject_create` automatically record created objects, so you typically don't need to call this manually.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the Game Object |
| instanceId | int | No | 0 | Instance ID of the object (preferred) |

**Returns:** `{ success, objectName, type }`

### `workflow_list`
List recorded tasks, newest first, as compact text rows. Each tracked write is one task with a short id `s<seq>`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 0 | Rows of tasks to return; `0` = 30 (rows) or all (objects); max 200 |
| agent | string | No | all | `all`, `self` (the calling agent), `unknown`, or an agent id such as `Codex` |
| before | string | No | null | An `s<seq>` or `c<n>` id: list only rows above it (pass the previous answer's `more`) |
| view | string | No | all | `all`, or `checkpoints` for one row per checkpoint with the steps it covers |
| format | string | No | rows | `rows`, or `objects` for the legacy object list (oldest first) |

**Returns (rows):** `{ success, count, agent, rows, more? }`: `agent` is the caller; `more` is present when older rows remain. Checkpoint rows (`c<n> time auto` or `c<n> time "label"`) sit among the steps; a `(undone)` step was reverted by a restore.

```
s344 14:36:10 light_set_properties Sun (2)
s343 14:35:58 light_create Sun (1)
s342 14:31:02 material_set_color Floor (1) @Codex
s341 14:28:11 gameobject_create_batch x12 (12)
```

A task row is `id time skill target (changes)`, plus `@agent` when another agent made it; `xN` counts an `items` batch. A caller the server could not identify yet (a bare `curl` or Python client) is `unknown` until the process lookup answers. Undo a row with `workflow_undo_task {"taskId":"s344"}`, newest first: undoing an older task restores its objects over any later edit to them.

**Returns (objects):** `{ success, count, history: [{ id, tag, description, time, changes, seq, agent, group }] }`. `group` is `g<seq>` for one agent's run of writes without a 30 s pause (an explicit `workflow_task_start`..`end` task is a run of its own) and `j<jobId>` for the chunks of one async batch job.

### `workflow_undo_task`
Undo one step or group: its targets go back to their state before it. Later steps on other targets stay. Each undo is recorded as a restore with a guard checkpoint, so `workflow_redo_task` can take it back.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | `s<seq>` or the task UUID (one step); a group id (`g<n>` / `b<n>`, every step of the group); or `c<n>` (every step of the group of the step at seq n) |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

**Returns:** `{ taskId, to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`, plus `failedItems` when something failed. A failed item makes the call report `failed > 0`; nothing is split. Unknown id: `errorCode: NOT_FOUND`. A later step of your own on the same target is a conflict too, so the undo refuses with `WORKFLOW_CONFLICT` until it is answered (see Conflicts under `workflow_restore`).

### `workflow_redo_task`
Undo a restore (from `workflow_restore` or `workflow_undo_task`): only the targets that restore changed go back, and steps after it stay. To go back to the moment before the restore instead, discarding everything since, call `workflow_restore {"to":"<guard>"}`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | No | null | The restore's `s<seq>`, its guard `c<n>`, or a step it reverted. Omitted = your newest undoable restore |
| conflicts | string | No | null | Omit to refuse on conflicts (`WORKFLOW_CONFLICT` lists them), `"keep"` to skip them, or the conflict ids to overwrite |

**Returns:** `{ to, guard, restored, deleted, recreated, failed, skipped, coverage, undo, compileTriggered }`. No undoable restore: `errorCode: NOT_FOUND`.

### `workflow_undone_list`
List restores that can still be undone with `workflow_redo_task`.

No parameters.

**Returns:** `{ success, count, restores: [{ seq, to, guard, agent, steps }] }`; `steps` is the number of steps the restore covered.

### `workflow_revert_task`
**(deprecated)** Alias for `workflow_undo_task`. Use `workflow_undo_task` instead.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | The UUID of the task to undo |

**Returns:** `{ success, taskId }`

### `workflow_delete_task`
Delete a task from history (does not revert changes, just removes the record).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | The `s<seq>` id or UUID of the task to delete |

**Returns:** `{ success, deletedId }`

### `workflow_clear_history`
Permanently clear **ALL** workflow history: every task, every restore record, and every backed-up file blob in the content-addressed store. High-risk and irreversible.
**This ONLY deletes tracking history — it does NOT undo or revert any change already applied to your project assets, scenes, or settings.** To roll changes back, use `workflow_undo_task` / `workflow_session_undo` *before* clearing.

Marked `SkillOperation.Delete` + `RiskLevel="high"`, so it is `NeverInSemi` (returns `MODE_FORBIDDEN` in Approval/Auto; only Bypass or an Allowlist hit can call it).

**Parameters:** None

**Returns:** `{ success, before, after, message }` where `before`/`after` each report `{ tasks, restores, historyFileBytes, fileStoreBytes }`.

## Snapshot Mechanism

History is an append-only log under `Library/UnitySkills/workflow/`: `log.jsonl` plus sealed segments `log.NNNNNN.jsonl` (a segment seals at 4 MB or 5000 lines). Each record states one task's current state, so replaying the log in order rebuilds the tasks; restore records are logged alongside. Snapshot payloads and asset / `.meta` bytes are content-addressed blobs under `workflow/blobs/`, written before the record that names them. Pre-3.0 history is moved to `Library/UnitySkills/workflow_legacy/` and deleted automatically 14 days later.

Snapshots are tiered by `SnapshotType`:

| Type | Trigger | What is stored | Undo behavior |
|------|---------|----------------|---------------|
| **Created** | New asset/folder | path + GUID only | Delete the created asset/folder |
| **Moved** | `asset_move` | old + new path only | Move back to the old path |
| **Deleted** | `asset_delete` etc. | file + `.meta` moved into the store | Full restore, **including `.cs` scripts** (old implementation could not restore `.cs`) |
| **Modified** | material / SO / scene / uss / uxml / shadergraph … | content-addressed backup + lightweight `originalJson` | Restore the backed-up bytes |
| **Setting** | editor / project settings | handled via `WorkflowSettingRestorerRegistry` | Registry restores the previous value |

Undo/redo return per-snapshot detail (`TaskUndoResult`: `total` / `succeeded` / `failed` / `details` / `error`). Operations run in reverse order; on the first failure, failed and unprocessed snapshots stay on their source stack so they can be retried.

### Auto-clean

`WorkflowAutoCleanConfig` (per-project settings `UnitySkills.Workflow.*`) has an `Enabled` toggle and two limits. `CapacityMB` (default 512) caps the log segments plus blobs together; `MaxAgeDays` (default 30) removes sealed segments whose newest record is older than that. Eviction removes whole segments, oldest first; `0` disables a limit. A blob is kept while any retained segment names it or it was written within the last 10 minutes. `workflow_delete_task` removes the task from history, but its disk space is freed only when its segment is evicted. Cleanup runs deferred, never inside a write: after the history loads, when a segment seals or the log grows past its size check, and on the History tab's Clean Now.

### Settings are now truly revertible

Setting-class skills used to be one-way; they now register a restorer and can be rolled back: `console_set_pause_on_error` / `console_set_collapse` / `console_set_clear_on_play`, `debug_set_defines`, `graphics_set_quality_level` / `graphics_set_default_render_pipeline` / `graphics_set_quality_render_pipeline` / `graphics_add_always_included_shader` / `graphics_remove_always_included_shader` / `graphics_set_shader_stripping`, `physics_set_gravity` / `physics_set_layer_collision`, `project_add_tag`.

Cinemachine's 28 write skills now set `TracksWorkflow=true` (the snapshot code existed but never auto-triggered). `scene_save` / `scene_create` are now rollback-capable (`scene_save` over an existing scene backs up the old file as a Modified snapshot).

### Known Limitations

- **`scene_save` undo restores the on-disk `.unity` file**; if the scene is currently open, you must **Reload Scene** for the restore to take effect.
- **Objects created in a never-saved scene**: their `GlobalObjectId` becomes invalid across an Editor restart, so undo will mark them as failed in the result detail.
- **External side effects** (Package Manager operations, etc.) cannot be rolled back.

## Minimal Example

```python
import unity_skills

# Session-level: wrap entire conversation for bulk undo
unity_skills.call_skill("workflow_session_start", tag="Build Player")
unity_skills.call_skill("gameobject_create", name="Player", primitiveType="Capsule")
unity_skills.call_skill("component_add", name="Player", componentType="Rigidbody")
unity_skills.call_skill("workflow_session_end")
# Later: undo entire session
sessions = unity_skills.call_skill("workflow_session_list")
unity_skills.call_skill("workflow_session_undo", sessionId=sessions["sessions"][0]["sessionId"])
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

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
