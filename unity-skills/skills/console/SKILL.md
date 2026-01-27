---
name: unity-console
description: "Capture and manage Unity console logs."
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
