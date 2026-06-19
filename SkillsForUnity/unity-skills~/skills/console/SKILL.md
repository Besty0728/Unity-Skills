---
name: unity-console
description: Capture and query the Unity Editor console — read/filter logs, write custom log entries, and adjust console settings. Use when inspecting console output, filtering errors or warnings, emitting log messages, or configuring the console, even if the user just says "看日志" or "控制台". 捕获并查询 Unity 编辑器控制台(读取/过滤日志、写入自定义日志、调整控制台设置);当用户要查看控制台输出、过滤错误或警告、输出日志消息时使用。
---

# Unity Console Skills

Work with the Unity console - capture logs, write messages, and debug your project.

## Operating Mode

- **Approval**: 只读 skill（`console_get_logs` / `console_get_stats`，标 `SkillMode.SemiAuto`）直接执行；其余 skill（`console_start_capture` / `console_stop_capture` / `console_clear` / `console_log` / `console_export` / `console_set_pause_on_error` / `console_set_collapse` / `console_set_clear_on_play`，默认 `SkillMode.FullAuto`）需用户 grant，grant 后一步执行返结果。
- **Auto / Bypass**: 直接执行。
- **本模块不含 Delete / PlayMode / Reload / RiskLevel=high 类 skill** —— 没有 `IsForbiddenInSemi` 拦截，不需要 Bypass 才能跑的高危操作。

**DO NOT** (common hallucinations):
- `console_filter` does not exist → use `console_get_logs` with `filter` parameter
- `console_read` does not exist → use `console_get_logs`
- `console_write` does not exist → use `console_log`
- Do not confuse with `debug_get_logs` — `console_get_logs` reads captured buffer, `debug_get_logs` reads all console entries

**Routing**:
- For compilation errors specifically → use `debug` module's `debug_check_compilation`
- For error stack traces → use `debug` module's `debug_get_stack_trace`
- For console settings (collapse, clear-on-play) → `console_set_collapse` / `console_set_clear_on_play` (this module)

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
Get Unity Console logs (reads existing console history directly; if `console_start_capture` is active, returns captured buffer with timestamps instead).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `type` | string | No | "All" | All / Error / Warning / Log |
| `filter` | string | No | null | Substring content filter |
| `limit` | int | No | 100 | Max results |

**Returns** (two shapes depending on mode):
- Capture mode (`console_start_capture` active): `{count, logs: [{type, message, time}], source: "capture"}` — `time` formatted `HH:mm:ss.fff`
- Direct mode (default, reads Unity Console history): `{count, logs: [{type, message, file, line}], source: "console"}` — `type` is `Error` / `Warning` / `Log`, `file` / `line` from Unity's `LogEntry`

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

**Returns** (two shapes depending on mode):
- Capture mode (buffer present, i.e. `console_start_capture` was called or buffer is non-empty): `{success, total, source: "capture", logs, warnings, errors, exceptions, asserts}`
- Direct mode (no capture buffer, reads Unity Console history): `{success, total, source: "console", logs, warnings, errors}` — `exceptions` / `asserts` are not reported in direct mode (folded into `errors`)

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
errors = unity_skills.call_skill("console_get_logs", type="Error")
if errors['count'] > 0:
    print(f"Found {errors['count']} errors!")

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

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.