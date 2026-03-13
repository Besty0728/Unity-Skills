---
name: unity-test
description: "Unity Test Runner operations. Use when users want to run, list, or check test results. Triggers: test, unit test, test runner, EditMode, PlayMode, Unity测试, Unity单元测试, Unity测试运行."
---

# Test Skills

Run and manage Unity tests.

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

**Returns:** `{ jobId, status, total, passed, failed, failedNames }`

### `test_list_categories`
List test categories.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| testMode | string | No | EditMode | EditMode or PlayMode |

**Returns:** `{ success, count, categories }`

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

**Returns:** `{ success, totalRuns, completedRuns, totalPassed, totalFailed, allFailedTests }`
