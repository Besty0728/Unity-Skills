### batch_execute
Execute a previously previewed batch operation by `confirmToken`. Large operations return a `jobId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `confirmToken` | string | Yes | - | Token from a preview or fixer call; single use, expires after 1 hour |
| `runAsync` | bool | No | true | Run as async job |
| `chunkSize` | int | No | 100 | Batch execution chunk size (clamped to 1-200) |
| `progressGranularity` | int | No | 10 | Emit a `progressEvent` every N items processed |

`runAsync: true` (the default) or an item count above `chunkSize` returns `{success, status: "accepted", jobId, workflowId, totalItems, message}` at once; wait on the job, then read `batch_report_get`. Only the inline path (`runAsync: false` **and** items ≤ `chunkSize`) blocks, and its wait is bounded: `50ms` per item with a `5s` floor and a hard **`30s` ceiling** (the shared main-thread wait cap: the inline wait spin-sleeps on the main thread, so it may never freeze the Editor for as long as the item count would imply). It returns `{success, status, jobId, reportId, workflowId, resultSummary, error}`; when the ceiling hits first the job is still running, so `success: false` with a non-`completed` `status` and the `jobId` is **not** a failure: carry on with `GET /jobs/{id}` / `job_status`, and prefer `runAsync: true` for anything big.

An unknown or expired token fails ("Call preview again"). Under the `guide` surface profile the preview still succeeds and returns its diff, plus a `surfaceExclusion` object (`manualDoc`, `hint`, `blockedSkill`, `blockedBy`, `surfaceProfile`, `category`) warning that execution will be refused; `batch_execute` (and `batch_retry_failed`) on a token minted for a GameObject / Component / Material operation then returns `SURFACE_EXCLUDED` with `surfaceProfile`, `category`, `operation`, `manualDoc`, `userControlled` and `hint` at the top level (not under `details`); the refusal does not consume the token, so after the user switches back to `full` the same token still executes, no new preview needed.
