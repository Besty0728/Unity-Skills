### job_status
Get the status of an asynchronous UnitySkills job. Prefer `GET /jobs/{id}` (or `?wait=<s>`), which skips the main-thread skill queue.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `includeDetails` | bool | No | false | Inline the full result payload as `details` instead of `resultAvailable` / `resultHint` |

Built for repeated polling, so **the job's result payload is not inlined by default** (v2.7+): a completed test or compile job can carry tens of KB, and polling used to resend all of it every call. Instead you get two fields:

- **`resultAvailable`** (bool): whether a payload exists at all.
- **`resultHint`** (string, `null` when `resultAvailable` is false): where to fetch it: `test_get_result(jobId)` for `kind: "test"`, `test_discover_get_result(jobId)` for `kind: "test_discovery"`, `batch_report_get(reportId=...)` for any job that produced a `reportId` (the hint quotes the id), and for every other kind a note to re-call with `includeDetails=true`, which is then the only route to that payload.

The **`details` key is still present either way**: it is `null` unless you asked for it, so reading `response["details"]` gives null rather than a missing-key error. Set `includeDetails=true` on the one call where you want the data, never in the polling loop.

**Returns**: `{jobId, status, progress, currentStage, resultAvailable, resultHint, details, ...}`.
