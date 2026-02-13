---
name: unity-asset
description: "Unity asset management. Use when users want to import, move, delete, duplicate, or organize project assets. Triggers: asset, import, export, folder, file, resource, AssetDatabase, Unity资源, 资产, Unity导入."
---

# Unity Asset Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ assets.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `asset_import` | `asset_import_batch` | Importing 2+ files |
| `asset_delete` | `asset_delete_batch` | Deleting 2+ assets |
| `asset_move` | `asset_move_batch` | Moving 2+ assets |

**No batch needed**:
- `asset_duplicate` - Duplicate single asset
- `asset_find` - Search assets (returns list)
- `asset_create_folder` - Create folder
- `asset_refresh` - Refresh AssetDatabase
- `asset_get_info` - Get asset information
- `asset_reimport` - Force reimport asset
- `asset_reimport_batch` - Reimport multiple assets

---

## Skills

### asset_import
Import an external file into the project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourcePath` | string | Yes | External file path |
| `destinationPath` | string | Yes | Project destination |

### asset_import_batch
Import multiple external files.

```python
unity_skills.call_skill("asset_import_batch", items=[
    {"sourcePath": "C:/Downloads/tex1.png", "destinationPath": "Assets/Textures/tex1.png"},
    {"sourcePath": "C:/Downloads/tex2.png", "destinationPath": "Assets/Textures/tex2.png"}
])
```

### asset_delete
Delete an asset from the project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path to delete |

### asset_delete_batch
Delete multiple assets.

```python
unity_skills.call_skill("asset_delete_batch", items=[
    {"path": "Assets/Textures/old1.png"},
    {"path": "Assets/Textures/old2.png"}
])
```

### asset_move
Move or rename an asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourcePath` | string | Yes | Current asset path |
| `destinationPath` | string | Yes | New path/name |

### asset_move_batch
Move multiple assets.

```python
unity_skills.call_skill("asset_move_batch", items=[
    {"sourcePath": "Assets/Old/mat1.mat", "destinationPath": "Assets/New/mat1.mat"},
    {"sourcePath": "Assets/Old/mat2.mat", "destinationPath": "Assets/New/mat2.mat"}
])
```

### asset_duplicate
Duplicate an asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset to duplicate |

### asset_find
Find assets by search filter.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchFilter` | string | Yes | - | Search query |
| `searchInFolders` | string | No | "Assets" | Folder to search |
| `limit` | int | No | 100 | Max results |

**Search Filter Syntax**:
| Filter | Example | Description |
|--------|---------|-------------|
| `t:Type` | `t:Texture2D` | By type |
| `l:Label` | `l:Architecture` | By label |
| `name` | `player` | By name |
| Combined | `t:Material player` | Multiple filters |

**Returns**: `{success, count, assets: [path]}`

### asset_create_folder
Create a folder in the project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `folderPath` | string | Yes | Full folder path |

### asset_refresh
Refresh the AssetDatabase after external changes.

No parameters.

### asset_get_info
Get information about an asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path |

### asset_reimport
Force reimport of an asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path to reimport |

### asset_reimport_batch
Reimport multiple assets matching a pattern.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pattern` | string | Yes | Search pattern (e.g., "Assets/Textures/*.png") |

---

## Example: Efficient Asset Organization

```python
import unity_skills

# BAD: 4 API calls
unity_skills.call_skill("asset_move", sourcePath="Assets/tex1.png", destinationPath="Assets/Textures/tex1.png")
unity_skills.call_skill("asset_move", sourcePath="Assets/tex2.png", destinationPath="Assets/Textures/tex2.png")
unity_skills.call_skill("asset_move", sourcePath="Assets/tex3.png", destinationPath="Assets/Textures/tex3.png")
unity_skills.call_skill("asset_move", sourcePath="Assets/tex4.png", destinationPath="Assets/Textures/tex4.png")

# GOOD: 1 API call
unity_skills.call_skill("asset_move_batch", items=[
    {"sourcePath": "Assets/tex1.png", "destinationPath": "Assets/Textures/tex1.png"},
    {"sourcePath": "Assets/tex2.png", "destinationPath": "Assets/Textures/tex2.png"},
    {"sourcePath": "Assets/tex3.png", "destinationPath": "Assets/Textures/tex3.png"},
    {"sourcePath": "Assets/tex4.png", "destinationPath": "Assets/Textures/tex4.png"}
])
```

## Best Practices

1. Organize assets in logical folders
2. Use consistent naming conventions
3. Refresh after external file changes
4. Use search filters for efficiency
5. Backup before bulk delete operations
