---
name: unity-batch
description: Unified batch and async-job orchestration
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Operating on many objects at once
- Running or polling long async jobs
- Preview-then-commit bulk edits
- 一次性操作大量对象、运行或轮询长时异步任务、先预览后提交的批量编辑

# Unity Batch Skills

Batch workflow orchestration for query, preview, execution, reports, and async jobs.

## POST /skills/batch — several skills, one HTTP call

This is an **HTTP endpoint, not a skill**: do not send `skills_batch` to `POST /skill/<name>`. It runs a sequence of ordinary skills inside one main-thread job, which removes one round-trip *and* one main-thread wakeup per step — the single largest efficiency lever in this protocol when you have more than two writes to make.

```json
POST /skills/batch
{
  "steps": [
    {"skill": "gameobject_create", "args": {"name": "Cube", "primitiveType": "Cube"}},
    {"skill": "component_add", "args": {"instanceId": {"$ref": "$0.instanceId"}, "componentType": "Rigidbody"}}
  ],
  "continueOnError": false
}
```

| Body field | Type | Default | Meaning |
|---|---|---|---|
| `steps` | array | required | `{skill, args}` objects, executed in order. **Max 50** — more returns `400` + `SEMANTIC_INVALID`; split into several calls. |
| `continueOnError` | bool | `false` | `false` = fail-fast; `true` = record the failure and keep going. |
| `params` | object | none | Fills `{"$param":"name"}` placeholder nodes in step args (static substitution, resolved before `$ref`). |

Query: `?mode=dryRun` validates every step without executing anything and never interrupts (the batch counterpart of the dryRun gate); `?mode=transactional` is all-or-nothing with Undo rollback; `?diff=1` adds a net `sceneDiff`. `?mode=plan` is **not** supported. Inter-step `$ref` (`{"$ref":"$0.instanceId"}`) and transactional details → [SKILL_FULL.md](../../references/SKILL_FULL.md).

`mode`/`dryRun` may equally be set in the body (`"mode":"dryRun"|"transactional"`, `"dryRun":true`) — each of the two keys is resolved independently, query-first per key, so a query `?mode=` and a body `"dryRun"` never fight over the same slot. The response always echoes what actually ran: top-level `mode` (`"dryRun"`|`"transactional"`|`"execute"`) and `dryRun` (bool). An unrecognized query key or body top-level key (anything outside `mode`/`dryRun`/`diff`/`steps`/`params`/`continueOnError`) is rejected with `400 UNKNOWN_PARAM`; a **blank** value on a recognized key (`?mode=`, `?dryRun=`, `?diff=`) is treated as if that key were simply omitted — it falls through to the other location or the default, it is not itself a rejection.

**Reading the response.** Whenever the batch ran at all the status is HTTP `200` and the verdict is in the body — only a rejected *request* (malformed body, >50 steps, unknown `?mode=`) is a `4xx`.

```json
{"status":"partial","dryRun":false,"executed":2,"failed":1,
 "results":[{"index":0,"skill":"...","status":"success","result":{...}},
            {"index":1,"skill":"...","status":"error","error":{"errorCode":"...","error":"..."}},
            {"index":2,"skill":"...","status":"skipped"}]}
```

- Top-level `status`: `completed` (nothing failed), `partial` (some step failed), or `rolled_back` (transactional mode reverted everything).
- Every step carries its `index` and `skill`, so a failure is locatable without diffing your input array. `status:"skipped"` means the batch had already halted before that step ran — it was never attempted, not "ran and did nothing".
- Fail-fast (`continueOnError:false`) stops at the first error and reports the rest as `skipped`. With `continueOnError:true` failures are recorded and later steps still run.
- **Authorization always interrupts**, `continueOnError` notwithstanding: a step answering `MODE_RESTRICTED` / `CONFIRMATION_REQUIRED` halts the batch and returns that step's full payload (grant token included) so you can complete the grant flow and resubmit the remaining steps.

> **Not the same thing as `batch_execute`.** `batch_execute(confirmToken)` commits *one* previewed bulk operation produced by a `batch_preview_*` skill (one verb over N objects, via a confirm token). `POST /skills/batch` composes *N different skills* over whatever targets you name, and takes no token. They do not substitute for each other, and `POST /skills/batch` is not a way to skip the preview/confirm gate — a `batch_execute` step inside a batch still needs its own `confirmToken`.

When a step returns a `jobId` (async batch execution, tests, compiles), poll it with **`GET /jobs/{id}`** rather than the `job_status` skill: it runs in the light lane instead of the main-thread skill queue, and its payload is far smaller than a skill response — an order of magnitude cheaper per poll on a long job.

## Operating Mode

本模块共 22 个 skill，按 Operation 区分为两类：

- **18 个 SemiAuto**（query / preview / report / job 查询类）：`batch_query_gameobjects` / `batch_query_components` / `batch_query_assets` / `batch_preview_rename` / `batch_preview_set_property` / `batch_preview_replace_material` / `batch_report_get` / `batch_report_list` / `job_status` / `job_progress` / `job_logs` / `job_list` / `batch_fix_missing_scripts` / `batch_standardize_naming` / `batch_set_render_layer` / `batch_replace_material` / `batch_validate_scene_objects` / `batch_cleanup_temp_objects`。Approval 模式下可直接执行。
- **4 个 FullAuto**（Execute 类，C# 未标 `Mode` 走默认 `SkillMode.FullAuto`）：`batch_execute` / `job_wait` / `job_cancel` / `batch_retry_failed`。Approval 模式下首次调用返 `MODE_RESTRICTED`，走 grant 协议。
- Auto / Bypass：两类都直接执行。**不含 NeverInSemi 高危 skill**（无 Delete/MayEnterPlayMode/MayTriggerReload 标记）。

> 注意：`batch_execute(confirmToken)` 本身放行，但它执行的 preview 内容可能包括对场景对象的删除/改属性等高影响动作 —— 请确保 `batch_preview_*` 返回的 sample/risk 字段已审阅。confirmToken 一次性消费、过期需重新 preview。

**Surface profile (guide tier):** `batch_preview_*` stays callable under every profile — a preview is read-only. When the operation a preview minted its `confirmToken` for lands in a category the active profile withdraws (GameObject / Component / Material under `guide`), the preview still succeeds and still returns its diff, but adds a `surfaceExclusion` object (`manualDoc`, `hint`, plus `blockedSkill`/`blockedBy`/`surfaceProfile`/`category`) warning that execute will be refused. At that point `batch_execute` / `batch_retry_failed` return `SURFACE_EXCLUDED` with `surfaceProfile`/`category`/`operation`/`manualDoc`/`userControlled`/`hint` at the **top level** of the response (not nested under `details`). The refusal does not consume the `confirmToken` — switch the profile back to `full` and the same token still executes, no new preview needed.

**DO NOT** (common hallucinations):
- Always call a `batch_preview_*` skill first — `batch_execute` requires a `confirmToken` from a preview, it cannot be called directly
- `batch_run` does not exist → use `batch_execute(confirmToken)`
- `job_poll` / `job_result` do not exist → `job_status` (or the cheaper `GET /jobs/{id}`) for state, then the skill named in its `resultHint` for the payload
- `batch_delete` / `batch_move` do not exist → use `asset` module for asset-level operations

**Routing**:
- For running several *different* skills in one call → `POST /skills/batch` (section above), not `batch_execute`
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
| `componentType` | string | Yes | - | Target component type |
| `propertyName` | string | Yes | - | Property or field name |
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
| `materialPath` | string | Yes | - | Replacement material asset path |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_execute
Execute a previously previewed batch operation by `confirmToken`. Large operations return a `jobId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `confirmToken` | string | Yes | - | Preview confirmation token |
| `runAsync` | bool | No | true | Run as async job |
| `chunkSize` | int | No | 100 | Batch execution chunk size |
| `progressGranularity` | int | No | 10 | Emit a `progressEvent` every N items processed |

`runAsync: true` (the default) or an item count above `chunkSize` returns `status: "accepted"` plus a `jobId` — poll it like any other job. Only the inline path (`runAsync: false` **and** items ≤ `chunkSize`) blocks, and its wait is bounded: `50ms` per item with a `5s` floor and a hard **`30s` ceiling** (the shared main-thread wait cap — the inline wait spin-sleeps on the main thread, so it can never be allowed to freeze the Editor for as long as the item count would imply). When that ceiling hits first, the job is still running: you get `success: false` with a non-`completed` `status` and the `jobId`, **not** a frozen Editor. That is not a failure — carry on with `job_status` / `GET /jobs/{id}`, and prefer `runAsync: true` for anything big enough to risk it.

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
Get status for an asynchronous UnitySkills job. Built for repeated polling, so **the job's result payload is not inlined by default** (v2.7+) — a completed test or compile job can carry tens of KB, and polling used to resend all of it every call.

Instead you get two fields:

- **`resultAvailable`** (bool) — whether a payload exists at all.
- **`resultHint`** (string, `null` when `resultAvailable` is false) — where to fetch it: `test_get_result(jobId)` for `kind: "test"`, `test_discover_get_result(jobId)` for `kind: "test_discovery"`, `batch_report_get(reportId=...)` for any job that produced a `reportId` (the hint quotes the id), and for every other kind a note to re-call with `includeDetails=true`, which is then the only route to that payload.

The **`details` key is still present either way** — it is simply `null` unless you asked for it, so a client reading `response["details"]` gets null rather than a missing-key error. Set `includeDetails=true` on the one call where you actually want the data, never on the polling loop.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `includeDetails` | bool | No | false | Inline the full result payload as `details` instead of `resultAvailable` / `resultHint` |

### job_progress
Get fine-grained progress events for a job via incremental polling. Use `offset` to fetch only new events since the last call (pass previous `totalCount` as next `offset`).

> **Note**: Also exposed as HTTP `GET /jobs/{id}/progress` and Python `client.get_job_progress(job_id, offset)` — all three paths share the same response shape.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `offset` | int | No | 0 | Skip first N events (use previous `totalCount` for incremental polling) |

Response fields: `jobId`, `status`, `totalCount`, `offset`, `events[]` (`timestamp` ms, `progress`, `stage`, `description`), `terminal`.

> **An empty `events[]` is not a malfunction.** Fine-grained, per-item progress exists only for batch-executor jobs (`rename` / `set_property` / `replace_material` / `set_render_layer` / `cleanup_temp_objects` / `fix_missing_scripts` / `standardize_naming`), which emit one event every `progressGranularity` items because only they own a countable item list. Other kinds emit at most coarse lifecycle events (queued → stage change → terminal), and `test_discovery` emits **none at all** — `totalCount: 0` there means "this kind has no progress stream", not "the job is stuck". For those, poll `status` via `GET /jobs/{id}` instead of watching for events.

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
Wait for a UnitySkills job to finish or until `timeoutMs` elapses. **Blocks the Unity main thread** while waiting, so `timeoutMs` is clamped server-side to `[0, 2000]` regardless of the value you pass — a 10000/60000 request will actually wait at most 2s.

For job kinds whose progress depends on Unity's own engine loop rather than this plugin's own pump (`compile`, `package`, `test`, `playmode`, `play_capture`, `build_player`), blocking this thread cannot make them advance — Unity's compiler/domain-reload, PackageManager Request resolution, TestRunner callbacks, PlayMode state machine, and BuildPipeline all need the main thread free to tick. For those kinds `job_wait` **does not enter a wait loop**: it returns the current snapshot immediately with `waitNotSupported: true` and a `hint` pointing at the non-blocking alternatives below. Self-driven kinds (batch executor jobs such as `rename` / `set_property` / `replace_material` / `set_render_layer` / `cleanup_temp_objects` / `fix_missing_scripts` / `standardize_naming`, and `test_smoke`) still block up to the clamped timeout since each `job_wait` tick genuinely advances their state.

**Recommended pattern for compile/package/test/playmode/play_capture/build_player jobs**: poll `GET /jobs/{id}` every 200-500ms, or long-poll `GET /events`. `GET /jobs/{id}` skips the main-thread skill queue (it runs in the light lane, drained every frame and exempt from the frame budget), but it is still answered *by* the Editor main thread between frames — while a long operation holds that thread, polling stalls and resumes once the thread ticks again. To stay responsive throughout a long job, use `GET /events`: it is a true HTTP-thread long-poll that never enters the main-thread queue at all.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `timeoutMs` | int | No | 2000 | Wait timeout in milliseconds; clamped to `[0, 2000]` |
| `includeDetails` | bool | No | false | Inline the full result payload as `details` instead of `resultAvailable` / `resultHint` |

Response adds `terminal` (bool) and `waitNotSupported` (bool) to the fields listed under job_status; `hint` is populated only when `waitNotSupported` is true. Like `job_status` it reports `resultAvailable` + `resultHint` and leaves `details` null unless you pass `includeDetails=true` — same semantics, same key set.

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
| `layer` | string | Yes | - | Target layer name (must already exist in Tags & Layers) |
| `recursive` | bool | No | false | Apply recursively to children |
| `sampleLimit` | int | No | DefaultSampleLimit | Max preview items |

### batch_replace_material
Preview replacing materials in batch. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON query filter envelope |
| `materialPath` | string | Yes | - | Replacement material asset path |
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
