---
name: unity-test
description: "Unity Test Runner operations. Use when users want to run, list, or check test results. Triggers: test, unit test, test runner, EditMode, PlayMode, Unity测试, Unity单元测试, Unity测试运行."
---

# Test Skills

Run and manage Unity tests.

## Guardrails

**Mode**: Mixed — query skills marked SkillMode.SemiAuto; mutators are SkillMode.FullAuto (need grant under Approval)

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
List available tests.
**Parameters:**
- `testMode` (string, optional): EditMode or PlayMode. Default: EditMode.
- `limit` (int, optional): Max tests to list. Default: 100.

### `test_run`
Run Unity tests (returns job ID for polling).
**Parameters:**
- `testMode` (string, optional): EditMode or PlayMode. Default: EditMode.
- `filter` (string, optional): Test name filter.

### `test_get_result`
Get the result of a test run.
**Parameters:**
- `jobId` (string): Job ID from test_run.

**Returns:** `{ success, jobId, status, totalTests, passedTests, failedTests, skippedTests, inconclusiveTests, otherTests, failedTestNames, elapsedSeconds, resultSummary, error }`

### `test_cancel`
Cancel a running test.
**Parameters:**
- `jobId` (string, optional): Job ID to cancel.

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
List test categories.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | EditMode | EditMode or PlayMode |

**Returns:** `{ success, count, categories }`

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
Create an EditMode test script template.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Name of the test class to create |
| folder | string | No | Assets/Tests/Editor | Folder path for the test script |

**Returns:** `{ success, path, testName, serverAvailability }`

### `test_create_playmode`
Create a PlayMode test script template.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testName | string | Yes | - | Name of the test class to create |
| folder | string | No | Assets/Tests/Runtime | Folder path for the test script |

**Returns:** `{ success, path, testName, serverAvailability }`

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