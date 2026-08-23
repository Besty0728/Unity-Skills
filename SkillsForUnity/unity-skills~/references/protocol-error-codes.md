# Protocol: Error Codes

## Read the body, not the status code

The HTTP status tells you whether the *request* was accepted, not whether the *skill* succeeded:

| Layer | Status | Body |
|---|---|---|
| Skill-level failure (bad target, bad value, refused write) | `200` | `status: "error"` + `errorCode` |
| Batch with some failed steps | `200` | `status: "partial"` (or `"rolled_back"` in transactional mode), per-step `status` in `results[]` |
| Endpoint-level failure (malformed body, unknown mode, >50 batch steps, oversized body) | `4xx` | `errorCode` for the request itself |
| Transport / transient (`COMPILING`, `RATE_LIMIT`, `QUEUE_FULL`, `SERVER_STOPPED`, `TIMEOUT`) | `429` / `503` / `504` | `errorCode` + `retryAfterSeconds` |

So a client that branches on `response.status_code == 200` will read a failed skill call as a success. Always parse the body and check `status` / `errorCode`. The two authorization outcomes (`MODE_RESTRICTED`, `MODE_FORBIDDEN`) and `SURFACE_EXCLUDED` are also HTTP `200` — they are decisions, not transport faults.

Every error response carries a top-level `errorCode` (plus `retryStrategy` / `retryAfterSeconds` when applicable — see `SkillErrorResponse.Build` in the server source). **The Python client (`unity_skills.py`) already auto-retries `COMPILING` / `RATE_LIMIT` / `QUEUE_FULL` / `SERVER_STOPPED`** using the server's own `retryAfterSeconds` — if you're calling through `unity_skills.call_skill(...)` you normally never see these codes at all; the table below is for building a custom client or interpreting a final failure after retries are exhausted.

## Transport / transient errors

| `errorCode` | HTTP | Meaning | Action |
|---|---|---|---|
| `COMPILING` | 503 | Unity is compiling or mid-Domain-Reload — expected right after script/define/package edits | Wait `retryAfterSeconds` (5s, 8s if a reload is pending) and retry. Auto-retried by the Python client |
| `RATE_LIMIT` | 429 | Too many requests/sec against the admission limiter | Wait `retryAfterSeconds` (1s) and retry. Auto-retried by the Python client |
| `QUEUE_FULL` | 503 | Too many requests already pending on the main-thread queue | Wait `retryAfterSeconds` (2s) and retry. Auto-retried by the Python client |
| `SERVER_STOPPED` | 503 | Server is stopping/stopped (manual stop or reload teardown) | Wait `retryAfterSeconds` (5s) for it to come back and retry. Auto-retried by the Python client |
| `TIMEOUT` | 504 | Main thread didn't respond within the request timeout | Wait (5s if a Domain Reload is pending, else 10s) and retry; if it persists, check for a stuck modal dialog or long-running operation in the Editor |
| `BODY_TOO_LARGE` | 413 | POST body exceeded the server's max size | Not retryable as-is — shrink the request (e.g. split a large `/skills/batch` into smaller batches) |
| `MODE_RESTRICTED` | 200 (`status:"error"` in the body) | Approval mode: the skill is `FullAuto` and needs a user grant | Follow the Approval Mode Grant Protocol — do not retry the raw call |
| `MODE_FORBIDDEN` | 200 (`status:"error"` in the body) | Skill is auto-classified `NeverInSemi` (Delete/PlayMode/Reload/high-risk) and the current mode isn't Bypass | Tell the user it needs Bypass mode or an Allowlist entry — do not attempt the grant flow |
| `SURFACE_EXCLUDED` | 200 (`status:"error"` in the body) | The current `surfaceProfile` hides this skill — either because it is a write in a hidden category, or by name for a master key like `editor_execute_menu`. Not a permission problem: the user took the operation off the menu, so no mode — Bypass included — and no Allowlist entry can run it | Follow `details.hint`. When `details.manualDoc` is set (`guide` profile), read that document and instruct the user through the Editor steps; when it is null — a name-hidden skill, or any `noSceneAuthoring` exclusion — name the menu path yourself or tell the user the step needs the profile back on `full`. Do not retry, and do not reach for another module that does the same write |

> **Two response shapes carry this code.** A name-hidden or category-hidden skill's rejection nests `manualDoc`/`hint` under `details`, like any other skill error. A *carried-write* rejection — `batch_execute` / `batch_retry_failed` acting on a `confirmToken`'s payload, or `workflow_undo_task` / `workflow_redo_task` / `workflow_revert_task` / `workflow_session_undo` restoring a snapshot — instead puts `surfaceProfile` / `category` / `operation` / `manualDoc` / `userControlled` / `hint` at the response's **top level**, because the router's skill-error pass-through forwards a skill's unrecognised members verbatim but drops a skill-authored `details` object; nesting them there would silently lose them.

## Business errors

A skill ran but refused the request. All of these come back as HTTP `200` with `status:"error"` in the body, and carry `retryStrategy` plus `suggestedFixes`/`relatedSkills` naming the skill to call next. None is auto-retried by the Python client: they need a corrected call, not a wait.

| `errorCode` | `retryStrategy` | Meaning | Action |
|---|---|---|---|
| `TARGET_NOT_FOUND` | `find_target_and_retry` | The GameObject / asset / component the skill was pointed at does not exist | Locate it first (`gameobject_find`, `scene_get_hierarchy`, `asset_find`, `component_list` — `relatedSkills` says which), then retry with the `entityId` or exact path it returns |
| `MISSING_PACKAGE` | `install_and_retry` | The skill's optional package (ProBuilder / XR / Netcode / DOTween / YooAsset / URP …) is not installed | Install it with `package_install` (the message names the package id), wait out the Domain Reload, then retry |
| `MISSING_PARAM` | `fix_and_retry` | A required parameter was omitted | Add the parameter named in the message; `POST /skill/<name>?mode=dryRun` returns the full schema without executing |
| `SEMANTIC_INVALID` | `fix_and_retry` | A parameter was supplied but rejected — out of range, unknown enum value, wrong asset type, or the target already exists | Read the accepted range/values from the message, correct the args, dryRun, retry |

> **Rejected enum values are self-describing.** A bad value for an enum-shaped parameter (`shadows`, `clearFlags`, `lightType`, `flags`, …) returns `SEMANTIC_INVALID` with `parameter` naming the offending argument and **`validValues` listing every accepted member** — take the list from there instead of guessing or re-fetching the schema. Matching is case-insensitive **and interior spaces are stripped first**, so an Inspector label pastes in verbatim (`"Normal Map"` → `NormalMap`, `"Low Quality"` → `CompressedLQ`). Some enums also carry an alias table mapping the Inspector's wording onto the CLR member name, and `validValues` lists both spellings — so if the label and the CLR name differ, either one is accepted.
>
> One case is *not* "genuinely unknown" yet still rejected: a **deprecated member that owns a numeric value no live member claims** — Unity's removed texture types (`Image`, `Bump`, `Cubemap`, `Reflection`, `Advanced`, `HDRI`) are refused rather than silently written. The filter is by value, not by name, so a deprecated *spelling* that shares its value with a live member keeps working: `LightType.Area` still resolves (it is the same value as `Rectangle`), as does `TextureImporterFormat.AutomaticCompressed` (same value as `Automatic`). Do not read "deprecated" as "rejected" — check `validValues`. Such a rejection applies **nothing**: sibling parameters in the same call are not written, so a retry with the value corrected is safe and non-cumulative. The same payload shape (plus `target`) is used per item inside a `*_batch` call.
| `SKILL_ERROR` | `abort` | A genuine runtime failure inside the skill (I/O error, reflection failure, unusable editor state) | Do not retry blind — report the message to the user or pick another approach |

> These codes are derived at the routing layer, so they apply across all skills, including ones whose own error text is a bare sentence. A skill may also declare `errorCode` / `retryStrategy` / `suggestedFixes` / `relatedSkills` on its error object, in which case those are passed through verbatim and override the derivation.
