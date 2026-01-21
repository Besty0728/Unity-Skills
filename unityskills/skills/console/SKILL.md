---
name: unity-console
description: Capture and manage Unity console logs via REST API
---

# Unity Console Skills

Work with the Unity console - capture logs, write messages, and debug your project.

## Capabilities

- Start/stop log capture
- Get captured logs with filters
- Clear console
- Write custom log messages

## Skills Reference

| Skill | Description |
|-------|-------------|
| `console_start_capture` | Start capturing logs |
| `console_stop_capture` | Stop capturing logs |
| `console_get_logs` | Get captured logs |
| `console_clear` | Clear console |
| `console_log` | Write log message |

## Parameters

### console_get_logs

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filter` | string | No | null | Log/Warning/Error |
| `limit` | int | No | 100 | Max results |

### console_log

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `message` | string | Yes | - | Log message |
| `type` | string | No | "Log" | Log/Warning/Error |

## Example Usage

```python
import unity_skills

# Start capturing logs
unity_skills.call_skill("console_start_capture")

# Do some operations that might generate logs
unity_skills.call_skill("editor_play")
# ... wait for some gameplay ...
unity_skills.call_skill("editor_stop")

# Get all captured logs
logs = unity_skills.call_skill("console_get_logs")
for log in logs['result']['logs']:
    print(f"[{log['type']}] {log['message']}")

# Get only errors
errors = unity_skills.call_skill("console_get_logs",
    filter="Error"
)

# Get only warnings
warnings = unity_skills.call_skill("console_get_logs",
    filter="Warning"
)

# Write custom log
unity_skills.call_skill("console_log",
    message="AI Agent: Starting automation task",
    type="Log"
)

# Write warning
unity_skills.call_skill("console_log",
    message="AI Agent: Performance might be affected",
    type="Warning"
)

# Clear console
unity_skills.call_skill("console_clear")

# Stop capturing
unity_skills.call_skill("console_stop_capture")
```

## Response Format

```json
{
  "status": "success",
  "skill": "console_get_logs",
  "result": {
    "success": true,
    "totalLogs": 25,
    "logs": [
      {
        "type": "Log",
        "message": "Player spawned at position (0, 1, 0)",
        "timestamp": "2024-01-15T10:30:45"
      },
      {
        "type": "Warning",
        "message": "Missing reference on Enemy script",
        "timestamp": "2024-01-15T10:30:46"
      },
      {
        "type": "Error",
        "message": "NullReferenceException in PlayerController",
        "timestamp": "2024-01-15T10:30:47"
      }
    ]
  }
}
```

## Best Practices

1. Start capture before play mode for runtime logs
2. Filter by Error to quickly find problems
3. Use custom logs to mark AI agent actions
4. Clear console before starting new capture session
5. Stop capture when done to free resources
