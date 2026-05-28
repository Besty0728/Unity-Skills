---
name: unity-batch
description: "Batch query, preview-confirm-execute, async job orchestration, and bulk scene cleanup for UnitySkills. Use when users want a unified batch query (gameobjects/components/assets), preview before mutation (rename/set property/replace material/missing scripts/standardize naming/render layer/cleanup temp), execute with confirmToken, manage async jobs (status/progress/wait/logs/list/cancel), inspect or list reports, retry failed items, or validate scene objects. Triggers: batch, preview, dry-run, confirmToken, execute batch, batch report, batch retry, job, jobId, async job, job status, job progress, job wait, job logs, job cancel, query gameobjects, query components, query assets, batch rename, batch set property, batch replace material, batch fix missing scripts, batch standardize naming, batch set render layer, batch cleanup temp, batch validate scene, 批处理, 批量, 预览执行, 确认令牌, 异步作业, 作业状态, 作业进度, 作业等待, 作业日志, 作业取消, 批量查询, 批量重命名, 批量改属性, 批量换材质, 修复缺失脚本, 规范命名, 清理临时对象, 场景校验."
---

# Unity Batch Skills

Batch workflow orchestration for query, preview, execution, reports, and async jobs.

## Operating Mode

本模块共 22 个 skill，按 Operation 区分为两类：

- **18 个 SemiAuto**（query / preview / report / job 查询类）：`batch_query_gameobjects` / `batch_query_components` / `batch_query_assets` / `batch_preview_rename` / `batch_preview_set_property` / `batch_preview_replace_material` / `batch_report_get` / `batch_report_list` / `job_status` / `job_progress` / `job_logs` / `job_list` / `batch_fix_missing_scripts` / `batch_standardize_naming` / `batch_set_render_layer` / `batch_replace_material` / `batch_validate_scene_objects` / `batch_cleanup_temp_objects`。Approval 模式下可直接执行。
- **4 个 FullAuto**（Execute 类，C# 未标 `Mode` 走默认 `SkillMode.FullAuto`）：`batch_execute` / `job_wait` / `job_cancel` / `batch_retry_failed`。Approval 模式下首次调用返 `MODE_RESTRICTED`，走 grant 协议。
- Auto / Bypass：两类都直接执行。**不含 NeverInSemi 高危 skill**（无 Delete/MayEnterPlayMode/MayTriggerReload 标记）。

> 注意：`batch_execute(confirmToken)` 本身放行，但它执行的 preview 内容可能包括对场景对象的删除/改属性等高影响动作 —— 请确保 `batch_preview_*` 返回的 sample/risk 字段已审阅。confirmToken 一次性消费、过期需重新 preview。

**DO NOT** (common hallucinations):
- Always call a `batch_preview_*` skill first — `batch_execute` requires a `confirmToken` from a preview, it cannot be called directly
- `batch_run` does not exist → use `batch_execute(confirmToken)`
- `job_poll` / `job_result` do not exist → use `job_status` to check and retrieve async job results
- `batch_delete` / `batch_move` do not exist → use `asset` module for asset-level operations

**Routing**:
- For asset-level bulk operations (move, copy, delete) → `asset` module
- For workflow session/task undo tracking → `workflow` module
- For scene object validation → `batch_validate_scene_objects` (this module)
- For async job polling → `job_status` / `job_wait` (this module)

## Skills

### batch_query_gameobjects
Query GameObjects with unified batch filters. `queryJson` supports `name/path/instanceId/tag/layer/active/componentType/sceneName/parentPath/prefabSource/includeInactive/limit`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `sampleLimit` | int | No | 20 | Max sample objects returned |

### batch_query_components
Query components with unified batch filters. Optional `componentType` narrows the result.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `componentType` | string | No | null | Optional component type constraint |
| `sampleLimit` | int | No | 20 | Max sample objects returned |

### batch_query_assets
Query project assets by type, path pattern, and labels. Read-only.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchFilter` | string | No | null | Raw Unity AssetDatabase filter string |
| `folder` | string | No | "Assets" | Search root folder |
| `typeFilter` | string | No | null | Asset type (prefix `t:` optional, e.g. `Texture2D`) |
| `namePattern` | string | No | null | Case-insensitive regex for filename |
| `labelFilter` | string | No | null | Asset label (prefix `l:` optional) |
| `maxResults` | int | No | 200 | Max results returned |

### batch_preview_rename
Preview batch renaming. `mode` supports `prefix` / `suffix` / `replace` / `regex_replace`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `mode` | string | No | "prefix" | Rename mode |
| `prefix` | string | No | null | Prefix to add |
| `suffix` | string | No | null | Suffix to add |
| `search` | string | No | null | Plain text search term |
| `replacement` | string | No | null | Plain text replacement |
| `regexPattern` | string | No | null | Regex search pattern |
| `regexReplacement` | string | No | null | Regex replacement text |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_preview_set_property
Preview setting a component property or field across queried targets.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `componentType` | string | No | null | Target component type |
| `propertyName` | string | No | null | Property or field name |
| `value` | string | No | null | Literal value |
| `referencePath` | string | No | null | Scene reference path |
| `referenceName` | string | No | null | Scene reference object name |
| `assetPath` | string | No | null | Asset reference path |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_preview_replace_material
Preview replacing Renderer materials across queried targets.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `materialPath` | string | No | null | Replacement material asset path |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_execute
Execute a previously previewed batch operation by `confirmToken`. Large operations return a `jobId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `confirmToken` | string | Yes | - | Preview confirmation token |
| `runAsync` | bool | No | true | Run as async job |
| `chunkSize` | int | No | 100 | Batch execution chunk size |
| `progressGranularity` | int | No | 10 | Emit a `progressEvent` every N items processed |

### batch_report_get
Get a batch execution report by `reportId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `reportId` | string | Yes | - | Batch report identifier |

### batch_report_list
List recent batch reports.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 20 | Max reports returned |

### job_status
Get status for an asynchronous UnitySkills job. For fine-grained progress events, use `job_progress` (below) with an `offset` cursor — that's the path-of-record for recent-event streaming.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |

### job_progress
Get fine-grained progress events for a job via incremental polling. Use `offset` to fetch only new events since the last call (pass previous `totalCount` as next `offset`).

> **Note**: Also exposed as HTTP `GET /jobs/{id}/progress` and Python `client.get_job_progress(job_id, offset)` — all three paths share the same response shape.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `offset` | int | No | 0 | Skip first N events (use previous `totalCount` for incremental polling) |

Response fields: `jobId`, `status`, `totalCount`, `offset`, `events[]` (`timestamp` ms, `progress`, `stage`, `description`), `terminal`.

### job_logs
Get structured logs for a UnitySkills job.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `limit` | int | No | 100 | Max log entries returned |

### job_list
List recent UnitySkills jobs.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 20 | Max jobs returned |

### job_wait
Wait for a UnitySkills job to finish or until `timeoutMs` elapses.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `timeoutMs` | int | No | 10000 | Wait timeout in milliseconds |

### job_cancel
Cancel a UnitySkills job if the job supports cancellation.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |

### batch_fix_missing_scripts
Preview batch removal of missing scripts. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_standardize_naming
Preview standardizing names by trimming whitespace and normalizing separators. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `separator` | string | No | "_" | Replacement separator |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_set_render_layer
Preview setting GameObject layers in batch. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `layer` | string | No | null | Target layer name |
| `recursive` | bool | No | false | Apply recursively to children |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_replace_material
Preview replacing materials in batch. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `materialPath` | string | No | null | Replacement material asset path |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_validate_scene_objects
Analyze scene objects for missing scripts, missing references, duplicate names, and empty objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `issueLimit` | int | No | 100 | Max issues returned |

### batch_cleanup_temp_objects
Preview deleting temporary helper objects by common temp-name patterns. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `patternsCsv` | string | No | null | Comma-separated temp-name patterns |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_retry_failed
Re-run only the failed items from a previous batch execution report. Returns a new `jobId` and `originalReportId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `reportId` | string | Yes | — | Prior batch report ID to resume from |
| `runAsync` | bool | No | true | Whether to run asynchronously (returns `jobId`) |
| `chunkSize` | int | No | 100 | Chunk size per retry batch |

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
