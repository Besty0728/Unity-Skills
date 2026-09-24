---
name: unity-batch
description: Unified batch and async-job orchestration
---

## Triggers
- Operating on many objects at once
- Running or polling long async jobs
- Preview-then-commit bulk edits
- 一次性操作大量对象、运行或轮询长时异步任务、先预览后提交的批量编辑

# Unity Batch Skills

The `POST /skills/batch` endpoint, query → preview → execute bulk edits, execution reports and async jobs. Parameters come from the schema, never from a skill's name; execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## POST /skills/batch — several skills, one HTTP call

An **HTTP endpoint, not a skill**: never send `skills_batch` to `POST /skill/<name>`. It runs up to 50 ordinary skill calls in order inside one main-thread job, each through the full single-skill pipeline (validation, permission gate, its own Undo group, audit): one round trip and one main-thread wake-up instead of N, the biggest saving once there are 3+ writes.

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

| Body field | Default | Meaning |
|---|---|---|
| `steps` | required | `{skill, args}` objects, in order. More than 50 → `400 SEMANTIC_INVALID`; split the call. |
| `continueOnError` | `false` | `false` = stop at the first failure; `true` = record it and keep going. |
| `params` | none | Values for `{"$param":"name"}` or `{"$param":"name","default":X}` nodes in args; static, resolved before `$ref`; a missing slot without default fails that step. |

- **`$ref`**: an args object whose only key is `$ref`, `{"$ref":"$N.path"}`, is replaced by the value at `path` (e.g. `instanceId`, `results[0].path`; bare `$N` = the whole result) in the result of earlier successful step `N` (0-based). Only structured args are scanned, never strings. A ref that cannot resolve (forward, failed step, no match) fails its step with `SEMANTIC_INVALID`. Refer to created objects through `$ref` (`instanceId`, or `entityId` on Unity 6000.4+) rather than by name: a same-named existing object may win a name lookup.
- **Query**: `?mode=dryRun` (the batch dryRun gate) validates every step, executes nothing and never halts; a step may name an object an earlier step would create (a warning notes the skipped live checks) and `$ref` args get a structural check only (`refsValidated`). `?mode=transactional` is all-or-nothing with Undo rollback, rejected up front (`400`) for an unknown skill, a step that may trigger a domain reload, or `continueOnError:true`. `?diff=1` adds a net `sceneDiff`; `?wire=v2` slims dryRun step payloads; `expectInstance` / `expectProject` work as on `/skill`. `?mode=plan` is not supported. `mode` / `dryRun` may sit in the body instead: each key resolves query-first and a blank value counts as absent. Any key outside these (body: `steps`, `params`, `continueOnError`, `mode`, `dryRun`) is `400 UNKNOWN_PARAM`.

**Reading the response.** HTTP `200` whenever the batch ran; only a rejected request (bad body, >50 steps, unknown mode or key) is `4xx`.

```json
{"status":"partial","mode":"execute","dryRun":false,"executed":2,"failed":1,
 "results":[{"index":0,"skill":"...","status":"success","result":{...}},
            {"index":1,"skill":"...","status":"error","error":{"errorCode":"...","error":"..."}},
            {"index":2,"skill":"...","status":"skipped"}]}
```

- `status`: `completed` (nothing failed), `partial`, or `rolled_back` (transactional: executed steps become `rolled_back`, asset-writing ones flagged `rollbackReliability:"partial"`). `mode` / `dryRun` echo what actually ran; every entry carries `index` and `skill`.
- **Partial failure**: `success` steps are applied; the failed step's Undo-recorded changes are reverted; `skipped` steps never ran. Resend only the fixed failed step and the skipped ones, never the successful ones.
- **Authorization always interrupts**, `continueOnError` notwithstanding: a `MODE_RESTRICTED` / `CONFIRMATION_REQUIRED` step halts the batch and returns its full payload (grant token included); complete the grant, then resubmit the remaining steps.

> **Not the same as `batch_execute`.** `batch_execute(confirmToken)` commits *one* previewed bulk operation (one verb over N objects). `POST /skills/batch` composes *N different skills* over any targets and takes no token; it never skips the preview/confirm gate — a `batch_execute` step still needs its own `confirmToken`.

**Jobs.** When a skill or step returns a `jobId` (async execution, tests, compiles), wait with `GET /jobs/{id}?wait=<s>` (up to 120 s; answers once the job is terminal, with `resultData`; `waitTimedOut:true` = GET it again; a `503` during a domain reload = repeat the same URL) or poll `GET /jobs/{id}`. Both stay out of the main-thread skill queue and are far smaller than `job_status`; `job_wait` blocks the main thread (max 2 s).

## Preview → execute

1. Select with `queryJson` (filters ANDed: `name` substring, `namePattern` regex, `path` / `parentPath` exact, `entityId`, `instanceId`, `tag`, `layer`, `componentType`, `sceneName`, `active`, `isStatic`, `prefabSource`, `includeInactive` (false), `limit` (500)); `batch_query_*` shows what matches.
2. Preview with `batch_preview_*` or a fixer (`batch_fix_missing_scripts`, `batch_standardize_naming`, `batch_set_render_layer`, `batch_replace_material`, `batch_cleanup_temp_objects`). Previews are read-only and return `{confirmToken, kind, summary, riskLevel, targetCount, executableCount, skipCount, sampleChanges: [{targetPath, action, before, after}], skipReasons}`.
3. Review samples and risk (execution may delete or modify many objects), then `batch_execute(confirmToken)`. The token is single-use and expires after an hour: preview again. Large runs return a `jobId`; the report (`batch_report_get`) lists failures, and `batch_retry_failed(reportId)` reruns only those.

Under the `guide` surface profile previews still work but carry `surfaceExclusion`; `batch_execute` then returns `SURFACE_EXCLUDED` without consuming the token.

## Operating Mode
All but four of the 22 skills are read-only `SemiAuto` (query, preview, fixers, validation, reports, job status/progress/logs/list) and run directly under Approval; the 4 `FullAuto` ones (`batch_execute`, `job_wait`, `job_cancel`, `batch_retry_failed`) answer `MODE_RESTRICTED` there first and need a grant. Auto/Bypass run all; none is NeverInSemi → [operating mode](../../references/protocol-operating-mode.md).

## Skills Overview
- Query: `batch_query_gameobjects`, `batch_query_components`, `batch_query_assets` (type/folder/name regex/label).
- Preview: `batch_preview_rename` (prefix/suffix/replace/regex_replace), `batch_preview_set_property`, `batch_preview_replace_material`, plus the fixers above; `batch_validate_scene_objects` only reports (missing scripts/references, duplicate names, empty objects).
- Execute and reports: `batch_execute`, `batch_retry_failed`, `batch_report_get`, `batch_report_list`.
- Jobs: `job_status`, `job_progress`, `job_logs`, `job_list`, `job_wait`, `job_cancel`.

Per-skill tables and result shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `batch_execute` cannot run without a `confirmToken` from a preview or fixer call
- `batch_run` does not exist → use `batch_execute(confirmToken)`
- `job_poll` / `job_result` do not exist → use `GET /jobs/{id}` (or `job_status`) for state; `job_status`'s `resultHint` names the skill that returns a large result
- `batch_delete` / `batch_move` do not exist → use `asset_*` skills for asset-level operations

**Elsewhere**: asset bulk move/copy/delete → `asset_*`; workflow session undo → `workflow_*`.

## Exact Signatures

There is no `Batch` category: `GET /skills/schema?category=Workflow` holds 16 of these skills (query, preview, execute, reports, jobs) and `?category=Validation` the 5 fixers plus `batch_validate_scene_objects`; or use `?names=a,b`.
