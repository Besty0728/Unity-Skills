# Protocol: Error Codes

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

## Business errors

A skill ran but refused the request. All of these come back as HTTP `200` with `status:"error"` in the body, and carry `retryStrategy` plus `suggestedFixes`/`relatedSkills` naming the skill to call next. None is auto-retried by the Python client: they need a corrected call, not a wait.

| `errorCode` | `retryStrategy` | Meaning | Action |
|---|---|---|---|
| `TARGET_NOT_FOUND` | `find_target_and_retry` | The GameObject / asset / component the skill was pointed at does not exist | Locate it first (`gameobject_find`, `scene_get_hierarchy`, `asset_find`, `component_list` — `relatedSkills` says which), then retry with the `entityId` or exact path it returns |
| `MISSING_PACKAGE` | `install_and_retry` | The skill's optional package (ProBuilder / XR / Netcode / DOTween / YooAsset / URP …) is not installed | Install it with `package_install` (the message names the package id), wait out the Domain Reload, then retry |
| `MISSING_PARAM` | `fix_and_retry` | A required parameter was omitted | Add the parameter named in the message; `POST /skill/<name>?mode=dryRun` returns the full schema without executing |
| `SEMANTIC_INVALID` | `fix_and_retry` | A parameter was supplied but rejected — out of range, unknown enum value, wrong asset type, or the target already exists | Read the accepted range/values from the message, correct the args, dryRun, retry |
| `SKILL_ERROR` | `abort` | A genuine runtime failure inside the skill (I/O error, reflection failure, unusable editor state) | Do not retry blind — report the message to the user or pick another approach |

> These codes are derived at the routing layer, so they apply across all skills, including ones whose own error text is a bare sentence. A skill may also declare `errorCode` / `retryStrategy` / `suggestedFixes` / `relatedSkills` on its error object, in which case those are passed through verbatim and override the derivation.
