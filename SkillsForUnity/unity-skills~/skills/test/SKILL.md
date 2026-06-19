---
name: unity-test
description: Run Unity Test Runner operations asynchronously — run/discover/list/cancel tests, poll job results, and create test templates. Use when running EditMode/PlayMode tests, discovering or listing tests, polling async test results, or scaffolding test files, even if the user just says "跑测试" or "单元测试". 异步执行 Unity Test Runner 操作(运行/发现/列出/取消测试、轮询任务结果、创建测试模板);当用户要运行 EditMode/PlayMode 测试、发现或列出测试、轮询异步测试结果、或生成测试文件时使用。
---

# Test Skills

Run and manage Unity tests.

## Operating Mode

- **Approval**: 只读 skill（`test_get_result` / `test_list` / `test_discover_get_result` / `test_get_last_result` / `test_list_categories` / `test_smoke_skills` / `test_get_summary`，标 `SkillMode.SemiAuto`）直接执行；执行/发现/创建型 skill（`test_run` / `test_run_by_name` / `test_discover_start` / `test_cancel` / `test_create_editmode` / `test_create_playmode`，默认 `SkillMode.FullAuto`）需用户 grant，grant 后服务端一步执行返结果（job 立即排进队列）。
- **Auto / Bypass**: 直接执行。
- **本模块有 4 个 NeverInSemi skill**（按 `IsForbiddenInSemi` 自动判定）：
  - `MayEnterPlayMode = true`: `test_run`、`test_run_by_name`
  - `MayTriggerReload = true`: `test_create_editmode`、`test_create_playmode`（同时标 `MutatesAssets = true`）

  Approval 模式下这 4 个返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可绕过。注意：`test_run(testMode="PlayMode")` / `test_run_by_name` 会让 Unity TestRunner 切入 PlayMode；`test_create_editmode` / `test_create_playmode` 落盘新的 .cs 文件后会触发 Domain Reload。
- **异步约定**：`test_run` / `test_run_by_name` / `test_discover_start` / `test_create_*` 立即返回 `jobId`；用 `test_get_result(jobId)` / `test_discover_get_result(jobId)` 轮询；Unity TestRunner 串行化，**正在跑测试时不要再起第二个 `test_run`**。

**DO NOT** (common hallucinations):
- `test_run_all` does not exist → use `test_run` or `test_run_by_name`
- `test_create_template` does not exist → use `test_create_editmode` or `test_create_playmode`
- `test_get_status` does not exist → use `test_get_result` with `jobId` from test run
- Test skills are async — they return a `jobId`, poll with `test_get_result(jobId)`
- Unity Test Runner is serialized here: do not start a second `test_run` while another test job is still active
- Prefer `unity_skills.get_skills(category="Test")` or `GET /skills/schema` for exact signatures instead of guessing from memory

**Routing**:
- For compile error checking → use `debug` module's `debug_check_compilation`
- For test script creation → `test_create_editmode` / `test_create_playmode`, then modify via `script` module
- For broad regression probes across many skills → `test_smoke_skills`, which uses transient probes to avoid polluting workflow/batch persistence

## Skills

### `test_list`
List available tests via Unity Test Runner async discovery. **Returns `pendingDiscovery=true` + `discoveryJobId` on first call (cache miss)** — poll `test_discover_get_result(jobId)` then retry `test_list`.
**Parameters:**
- `testMode` (string, optional): EditMode or PlayMode. Default: EditMode.
- `limit` (int, optional): Max tests to list. Default: 100.

**Returns:** `{ success, testMode, count, tests, pendingDiscovery, discoveryJobId, discoveryStatus }`

### `test_run`
Run Unity tests asynchronously. Returns a `jobId` immediately; poll with `test_get_result(jobId)`.
**Parameters:**
- `testMode` (string, optional): EditMode or PlayMode. Default: EditMode.
**Returns:** `{ success, status, jobId, kind, testMode, filter, message }`

### `test_get_result`
Get the result of a test run.
**Parameters:**
- `jobId` (string, required): Job ID from `test_run` / `test_run_by_name`.

**Returns:** `{ success, jobId, status, totalTests, passedTests, failedTests, skippedTests, inconclusiveTests, otherTests, failedTestNames, elapsedSeconds, resultSummary, error }`

### `test_cancel`
Cancel a running test job if supported (Unity TestRunner has no hard cancel — best-effort).
**Parameters:**
- `jobId` (string, required): Job ID to cancel.

**Returns:** `{ success, jobId, status, cancelled, note, warnings }`

### `test_discover_start`
Start asynchronous Unity Test Runner discovery and return a discovery `jobId`. Use this directly when you want explicit control over discovery; otherwise `test_list` / `test_list_categories` will trigger it on cache miss.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | EditMode | EditMode or PlayMode |

**Returns:** `{ success, status, jobId, kind, testMode, message }`

### `test_discover_get_result`
Get the result of an asynchronous Unity Test Runner discovery job.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| jobId | string | Yes | - | Discovery job ID |
| limit | int | No | 100 | Max tests to return |

**Returns:** `{ success, jobId, status, testMode, discoveryMode, count, tests, error }`

### `test_run_by_name`
Run specific tests by class or method name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Test class or method name to run |
| testMode | string | No | EditMode | EditMode or PlayMode |

**Returns:** `{ success, jobId, testName, testMode }`

### `test_get_last_result`
Get the most recent test run result.

No parameters.

**Returns:** `{ jobId, status, total, passed, failed, skipped, inconclusive, other, failedNames }`

### `test_list_categories`
List test categories via Unity Test Runner async discovery. Same cache-miss / poll pattern as `test_list`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | EditMode | EditMode or PlayMode |

**Returns:** `{ success, count, categories, pendingDiscovery, discoveryJobId, discoveryStatus }`

### `test_smoke_skills`
Run a reusable smoke test across registered skills.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| category | string | No | - | Only test one skill category |
| nameContains | string | No | - | Filter skills by partial name |
| excludeNamesCsv | string | No | - | Comma-separated skill names to exclude |
| executeReadOnly | bool | No | true | Execute safe read-only skills directly |
| includeMutating | bool | No | true | Include mutating skills via dryRun smoke testing |
| limit | int | No | 0 | Max skills to inspect; 0 means all |

**Returns:** `{ success, totalSkills, executedCount, dryRunCount, failureCount, results }`

### `test_create_editmode`
Create an EditMode test script template. Writes the .cs file synchronously and returns a compile-monitor `jobId`; the script create **will trigger a Domain Reload**, so the server may be temporarily unavailable — `serverAvailability` carries the transient-unavailable hint.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Name of the test class to create |
| folder | string | No | Assets/Tests/Editor | Folder path for the test script |

**Returns:** `{ success, status, path, testName, jobId, serverAvailability }`

### `test_create_playmode`
Create a PlayMode test script template. Writes the .cs file synchronously and returns a compile-monitor `jobId`; same Domain Reload + transient-unavailable note as `test_create_editmode`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Name of the test class to create |
| folder | string | No | Assets/Tests/Runtime | Folder path for the test script |

**Returns:** `{ success, status, path, testName, jobId, serverAvailability }`

### `test_get_summary`
Get aggregated test summary across all runs.

No parameters.

**Returns:** `{ success, totalRuns, completedRuns, totalPassed, totalFailed, totalSkipped, totalInconclusive, totalOther, allFailedTests }`

---
## Minimal Example

```python
import unity_skills, time

# Run tests and poll for result (async pattern required)
result = unity_skills.call_skill("test_run", testMode="EditMode")
job_id = result["jobId"]

# Poll until done (test_* skills are async)
for _ in range(30):
    status = unity_skills.call_skill("test_get_result", jobId=job_id)
    if status.get("status") == "Completed":
        print(f"Passed: {status['totalPassed']}, Failed: {status['totalFailed']}")
        break
    time.sleep(2)
```

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.