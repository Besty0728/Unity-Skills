---
name: unity-optimization
description: "Project optimization utilities. Use when users want to optimize textures, meshes, or improve performance. Triggers: optimize, compression, texture size, mesh compression, performance, LOD, Unity优化, Unity压缩, Unity性能."
---

# Optimization Skills

Optimize project assets (Textures, Models).

## Skills

### `optimize_textures`
Optimize texture settings (maxSize, compression). Returns list of modified textures.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| maxTextureSize | int | No | 2048 | Max texture size |
| enableCrunch | bool | No | true | Enable crunch compression |
| compressionQuality | int | No | 50 | Compression quality 0-100 |
| filter | string | No | "" | Asset filter |

**Returns:** `{ success, count, message, modified }`

### `optimize_mesh_compression`
Set mesh compression for 3D models.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| compressionLevel | string | No | "Medium" | Compression level: Off/Low/Medium/High |
| filter | string | No | "" | Asset filter |

**Returns:** `{ success, count, compression, modified }`

### `optimize_analyze_scene`
Analyze scene for performance bottlenecks (high-poly meshes, excessive materials).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| polyThreshold | int | No | 10000 | Triangle count threshold for high-poly warning |
| materialThreshold | int | No | 5 | Material slot count threshold for excessive materials warning |

**Returns:** `{ success, totalRenderers, totalTriangles, totalMaterialSlots, issueCount, issues }`

### `optimize_find_large_assets`
Find assets exceeding a size threshold (in KB).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| thresholdKB | int | No | 1024 | Size threshold in KB |
| assetType | string | No | "" | Asset type filter (e.g. Texture2D, AudioClip) |
| limit | int | No | 50 | Maximum number of results |

**Returns:** `{ success, threshold, count, assets }`

### `optimize_set_static_flags`
Set static flags on GameObjects. flags: Everything/Nothing/BatchingStatic/OccludeeStatic/OccluderStatic/NavigationStatic/ReflectionProbeStatic

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| flags | string | No | "Everything" | Static flags to set |
| includeChildren | bool | No | false | Apply to all children recursively |

**Returns:** `{ success, gameObject, flags, affectedCount }`

### `optimize_get_static_flags`
Get static flags of a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |

**Returns:** `{ success, gameObject, flags, isStatic }`

### `optimize_audio_compression`
Batch set audio compression. compressionFormat: PCM/Vorbis/ADPCM. loadType: DecompressOnLoad/CompressedInMemory/Streaming

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| compressionFormat | string | No | "Vorbis" | Audio compression format: PCM/Vorbis/ADPCM |
| loadType | string | No | "CompressedInMemory" | Audio load type: DecompressOnLoad/CompressedInMemory/Streaming |
| quality | float | No | 0.5 | Compression quality 0.0-1.0 |
| filter | string | No | "" | Asset filter |

**Returns:** `{ success, count, compressionFormat, loadType, modified }`

### `optimize_find_duplicate_materials`
Find materials with identical shader and properties.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of duplicate groups to return |

**Returns:** `{ success, duplicateGroups, groups }`

### `optimize_analyze_overdraw`
Analyze transparent objects that may cause overdraw.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of results |

**Returns:** `{ success, transparentObjectCount, objects }`

### `optimize_set_lod_group`
Add or configure LOD Group. lodDistances: comma-separated screen-relative heights (e.g. '0.6,0.3,0.1')

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| lodDistances | string | No | "0.6,0.3,0.1" | Comma-separated screen-relative transition heights |

**Returns:** `{ success, gameObject, lodLevels, distances }`
