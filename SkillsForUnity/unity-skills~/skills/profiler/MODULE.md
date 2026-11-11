# Unity Profiler Skills

Instant, read-only snapshots of runtime performance and memory. Parameters come from the schema (`GET /skills/schema?category=Profiler&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Checking frame rate, draw calls or batches at this moment
- Reading memory use by category: heap, textures, meshes, materials, audio, objects
- Finding the largest runtime memory consumers

## Rules
- Every skill is `SemiAuto` and read-only: it runs in every mode without a grant.
- Snapshots only. For continuous recording use the Unity Profiler window.

## Routing
- Scene-level performance hints → `scene_performance_hints` (`perception` module).
- Asset or scene optimisation → `optimization` module.
- Memory overview from the debug side → `debug_get_memory_info` (`debug` module).

## Skills

### `profiler_get_stats`
FPS, frame time, rendering counters and memory in one call.
**Parameters:** None.

**Returns:** `{ success, fps, frameTime, renderTime, triangles, vertices, batches, setPassCalls, drawCalls, dynamicBatchedDrawCalls, staticBatchedDrawCalls, instancedBatchedDrawCalls, visibleSkinnedMeshes, visibleAnimators, memory: { totalAllocatedMB, totalReservedMB, unusedReservedMB, monoHeapMB, monoUsedMB } }`

### `profiler_get_memory`
Memory overview: total allocated, reserved, and the Mono heap.
**Parameters:** None.

**Returns:** `{ success, totalAllocatedMB, totalReservedMB, unusedReservedMB, monoHeapMB, monoUsedMB }`

### `profiler_get_runtime_memory`
Top N objects by runtime memory use in the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 20 | Maximum number of objects to return |

**Returns:** `{ success, totalTrackedMB, showing, objects: [{ name, type, sizeKB }] }`

### `profiler_get_texture_memory`
Memory use of all loaded textures.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of textures to return |

**Returns:** `{ success, totalCount, totalMB, topTextures: [{ name, type, sizeKB, width, height }] }`

### `profiler_get_mesh_memory`
Memory use of all loaded meshes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of meshes to return |

**Returns:** `{ success, totalCount, totalMB, topMeshes: [{ name, sizeKB, vertices, triangles }] }`

### `profiler_get_material_memory`
Memory use of all loaded materials.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of materials to return |

**Returns:** `{ success, totalCount, totalMB, topMaterials: [{ name, shader, sizeKB }] }`

### `profiler_get_audio_memory`
Memory use of all loaded AudioClips.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of clips to return |

**Returns:** `{ success, totalCount, totalMB, topClips: [{ name, sizeKB, length, channels, frequency }] }`

### `profiler_get_object_count`
Count all loaded objects, grouped by type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| topN | int | No | 20 | Number of top types to return |

**Returns:** `{ success, totalObjects, topTypes: [{ type, count }] }`

### `profiler_get_rendering_stats`
Rendering counters: batches, triangles, vertices, draw calls and shadow casters.
**Parameters:** None.

**Returns:** `{ success, frameTime, renderTime, triangles, vertices, batches, setPassCalls, drawCalls, dynamicBatchedDrawCalls, staticBatchedDrawCalls, instancedBatchedDrawCalls, shadowCasters }`

### `profiler_get_asset_bundle_stats`
Loaded AssetBundles.
**Parameters:** None.

**Returns:** `{ success, count, bundles: [{ name, isStreamedSceneAssetBundle }] }`

**DO NOT** (common hallucinations):
- `profiler_start`, `profiler_stop` and `profiler_record` do not exist: these skills are snapshots, not recording controls.
- `profiler_analyze` and `profiler_get_fps` do not exist: use `profiler_get_stats` for FPS, batches and draw calls, or `profiler_get_memory` for heap sizes.
