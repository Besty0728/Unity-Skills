# Protocol: Operating Mode (v1.9.0+)

Operating mode is a **server-side permission gate**, configured in the Unity panel (`Window > UnitySkills` → ⚙ Settings → Server section) and persisted in EditorPrefs per-machine. It is not an AI routing policy and **cannot** be switched via chat or REST — chat-side trigger words no longer apply.

## Boot Handshake

> The root `SKILL.md` "Target the right Editor" is the condensed version of this section. Details below.

`GET /health` is answered off the main thread, never checks an expected instance (below) and has no side effects. It is not a ritual before every session: read it when the mode, the grant channel or the surface profile matters (before sensitive writes, or after a refused one), or when you need the identity or liveness. It reports:

- `currentMode` — `"approval"` / `"auto"` / `"bypass"`
- `panelApprovalRequired` — only meaningful under Approval; selects the grant channel
- `pendingCount` — outstanding grant requests
- `surfaceProfile` — `full` / `guide` / `noSceneAuthoring`, see "Surface profile" below; `surfaceProfileHint` carries text only when the profile is not `full`, else null
- `dryRunPolicy` — `off` / `highRisk` / `allWrites`, see "dryRun policy" below
- `instanceId`, `projectName`, `port` — who is serving, see "Which Editor answered?" below
- `mainThreadIdleMs` — milliseconds since Unity's main thread last ran the request loop. `/health` is answered off the main thread, so a fast reply with a **large** `mainThreadIdleMs` means *"the server is alive but Unity is busy"* (a long skill, an import, or a modal dialog) — not *"the server is down"*. Single/double digits is a healthy idle editor; seconds means keep waiting rather than restart. `-1` means the loop has not ticked yet. Add `?live=1` to force the request through the main-thread queue when you need strictly live values instead of a snapshot up to ~1s old.
- `workflowRecoveryMode` — `true` when workflow history failed to load this session: rollback data is degraded and file-store cleanup is suspended until the history is cleared.
- `summaryAutoTruncate`, `summaryPageSize`, `tokenLevel` — the current token-saving settings; see "Token Level" below.

### Which Editor answered? (multi-instance)

Several Unity projects can run UnitySkills at once; each server takes the first free port in `8090`–`8100` in **launch order**, so a port number is not a project identity. `/health` reports `instanceId` / `projectName`, and every response carries `X-Unity-Instance` and `X-Unity-Project` headers (the project name percent-encoded when it is not ASCII).

**Expected instance (precondition).** Name the Editor a request is meant for, and any other Editor refuses it instead of running it:

- `?expectInstance=<instanceId>` — exact; an instanceId is unique per project path (`/health`, or the registry entry's `id`).
- `?expectProject=<name>` — the productName or the project folder name. Two copies of one project share a productName, so use the folder name or `expectInstance` when that can happen.
- The same values also work as `X-Expect-Instance` / `X-Expect-Project` headers (percent-encoded values are decoded; a non-blank query value wins over the header of the same kind). Comparison is case-insensitive; when both kinds are given, both must hold.
- Every endpoint except `/health`, `/` and CORS preflights checks it, on the listener thread before anything is queued. A mismatch answers **409 `INSTANCE_MISMATCH`** and nothing runs: `details.server` (instanceId, projectName, projectPath, port) says who answered, `details.expected` echoes the expectation, `details.instances` lists the other live registered Editors (`instanceId`, `projectName`, `projectPath`, `port`, `status`), and every instance that matches gets a `suggestedFixes` entry whose `args.port` / `args.url` is where to resend. When none matches, the fix says to open that project or to drop the expectation.

**Registry** — `~/.unity_skills/registry.json`, one entry per project path: `id` (instanceId), `name`, `path`, `port`, `pid`, `last_active`, `unityVersion`, `status`, `statusSince`, plus the Unity CLI binding (`cliBound`, `cliPath`; see [unity-cli](protocol-unity-cli.md)).

| `status` | Meaning |
|---|---|
| `running` (or absent, written by older servers) | Serving; heartbeat every ~30 s. Stale after 120 s without one, or once its process is gone. |
| `reloading` | Domain reload in progress: the entry and its port are kept, and the restarted server flips it back to `running`. |
| `stopped` | The server stopped while the Editor stays open (watchdog restart, failed auto-restart); it may serve again. |

Quitting the Editor removes its entry. A `reloading` or `stopped` entry has no heartbeat, so it is dropped only when its process is gone (or, after 30 minutes, when its pid has been reused by another program).

**During a domain reload** (script, package or define change) the port answers `503` (`COMPILING` / `SERVER_STOPPED`) or refuses connections for a few seconds. Retry the same port — or follow the entry if the Editor comes back on another port — and never switch to a different project's Editor. A `GET /jobs/<id>?wait=` long poll survives the reload as well; see [observability](protocol-observability.md).

- **Python client** — when the cwd lies inside a registered project, or you pass `target=`, the client pins that instance: every request carries `X-Expect-Instance`, and while it reloads or restarts the client waits for it on its own port (following it if the port moves) for up to the server's request timeout (`requestTimeoutMinutes` from the last `/health` it read; 120 s before any), then fails rather than switching projects. Only when the cwd belongs to no registered project, or that project's Editor is gone, does it try the other live entries by freshest heartbeat, then a port scan. `python unity_skills.py --list-instances` prints every registry entry with its `status`; pin a choice with `--port <n>` (or `--version "6"` / `"2022"`) when more than one instance is live or the cwd is outside the project.
- **Bare HTTP** — take the port from the task, the registry or `/health` on each port; never assume `8090`. Add `expectProject` / `expectInstance` to every write.
- **Mismatch** — resend to the instance the 409 names; when none matches, tell the user which project answered. Do not "fix" the wrong project.

## Surface profile

`surfaceProfile` (on `/health`, and on recommend or listing envelopes when it is not `full`) says which slice of the skill surface the user exposed. Only the user can switch it, in the UnitySkills panel; `guideMode` is a legacy boolean, `surfaceProfile` is authoritative.

| Profile | Hidden | What to do |
|---|---|---|
| `full` | nothing | Normal automation. |
| `guide` | write skills in GameObject, Component, Material, Scene and Sample | Give manual steps via [SKILL_GUIDE.md](../SKILL_GUIDE.md) and the `manual-*` docs. Read-only skills there still work, as does every other module. |
| `noSceneAuthoring` | every scene-authoring write, incl. any `mutatesScene` skill | Do the rest of the task normally; if it genuinely needs scene authoring, say so and let the user switch back to `full`. |

Calling a hidden skill returns `SURFACE_EXCLUDED`, and the response names the document to read (`details.manualDoc` under `guide`) or the profile to leave. It is a configuration boundary, not a failure: never retry it or route around it through another module. Response shapes: [error codes](protocol-error-codes.md).

## dryRun policy

`dryRunPolicy` (on `/health`, `/permission/status` and `unity_diagnose`) says which writes must be previewed first. Only the user sets it, per project, in the UnitySkills panel; no REST call changes it.

| Policy | Gated skills |
|---|---|
| `off` (default) | none; every response is unchanged |
| `highRisk` | the never-in-semi writes: `Operation.Delete`, `mayTriggerReload`, `mayEnterPlayMode`, `riskLevel` high |
| `allWrites` | every skill that is not `readOnly` |

Never gated: `supportsDryRun:false` skills, skills with their own `confirmToken` step (`batch_execute`, `cleaner_delete_assets`), and, while `RequireConfirmation` is on, the high-risk skills its `CONFIRMATION_REQUIRED` challenge already covers (that challenge carries the preview). The Allowlist, the operating mode and the surface profile do not change the gated set.

- **Token.** A valid `POST /skill/<name>?mode=dryRun` of a gated call carries `dryRunToken` right after `valid`. Execute with the same body plus `?dryRunToken=<token>`: single use, 300 s, bound to the skill and the body. Key order and whitespace do not matter; `verbose`, the paging keys and `_confirm` are ignored unless the skill declares a parameter of that name.
- **No usable token** → `DRYRUN_REQUIRED` (HTTP 200, nothing ran). `details.dryRun` is the v2 preview, `details.dryRunToken` a fresh token, `details.reason` one of `missingToken` / `tokenUnknownOrUsed` / `tokenExpired` / `argsChanged`, and `suggestedFixes[0]` the exact re-call. "dryRun first" and "execute, then resend" both take two calls. A domain reload clears tokens; the next rejection issues a new one.
- **Order.** Parameter errors, `SURFACE_EXCLUDED` and `MISSING_PACKAGE` answer first, and a call that can only end in `MODE_FORBIDDEN` gets that answer instead of a token. `MODE_RESTRICTED` comes after the token is consumed; the grant replay does not ask for it again.
- **`/skills/batch`.** One token per batch, bound to `steps`, `params` and `continueOnError` as sent, not to the mode. When any step is gated, the dryRun envelope carries `dryRunToken` after `dryRun` (even if some steps fail validation), and that token serves the execute or transactional run of the same body. Steps inside the batch are not gated one by one, so `$ref` steps work.
- **Not `_confirm`.** A dryRun token never satisfies `_confirm`, and a confirmation token is never a dryRun token.
- **Python.** `call(..., dry_run_token=...)` / `execute_batch(..., dry_run_token=...)`, CLI `--dry-run-token`; `retry_dry_run_required=True` opts into resending once with the token a rejection carries.

## Token Level

`tokenLevel` on `/health` is a **derived** view, not a separate persisted setting — it is computed from three source values every time it's read: `surfaceProfile` (see "Surface profile" above), `summaryAutoTruncate`, and `summaryPageSize`. Changing any of the three source values immediately changes the reported `tokenLevel`; there is no separate "set token level" call.

| `tokenLevel` | `surfaceProfile` | `summaryAutoTruncate` | `summaryPageSize` |
|---|---|---|---|
| `minimal` | `noSceneAuthoring` | `true` | `5` |
| `standard` | `guide` | `true` | `10` |
| `full` | `full` | `true` | `20` |
| `maximum` | `full` | `false` | (ignored — pagination is inactive while truncation is off) |
| `custom` | any other combination of the three source values | | |

What `summaryAutoTruncate` / `summaryPageSize` actually do: in brief mode (`verbose` not `true`), if a skill's response contains an array (top-level, or nested under `items` / `assets` / `objects` / `groups` / `entries`) longer than `summaryPageSize`, the response is wrapped with `isTruncated: true`, `totalCount`, `showing`, and only the first `summaryPageSize` items. Page through the rest with `pageOffset` / `pageLimit` (or pass `verbose=true` to skip pagination and get everything at once). Explicit `pageOffset`/`pageLimit` always page the response, even when `summaryAutoTruncate` is `false`.

## Three Modes (aligned with Claude Code permission modes)

> **Factory default:** a fresh install starts in **Auto**; an upgraded install (any pre-existing `UnitySkills_*` pref) starts in **Bypass**. It **never** defaults to Approval. The "Claude Code 类比" column below is only a mental model, **not** the factory default — never assume the mode; read `/health.currentMode` whenever it matters.

| Mode | Claude Code 类比（心智对照，非默认） | FullAuto skill | Auto-detected NeverInSemi skill |
|---|---|---|---|
| **Approval** | ≈ `default` / `plan` | First call returns `MODE_RESTRICTED`; run the grant protocol below | `MODE_FORBIDDEN` |
| **Auto** | ≈ `acceptEdits` | Executes directly (audit written); **you must self-assess** sensitive cases | `MODE_FORBIDDEN` |
| **Bypass** | ≈ `bypassPermissions` | Executes directly | Executes directly (only `ConfirmationToken` still gates high-risk) |

`NeverInSemi` is derived automatically by `IsForbiddenInSemi()` — there is no manual marker. See "Skill Mode Annotation" below.

**Previewing the gate.** A dryRun (`POST /skill/<name>?mode=dryRun`) shows the verdict without consuming a grant or writing an audit entry: its top-level `authorization` is `{allowed, blockedBy, currentMode, allowlisted, hint}` (plus `surfaceProfile` when `blockedBy` is `SURFACE_EXCLUDED`), and `blockedBy` is `MODE_RESTRICTED`, `MODE_FORBIDDEN`, `SURFACE_EXCLUDED`, or null when the call would run. It is a prediction, not a reservation: the mode or the Allowlist can change before the execute call. The root `SKILL.md` asks for a dryRun first in approval mode for this reason.

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
- The dryRun policy (above) is a separate gate with its own tokens; neither token satisfies the other

## Skill Mode Annotation

The REST surface (`805` skills) is partitioned by `[UnitySkill]` `Mode` and runtime metadata. Use schema endpoints for the canonical list:

| Annotation | Count | Source |
|---|---|---|
| `SkillMode.SemiAuto` | ~`270` | Manually annotated. Covers read-only / query / analyze skills across `script` / `perception` / `scene` / `editor` / `asset` / `workflow` / `debug` / `console` and most modules' info / list / get / find skills |
| Auto-detected NeverInSemi | ~`75-79` | `IsForbiddenInSemi()` derives purely from `Operation.Delete`, `MayEnterPlayMode`, `MayTriggerReload`, `RiskLevel="high"` (no fallback list) |
| `SkillMode.FullAuto` (default) | remainder | Unannotated skills (write / mutate by default). Approval requires grant; Auto / Bypass execute directly |

SemiAuto (read/query/analyze) skills are directly callable in every mode and span the modules below; use `GET /skills?category=<Category>` for the exact list (write skills in the same modules stay FullAuto):

- **script** (`script_read` / `script_list` / `script_get_info` / `script_find_in_file` / `script_get_compile_feedback`) · **perception** (`scene_analyze` / `scene_context` / `scene_health_check` / `scene_find_hotspots` / `project_stack_detect` — the module is named perception but its skills carry the `scene_*` prefix) · **scene** (`scene_get_info` / `scene_get_hierarchy` / `scene_get_loaded` / `scene_find_objects`) · **editor** (`editor_get_context` / `editor_get_state` / `editor_get_selection` / `editor_get_tags` / `editor_get_layers`) · **asset** (`asset_find` / `asset_get_info`) · **workflow** (`workflow_list` / `workflow_session_*` / `workflow_plan` — prefer workflow & batch helpers for planning/preview/jobs/rollback) · **debug + console** (`debug_check_compilation` / `debug_get_errors` / `debug_get_system_info` / `debug_get_memory_info` / `debug_get_logs` / `console_get_logs`)
- plus most modules' own info / list / get / find skills. **Advisory**: `28` docs-only modules (design guides, `manual-*`, `unity-cli`; no REST skills) — see the module index in `skills/SKILL.md`.
