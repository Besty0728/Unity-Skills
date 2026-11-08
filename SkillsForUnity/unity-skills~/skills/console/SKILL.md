---
name: unity-console
description: Capture and query the Unity Editor console
---

# Unity Console Skills

Capture, query, write and export the Unity Console: retained timestamped logs, paged reads, statistics and console settings. Parameters come from the schema (`GET /skills/schema?category=Console&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Inspecting console output
- Filtering errors or warnings
- Emitting log messages
- Configuring the console

## Rules
- `console_get_logs` and `console_get_stats` are read-only and run under Approval. The other skills need a grant there.
- The managed log history is always collected. `console_start_capture` only records a cursor, and `console_clear` is the only skill that deletes retained history.
- Start with collapsed error groups. Request `collapse:false` with a `groupId` or time range only when individual occurrences matter.
- Follow `nextCursor` until `hasMore:false`. Reject a result with `complete:false` as incomplete evidence.
- Records with a `seq` at or below `coverage.incompleteThroughSeq` may be missing, so only a window whose `afterSeq` is at or above it can be complete. `coverage.corruptRows` counts unreadable rows and `coverage.durabilityIssue` flags a failing disk write. Retention is 50,000 records and 64 MiB.
- Use `coverage.editorLogPath` only for manual investigation: it is not timestamp-normalized or merged into this API.
- `console_export` and `console_get_stats` report `truncated`; check it before treating an export or a zero error count as evidence.
- Compilation errors belong to `debug_check_compilation`, and stack traces of a log entry to `debug_get_stack_trace` ([debug](../debug/SKILL.md)).

## Pitfalls
- `console_filter` does not exist: use `console_get_logs` with `filter`.
- `console_read` does not exist: use `console_get_logs`.
- `console_write` does not exist: use `console_log`.
- `console_get_logs` is the durable, timestamped history API; `debug_get_logs` is the native Console compatibility reader.

## Skills Overview

| Skill | Description |
|-|-|
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

### console_set_pause_on_error
Enable or disable Error Pause in Play mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `enabled` | bool | No | true | Enable or disable error pause |

**Returns:** `{ success, enabled }`

### console_export
Export retained console history to a file, including available UTC timestamps and complete stack traces.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | No | "Assets/console_log.txt" | File path to save logs |

**Returns:** `{ success, path, count, source, complete, dropped, truncated }`. Check `truncated` before treating an export as complete evidence.

### console_get_stats
Get statistics for retained log history.

No parameters.

**Returns:** `{success, total, source, logs, warnings, errors, exceptions, asserts, complete, dropped, truncated}`. Check `truncated` before using a zero error count as evidence of a clean run.

### console_set_collapse
Set console log collapse mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `enabled` | bool | Yes | - | Enable or disable collapse mode |

**Returns:** `{ success, setting, enabled }`

### console_set_clear_on_play
Set clear on play mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `enabled` | bool | Yes | - | Enable or disable clear on play |

**Returns:** `{ success, setting, enabled }`

## Examples

Page through errors with stack traces. The first response fixes a snapshot; later pages with its `nextCursor` do not admit new logs:

```bash
u="http://localhost:<port>/skill"; p="expectProject=<project>"
curl -s "$u/console_get_logs?$p" -d '{"type":"Error","includeStackTrace":true}'
curl -s "$u/console_get_logs?$p" -d '{"type":"Error","includeStackTrace":true,"cursor":"<nextCursor from the previous response>"}'
```

Occurrence-level evidence in a time window, without collapsing duplicates:

```bash
curl -s "$u/console_get_logs?$p" -d '{"collapse":false,"fromUtc":"2026-10-10T00:00:00Z","toUtc":"2026-10-11T00:00:00Z"}'
```

Write messages:

```bash
curl -s "$u/console_log?$p" -d '{"message":"AI Agent: Task completed","type":"Log"}'
curl -s "$u/console_log?$p" -d '{"message":"AI Agent: Performance issue detected","type":"Warning"}'
```

Clear invalidates every old cursor and deletes retained history. A capture session is optional: it limits reads, exports and stats to records created after its start, without deleting background history.

```bash
curl -s "$u/console_start_capture?$p" -d '{}'
curl -s "$u/console_clear?$p" -d '{}'
curl -s "$u/console_stop_capture?$p" -d '{}'
```
