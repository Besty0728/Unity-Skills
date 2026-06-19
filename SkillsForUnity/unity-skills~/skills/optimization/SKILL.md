---
name: unity-optimization
description: Optimize project assets and scenes — batch texture/mesh/audio compression and analyze poly/material counts. Use when reducing build size, batch-compressing assets, or analyzing scene poly and material usage for optimization, even if the user just says "优化资源" or "包体太大". 优化工程资源与场景(批量压缩 texture/mesh/audio、分析面数/材质数);当用户要缩减包体、批量压缩资源、或分析场景面数与材质用量以优化时使用。
---

# Optimization Skills

Optimize project assets (Textures, Models).

## Operating Mode

- **Approval**: 只读分析类 skill（`optimize_analyze_scene` / `optimize_find_large_assets` / `optimize_get_static_flags` / `optimize_find_duplicate_materials` / `optimize_analyze_overdraw`，标 `SkillMode.SemiAuto`）直接执行；写型 skill（`optimize_textures` / `optimize_mesh_compression` / `optimize_audio_compression` / `optimize_set_static_flags` / `optimize_set_lod_group`，默认 `SkillMode.FullAuto`）需用户 grant，grant 后一步执行返结果。
- **Auto / Bypass**: 直接执行。
- **本模块不含 Delete / PlayMode / Reload / RiskLevel=high 类 skill** —— 写型 skill 改的是 Importer / 组件设置，会重新导入资源，但不会触发 Domain Reload，也不会被 `IsForbiddenInSemi` 拦截。批量操作前务必检查筛选范围（`filter` / `assetType`），改完无 dry-run 回滚。

**DO NOT** (common hallucinations):
- `optimize_scene` / `optimization_run` do not exist → use specific skills: `optimize_analyze_scene`, `optimize_find_large_assets`, `optimize_find_duplicate_materials`, etc.
- `optimize_compress` / `optimize_compress_textures` / `optimize_compress_meshes` / `optimize_compress_audio` do not exist → use `optimize_textures` (textures), `optimize_mesh_compression` (meshes), `optimize_audio_compression` (audio)
- `optimize_set_lod` / `optimize_setup_lod` do not exist → use `optimize_set_lod_group`
- Optimization skills are analysis + action tools — always review results before applying batch changes

**Routing**:
- For profiler metrics → use `profiler` module
- For static batching flags → `optimize_set_static_flags` (this module)
- For performance review guidance → load `performance` advisory module

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

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.