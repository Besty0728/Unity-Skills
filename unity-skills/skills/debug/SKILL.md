---
name: unity-debug
description: "Debug and diagnostics. Use when users want to check compilation errors, get system info, or debug issues. Triggers: debug, error, compilation, recompile, system info, diagnostics, Unity调试, Unity编译错误, Unity诊断."
---

# Debug Skills

Debug utilities for error checking and diagnostics.

## Skills

### `debug_get_logs`
Get console logs filtered by type and content.
**Parameters:**
- `type` (string, optional): Filter by type (Error/Warning/Log). Default: Error.
- `filter` (string, optional): Filter by content.
- `limit` (int, optional): Max entries. Default: 50.

### `debug_get_errors`
Get only active errors and exceptions from console.
**Parameters:**
- `limit` (int, optional): Max entries. Default: 50.

### `debug_check_compilation`
Check if there are any compilation errors.
**Parameters:** None.

### `debug_force_recompile`
Force Unity to recompile all scripts.
**Parameters:** None.

### `debug_get_system_info`
Get system and Unity environment information.
**Parameters:** None.

### `debug_get_stack_trace`
Get stack trace for a log entry by index.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| entryIndex | int | Yes | - | Index of the log entry to retrieve stack trace for |

**Returns:** `{ index, message, stackTrace }`

### `debug_get_assembly_info`
Get project assembly information.

**Parameters:** None.

**Returns:** `{ success, count, assemblies }`

### `debug_get_defines`
Get scripting define symbols for current platform.

**Parameters:** None.

**Returns:** `{ success, buildTargetGroup, defines }`

### `debug_set_defines`
Set scripting define symbols for current platform.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| defines | string | Yes | - | Scripting define symbols to set |

**Returns:** `{ success, buildTargetGroup, defines, serverAvailability }`

### `debug_get_memory_info`
Get memory usage information.

**Parameters:** None.

**Returns:** `{ success, totalAllocatedMB, totalReservedMB, totalUnusedReservedMB, monoUsedSizeMB, monoHeapSizeMB }`
