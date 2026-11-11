# Unity Validation Skills

Find problems in the scene and project (missing scripts and references, unused assets, oversized textures, shader errors). Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Checking broken or missing references
- Validating scene or project integrity
- Cleaning up before a build

## Rules
- Analysis skills are read-only. `validate_fix_missing_scripts` and `validate_cleanup_empty_folders` delete when `dryRun=false`; both default to `dryRun=true`, so preview first.
- Compile errors belong to `debug_check_compilation` in the [debug](../debug/MODULE.md) module.
- Unused or duplicate asset cleanup belongs to the [cleaner](../cleaner/MODULE.md) module.
- Batch fixes and previews that carry `SkillCategory.Validation` are documented in [batch](../batch/MODULE.md) (see the table below).

## Pitfalls
- The prefix is `validate_*`, not `validation_*`. `validation_run`, `validation_check`, `validation_fix` and `validation_clean` do not exist.
- The validate skills report issues; they do not fix them. Use `cleaner_fix_missing_scripts` in the [cleaner](../cleaner/MODULE.md) module to fix missing scripts.

## Skills

### validate_scene
Comprehensive scene validation.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `checkMissingScripts` | bool | No | true | Check for missing scripts |
| `checkMissingPrefabs` | bool | No | true | Check for missing prefabs |
| `checkDuplicateNames` | bool | No | true | Check duplicate names |
| `checkEmptyGameObjects` | bool | No | false | Check empty GameObjects (no components) |

**Returns**: `{scene, totalIssues, summary: {errors, warnings, info}, issues: [{type, severity, gameObject, path, message, count}]}`

### validate_find_missing_scripts
Find objects with missing script references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchInPrefabs` | bool | No | true | Also check prefab assets |

**Returns**: `{totalFound, objects: [{source, gameObject, path, missingCount, prefabPath?}]}` (`prefabPath` only present when `source="Prefab"`)

### validate_fix_missing_scripts
Remove missing script components.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `dryRun` | bool | No | true | Preview only, don't remove |

**Returns**: `{success, dryRun, fixedCount, message, objects: [{gameObject, path, missingCount}]}`

### validate_cleanup_empty_folders
Remove empty folders from the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `dryRun` | bool | No | true | Preview only, don't delete |

**Returns**: `{success, dryRun, emptyFolderCount, folders, message}`

### validate_find_unused_assets
Find potentially unused assets.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | "Material" | Filter: Texture, Material, Prefab, etc. |
| `limit` | int | No | 100 | Max results |

**Returns**: `{assetType, potentiallyUnusedCount, note, assets}`. `note` warns that assets may still be used through scripts or `Resources.Load`.

### validate_texture_sizes
Check for oversized textures.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxRecommendedSize` | int | No | 2048 | Warn if larger |
| `limit` | int | No | 50 | Max results |

**Returns**: `{maxRecommendedSize, largeTextureCount, textures: [{path, name, width, height, maxTextureSize, format, recommendation}]}`

### validate_project_structure
Get a project folder structure overview.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `maxDepth` | int | No | 2 | Max folder depth |

**Returns**: `{success, rootPath, assetCounts, structure}`

### validate_missing_references
Find null or missing object references on components in the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 50 | Max results |

**Returns**: `{success, count, issues: [{gameObject, path, component, property}]}`

### validate_mesh_collider_convex
Find non-convex MeshColliders (a performance issue).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 50 | Max results |

**Returns**: `{success, count, nonConvexColliders: [{gameObject, path, vertexCount}]}`

### validate_shader_errors
Find shaders with compilation errors.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 50 | Max results |

**Returns**: `{success, count, shaders: [{name, path, errorCount}]}`

## Batch-owned validation skills

Six more skills carry `SkillCategory.Validation` but are documented in [batch](../batch/MODULE.md), so `GET /skills/recommend?category=validation` returns them with the ten above. Each is a preview that you commit with `batch_execute(confirmToken)`, except `batch_validate_scene_objects`, a read-only report.

| Skill | Description |
|-------|-------------|
| `batch_validate_scene_objects` | Analyze scene objects for missing scripts, missing references, duplicate names and empty objects |
| `batch_fix_missing_scripts` | Preview batch removal of missing scripts |
| `batch_cleanup_temp_objects` | Preview deleting temporary helper objects by common temp-name patterns |
| `batch_standardize_naming` | Preview standardizing names by trimming whitespace and normalizing separators |
| `batch_set_render_layer` | Preview setting GameObject layers in batch |
| `batch_replace_material` | Preview replacing materials in batch |

## Workflow

1. Run `validate_scene` and `validate_find_missing_scripts` to see what is broken.
2. Preview a fix: `POST /skill/validate_fix_missing_scripts?expectProject=<project>` with `{"dryRun":true}`.
3. Run it with `{"dryRun":false}` only after the preview looks right.
