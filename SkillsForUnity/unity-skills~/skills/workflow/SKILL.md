---
name: unity-workflow
description: Persistent operation history and orchestration — snapshots, task/session undo, bookmarks, and batch planning/retry/rollback. Use when undoing a whole task or session, snapshotting before risky changes, planning or previewing batch operations, or rolling back, even if the user just says "撤销整个操作" or "回滚". 持久化操作历史与编排(快照、任务/会话级撤销、书签、批量规划/重试/回滚);当用户要撤销整个任务或会话、在高危改动前快照、规划或预览批量操作、或回滚时使用。
---

# Workflow Skills

Persistent history and rollback system for AI operations ("Time Machine").
Allows tagging tasks, snapshotting objects before modification, and undoing specific tasks even after Editor restarts.

**NEW: Session-level undo** - Group all changes from a conversation and undo them together.

## Operating Mode

- **Approval**：本模块大部分 skill 标 `SkillMode.SemiAuto`（bookmark / history / task / session 系列 + `workflow_plan`，后者 ReadOnly=true 仅生成聚合计划），可直接执行。少数写类 skill (`workflow_snapshot_object` / `workflow_snapshot_created` / `batch_retry_failed`) 走默认 `SkillMode.FullAuto`，需 grant。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`bookmark_delete` / `workflow_delete_task`（标 Operation.Delete，删除书签/任务记录）、`workflow_clear_history`（Operation.Delete + RiskLevel=high，清空全部历史+redo栈+文件存储，不可逆）。这些在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

> 注意：`workflow_undo_task` / `workflow_session_undo` 不是 Delete operation（标的是 Modify/Execute），它们能在 Approval/Auto 直接撤销已记录任务。

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
| `namePattern` | string | No | - | Regex applied to file name without extension |
| `labelFilter` | string | No | - | Asset label filter such as `l:Addressable` |
| `maxResults` | int | No | `200` | Max assets returned |

**Returns:** `{ count, totalMatched, summary, assets }`

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

**Returns:** `{ success, sessionId, message }`

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

**Returns:** `{ success, taskId, message }`

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
List persistent workflow history.

No parameters.

**Returns:** `{ success, count, history: [{ id, tag, description, time, changes }] }`

### `workflow_undo_task`
Undo changes from a specific task (restore to previous state). The undone task is saved and can be redone later.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | Yes | - | The UUID of the task to undo |

**Returns:** `{ success, taskId }`

### `workflow_redo_task`
Redo a previously undone task (restore changes).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| taskId | string | No | null | The UUID of the task to redo. If not provided, redoes the most recently undone task |

**Returns:** `{ success, taskId }`

### `workflow_undone_list`
List all undone tasks that can be redone.

No parameters.

**Returns:** `{ success, count, undoneStack: [{ id, tag, description, time, changes }] }`

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
| taskId | string | Yes | - | The UUID of the task to delete |

**Returns:** `{ success, deletedId }`

### `workflow_clear_history`
Permanently clear **ALL** workflow history: every task, the redo (undone) stack, and every backed-up file blob in the content-addressed store. High-risk and irreversible.
**This ONLY deletes tracking history — it does NOT undo or revert any change already applied to your project assets, scenes, or settings.** To roll changes back, use `workflow_undo_task` / `workflow_session_undo` *before* clearing.

Marked `SkillOperation.Delete` + `RiskLevel="high"`, so it is `NeverInSemi` (returns `MODE_FORBIDDEN` in Approval/Auto; only Bypass or an Allowlist hit can call it).

**Parameters:** None

**Returns:** `{ success, before, after, message }` where `before`/`after` each report `{ tasks, undoneStack, historyFileBytes, fileStoreBytes }`.

## Snapshot Mechanism

History is persisted to `workflow_history.json` (`schemaVersion` 4). Asset and `.meta` bytes are independently content-addressed in `Library/UnitySkills/workflow_files/<sha1>`; history keeps `fileHash` / `metaFileHash` references. Schema 2/3 histories are migrated atomically: legacy blobs are made durable before inline base64 is removed.

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

`WorkflowAutoCleanConfig` (EditorPrefs keys `UnitySkills.Workflow.*`) trims history and the file store after `EndTask` and after `LoadHistory`. Defaults: `MaxTasks=200`, `MaxHistoryMB=32`, `MaxTaskAgeDays=30`, `MaxStoreMB=512`, `StoreMaxAgeDays=7`. Store age/size pruning never removes a blob referenced by retained history. A value of `0` disables that individual limit.

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
