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
