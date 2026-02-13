---
name: unity-profiler
description: "Performance profiling. Use when users want to get FPS, memory usage, or performance statistics. Triggers: profiler, performance, FPS, memory, stats, benchmark, Unity性能, Unity帧率, Unity内存."
---

# Profiler Skills

Get performance statistics.

## Skills

### `profiler_get_stats`
Get performance statistics (FPS, Memory, Batches).
**Parameters:** None.

**Returns:**
```json
{
  "fps": 60.0,
  "triangles": 1500,
  "batches": 12,
  "memory": { "totalAllocatedMB": 256.5, ... }
}
```
