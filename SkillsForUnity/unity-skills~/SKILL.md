---
name: unity-skills
description: "Unity Editor automation via REST API — create scripts, analyze scenes, manage assets, control editor, and orchestrate workflows. Triggers: Unity, Unity Skills, in Unity, automate Unity, editor automation, create script, scene summary, build scene, Unity自动化, Unity编辑器, Unity技能, 操作Unity，在Unity中."
---

# Unity Skills

Use this skill when the user wants to automate the Unity Editor through the local UnitySkills REST server.

## Canonical Schema First

For exact skill names, parameters, defaults, and returns, query schema first:
- `unity_skills.get_skill_schema()`
- `GET /skills/schema`
- `GET /skills?category=<Category>`

Use module `SKILL.md` files for routing guidance, guardrails, and minimal examples, not as the canonical source of exact signatures.

Current snapshot: `714` REST skills, `51` functional source modules, `68` module documentation directories (`49` REST/module docs + `19` advisory docs), Unity `2022.3+`, default timeout `15 minutes`.

Python helper: `unity-skills/scripts/unity_skills.py`

## Operating Mode (v1.9.0+)

Operating mode is a **server-side permission gate**, configured in `Window > UnitySkills > Server` and persisted in EditorPrefs per-machine. It is not an AI routing policy and **cannot** be switched via chat or REST — chat-side trigger words no longer apply.

### Boot Handshake

On session start (or before the first skill call), call `GET /health` and read:

- `currentMode` — `"approval"` / `"auto"` / `"bypass"`
- `panelApprovalRequired` — only meaningful under Approval; selects the grant channel
- `pendingCount` — outstanding grant requests

### Three Modes (aligned with Claude Code permission modes)

| Mode | Claude Code 类比 | FullAuto skill | Auto-detected NeverInSemi skill |
|---|---|---|---|
| **Approval** | `default` / `plan` | First call returns `MODE_RESTRICTED`; run the grant protocol below | `MODE_FORBIDDEN` |
| **Auto** | `acceptEdits` | Executes directly (audit written); **you must self-assess** sensitive cases | `MODE_FORBIDDEN` |
| **Bypass** | `bypassPermissions` | Executes directly | Executes directly (only `ConfirmationToken` still gates high-risk) |

`NeverInSemi` is derived automatically by `IsForbiddenInSemi()` — there is no manual marker. See "Skill Mode Annotation" below.

### Approval Mode Grant Protocol

On `MODE_RESTRICTED`, branch on `details.approvalChannel`:

**Dialog channel** (`"dialog"`, default — `panelApprovalRequired = false`)

1. Tell the user in chat: "要调用 `<skill>` 来 `<目的>`，参数 `<argsSummary>`，请求码 #`<token 前 6 位>`，是否允许？"
2. After explicit user consent, call `POST /permission/grant { skill, token, args }`
3. Retry the original skill call

**Panel channel** (`"panel"`, when `panelApprovalRequired = true`)

1. Tell the user in chat: "要调用 `<skill>` 来 `<目的>`，请到 `Window > UnitySkills` 面板的 Pending Grant Requests 点 `[Approve]`（请求码 #`<token 前 6 位>`）"
2. **Do not call `/permission/grant`** — calling it before panel approval returns `GRANT_PENDING_APPROVAL`
3. Optionally poll `GET /permission/status?token=<token>` to observe state
4. Once the user presses Approve in the panel, the skill is added to `GrantedSkills` automatically — **retry the original skill directly**, no extra grant call needed

On `MODE_FORBIDDEN`: the skill is auto-classified as NeverInSemi (Delete / Domain Reload / Play Mode / high-risk / fallback list). It is callable only under Bypass. **Do not attempt the grant flow** — tell the user the action requires Bypass mode or offer an alternative skill.

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

The REST surface (~`714` skills) is partitioned by `[UnitySkill]` `Mode` and runtime metadata. Use schema endpoints for the canonical list:

| Annotation | Count | Source |
|---|---|---|
| `SkillMode.SemiAuto` | ~`121` | Manually annotated. Covers categories: `script` / `perception` / `scene` / `editor` / `asset` / `workflow` / `debug` / `console`, plus every read-only query skill |
| Auto-detected NeverInSemi | ~`40+` | `IsForbiddenInSemi()` derives from `Operation.Delete`, `MayEnterPlayMode`, `MayTriggerReload`, `RiskLevel="high"`, plus explicit fallback list (`scene_clear`, `scene_new`, `batch_apply`, select `cleaner_*`) |
| `SkillMode.FullAuto` (default) | remainder | Unannotated skills. Approval requires grant; Auto / Bypass execute directly |

SemiAuto category overview (use `GET /skills?category=<Category>` for the exact callable list):

| Category | Modules | Representative Skills |
|----------|---------|----------------------|
| Script | script | script_create, script_read, script_replace, script_append |
| Perception | perception | scene_analyze, scene_health_check, project_stack_detect |
| Scene Mgmt | scene | scene_save, scene_load, scene_context, scene_find_objects |
| Editor | editor | editor_get_context, editor_undo, editor_redo |
| Asset Basic | asset | asset_refresh, asset_find, asset_get_info |
| Workflow | workflow | workflow_task_start/end, workflow_undo_task; use workflow/batch helpers for planning, preview, jobs, and rollback, not free-form scene construction |
| Debug | debug, console | debug_check_compilation, console_get_logs |
| Advisory | 19 modules | Design-only guidance modules (no REST skills) |

## Core Rules

1. If the user specifies a Unity version or editor line, set instance/version routing first with `unity_skills.set_unity_version(...)`.
2. Under Auto / Bypass modes (or after grant under Approval), prefer `*_batch` skills whenever the task touches `2+` objects.
3. For multi-step editor mutations, prefer workflow wrappers instead of free-form mutation sequences.
4. Script edits, define changes, package changes, some imports, and test template creation can trigger compilation or Domain Reload. Wait and retry on transient unavailability.
5. `test_*` skills are async. They return a `jobId` and must be polled with `test_get_result(jobId)`.

## Route

- Module index: `unity-skills/skills/SKILL.md`
- Script guidance: `unity-skills/skills/script/SKILL.md`
- Advisory guidance: load advisory modules on demand from the module index

> **XR rule**: Before calling any `xr_*` skill in a session, load `skills/xr/SKILL.md` first. XR is reflection-based; wrong property names can fail silently.
