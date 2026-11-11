# Unity Optimization Skills

Analyse and optimise project assets (textures, models, audio) and scene objects (static flags, LOD groups). Parameters come from the schema (`GET /skills/schema?category=Optimization&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Reducing build size or batch-compressing assets
- Finding large assets, duplicate materials or overdraw
- Analysing scene polygon and material usage
- Setting static flags or LOD groups

## Rules
- Analysis skills are read-only and run in every mode; the write skills need a grant under Approval.
- Write skills re-import assets or change components, without a dry-run rollback. Check `filter` or `assetType` before a batch run, and review the returned `modified` list.
- No skill here deletes or triggers a domain reload.

## Pitfalls
- `optimize_textures` `limit` is a cap on modified textures; 0 (default) means no cap.
- `optimize_set_static_flags`: `Everything` and `Nothing` are aliases added by this API, not enum members. `Everything` is the OR of the five non-deprecated members, so `NavigationStatic` and `OffMeshLinkGeneration` must be named explicitly. A flag name that does not resolve fails the whole call.

## Routing
- Profiler metrics → `profiler` module.
- Performance review guidance → the `performance` advisory module.

## Skills

### `optimize_textures`
Optimize texture settings (maxSize, compression). Returns the list of modified textures.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| maxTextureSize | int | No | 2048 | Max texture size |
| enableCrunch | bool | No | true | Enable crunch compression |
| compressionQuality | int | No | 50 | Compression quality 0-100 |
| filter | string | No | "" | Asset search filter |
| limit | int | No | 0 | Maximum textures to modify; 0 = no limit |

**Returns:** `{ success, count, message, modified }`

### `optimize_mesh_compression`
Set mesh compression for 3D models.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| compressionLevel | string | No | "Medium" | Compression level: Off/Low/Medium/High |
| filter | string | No | "" | Asset filter |

**Returns:** `{ success, count, compression, modified }`

### `optimize_analyze_scene`
Analyze the scene for performance bottlenecks (high-poly meshes, excessive materials).

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
Set static flags on a GameObject. `flags` is comma-separated, drawn from `ContributeGI`, `OccluderStatic`, `OccludeeStatic`, `BatchingStatic`, `ReflectionProbeStatic`, plus the deprecated `NavigationStatic` and `OffMeshLinkGeneration`; the `Everything` and `Nothing` aliases are described under Pitfalls.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| flags | string | No | "Everything" | Comma-separated static flags, or the `Everything` / `Nothing` aliases |
| includeChildren | bool | No | false | Apply to all children recursively |

**Returns:** `{ success, gameObject, flags, affectedCount }`

### `optimize_get_static_flags`
Get the static flags of a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |

**Returns:** `{ success, gameObject, flags, isStatic }`

### `optimize_audio_compression`
Batch-set audio compression. `compressionFormat`: PCM, Vorbis or ADPCM. `loadType`: DecompressOnLoad, CompressedInMemory or Streaming.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| compressionFormat | string | No | "Vorbis" | PCM, Vorbis or ADPCM |
| loadType | string | No | "CompressedInMemory" | DecompressOnLoad, CompressedInMemory or Streaming |
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
Find transparent objects that may cause overdraw.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 50 | Maximum number of results |

**Returns:** `{ success, transparentObjectCount, objects }`

### `optimize_set_lod_group`
Add or configure an LOD Group. `lodDistances` is a comma-separated list of screen-relative heights, e.g. `0.6,0.3,0.1`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| lodDistances | string | No | "0.6,0.3,0.1" | Comma-separated screen-relative transition heights |

**Returns:** `{ success, gameObject, lodLevels, distances }`

**DO NOT** (common hallucinations):
- `optimize_scene` and `optimization_run` do not exist: use the specific analysis skills, e.g. `optimize_analyze_scene`, `optimize_find_large_assets`, `optimize_find_duplicate_materials`.
- `optimize_compress`, `optimize_compress_textures`, `optimize_compress_meshes` and `optimize_compress_audio` do not exist: use `optimize_textures`, `optimize_mesh_compression` or `optimize_audio_compression`.
- `optimize_set_lod` and `optimize_setup_lod` do not exist: use `optimize_set_lod_group`.
