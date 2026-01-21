---
name: unity-validation
description: Validate and clean up Unity projects - find missing scripts, unused assets, and issues via REST API
---

# Unity Validation Skills

Maintain project health - find problems, clean up, and validate your Unity project.

## Capabilities

- Validate scene for common issues
- Find missing script references
- Clean up empty folders
- Find potentially unused assets
- Check texture sizes for optimization
- Get project structure overview
- Fix missing scripts automatically

## Skills Reference

| Skill | Description |
|-------|-------------|
| `validate_scene` | Comprehensive scene validation |
| `validate_find_missing_scripts` | Find objects with missing scripts |
| `validate_cleanup_empty_folders` | Remove empty folders |
| `validate_find_unused_assets` | Find potentially unused assets |
| `validate_texture_sizes` | Check texture sizes |
| `validate_project_structure` | Get project overview |
| `validate_fix_missing_scripts` | Remove missing script components |

## Parameters

### validate_scene

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `checkMissingScripts` | bool | No | true | Check for missing scripts |
| `checkMissingPrefabs` | bool | No | true | Check for missing prefabs |
| `checkDuplicateNames` | bool | No | false | Check duplicate names |

### validate_find_missing_scripts

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchInPrefabs` | bool | No | false | Also check prefab assets |

### validate_cleanup_empty_folders

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `dryRun` | bool | No | true | Preview only, don't delete |

### validate_find_unused_assets

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | null | Filter: Texture/Material/Prefab/etc |
| `limit` | int | No | 100 | Max results |

### validate_texture_sizes

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxRecommendedSize` | int | No | 2048 | Warn if larger |
| `limit` | int | No | 50 | Max results |

### validate_project_structure

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `rootPath` | string | No | "Assets" | Starting folder |
| `maxDepth` | int | No | 3 | Max folder depth |

### validate_fix_missing_scripts

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `dryRun` | bool | No | true | Preview only, don't remove |

## Example Usage

```python
import unity_skills

# Comprehensive scene validation
result = unity_skills.call_skill("validate_scene",
    checkMissingScripts=True,
    checkMissingPrefabs=True,
    checkDuplicateNames=True
)
print(f"Issues found: {result['result']['totalIssues']}")

# Find missing scripts
missing = unity_skills.call_skill("validate_find_missing_scripts",
    searchInPrefabs=True
)
for obj in missing['result']['objectsWithMissingScripts']:
    print(f"Missing script on: {obj['path']}")

# Preview empty folder cleanup
preview = unity_skills.call_skill("validate_cleanup_empty_folders",
    rootPath="Assets",
    dryRun=True
)
print(f"Would delete: {len(preview['result']['foldersToDelete'])} folders")

# Actually clean up (set dryRun=False)
unity_skills.call_skill("validate_cleanup_empty_folders",
    rootPath="Assets",
    dryRun=False
)

# Find large textures
textures = unity_skills.call_skill("validate_texture_sizes",
    maxRecommendedSize=1024
)
for tex in textures['result']['oversizedTextures']:
    print(f"{tex['path']}: {tex['width']}x{tex['height']}")

# Find unused materials
unused = unity_skills.call_skill("validate_find_unused_assets",
    assetType="Material"
)

# Get project overview
structure = unity_skills.call_skill("validate_project_structure",
    maxDepth=2
)

# Fix missing scripts (preview first)
preview = unity_skills.call_skill("validate_fix_missing_scripts",
    dryRun=True
)
print(f"Would fix {preview['result']['totalFixed']} objects")

# Actually fix
unity_skills.call_skill("validate_fix_missing_scripts",
    dryRun=False
)
```

## Response Format

### validate_scene Response

```json
{
  "status": "success",
  "skill": "validate_scene",
  "result": {
    "success": true,
    "sceneName": "MainScene",
    "totalIssues": 3,
    "missingScripts": [
      {"name": "Player", "path": "/Player", "missingCount": 1}
    ],
    "missingPrefabs": [],
    "duplicateNames": [
      {"name": "Cube", "count": 5}
    ]
  }
}
```

### validate_texture_sizes Response

```json
{
  "result": {
    "success": true,
    "totalChecked": 45,
    "oversizedCount": 3,
    "oversizedTextures": [
      {
        "path": "Assets/Textures/hero.png",
        "width": 4096,
        "height": 4096,
        "recommendation": "Consider 2048x2048 for mobile"
      }
    ]
  }
}
```

### validate_project_structure Response

```json
{
  "result": {
    "success": true,
    "structure": {
      "Assets": {
        "Scripts": {"fileCount": 25},
        "Prefabs": {"fileCount": 12},
        "Materials": {"fileCount": 8},
        "Textures": {"fileCount": 45}
      }
    },
    "summary": {
      "totalFolders": 15,
      "totalAssets": 156
    }
  }
}
```

## Common Validation Workflows

### Pre-Build Check
```python
# Run before building
scene_result = unity_skills.call_skill("validate_scene")
texture_result = unity_skills.call_skill("validate_texture_sizes", maxRecommendedSize=2048)

if scene_result['result']['totalIssues'] > 0:
    print("⚠️ Scene has issues - fix before build")
```

### Project Cleanup
```python
# 1. Find and fix missing scripts
unity_skills.call_skill("validate_fix_missing_scripts", dryRun=False)

# 2. Clean empty folders
unity_skills.call_skill("validate_cleanup_empty_folders", dryRun=False)

# 3. Review unused assets (manual review recommended)
unused = unity_skills.call_skill("validate_find_unused_assets")
```

## Best Practices

1. Always use `dryRun=True` first to preview changes
2. Run validation before major builds
3. Review unused assets manually before deletion
4. Keep texture sizes appropriate for target platform
5. Fix missing scripts before they cause runtime errors
6. Regular cleanup prevents project bloat
