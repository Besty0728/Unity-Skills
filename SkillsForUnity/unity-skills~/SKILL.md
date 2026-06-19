---
name: unity-skills
description: Automate the Unity Editor through a local REST API — create and edit scripts, build scenes and prefabs, manage assets/materials/lighting, run tests, and drive hundreds of Editor operations across modules. Use whenever the user wants to operate Unity from chat — create or modify GameObjects/scripts/scenes/assets, batch-edit, or run any Unity Editor automation, even if they just say "在 Unity 里…" or "操作 Unity". 通过本地 REST API 自动化 Unity 编辑器(创建与编辑脚本、搭建场景与 Prefab、管理资源/材质/灯光、运行测试,覆盖跨模块的数百项编辑器操作);当用户想从对话里操作 Unity——创建或修改 GameObject/脚本/场景/资源、批量编辑、或执行任何 Unity 编辑器自动化时使用。
---

# Unity Skills

Use this skill when the user wants to automate the Unity Editor through the local UnitySkills REST server.

## Schema: query only when unsure

The schema is the canonical source for exact skill names, parameters, defaults, and returns — **but you do not need it for every call**. For common read-only or simple write calls whose parameters you already know, call directly; query schema only when a skill name or signature is uncertain.

- `unity_skills.get_skill_schema()` / `GET /skills/schema` — full schema (large: ~`578 KB` for all skills). Fetch at most **once per session** and reuse it; do not re-pull before every call.
- `GET /skills?category=<Category>` — returns a **manifest** (lightweight: `mode` / `approvalBehavior` / parameters per skill), **not** the full schema. Use it to scope by category. Note: `/skills/schema` itself does **not** currently filter by category.

Use module `SKILL.md` files for routing guidance, guardrails, and minimal examples, not as the canonical source of exact signatures.

Current snapshot: `750` REST skills, `51` functional source modules, `68` module documentation directories (`49` REST/module docs + `19` advisory docs), Unity `2022.3+`, default timeout `15 minutes`.

Python helper: `unity-skills/scripts/unity_skills.py`

## Operating Mode (v1.9.0+)

Operating mode is a **server-side permission gate**, configured in `Window > UnitySkills > Server` and persisted in EditorPrefs per-machine. It is not an AI routing policy and **cannot** be switched via chat or REST — chat-side trigger words no longer apply.

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

The Allowlist is a **user-managed** permanent whitelist of skill names, configured in `Window > UnitySkills > Server` settings drawer (Allowlist Skills section / `+ Add Skill` button). It is independent of Approval grants:

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

The REST surface (~`750` skills) is partitioned by `[UnitySkill]` `Mode` and runtime metadata. Use schema endpoints for the canonical list:

| Annotation | Count | Source |
|---|---|---|
| `SkillMode.SemiAuto` | ~`270` | Manually annotated. Covers read-only / query / analyze skills across `script` / `perception` / `scene` / `editor` / `asset` / `workflow` / `debug` / `console` and most modules' info / list / get / find skills |
| Auto-detected NeverInSemi | ~`75-79` | `IsForbiddenInSemi()` derives purely from `Operation.Delete`, `MayEnterPlayMode`, `MayTriggerReload`, `RiskLevel="high"` (no fallback list) |
| `SkillMode.FullAuto` (default) | remainder | Unannotated skills (write / mutate by default). Approval requires grant; Auto / Bypass execute directly |

SemiAuto (read/query/analyze) skills are directly callable in every mode and span the modules below; use `GET /skills?category=<Category>` for the exact list (write skills in the same modules stay FullAuto):

- **script** (read/list/get_info/find_in_file/get_compile_feedback) · **perception** (scene_analyze/context/health_check/find_hotspots, project_stack_detect) · **scene** (get_info/get_hierarchy/get_loaded/find_objects) · **editor** (get_context/state/selection/tags/layers) · **asset** (find/get_info) · **workflow** (list/session_*/plan — prefer workflow & batch helpers for planning/preview/jobs/rollback) · **debug + console** (check_compilation/get_errors/get_system_info/get_memory_info/get_logs)
- plus most modules' own info / list / get / find skills. **Advisory**: `19` design-only modules (no REST skills) — see Coding Reference Index below.

## Core Rules

1. If the user specifies a Unity version or editor line, set instance/version routing first with `unity_skills.set_unity_version(...)`.
2. **BATCH-FIRST** — whenever the task touches `2+` objects, use the `*_batch` variant. Calling the single-object skill in a loop is N round-trips (and `2N` under Approval, since each call needs its own grant). Always look for a `*_batch` form before looping.
3. For multi-step editor mutations, prefer workflow wrappers instead of free-form mutation sequences.
4. Script edits, define changes, package changes, some imports, and test template creation can trigger compilation or Domain Reload. Wait and retry on transient unavailability.
5. `test_*` skills are async. They return a `jobId` and must be polled with `test_get_result(jobId)`.
6. **Object location (Unity 6000.4+)** — on Unity 6000.4+ the legacy `instanceId` is reported as `0` and is no longer a reliable handle; locate GameObjects/components by `entityId` (the `entityId` field returned by object skills) instead. Locator priority is `entityId > instanceId > path > name`. Object skills accept a synthetic `entityId` parameter and return both `entityId` and `instanceId`; on Unity < 6000.4 the `instanceId` path still works unchanged.

## Coding Reference Index

Before writing or refactoring Unity code, **load the relevant advisory module first**. These are the `19` `Documentation only` design modules (no REST skills — loadable under any mode) that pin rules to engine source and prevent hallucinated / removed APIs. Load on demand by topic, not all at once.

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
