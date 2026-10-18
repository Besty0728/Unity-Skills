---
name: unity-console
description: Capture and query the Unity Editor console
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Inspecting console output
- Filtering errors or warnings
- Emitting log messages
- Configuring the console
- 查看控制台输出、过滤错误或警告、输出日志、配置控制台

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
- Do not confuse with `debug_get_logs` — `console_get_logs` is the durable, timestamped history API; `debug_get_logs` is the native Console compatibility reader

**Routing**:
- For compilation errors specifically → use `debug` module's `debug_check_compilation`
- For error stack traces → use `debug` module's `debug_get_stack_trace`
- For console settings (collapse, clear-on-play) → `console_set_collapse` / `console_set_clear_on_play` (this module)

## Skills Overview

| Skill | Description |
|-------|-------------|
| `console_start_capture` | Start capturing logs |
| `console_stop_capture` | Stop capturing logs |
| `console_get_logs` | Get durable, timestamped logs with fixed-snapshot pagination |
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
Start a compatibility capture session. Managed log history is always collected, so this records a session cursor and never clears earlier background history.

No parameters.

### console_stop_capture
Stop the compatibility capture session. It does not discard retained history.

No parameters.

### console_get_logs
Get retained Unity Console logs. By default, identical full `(type, message, stackTrace)` records are collapsed into one group. This read setting is independent of Unity Console's UI Collapse toggle.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `type` | string | No | "All" | All / Error / Warning / Log (case-insensitive; a value naming none of them is rejected with `SEMANTIC_INVALID`) |
| `filter` | string | No | null | Case-insensitive substring in the full message or stack trace |
| `limit` | int | No | 100 | Results per page (1–1000) |
| `collapse` | bool | No | true | Return stable duplicate groups instead of individual records |
| `fromUtc` | string | No | null | Inclusive ISO-8601 UTC bound (`...Z` or a zero offset) |
| `toUtc` | string | No | null | Exclusive ISO-8601 UTC bound; the range is `[fromUtc, toUtc)` |
| `groupId` | string | No | null | Restrict to one group id returned by a collapsed response |
| `cursor` | string | No | null | Opaque fixed-snapshot page cursor returned by the previous response; invalidated by `console_clear` |
| `includeStackTrace` | bool | No | false | Include complete stack traces in returned records/groups |

**Returns:** `{count, logs, source, cursor, nextCursor, totalMatched, hasMore, complete, dropped, coverage}`. Individual records carry `{seq, type, message, stackTrace?, utc, timeKnown, sessionId, source}`. Collapsed rows carry `{groupId, type, message, stackTrace?, count, countKnown, firstUtc, lastUtc, firstSeq, lastSeq}`. `groupId` is stable for the complete `(type, message, stackTrace)` tuple. `cursor` fixes the high-water sequence for every later page; new logs cannot enter that page sequence. `complete:false` or `dropped>0` means retention, clear history, corruption, persistence trouble, or old native records prevent a complete assertion. Native logs from before this service have `utc:null` and `timeKnown:false`; no timestamp is invented for them. Their `count` is null and `countKnown:false`, because Unity's old Console reflection does not expose an uncollapsed occurrence count. Native messages can be truncated by Unity reflection, so full-text filtering is guaranteed only for managed records. `coverage` reports this, the bounded retention, persistence health, and the available `Editor.log` path; `Editor.log` is not imported into cursor history and is only a manual fallback.

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
Export retained console history to a file, including available UTC timestamps and complete stack traces.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | No | "Assets/console_log.txt" | File path to save logs |

**Returns:** `{ success, path, count, source, complete, dropped, truncated }`. Check `truncated` before treating an export as complete evidence.

### `console_get_stats`
Get statistics for retained log history.

No parameters.

**Returns:** `{success, total, source, logs, warnings, errors, exceptions, asserts, complete, dropped, truncated}`. Check `truncated` before using a zero error count as evidence of a clean run.

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

# History starts automatically. The first response fixes a snapshot; later pages do not admit new logs.
page = unity_skills.call_skill("console_get_logs", type="Error", includeStackTrace=True)
while page["hasMore"]:
    page = unity_skills.call_skill("console_get_logs", type="Error", includeStackTrace=True,
                                  cursor=page["nextCursor"])

# For occurrence-level evidence instead of duplicate groups, opt out of collapse and constrain time.
logs = unity_skills.call_skill("console_get_logs", collapse=False,
    fromUtc="2026-10-10T00:00:00Z", toUtc="2026-10-11T00:00:00Z")

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

# Clear invalidates every old cursor and deletes retained history. A capture session is optional
# and limits reads/exports/stats to records created after its start without deleting background history.
unity_skills.call_skill("console_start_capture")
unity_skills.call_skill("console_clear")
unity_skills.call_skill("console_stop_capture")
```

## Best Practices

1. Start with collapsed error groups, then request `collapse:false` and a `groupId` or time range only when individual occurrences matter.
2. Follow `nextCursor` until `hasMore:false`; preserve the first page's fixed snapshot and reject a result when `complete:false`.
3. `complete:false` is the incompleteness signal. Records with seq at or below `coverage.incompleteThroughSeq` may be missing (unreadable rows, an editor kill), so only a window whose `afterSeq` is at or above it can be complete; `coverage.corruptRows` counts those rows and `coverage.durabilityIssue` flags a failing disk write. Retention is limited to 50,000 records and 64 MiB.
4. Use `console_start_capture` only to bracket a compatibility session; automatic managed capture is already running and capture start never clears history.
5. Use the `coverage.editorLogPath` fallback only for manual investigation. It is not timestamp-normalized or merged into this API.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
