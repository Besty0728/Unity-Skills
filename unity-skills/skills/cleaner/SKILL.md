---
name: unity-cleaner
description: "Project cleanup utilities. Use when users want to find unused assets, duplicate files, or clean up the project. Triggers: unused, duplicate, cleanup, optimize, dead code, orphan, Unity清理, Unity未使用, 重复文件."
---

# Unity Cleaner Skills

> **Safety**: All delete operations default to `dryRun=true`. Set `dryRun=false` to actually delete.

## Skills Overview

| Skill | Description |
|-------|-------------|
| `cleaner_find_unused_assets` | Find assets not referenced by others |
| `cleaner_find_duplicates` | Find duplicate files by content hash |
| `cleaner_find_missing_references` | Find missing scripts/asset references |
| `cleaner_delete_assets` | Delete assets (with dryRun protection) |
| `cleaner_get_asset_usage` | Find what references a specific asset |
| `cleaner_find_empty_folders` | Find empty folders in the project |
| `cleaner_find_large_assets` | Find largest assets by file size |
| `cleaner_delete_empty_folders` | Delete all empty folders |
| `cleaner_fix_missing_scripts` | Remove missing script components from GameObjects |
| `cleaner_get_dependency_tree` | Get dependency tree for an asset |

---

## Skills

### cleaner_find_unused_assets
Find potentially unused assets of a specific type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | "Material" | Asset type filter |
| `searchPath` | string | No | "Assets" | Search path |
| `limit` | int | No | 100 | Max results |

**Returns**: `{success, assetType, potentiallyUnusedCount, assets: [{path, name, type, sizeBytes}]}`

```python
# Find unused materials
result = call_skill("cleaner_find_unused_assets", assetType="Material")

# Find unused textures in a specific folder
result = call_skill("cleaner_find_unused_assets", 
    assetType="Texture2D", searchPath="Assets/Textures")
```

### cleaner_find_duplicates
Find duplicate files by MD5 hash.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | "Texture2D" | Asset type |
| `searchPath` | string | No | "Assets" | Search path |
| `limit` | int | No | 50 | Max groups |

**Returns**: `{success, duplicateGroupCount, totalWastedBytes, totalWastedMB, groups: [{count, sizeBytes, wastedBytes, files}]}`

```python
# Find duplicate textures
result = call_skill("cleaner_find_duplicates", assetType="Texture2D")
print(f"Wasted space: {result['totalWastedMB']:.2f} MB")
```

### cleaner_find_missing_references
Find components with missing scripts or null references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeInactive` | bool | No | true | Include inactive objects |

**Returns**: `{success, issueCount, missingScripts, missingReferences, issues: [{type, gameObject, path, ...}]}`

```python
# Scan for all missing references
result = call_skill("cleaner_find_missing_references")
print(f"Missing scripts: {result['missingScripts']}")
print(f"Missing references: {result['missingReferences']}")
```

### cleaner_delete_assets
Delete specified assets with **two-step confirmation**.

> ⚠️ **Safety First**: Deletion requires TWO calls - first preview, then confirm.

**Step 1 - Preview** (no confirmToken):

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `paths` | string[] | Yes | Asset paths to delete |

**Returns**: `{action: "preview", confirmToken, assetsToDelete, message}`

**Step 2 - Confirm** (with confirmToken):

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `confirmToken` | string | Yes | Token from preview step |

**Returns**: `{action: "deleted", deletedCount, results}`

```python
# Step 1: Preview what will be deleted
preview = call_skill("cleaner_delete_assets", 
    paths=["Assets/Unused/mat1.mat", "Assets/Unused/mat2.mat"])
print(preview['message'])  # Shows: "⚠️ PREVIEW ONLY - 2 assets will be deleted..."
print(preview['assetsToDelete'])  # Full list with sizes

# Step 2: Confirm deletion using token (expires in 5 minutes)
if input("Proceed? (y/n): ") == 'y':
    result = call_skill("cleaner_delete_assets", 
        confirmToken=preview['confirmToken'])
    print(result['message'])  # "Successfully deleted 2 assets"
```

### cleaner_get_asset_usage
Find what objects reference a specific asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path |
| `limit` | int | No | Max results (default 50) |

**Returns**: `{success, asset, usedByCount, usedBy: [{path, name, type}]}`

```python
# Check what uses a texture
result = call_skill("cleaner_get_asset_usage",
    assetPath="Assets/Textures/player.png")
```

### `cleaner_find_empty_folders`
Find empty folders in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchPath` | string | No | "Assets" | Search path |

**Returns:** `{ success, count, folders }`

```python
# Find empty folders
result = call_skill("cleaner_find_empty_folders")
print(f"Found {result['count']} empty folders")
```

### `cleaner_find_large_assets`
Find largest assets by file size.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchPath` | string | No | "Assets" | Search path |
| `limit` | int | No | 20 | Max results |
| `minSizeBytes` | long | No | 0 | Minimum file size in bytes |

**Returns:** `{ success, count, assets: [{ path, sizeBytes, sizeMB }] }`

```python
# Find top 10 largest assets over 1 MB
result = call_skill("cleaner_find_large_assets", limit=10, minSizeBytes=1048576)
for a in result['assets']:
    print(f"{a['sizeMB']:.2f} MB - {a['path']}")
```

### `cleaner_delete_empty_folders`
Delete all empty folders.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchPath` | string | No | "Assets" | Search path |

**Returns:** `{ success, deleted, total }`

```python
# Delete all empty folders
result = call_skill("cleaner_delete_empty_folders")
print(f"Deleted {result['deleted']} of {result['total']} empty folders")
```

### `cleaner_fix_missing_scripts`
Remove missing script components from GameObjects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeInactive` | bool | No | true | Include inactive objects |

**Returns:** `{ success, removedComponents }`

```python
# Remove all missing script components
result = call_skill("cleaner_fix_missing_scripts")
print(f"Removed {result['removedComponents']} missing script components")
```

### `cleaner_get_dependency_tree`
Get dependency tree for an asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetPath` | string | Yes | - | Asset path |
| `recursive` | bool | No | true | Recursively resolve dependencies |

**Returns:** `{ success, assetPath, dependencyCount, dependencies: [{ path, type }] }`

```python
# Get full dependency tree for a prefab
result = call_skill("cleaner_get_dependency_tree",
    assetPath="Assets/Prefabs/Player.prefab")
print(f"Dependencies: {result['dependencyCount']}")
```

---

## Example Workflow: Clean Project

```python
import unity_skills

# 1. Find all missing references and fix them
missing = unity_skills.call_skill("cleaner_find_missing_references")
for issue in missing['issues']:
    if issue['type'] == 'MissingScript':
        print(f"⚠️ Missing script on: {issue['path']}")

# 2. Find duplicate textures
dupes = unity_skills.call_skill("cleaner_find_duplicates", assetType="Texture2D")
if dupes['totalWastedMB'] > 10:
    print(f"🗑️ {dupes['totalWastedMB']:.1f} MB wasted on duplicates")

# 3. Find unused materials
unused = unity_skills.call_skill("cleaner_find_unused_assets", assetType="Material")
print(f"📦 {unused['potentiallyUnusedCount']} potentially unused materials")

# 4. Preview cleanup
paths_to_delete = [a['path'] for a in unused['assets'][:5]]
preview = unity_skills.call_skill("cleaner_delete_assets", 
    paths=paths_to_delete, dryRun=True)
print(f"Would free: {preview['totalMB']:.2f} MB")
```
