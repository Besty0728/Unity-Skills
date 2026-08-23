# Protocol: Operating Mode (v1.9.0+)

Operating mode is a **server-side permission gate**, configured in the Unity panel (`Window > UnitySkills` → ⚙ Settings → Server section) and persisted in EditorPrefs per-machine. It is not an AI routing policy and **cannot** be switched via chat or REST — chat-side trigger words no longer apply.

## Boot Handshake

> See the root `SKILL.md` "First Contact Checklist" for the condensed version of this step. Details below.

On session start (or before the first skill call), call `GET /health` and read:

- `currentMode` — `"approval"` / `"auto"` / `"bypass"`
- `panelApprovalRequired` — only meaningful under Approval; selects the grant channel
- `pendingCount` — outstanding grant requests
- `mainThreadIdleMs` — milliseconds since Unity's main thread last ran the request loop. `/health` is answered off the main thread, so a fast reply with a **large** `mainThreadIdleMs` means *"the server is alive but Unity is busy"* (a long skill, an import, or a modal dialog) — not *"the server is down"*. Single/double digits is a healthy idle editor; seconds means keep waiting rather than restart. `-1` means the loop has not ticked yet. Add `?live=1` to force the request through the main-thread queue when you need strictly live values instead of a snapshot up to ~1s old.
- `workflowRecoveryMode` — `true` when workflow history failed to load this session: rollback data is degraded and file-store cleanup is suspended until the history is cleared.

## Three Modes (aligned with Claude Code permission modes)

> **Factory default:** a fresh install starts in **Auto**; an upgraded install (any pre-existing `UnitySkills_*` pref) starts in **Bypass**. It **never** defaults to Approval. The "Claude Code 类比" column below is only a mental model, **not** the factory default — always read `/health.currentMode` before acting.

| Mode | Claude Code 类比（心智对照，非默认） | FullAuto skill | Auto-detected NeverInSemi skill |
|---|---|---|---|
| **Approval** | ≈ `default` / `plan` | First call returns `MODE_RESTRICTED`; run the grant protocol below | `MODE_FORBIDDEN` |
| **Auto** | ≈ `acceptEdits` | Executes directly (audit written); **you must self-assess** sensitive cases | `MODE_FORBIDDEN` |
| **Bypass** | ≈ `bypassPermissions` | Executes directly | Executes directly (only `ConfirmationToken` still gates high-risk) |

`NeverInSemi` is derived automatically by `IsForbiddenInSemi()` — there is no manual marker. See "Skill Mode Annotation" below.

## Approval Mode Grant Protocol

Approval grants are **single-shot one-step execution**: a successful `/permission/grant` call runs the original skill server-side and returns the result in the same response. You do **not** retry the skill after grant. Grants are **not** persisted — calling the same skill a second time will hit `MODE_RESTRICTED` again and must go through grant again. If the user wants permanent bypass for a skill, direct them to the Allowlist (see below).

On `MODE_RESTRICTED`, branch on `details.approvalChannel`:

### Dialog channel (`"dialog"`, default — `panelApprovalRequired = false`)

1. Tell the user in chat: "要调用 `<skill>` 来 `<目的>`，参数 `<argsSummary>`，请求码 #`<token 前 6 位>`，是否允许？"
2. After explicit user consent, call `POST /permission/grant { skill, token, args }` **once**
3. On success, the response contains `{ ok: true, executed: true, skill, result: <Execute output> }` — the skill has already run server-side. Consume `result` directly; **do not call the original skill endpoint again**

### Panel channel (`"panel"`, when `panelApprovalRequired = true`)

1. Tell the user in chat: "要调用 `<skill>` 来 `<目的>`，请到 `Window > UnitySkills` 面板的 Pending Grant Requests 点 `[Approve]`（请求码 #`<token 前 6 位>`）"
2. **Do not call `/permission/grant` yet** — calling it before the user clicks Approve returns `GRANT_PENDING_APPROVAL`
3. Poll `GET /permission/status?token=<token>` to observe the request state (look at `focus.approvedByPanel`)
4. Once the user has pressed Approve in the panel, call `POST /permission/grant { skill, token, args }` **once** — this takes the Granted branch and triggers one-step execution, returning `{ ok: true, executed: true, skill, result }`. Consume `result` directly; **do not call the original skill endpoint again**

> Note: panel approval no longer auto-routes the result back to the AI. The Approve click only flips the request into the Granted state; AI must follow up with one `/permission/grant` call to fetch the execution result.

On `MODE_FORBIDDEN`: the skill is auto-classified as NeverInSemi (Delete / Domain Reload / Play Mode / high-risk). It is callable only under Bypass, **or** if the user has explicitly added it to the Allowlist (see below). **Do not attempt the grant flow** — tell the user the action requires Bypass mode, an Allowlist entry, or offer an alternative skill.

## Allowlist (user-managed permanent bypass)

The Allowlist is a **user-managed** permanent whitelist of skill names, configured in the `Window > UnitySkills` panel's ⚙ settings drawer (Allowlist Skills section / `+ Add Skill` button). It is independent of Approval grants:

- Allowlisted skills execute directly under any mode — the server skips the Approval/MODE_RESTRICTED gate
- **An Allowlist entry overrides MODE_FORBIDDEN** for that skill (covers Delete / MayEnterPlayMode / MayTriggerReload / `RiskLevel="high"`). This is intentional: the user has explicitly opted in
- **Allowlist does NOT bypass the high-risk ConfirmationToken gate.** When `RequireConfirmation` is enabled (Settings drawer → Runtime → Require Confirmation), high-risk skills still require the `_confirm` token two-step handshake even if allowlisted — Allowlist only covers the mode/approval channel, not the per-call safety confirmation
- The list is **opaque to the AI**: allowlisted skills look like normal successful calls, never returning `MODE_RESTRICTED`
- **The AI should not call `/permission/allowlist/add` on its own initiative.** Only call it when the user has explicitly authorized a session-scoped bulk add (e.g. "把这几个 skill 加白名单方便我后面批量调"); otherwise direct the user to add entries through the panel
- Allowlist endpoints: `GET /permission/allowlist` / `POST /permission/allowlist/add` / `POST /permission/allowlist/remove` (body `{skill}` or `{all: true}`)

> The previous `GrantedSkills` semantics ("after one grant the skill is permanently auto-allowed") has been removed. Grants are now single-shot. Permanent allow == Allowlist; one-shot approval == grant.

## Auto Mode Self-Assessment

Under Auto, FullAuto skills run directly. You **must pause and confirm with the user** in chat when any of the following apply:

- Batch operation touching ≥ `5` objects
- Prefab apply / scene-level mutation / asset overwrite
- Dry-run shows irreversible changes (deletes, overrides, cascading edits)

This confirmation is a chat-level check (explain plan + risk + ask), independent of the server-side mode gate. The server will not stop you in Auto — the audit log records the call regardless.

## Relationship with `ConfirmationTokenService`

Mode authorization (persistent, per-skill) and `ConfirmationToken` (single-shot, per-call) are **orthogonal**:

- Mode check runs first; if allowed, the existing confirmation gate may still issue `CONFIRMATION_REQUIRED` with a dry-run for `RiskLevel=high` or `Operation.Delete` skills
- Granted skills still flow through `ConfirmationToken` when triggered — continue using the original dry-run → user consent → retry with `_confirm` loop
- Neither replaces the other

## Skill Mode Annotation

The REST surface (`785` skills) is partitioned by `[UnitySkill]` `Mode` and runtime metadata. Use schema endpoints for the canonical list:

| Annotation | Count | Source |
|---|---|---|
| `SkillMode.SemiAuto` | ~`270` | Manually annotated. Covers read-only / query / analyze skills across `script` / `perception` / `scene` / `editor` / `asset` / `workflow` / `debug` / `console` and most modules' info / list / get / find skills |
| Auto-detected NeverInSemi | ~`75-79` | `IsForbiddenInSemi()` derives purely from `Operation.Delete`, `MayEnterPlayMode`, `MayTriggerReload`, `RiskLevel="high"` (no fallback list) |
| `SkillMode.FullAuto` (default) | remainder | Unannotated skills (write / mutate by default). Approval requires grant; Auto / Bypass execute directly |

SemiAuto (read/query/analyze) skills are directly callable in every mode and span the modules below; use `GET /skills?category=<Category>` for the exact list (write skills in the same modules stay FullAuto):

- **script** (`script_read` / `script_list` / `script_get_info` / `script_find_in_file` / `script_get_compile_feedback`) · **perception** (`scene_analyze` / `scene_context` / `scene_health_check` / `scene_find_hotspots` / `project_stack_detect` — the module is named perception but its skills carry the `scene_*` prefix) · **scene** (`scene_get_info` / `scene_get_hierarchy` / `scene_get_loaded` / `scene_find_objects`) · **editor** (`editor_get_context` / `editor_get_state` / `editor_get_selection` / `editor_get_tags` / `editor_get_layers`) · **asset** (`asset_find` / `asset_get_info`) · **workflow** (`workflow_list` / `workflow_session_*` / `workflow_plan` — prefer workflow & batch helpers for planning/preview/jobs/rollback) · **debug + console** (`debug_check_compilation` / `debug_get_errors` / `debug_get_system_info` / `debug_get_memory_info` / `debug_get_logs` / `console_get_logs`)
- plus most modules' own info / list / get / find skills. **Advisory**: `21` design-only modules (no REST skills) — see the module index in `skills/SKILL.md`.
