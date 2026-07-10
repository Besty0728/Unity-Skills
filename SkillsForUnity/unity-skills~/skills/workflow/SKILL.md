---
name: unity-workflow
description: Persistent operation history and orchestration — snapshots, task/session undo, bookmarks, macro record→replay, and batch planning/retry/rollback. Use when undoing a whole task or session, snapshotting before risky changes, recording manual edits to replay them, planning or previewing batch operations, or rolling back, even if the user just says "撤销整个操作" or "回滚". 持久化操作历史与编排(快照、任务/会话级撤销、书签、示教录制→回放、批量规划/重试/回滚);当用户要撤销整个任务或会话、在高危改动前快照、录制手工操作以便回放、规划或预览批量操作、或回滚时使用。
---

# Workflow Skills

Persistent history and rollback system for AI operations ("Time Machine").
Allows tagging tasks, snapshotting objects before modification, and undoing specific tasks even after Editor restarts.

**NEW: Session-level undo** - Group all changes from a conversation and undo them together.

## Operating Mode

- **Approval**：本模块大部分 skill 标 `SkillMode.SemiAuto`（bookmark / history / task / session 系列 + `workflow_plan`，后者 ReadOnly=true 仅生成聚合计划），可直接执行。少数写类 skill (`workflow_snapshot_object` / `workflow_snapshot_created` / `batch_retry_failed`) 走默认 `SkillMode.FullAuto`，需 grant。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`bookmark_delete` / `workflow_delete_task`（标 Operation.Delete，删除书签/任务记录）。这些在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

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

## Macro Recording (Demonstration → Replay)

Record manual Editor operations, then invert them into a replayable `POST /skills/batch` step sequence — teach by doing, then generalize. Two change sources are fused: `ObjectChangeEvents` (structural: create / parent / delete) and `Undo.postprocessModifications` (property-level, debounced to the final value). Only one session runs at a time; a Domain Reload **voids** the active recording (surfaced as `macro_record_status.interruptedByReload = true`).

### `macro_record_start`
Start a demonstration-recording session. Manual scene edits (and REST-driven ones) are captured for later inversion.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| note | string | No | null | Optional session label |

**Returns:** `{ recording, startedAtUtc, catalogSize }`
Only one session may be active; the buffer holds up to 1000 records and auto-stops with `stoppedReason: "buffer_full"`.

### `macro_record_stop`
Stop the active session and return a summary. The stopped session stays exportable until the next `macro_record_start`.

No parameters.

**Returns:** `{ recordCount, durationSec, byKind, stoppedReason }`

### `macro_record_status`
Get the recorder state.

No parameters.

**Returns:** `{ recording, recordCount, startedAtUtc, stoppedReason, hasExportableSession, interruptedByReload }` — `interruptedByReload = true` means the last recording was discarded by a Domain Reload.

### `macro_export`
Invert the most recent stopped recording into a replayable step sequence.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| format | string | No | "batch" | Output format; `batch` produces a body directly POSTable to `/skills/batch` |

**Returns:** `{ steps, warnings, replayable }` — `steps` is a ready `/skills/batch` body; objects created during the recording are wired together with `{"$ref":"$N.instanceId"}` inter-step references. Sibling-order changes are inverted into `gameobject_set_sibling_index` steps emitted after all other steps (net final order); unambiguous component removals become `component_remove` steps. Changes that cannot be inverted (asset edits, prefab-instance edits, ambiguous same-type component removal, Undo/Redo) are listed in `warnings` and set `replayable: false`; objects both created **and** destroyed within the recording are dropped entirely (net effect zero).

## Macro Library (macro = one standalone skill)

A stopped recording can be promoted into the **macro library**. Each saved macro then behaves like a single skill: `macro_run` executes the whole sequence in one call through the shared `/skills/batch` pipeline ($param/$ref resolution, per-step permission gate/undo/audit), and macros are managed (listed / inspected / deleted) individually by name.

Two scopes (`scope` parameter on every library skill, optional):

| Scope | Directory | Use |
|-------|-----------|-----|
| `project` (default) | `Library/UnitySkillsMacros/<name>.json` (never committed) | Macros tied to this project's scene/asset names |
| `global` | `~/.unity_skills/macros/<name>.json` | Cross-project reuse — self-contained macros (e.g. "standard light rig") callable from any Unity project on this machine |

Scope resolution when omitted: `macro_save` writes to `project`; `macro_list` merges both scopes (each entry carries its `scope`); `macro_get` / `macro_run` / `macro_delete` search `project` first, then `global` (a project macro shadows a same-named global one), and report the scope that was hit.

### `macro_save`
Save the most recent stopped recording under a name. One-liner: recording → named, replayable library entry.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Library name (also the file name — path separators and other invalid characters are rejected) |
| overwrite | bool | No | false | Replace an existing macro of the same name (within the same scope) |
| scope | string | No | project | `project` or `global` (cross-project, `~/.unity_skills/macros`) |

**Returns:** `{ name, scope, path, stepCount, replayable }` — fails when no stopped session exists (never recorded / still recording) or the name is taken in that scope and `overwrite=false`.

### `macro_list`
List all library macros with `{name, stepCount, replayable, recordedAtUtc, note, fileSizeBytes, params:[{name, hasDefault, default}]}`. `params` aggregates the `$param` slots in the macro's steps; entries without a default are mandatory for `macro_run`. An empty library returns an empty array.

### `macro_get`
Get one macro's full content — `steps`, `params` (saved defaults), `paramDeclarations`, `warnings`, recording metadata. A miss lists the available names.

### `macro_delete`
Delete exactly one macro file by name; other macros are untouched. A miss lists the available names.

### `macro_run`
Run a saved macro **as one skill call** — the "macro = standalone skill" entry point.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | Yes | - | Library macro to run |
| params | object | No | null | Values for `$param` slots, overriding the macro's saved defaults |
| continueOnError | bool | No | false | Keep running after a failed step (authorization interrupts always stop) |
| scope | string | No | (auto) | Pin `project` / `global`; omit to search project first, then global |

**Returns:** the `/skills/batch`-shaped result `{ status, executed, failed, results }` plus `macro`/`scope`/`stepCount`. Bare `$param` slots left without a value fail **before** execution with the missing names; the whole run is one undo group.

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
