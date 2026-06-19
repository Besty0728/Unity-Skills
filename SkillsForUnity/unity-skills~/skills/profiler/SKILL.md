---
name: unity-profiler
description: Capture read-only Unity runtime performance snapshots — sample frame timing, memory, and runtime stats without mutating anything. Use when checking runtime performance, taking a profiler snapshot, or inspecting memory/frame stats, even if the user just says "性能数据" or "看看帧率". 采集只读的 Unity 运行时性能快照(采样帧耗时、内存、运行时统计,不做任何改动);当用户要查看运行时性能、抓取 profiler 快照、或检查内存/帧率统计时使用。
---

# Profiler Skills

Get performance statistics.

## Operating Mode

- **Approval / Auto / Bypass**: 所有 skill 直接执行。本模块**全部**标 `ReadOnly = true, Mode = SkillMode.SemiAuto`，三档模式下都不需要 grant。
- **本模块不含 Delete / PlayMode / Reload / RiskLevel=high 类 skill** —— 没有 `IsForbiddenInSemi` 拦截。
- 注：本组 skill 仅做"瞬时快照"读取，不是 Profiler 录制控制；持续采样请用 Unity Profiler 窗口。

**DO NOT** (common hallucinations):
- `profiler_start` / `profiler_stop` do not exist → profiler skills are read-only snapshots, not recording controls
- `profiler_record` does not exist → use Unity Profiler window for recording
- `profiler_analyze` / `profiler_get_fps` do not exist → use `profiler_get_stats` (FPS, batches, draw calls) or `profiler_get_memory` (heap sizes)

**Routing**:
- For scene performance hints → use `perception` module's `scene_performance_hints`
- For memory info → `debug_get_memory_info` (debug module) or `profiler_get_memory` (this module)
- For optimization suggestions → use `optimization` module

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

### `profiler_get_memory`
Get memory usage overview (total allocated, reserved, mono heap).
**Parameters:** None.

**Returns:** `{ success, totalAllocatedMB, totalReservedMB, unusedReservedMB, monoHeapMB, monoUsedMB }`

### `profiler_get_runtime_memory`
Get top N objects by runtime memory usage in the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 20 | Maximum number of objects to return |

**Returns:** `{ success, totalTrackedMB, showing, objects: [{ name, type, sizeKB }] }`

### `profiler_get_texture_memory`
Get memory usage of all loaded textures.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of textures to return |

**Returns:** `{ success, totalCount, totalMB, topTextures: [{ name, type, sizeKB, width, height }] }`

### `profiler_get_mesh_memory`
Get memory usage of all loaded meshes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of meshes to return |

**Returns:** `{ success, totalCount, totalMB, topMeshes: [{ name, sizeKB, vertices, triangles }] }`

### `profiler_get_material_memory`
Get memory usage of all loaded materials.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of materials to return |

**Returns:** `{ success, totalCount, totalMB, topMaterials: [{ name, shader, sizeKB }] }`

### `profiler_get_audio_memory`
Get memory usage of all loaded AudioClips.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of clips to return |

**Returns:** `{ success, totalCount, totalMB, topClips: [{ name, sizeKB, length, channels, frequency }] }`

### `profiler_get_object_count`
Count all loaded objects grouped by type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| topN | int | No | 20 | Number of top types to return |

**Returns:** `{ success, totalObjects, topTypes: [{ type, count }] }`

### `profiler_get_rendering_stats`
Get rendering statistics (batches, triangles, vertices, etc.).
**Parameters:** None.

**Returns:** `{ success, frameTime, renderTime, triangles, vertices, batches, setPassCalls, drawCalls, dynamicBatchedDrawCalls, staticBatchedDrawCalls, instancedBatchedDrawCalls, shadowCasters }`

### `profiler_get_asset_bundle_stats`
Get information about all loaded AssetBundles.
**Parameters:** None.

**Returns:** `{ success, count, bundles: [{ name, isStreamedSceneAssetBundle }] }`

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
