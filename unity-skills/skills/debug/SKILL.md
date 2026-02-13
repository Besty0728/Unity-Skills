---
name: unity-debug
description: "Debug and diagnostics. Use when users want to check compilation errors, get system info, or debug issues. Triggers: debug, error, compilation, recompile, system info, diagnostics, Unity调试, Unity编译错误, Unity诊断."
---

# Debug Skills

Debug utilities for error checking and diagnostics.

## Skills

### `debug_log`
Write a debug message to the console.
**Parameters:**
- `message` (string): Message to log.
- `type` (string, optional): Log type (Log/Warning/Error). Default: Log.

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
