---
name: unity-validation
description: Validate project and scene health plus cleanup — find broken references, missing scripts, and other integrity issues. Use when checking for broken or missing references, validating scene/project integrity, or cleaning up issues before a build, even if the user just says "检查引用" or "有没有丢失". 校验项目与场景健康度并清理(查找断裂引用、丢失脚本及其他完整性问题);当用户要检查断裂或丢失引用、校验场景/项目完整性、或在构建前清理问题时使用。
---

# Unity Validation Skills

Maintain project health - find problems, clean up, and validate your Unity project.

## Operating Mode

- **Approval**: 只读分析 skill（`validate_scene` / `validate_find_missing_scripts` / `validate_find_unused_assets` / `validate_texture_sizes` / `validate_project_structure` / `validate_missing_references` / `validate_mesh_collider_convex` / `validate_shader_errors`，标 `SkillMode.SemiAuto`）直接执行；含 Delete 的 skill（`validate_cleanup_empty_folders` 标 `Analyze | Delete`、`validate_fix_missing_scripts` 标 `Execute | Delete`，默认 `SkillMode.FullAuto`）需用户 grant。
- **Auto / Bypass**: 直接执行。
- **本模块含 Delete 类高危 skill**：`validate_cleanup_empty_folders` / `validate_fix_missing_scripts` 一旦 `dryRun=false` 即真删；它们在 Approval / Auto 下被 `IsForbiddenInSemi` 自动拦截，**仅 Bypass 或 Allowlist 命中可执行**。**强烈建议先用 `dryRun=true` 预览**。

**DO NOT** (common hallucinations):
- Validation skill routes use the `validate_*` prefix, not `validation_*`
- `validation_run` / `validation_check` do not exist → use specific skills such as `validate_scene`, `validate_project_structure`, `validate_missing_references`
- `validation_fix` does not exist → validation skills report issues; use other modules to fix them
- `validation_clean` does not exist → use `cleaner` module for cleanup operations

**Routing**:
- For unused/duplicate asset cleanup → use `cleaner` module
- For missing script fix → `cleaner_fix_missing_scripts` (cleaner module)
- For compile errors → `debug_check_compilation` (debug module)

## Skills Overview

| Skill | Description |
|-------|-------------|
| `validate_scene` | Comprehensive scene validation |
| `validate_find_missing_scripts` | Find objects with missing scripts |
| `validate_fix_missing_scripts` | Remove missing script components |
| `validate_cleanup_empty_folders` | Remove empty folders |
| `validate_find_unused_assets` | Find potentially unused assets |
| `validate_texture_sizes` | Check texture sizes |
| `validate_project_structure` | Get project overview |
| `validate_missing_references` | Find null/missing object references on components |
| `validate_mesh_collider_convex` | Find non-convex MeshColliders |
| `validate_shader_errors` | Find shaders with compilation errors |

---

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
Remove empty folders from project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `dryRun` | bool | No | true | Preview only, don't delete |

**Returns**: `{ success, dryRun, emptyFolderCount, folders, message }`

### validate_find_unused_assets
Find potentially unused assets.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | "Material" | Filter: Texture/Material/Prefab/etc |
| `limit` | int | No | 100 | Max results |

**Returns**: `{ success, assetType, potentiallyUnusedCount, assets }`

### validate_texture_sizes
Check for oversized textures.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxRecommendedSize` | int | No | 2048 | Warn if larger |
| `limit` | int | No | 50 | Max results |

**Returns**: `{maxRecommendedSize, largeTextureCount, textures: [{path, name, width, height, maxTextureSize, format, recommendation}]}`

### validate_project_structure
Get project folder structure overview.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `maxDepth` | int | No | 2 | Max folder depth |

**Returns**: `{ success, rootPath, assetCounts, structure }`

### `validate_missing_references`
Find null/missing object references on components in the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 50 | Max results |

**Returns**: `{ success, count, issues: [{ gameObject, path, component, property }] }`

### `validate_mesh_collider_convex`
Find non-convex MeshColliders (potential performance issue).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 50 | Max results |

**Returns**: `{ success, count, nonConvexColliders: [{ gameObject, path, vertexCount }] }`

### `validate_shader_errors`
Find shaders with compilation errors.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 50 | Max results |

**Returns**: `{ success, count, shaders: [{ name, path, errorCount }] }`

---

## Common Workflows

### Pre-Build Check
```python
import unity_skills

# Validate scene
scene_result = unity_skills.call_skill("validate_scene")
if scene_result['totalIssues'] > 0:
    print(f"Warning: {scene_result['totalIssues']} issues found")

# Check texture sizes
texture_result = unity_skills.call_skill("validate_texture_sizes", maxRecommendedSize=2048)
if texture_result['largeTextureCount'] > 0:
    print(f"Warning: {texture_result['largeTextureCount']} oversized textures")
```

### Project Cleanup
```python
import unity_skills

# 1. Preview missing scripts fix
preview = unity_skills.call_skill("validate_fix_missing_scripts", dryRun=True)
print(f"Would fix {preview['fixedCount']} objects")

# 2. Actually fix (if preview looks good)
unity_skills.call_skill("validate_fix_missing_scripts", dryRun=False)

# 3. Preview empty folder cleanup
preview = unity_skills.call_skill("validate_cleanup_empty_folders", dryRun=True)
print(f"Would delete {len(preview['folders'])} folders")

# 4. Actually cleanup
unity_skills.call_skill("validate_cleanup_empty_folders", dryRun=False)

# 5. Review unused assets (manual review recommended)
unused = unity_skills.call_skill("validate_find_unused_assets")
for asset in unused['assets']:
    print(f"Potentially unused: {asset}")
```

## Best Practices

1. **Always use `dryRun=True` first** to preview changes
2. Run validation before major builds
3. Review unused assets manually before deletion
4. Keep texture sizes appropriate for target platform
5. Fix missing scripts before they cause runtime errors
6. Regular cleanup prevents project bloat

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
