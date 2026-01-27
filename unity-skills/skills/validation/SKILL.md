---
name: unity-validation
description: "Validate and clean up Unity projects - find missing scripts, unused assets, and issues."
---

# Unity Validation Skills

Maintain project health - find problems, clean up, and validate your Unity project.

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

---

## Skills

### validate_scene
Comprehensive scene validation.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `checkMissingScripts` | bool | No | true | Check for missing scripts |
| `checkMissingPrefabs` | bool | No | true | Check for missing prefabs |
| `checkDuplicateNames` | bool | No | false | Check duplicate names |

**Returns**: `{success, sceneName, totalIssues, missingScripts, missingPrefabs, duplicateNames}`

### validate_find_missing_scripts
Find objects with missing script references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchInPrefabs` | bool | No | false | Also check prefab assets |

**Returns**: `{success, count, objectsWithMissingScripts: [{name, path, missingCount}]}`

### validate_fix_missing_scripts
Remove missing script components.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `dryRun` | bool | No | true | Preview only, don't remove |

**Returns**: `{success, totalFixed, fixedObjects}`

### validate_cleanup_empty_folders
Remove empty folders from project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `dryRun` | bool | No | true | Preview only, don't delete |

**Returns**: `{success, count, foldersToDelete: [path]}`

### validate_find_unused_assets
Find potentially unused assets.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | null | Filter: Texture/Material/Prefab/etc |
| `limit` | int | No | 100 | Max results |

**Returns**: `{success, count, unusedAssets: [path]}`

### validate_texture_sizes
Check for oversized textures.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxRecommendedSize` | int | No | 2048 | Warn if larger |
| `limit` | int | No | 50 | Max results |

**Returns**: `{success, totalChecked, oversizedCount, oversizedTextures: [{path, width, height, recommendation}]}`

### validate_project_structure
Get project folder structure overview.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `maxDepth` | int | No | 3 | Max folder depth |

**Returns**: `{success, structure, summary: {totalFolders, totalAssets}}`

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
if texture_result['oversizedCount'] > 0:
    print(f"Warning: {texture_result['oversizedCount']} oversized textures")
```

### Project Cleanup
```python
import unity_skills

# 1. Preview missing scripts fix
preview = unity_skills.call_skill("validate_fix_missing_scripts", dryRun=True)
print(f"Would fix {preview['totalFixed']} objects")

# 2. Actually fix (if preview looks good)
unity_skills.call_skill("validate_fix_missing_scripts", dryRun=False)

# 3. Preview empty folder cleanup
preview = unity_skills.call_skill("validate_cleanup_empty_folders", dryRun=True)
print(f"Would delete {len(preview['foldersToDelete'])} folders")

# 4. Actually cleanup
unity_skills.call_skill("validate_cleanup_empty_folders", dryRun=False)

# 5. Review unused assets (manual review recommended)
unused = unity_skills.call_skill("validate_find_unused_assets")
for asset in unused['unusedAssets']:
    print(f"Potentially unused: {asset}")
```

## Best Practices

1. **Always use `dryRun=True` first** to preview changes
2. Run validation before major builds
3. Review unused assets manually before deletion
4. Keep texture sizes appropriate for target platform
5. Fix missing scripts before they cause runtime errors
6. Regular cleanup prevents project bloat
