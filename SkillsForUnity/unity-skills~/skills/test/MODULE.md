# Unity Test Skills

Run and manage Unity Test Runner tests. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Running EditMode or PlayMode tests
- Discovering or listing tests
- Polling asynchronous test results
- Scaffolding test files

## Rules
- Test runs are asynchronous. `test_run`, `test_run_by_name`, `test_discover_start` and the `test_create_*` skills return a `jobId` at once. Poll with `test_get_result(jobId)` or `test_discover_get_result(jobId)`.
- Unity Test Runner runs one job at a time. Do not start a second `test_run` while another test job is active.
- `test_run` in PlayMode and `test_run_by_name` in PlayMode enter Play Mode (`MayEnterPlayMode`).
- `test_create_editmode` and `test_create_playmode` write a `.cs` file and trigger a domain reload. The server is briefly unavailable; retry as the root protocol describes.
- `test_list` and `test_list_categories` return `pendingDiscovery=true` with a `discoveryJobId` on a cache miss. Poll `test_discover_get_result(jobId)`, then call again.
- For compile errors use `debug_check_compilation` in the [debug](../debug/MODULE.md) module. To edit a generated test, use the [script](../script/MODULE.md) module.
- `test_smoke_skills` uses transient probes, so it does not pollute workflow or batch persistence. It skips any skill with `MayTriggerReload`.

## Pitfalls
- `test_run_all`, `test_create_template`, `test_get_status` and `test_run_async` do not exist. Use `test_run` or `test_run_by_name`, `test_create_editmode` or `test_create_playmode`, and `test_get_result`.
- `test_discover_get_result` and `test_list` both report counts, but `count` means different things. In `test_list`, `count` is the number returned and `total` is the number discovered. In `test_discover_get_result`, `count` is the number discovered and `returned` is the number in `tests`. The default `limit` (100) caps a larger suite: check `truncated`, and take the suite size from `total` (test_list) or `count` (discover).
- `test_cancel` is best effort: Unity Test Runner has no hard cancel.

## Skills

### test_run
Run tests asynchronously. Returns a `jobId` at once.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | "EditMode" | `EditMode` or `PlayMode` |
| filter | string | No | - | Test filter passed to the runner |

**Returns**: `{success, status, jobId, kind, testMode, filter, message}`

### test_run_by_name
Run specific tests by class or method name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Test class or method name |
| testMode | string | No | "EditMode" | `EditMode` or `PlayMode` |

**Returns**: `{success, jobId, testName, testMode}`

### test_get_result
Get the result of a test run.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| jobId | string | Yes | Job ID from `test_run` or `test_run_by_name` |

**Returns**: `{success, jobId, status, totalTests, passedTests, failedTests, skippedTests, inconclusiveTests, otherTests, failedTestNames, failedTestDetails, elapsedSeconds, resultSummary, error}`. Each entry in `failedTestDetails` has `name`, `resultState`, `message`, `stackTrace`, `durationSeconds` and `output`.

### test_get_last_result
Get the most recent test run result.

**Parameters**: none.

**Returns**: `{jobId, status, total, passed, failed, skipped, inconclusive, other, failedNames}`

### test_cancel
Cancel a running test job, best effort.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| jobId | string | Yes | Job ID to cancel |

**Returns**: `{success, jobId, status, cancelled, note, warnings}`

### test_list
List tests through asynchronous discovery.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | "EditMode" | `EditMode` or `PlayMode` |
| limit | int | No | 100 | Max tests to return |

**Returns**: `{success, testMode, discoveryMode, count, total, truncated, tests, pendingDiscovery, discoveryJobId, discoveryStatus}`. `count` is the number returned; `total` is the number discovered.

### test_list_categories
List test categories through asynchronous discovery. Same cache-miss pattern as `test_list`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | "EditMode" | `EditMode` or `PlayMode` |

**Returns**: `{success, testMode, discoveryMode, count, categories, pendingDiscovery, discoveryJobId, discoveryStatus, note}`

### test_discover_start
Start asynchronous discovery and return a discovery `jobId`. Use it when you want explicit control; otherwise `test_list` and `test_list_categories` start discovery on a cache miss.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | "EditMode" | `EditMode` or `PlayMode` |

**Returns**: `{success, status, jobId, kind, testMode, message}`

### test_discover_get_result
Get the result of a discovery job.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| jobId | string | Yes | - | Discovery job ID |
| limit | int | No | 100 | Max tests in the response |

**Returns**: `{success, jobId, status, testMode, discoveryMode, count, returned, truncated, tests, error}`. `count` is the discovered total; `returned` is the number in `tests`.

### test_get_summary
Get the aggregated summary across all runs.

**Parameters**: none.

**Returns**: `{success, totalRuns, completedRuns, totalPassed, totalFailed, totalSkipped, totalInconclusive, totalOther, allFailedTests}`

### test_smoke_skills
Run a reusable smoke test across the registered skills.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| category | string | No | - | One `SkillCategory` name. An unknown name is rejected rather than tested as "all". |
| nameContains | string | No | - | Filter skills by partial name |
| excludeNamesCsv | string | No | - | Comma-separated skill names to exclude |
| executeReadOnly | bool | No | true | Execute safe read-only skills directly |
| includeMutating | bool | No | true | Include mutating skills through dryRun smoke testing |
| limit | int | No | 0 | Max skills to inspect; 0 means all |
| runAsync | bool | No | true | Run as a job |
| chunkSize | int | No | 25 | Skills per chunk |
| failureItemLimit | int | No | 50 | Max failure entries reported |

**Returns**: `{success, totalSkills, executedCount, dryRunCount, failureCount, results}`

### test_create_editmode
Create an EditMode test script template.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Test class name |
| folder | string | No | "Assets/Tests/Editor" | Folder for the script |

**Returns**: `{success, status, path, testName, jobId, serverAvailability}`. The `jobId` tracks the compile.

### test_create_playmode
Create a PlayMode test script template. Same behavior as `test_create_editmode`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Test class name |
| folder | string | No | "Assets/Tests/Runtime" | Folder for the script |

**Returns**: `{success, status, path, testName, jobId, serverAvailability}`

## Example

Run the EditMode suite and poll with the job ID:

```
curl -s "http://localhost:<port>/skill/test_run?expectProject=<project>" -d '{"testMode":"EditMode"}'
curl -s --retry 20 --retry-all-errors --retry-delay 2 "http://localhost:<port>/jobs/<jobId>?wait=60"
```
