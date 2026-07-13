---
name: unity-skills
description: Automate the Unity Editor through a local REST API — create and edit scripts, build scenes and prefabs, manage assets/materials/lighting, run tests, and drive hundreds of Editor operations across modules. Use whenever the user wants to operate Unity from chat — create or modify GameObjects/scripts/scenes/assets, batch-edit, or run any Unity Editor automation, even if they just say "在 Unity 里…" or "操作 Unity". 通过本地 REST API 自动化 Unity 编辑器(创建与编辑脚本、搭建场景与 Prefab、管理资源/材质/灯光、运行测试,覆盖跨模块的数百项编辑器操作);当用户想从对话里操作 Unity——创建或修改 GameObject/脚本/场景/资源、批量编辑、或执行任何 Unity 编辑器自动化时使用。
---

# Unity Skills

Use this skill when the user wants to automate the Unity Editor through the local UnitySkills REST server.

## Schema: pick the cheapest layer that answers your question

The schema is the canonical source for exact skill names, parameters, defaults, and returns — **but you rarely need the expensive layers**. Route by task shape (all layers are server-cached with ETag/304 and served off the main thread):

- **Intent is specific** ("create a cube", "set this SO field") → `GET /skills/recommend?intent=<words>&topN=10&includeSchema=true` (~2-5 KB) returns scored candidates **with parameter schemas** — often the only lookup you need. If you already know the skill name, skip lookups entirely and go straight to the dryRun gate below.
- **Task touches one or two areas** → directory first: `GET /skills?brief=1` (~19 KB ≈ 3.4K tokens — all 733 skill names grouped by module, names are self-describing `module_verb`) to lock the module(s), then `GET /skills/schema?category=<Category>` (~13–44 KB) for exact signatures. Typical session cost ≈ 10K tokens instead of 35K.
- **Exploratory / cross-module / unsure what exists** → full awareness: `GET /skills?summary=1` (~143 KB ≈ 35K tokens — every skill's full description). The only layer with all descriptions at once; reach for it when the cheaper layers left you unsure, **not by default**.
- **Full detail (rare)**: `GET /skills/schema` — full schema with exact parameter schemas (~`618 KB` ≈ 150K tokens, client-cached 300s + disk-cached under `~/.unity_skills/cache/` with ETag/304 revalidation, so short-lived CLI processes reuse it too). Only when you need many modules' exact signatures at once.

Python helper shortcuts: `unity_skills.search_skills("keyword")` greps the cached summary **locally** and returns only matching entries — the 143 KB stays on disk, out of your context. `get_skills_summary()` / `get_skill_schema()` wrap the layers above with memory+disk caching.

**Before executing a skill — the dryRun gate (do not skip).** The lite/summary manifest is for *awareness* (picking the right skill), not for calling. Descriptions are informal (human-written, not a formal signature; some omit parameter hints) and parameter schemas are omitted. Before the first execution of any skill whose exact parameters you don't already hold in context, **dryRun it**: `POST /skill/<name>?mode=dryRun` with your best-guess args. The server validates parameters and, on error, returns `unknownParams` with `suggestions` (the correct parameter names) plus the full `parameters` schema — iterate until `valid: true`, then execute without `?mode=dryRun`. This is the mechanism that turns "awareness" into "correct operation steps"; never guess parameters from descriptions and never skip dryRun for a skill you have not yet called successfully this session. Mode values are strictly validated (v2.1.0+): a mistyped `?mode=` / `?dryRun=` value (e.g. `mode=dry_run`, `dryRun=1`) is rejected with `INVALID_MODE` and the request is **not** executed — a typo can never silently fall through to a real execution.

**Inspect what a write changed — `?diff=1` (opt-in).** Append `?diff=1` to `POST /skill/<name>` or `POST /skills/batch`. A successful response carries a top-level `sceneDiff` with `changed` / `added` / `removed`; batch returns the final net change across successful steps and builds the diff after transactional rollback. Read-only calls return a note, dry runs do not capture, and invalid values are rejected before execution.

**Multi-skill tasks — aggregate-plan first.** When a task needs several skills in sequence, call `workflow_plan` (`POST` a JSON array of `{name, params}` steps) before executing any of them. It returns combined `steps`, `dependencies`, `totalRisk`, and `warnings`, so you sequence correctly and surface cross-step blockers before the first mutation. Then dryRun + execute each step in order.

**Multi-skill execution — `POST /skills/batch` (v2.1.0+).** Execute a sequence in **one** HTTP call instead of N: body `{"steps":[{"skill":"<name>","args":{...}}, ...], "continueOnError":false}` (≤50 steps). Each step runs the full single-skill pipeline (validation, permission gate, undo, audit). Default is fail-fast — on a step error the rest are returned as `skipped`; `continueOnError: true` skips failed steps instead. Authorization responses (`MODE_RESTRICTED` / `CONFIRMATION_REQUIRED`) always interrupt regardless and carry the grant token in that step's error. Response: `{status, executed, failed, results:[{index, skill, status, result|error}]}`. `?mode=dryRun` validates every step in one shot without executing (never interrupts) — the batch counterpart of the dryRun gate.

- **Inter-step references (`$ref`)** — a later step can now consume an earlier step's output. Anywhere in `args`, at any depth, an object whose **only** key is `$ref` — `{"$ref":"$N.path"}` — is replaced by `SelectToken(path)` on step `N`'s (0-based) unwrapped result: e.g. `{"instanceId":{"$ref":"$0.instanceId"}}` feeds the `instanceId` created by step 0 into a later step. A ref that fails to resolve fails that step with `SEMANTIC_INVALID`. Under `?mode=dryRun` refs are structurally validated (reported in `refsValidated` with `structural:true`) since the target value does not exist yet — treat any semantic mismatch there as a warning, not a hard failure. This lifts the old "later step cannot reference an earlier step's output" boundary; step-by-step calls are no longer required just to thread returned ids.
- **Transactional mode (`?mode=transactional`)** — all-or-nothing. If any step fails (including an authorization interrupt), every already-executed step is rolled back via Unity Undo and the response returns top-level `status:"rolled_back"`, `rolledBack:true`, with executed steps marked `rolled_back`. Pre-check **rejects (`400`)** a batch that combines `continueOnError` with any step that `MayTriggerReload`. Steps that mutate assets are flagged `rollbackReliability:"partial"` — Undo cannot fully revert on-disk asset writes.

Use module `SKILL.md` files for routing guidance, guardrails, and minimal examples, not as the canonical source of exact signatures.

Current snapshot: `733` REST skills, `51` functional source modules, `69` module documentation directories (`49` REST/module docs + `20` advisory docs), Unity `2022.3+`, default timeout `15 minutes`.

Python helper: `unity-skills/scripts/unity_skills.py`

## Operating Mode (v1.9.0+)

Operating mode is a **server-side permission gate**, configured in the Unity panel (`Window > UnitySkills` → ⚙ Settings → Server section) and persisted in EditorPrefs per-machine. It is not an AI routing policy and **cannot** be switched via chat or REST — chat-side trigger words no longer apply.

### Boot Handshake

On session start (or before the first skill call), call `GET /health` and read:

- `currentMode` — `"approval"` / `"auto"` / `"bypass"`
- `panelApprovalRequired` — only meaningful under Approval; selects the grant channel
- `pendingCount` — outstanding grant requests

### Three Modes (aligned with Claude Code permission modes)

> **Factory default:** a fresh install starts in **Auto**; an upgraded install (any pre-existing `UnitySkills_*` pref) starts in **Bypass**. It **never** defaults to Approval. The "Claude Code 类比" column below is only a mental model, **not** the factory default — always read `/health.currentMode` before acting.

| Mode | Claude Code 类比（心智对照，非默认） | FullAuto skill | Auto-detected NeverInSemi skill |
|---|---|---|---|
| **Approval** | ≈ `default` / `plan` | First call returns `MODE_RESTRICTED`; run the grant protocol below | `MODE_FORBIDDEN` |
| **Auto** | ≈ `acceptEdits` | Executes directly (audit written); **you must self-assess** sensitive cases | `MODE_FORBIDDEN` |
| **Bypass** | ≈ `bypassPermissions` | Executes directly | Executes directly (only `ConfirmationToken` still gates high-risk) |

`NeverInSemi` is derived automatically by `IsForbiddenInSemi()` — there is no manual marker. See "Skill Mode Annotation" below.

### Approval Mode Grant Protocol

Approval grants are **single-shot one-step execution**: a successful `/permission/grant` call runs the original skill server-side and returns the result in the same response. You do **not** retry the skill after grant. Grants are **not** persisted — calling the same skill a second time will hit `MODE_RESTRICTED` again and must go through grant again. If the user wants permanent bypass for a skill, direct them to the Allowlist (see below).

On `MODE_RESTRICTED`, branch on `details.approvalChannel`:

**Dialog channel** (`"dialog"`, default — `panelApprovalRequired = false`)

1. Tell the user in chat: "要调用 `<skill>` 来 `<目的>`，参数 `<argsSummary>`，请求码 #`<token 前 6 位>`，是否允许？"
2. After explicit user consent, call `POST /permission/grant { skill, token, args }` **once**
3. On success, the response contains `{ ok: true, executed: true, skill, result: <Execute output> }` — the skill has already run server-side. Consume `result` directly; **do not call the original skill endpoint again**

**Panel channel** (`"panel"`, when `panelApprovalRequired = true`)

1. Tell the user in chat: "要调用 `<skill>` 来 `<目的>`，请到 `Window > UnitySkills` 面板的 Pending Grant Requests 点 `[Approve]`（请求码 #`<token 前 6 位>`）"
2. **Do not call `/permission/grant` yet** — calling it before the user clicks Approve returns `GRANT_PENDING_APPROVAL`
3. Poll `GET /permission/status?token=<token>` to observe the request state (look at `focus.approvedByPanel`)
4. Once the user has pressed Approve in the panel, call `POST /permission/grant { skill, token, args }` **once** — this takes the Granted branch and triggers one-step execution, returning `{ ok: true, executed: true, skill, result }`. Consume `result` directly; **do not call the original skill endpoint again**

> Note: panel approval no longer auto-routes the result back to the AI. The Approve click only flips the request into the Granted state; AI must follow up with one `/permission/grant` call to fetch the execution result.

On `MODE_FORBIDDEN`: the skill is auto-classified as NeverInSemi (Delete / Domain Reload / Play Mode / high-risk). It is callable only under Bypass, **or** if the user has explicitly added it to the Allowlist (see below). **Do not attempt the grant flow** — tell the user the action requires Bypass mode, an Allowlist entry, or offer an alternative skill.

### Allowlist (user-managed permanent bypass)

The Allowlist is a **user-managed** permanent whitelist of skill names, configured in the `Window > UnitySkills` panel's ⚙ settings drawer (Allowlist Skills section / `+ Add Skill` button). It is independent of Approval grants:

- Allowlisted skills execute directly under any mode — the server skips the Approval/MODE_RESTRICTED gate
- **An Allowlist entry overrides MODE_FORBIDDEN** for that skill (covers Delete / MayEnterPlayMode / MayTriggerReload / `RiskLevel="high"`). This is intentional: the user has explicitly opted in
- **Allowlist does NOT bypass the high-risk ConfirmationToken gate.** When `RequireConfirmation` is enabled (Settings drawer → Runtime → Require Confirmation), high-risk skills still require the `_confirm` token two-step handshake even if allowlisted — Allowlist only covers the mode/approval channel, not the per-call safety confirmation
- The list is **opaque to the AI**: allowlisted skills look like normal successful calls, never returning `MODE_RESTRICTED`
- **The AI should not call `/permission/allowlist/add` on its own initiative.** Only call it when the user has explicitly authorized a session-scoped bulk add (e.g. "把这几个 skill 加白名单方便我后面批量调"); otherwise direct the user to add entries through the panel
- Allowlist endpoints: `GET /permission/allowlist` / `POST /permission/allowlist/add` / `POST /permission/allowlist/remove` (body `{skill}` or `{all: true}`)

> The previous `GrantedSkills` semantics ("after one grant the skill is permanently auto-allowed") has been removed. Grants are now single-shot. Permanent allow == Allowlist; one-shot approval == grant.

### Auto Mode Self-Assessment

Under Auto, FullAuto skills run directly. You **must pause and confirm with the user** in chat when any of the following apply:

- Batch operation touching ≥ `5` objects
- Prefab apply / scene-level mutation / asset overwrite
- Dry-run shows irreversible changes (deletes, overrides, cascading edits)

This confirmation is a chat-level check (explain plan + risk + ask), independent of the server-side mode gate. The server will not stop you in Auto — the audit log records the call regardless.

### Relationship with `ConfirmationTokenService`

Mode authorization (persistent, per-skill) and `ConfirmationToken` (single-shot, per-call) are **orthogonal**:

- Mode check runs first; if allowed, the existing confirmation gate may still issue `CONFIRMATION_REQUIRED` with a dry-run for `RiskLevel=high` or `Operation.Delete` skills
- Granted skills still flow through `ConfirmationToken` when triggered — continue using the original dry-run → user consent → retry with `_confirm` loop
- Neither replaces the other

### Skill Mode Annotation

The REST surface (`733` skills) is partitioned by `[UnitySkill]` `Mode` and runtime metadata. Use schema endpoints for the canonical list:

| Annotation | Count | Source |
|---|---|---|
| `SkillMode.SemiAuto` | ~`270` | Manually annotated. Covers read-only / query / analyze skills across `script` / `perception` / `scene` / `editor` / `asset` / `workflow` / `debug` / `console` and most modules' info / list / get / find skills |
| Auto-detected NeverInSemi | ~`75-79` | `IsForbiddenInSemi()` derives purely from `Operation.Delete`, `MayEnterPlayMode`, `MayTriggerReload`, `RiskLevel="high"` (no fallback list) |
| `SkillMode.FullAuto` (default) | remainder | Unannotated skills (write / mutate by default). Approval requires grant; Auto / Bypass execute directly |

SemiAuto (read/query/analyze) skills are directly callable in every mode and span the modules below; use `GET /skills?category=<Category>` for the exact list (write skills in the same modules stay FullAuto):

- **script** (read/list/get_info/find_in_file/get_compile_feedback) · **perception** (scene_analyze/context/health_check/find_hotspots, project_stack_detect) · **scene** (get_info/get_hierarchy/get_loaded/find_objects) · **editor** (get_context/state/selection/tags/layers) · **asset** (find/get_info) · **workflow** (list/session_*/plan — prefer workflow & batch helpers for planning/preview/jobs/rollback) · **debug + console** (check_compilation/get_errors/get_system_info/get_memory_info/get_logs)
- plus most modules' own info / list / get / find skills. **Advisory**: `20` design-only modules (no REST skills) — see Coding Reference Index below.

## Compilation Feedback, Events & Telemetry

Three read-only endpoints close the loop after a mutation — most useful across Domain Reload, when the server briefly drops out (see Core Rules #4).

**`GET /compile/status` — did my script edit compile?** After `script_*` writes (or any change that recompiles) the editor runs a Domain Reload and the server briefly answers `503/504`. Once it responds again, this endpoint reports the last compilation without re-reading files: `{status, isCompiling, isUpdating, domainReloadPending, lastCompilation:{finishedAtUtc, durationMs, success, errorCount, warningCount, errors:[{file, line, column, message, assembly}], warnings:[...], truncated} | null}`. `errors` are capped at 200, `warnings` at 50. `lastCompilation` survives the reload (persisted via `SessionState` for the editor session), so the pass/fail verdict and exact error lines are still there after the server returns. Recommended write-then-verify loop: `script_*` write → wait out the transient unavailability → `GET /compile/status` for success + error lines.

**`GET /events` — long-poll event channel.** Instead of hammering `/compile/status` in a loop, subscribe to a 500-entry in-memory ring buffer. Query: `since` (omit = wait only for events newer than the current max seq; `0` = replay the whole buffer), `timeout` (seconds, default 25, clamped 1–55), `types` (comma-separated filter). Response: `{status, events:[{seq, type, tsUtc, payload}], cursor, oldestSeq, dropped, timedOut}`; carry `cursor` into the next call's `since` to resume. Event types: `compilation_started` / `compilation_finished` (carries `firstErrors`, first 5) / `before_domain_reload` / `after_domain_reload` / `server_restored` / `playmode_changed` / `console_error` (throttled 20/s with `droppedSinceLast`) / `job_completed` / `job_failed`. `seq` is monotonic and never rewinds across reloads; a reload discards in-flight events, signalled by `dropped:true`. **Reconnect anchor:** the "compilation succeeded" event is lost when Domain Reload tears down the connection — after reconnecting, read `server_restored`, whose `payload` carries the `lastCompilation` summary, to recover the verdict you missed.

**`GET /analytics` — execution telemetry.** Aggregates how skills have been performing. Query `?window=1h|24h|7d|all` (default `24h`). The same local data feeds `/skills/recommend`: at least 5 valid calls are required before a high failure rate applies a bounded 1–3 point penalty; slow skills are warned but not penalized. Client/permission errors are ignored, and telemetry disabled means recommendation order remains semantic-only.

## Core Rules

1. If the user specifies a Unity version or editor line, set instance/version routing first with `unity_skills.set_unity_version(...)`.
2. **BATCH-FIRST** — whenever the task touches `2+` objects, use the `*_batch` variant. Calling the single-object skill in a loop is N round-trips (and `2N` under Approval, since each call needs its own grant). Always look for a `*_batch` form before looping.
3. For multi-step editor mutations, prefer workflow wrappers instead of free-form mutation sequences.
4. Script edits, define changes, package changes, some imports, and test template creation can trigger compilation or Domain Reload. Wait and retry on transient unavailability.
5. `test_*` skills are async. They return a `jobId` and must be polled with `test_get_result(jobId)`.
6. **Object location (Unity 6000.4+)** — on Unity 6000.4+ the legacy `instanceId` is reported as `0` and is no longer a reliable handle; locate GameObjects/components by `entityId` (the `entityId` field returned by object skills) instead. Locator priority is `entityId > instanceId > path > name`. Object skills accept a synthetic `entityId` parameter and return both `entityId` and `instanceId`; on Unity < 6000.4 the `instanceId` path still works unchanged.

## Coding Reference Index

Before writing or refactoring Unity code, **load the relevant advisory module first**. These are the `20` `Documentation only` design modules (no REST skills — loadable under any mode) that pin rules to engine source and prevent hallucinated / removed APIs. Load on demand by topic, not all at once.

**General coding & architecture** — before writing gameplay code or making structural decisions:

| Module | Load when |
|---|---|
| `project-scout` | Before proposing changes in an existing project — first check Unity version, packages, asmdef, folders, coding patterns |
| `architecture` | Module boundaries, scene design, SOLID structure, decoupling, refactor direction |
| `script-roles` | Whether a class should be a MonoBehaviour, ScriptableObject, plain C# service, or installer |
| `scriptdesign` | Code review, reducing coupling, improving maintainability, refactoring scripts |
| `patterns` | Choosing among ScriptableObject / event / state-machine / object-pool / observer designs |
| `testability` | Improving testability, isolating logic out of MonoBehaviour, planning EditMode/PlayMode tests |
| `asmdef` | Module boundaries, faster compiles, clearer dependencies, editor/runtime/test split |
| `async` | Choosing among Update / coroutine / UniTask / timers, or cleanup & cancellation |
| `inspector` | SerializeField usage, Tooltip/Header organization, validation, Inspector UX |
| `scene-contracts` | Required scene objects, component dependencies, bootstrap logic, reference wiring |
| `adr` | Comparing options, choosing among approaches, locking in a design decision |
| `performance` | Performance review, frame drops, Update/allocation/pooling/physics optimization |
| `blueprints` | Starter structure for a small game (platformer, shooter, runner, puzzle, tower-defense, clicker, card) |

**Library-specific** — before writing code against that library (guards against removed / hallucinated APIs):

| Module | Load before writing |
|---|---|
| `addressables-design` | `InitializeAsync` / `LoadAssetAsync` / `LoadSceneAsync` / `UpdateCatalogs` / `AssetReference` |
| `dotween-design` | `DOTween.Init` / `DOMove` / `Sequence` / `SetLoops` / `SetLink` / `ToUniTask` |
| `netcode-design` | `NetworkBehaviour` / RPC / `NetworkVariable` / Spawn |
| `shadergraph-design` | Graph structure, node chains, SubGraph boundaries, keyword / blackboard layout |
| `unitask-design` | `async UniTask` / `UniTaskVoid` / `PlayerLoopTiming` / `CancellationToken` / `WhenAll` |
| `yooasset-design` | `ResourcePackage` / `AssetHandle` / `Downloader` / `FileSystem` / `AssetBundleBuilder` |
| `yaml-editing` | Hand-editing `.unity` / `.prefab` / `.asset` / `.meta` / ProjectSettings YAML when REST cannot reach (compile failure, `.meta`, hidden ProjectSettings fields, merge conflict) |

**Unity API reference**: `references/*.md` — official API grouped by topic (`2d`, `3d`, `animation`, `assets`, `audio`, `editor`, `networking`, `physics`, `rendering`, `scripting`, `shaders`, `ui`, `xr`, …). Read the relevant file to ground exact signatures instead of guessing.

Load any module via the index: `unity-skills/skills/<module>/SKILL.md`.

## Route

- Module index: `unity-skills/skills/SKILL.md`
- Script guidance: `unity-skills/skills/script/SKILL.md`
- Advisory guidance: load advisory modules on demand from the module index

> **XR rule**: Before calling any `xr_*` skill in a session, load `skills/xr/SKILL.md` first. XR is reflection-based; wrong property names can fail silently.
