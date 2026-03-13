---
name: unity-console
description: "Unity console log management. Use when users want to capture, filter, or clear console logs. Triggers: console, log, warning, error, debug, print, Unity控制台, Unity日志, Unity错误."
---

# Unity Console Skills

Work with the Unity console - capture logs, write messages, and debug your project.

## Skills Overview

| Skill | Description |
|-------|-------------|
| `console_start_capture` | Start capturing logs |
| `console_stop_capture` | Stop capturing logs |
| `console_get_logs` | Get captured logs |
| `console_clear` | Clear console |
| `console_log` | Write log message |
| `console_set_pause_on_error` | Enable or disable Error Pause in Play mode |
| `console_export` | Export console logs to a file |
| `console_get_stats` | Get log statistics (count by type) |
| `console_set_collapse` | Set console log collapse mode |
| `console_set_clear_on_play` | Set clear on play mode |

---

## Skills

### console_start_capture
Start capturing Unity console logs.

No parameters.

### console_stop_capture
Stop capturing logs.

No parameters.

### console_get_logs
Get captured logs with optional filtering.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filter` | string | No | null | Log/Warning/Error |
| `limit` | int | No | 100 | Max results |

**Returns**: `{success, totalLogs, logs: [{type, message, timestamp}]}`

### console_clear
Clear the Unity console.

No parameters.

### console_log
Write a custom log message.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `message` | string | Yes | - | Log message |
| `type` | string | No | "Log" | Log/Warning/Error |

### `console_set_pause_on_error`
Enable or disable Error Pause in Play mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `enabled` | bool | No | true | Enable or disable error pause |

**Returns:** `{ success, enabled }`

### `console_export`
Export console logs to a file. Uses captured buffer when console_start_capture is active; otherwise reads directly from Unity Console history (no setup needed).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | No | "Assets/console_log.txt" | File path to save logs |

**Returns:** `{ success, path, count, source }`

### `console_get_stats`
Get log statistics (count by type). Uses captured buffer when console_start_capture is active; otherwise reads directly from Unity Console history.

No parameters.

**Returns:** `{ success, total, source, logs, warnings, errors, exceptions, asserts }`

### `console_set_collapse`
Set console log collapse mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `enabled` | bool | Yes | - | Enable or disable collapse mode |

**Returns:** `{ success, setting, enabled }`

### `console_set_clear_on_play`
Set clear on play mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `enabled` | bool | Yes | - | Enable or disable clear on play |

**Returns:** `{ success, setting, enabled }`

---

## Example Usage

```python
import unity_skills

# Start capturing logs before play mode
unity_skills.call_skill("console_start_capture")

# Enter play mode
unity_skills.call_skill("editor_play")
# ... gameplay generates logs ...
unity_skills.call_skill("editor_stop")

# Get all captured logs
logs = unity_skills.call_skill("console_get_logs")
for log in logs['logs']:
    print(f"[{log['type']}] {log['message']}")

# Get only errors
errors = unity_skills.call_skill("console_get_logs", filter="Error")
if errors['totalLogs'] > 0:
    print(f"Found {errors['totalLogs']} errors!")

# Write custom log
unity_skills.call_skill("console_log",
    message="AI Agent: Task completed",
    type="Log"
)

# Write warning
unity_skills.call_skill("console_log",
    message="AI Agent: Performance issue detected",
    type="Warning"
)

# Clear and stop
unity_skills.call_skill("console_clear")
unity_skills.call_skill("console_stop_capture")
```

## Best Practices

1. Start capture before play mode for runtime logs
2. Filter by Error to quickly find problems
3. Use custom logs to mark AI agent actions
4. Clear console before starting new capture session
5. Stop capture when done to free resources
