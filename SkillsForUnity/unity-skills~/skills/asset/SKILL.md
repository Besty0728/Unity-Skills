---
name: unity-asset
description: "Unity asset management. Use when users want to import, move, delete, duplicate, or organize project assets. Triggers: asset, import, export, folder, file, resource, AssetDatabase, Unity资源, 资产, Unity导入."
---

# Unity Asset Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ assets.

## Guardrails

**Mode**: Semi-Auto (available by default)

**DO NOT** (common hallucinations):
- `asset_create` does not exist → use `asset_create_folder` (folders), `material_create` (materials), `script_create` (scripts)
- `asset_rename` does not exist → use `asset_move` with new path
- `asset_search` does not exist → use `asset_find` with searchFilter syntax (e.g. `t:Texture2D player`)
- `asset_copy` does not exist → use `asset_duplicate`

**Routing**:
- For texture/model/audio import settings → use `importer` module (Full-Auto)
- For material creation → use `material` module (Full-Auto)
- For script creation → use `script` module

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

`items` currently expects a JSON string, not a native array.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, sourcePath, destinationPath}]}`

```python
import json

unity_skills.call_skill("asset_import_batch", items=json.dumps([
    {"sourcePath": "C:/Downloads/tex1.png", "destinationPath": "Assets/Textures/tex1.png"},
    {"sourcePath": "C:/Downloads/tex2.png", "destinationPath": "Assets/Textures/tex2.png"}
]))
```

### asset_delete
Delete an asset from the project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path to delete |

### asset_delete_batch
Delete multiple assets.

`items` currently expects a JSON string, not a native array.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, path}]}`

```python
import json

unity_skills.call_skill("asset_delete_batch", items=json.dumps([
    {"path": "Assets/Textures/old1.png"},
    {"path": "Assets/Textures/old2.png"}
]))
```

### asset_move
Move or rename an asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourcePath` | string | Yes | Current asset path |
| `destinationPath` | string | Yes | New path/name |

### asset_move_batch
Move multiple assets.

`items` currently expects a JSON string, not a native array.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, sourcePath, destinationPath}]}`

```python
import json

unity_skills.call_skill("asset_move_batch", items=json.dumps([
    {"sourcePath": "Assets/Old/mat1.mat", "destinationPath": "Assets/New/mat1.mat"},
    {"sourcePath": "Assets/Old/mat2.mat", "destinationPath": "Assets/New/mat2.mat"}
]))
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
| `limit` | int | No | 50 | Max results to return |
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
| `searchFilter` | string | No | AssetDatabase search filter (default `*`) |
| `folder` | string | No | Folder root to search (default `Assets`) |
| `limit` | int | No | Max assets to reimport (default `100`) |

### asset_set_labels
Set labels on an asset (overwrites existing labels).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path |
| `labels` | string | Yes | Comma-separated labels (e.g. `"ui,icon,hud"`). Empty entries are dropped |

**Returns**: `{success, assetPath, labels: [...]}`

### asset_get_labels
Get the labels currently attached to an asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path |

**Returns**: `{success, assetPath, labels: [...]}`

---

## Minimal Example

```python
import unity_skills

# GOOD: 1 API call instead of 4
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

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
