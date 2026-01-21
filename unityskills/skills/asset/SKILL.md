---
name: unity-asset
description: Import, manage, and organize assets in Unity Editor via REST API
---

# Unity Asset Skills

Manage project assets - import, move, delete, and organize files in your Unity project.

## Capabilities

- Import external files into project
- Delete, move, duplicate assets
- Create folders
- Find assets by filter
- Refresh asset database
- Get asset information

## Skills Reference

| Skill | Description |
|-------|-------------|
| `asset_import` | Import external file |
| `asset_delete` | Delete an asset |
| `asset_move` | Move/rename asset |
| `asset_duplicate` | Duplicate asset |
| `asset_find` | Find assets |
| `asset_create_folder` | Create folder |
| `asset_refresh` | Refresh AssetDatabase |
| `asset_get_info` | Get asset information |

## Parameters

### asset_import

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourcePath` | string | Yes | External file path |
| `destinationPath` | string | Yes | Project destination |

### asset_delete

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path to delete |

### asset_move

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourcePath` | string | Yes | Current asset path |
| `destinationPath` | string | Yes | New path/name |

### asset_duplicate

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset to duplicate |

### asset_find

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchFilter` | string | Yes | - | Search query |
| `searchInFolders` | string | No | "Assets" | Folder to search |
| `limit` | int | No | 100 | Max results |

### asset_create_folder

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `folderPath` | string | Yes | Full folder path |

### asset_get_info

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path |

## Search Filter Syntax

| Filter | Description | Example |
|--------|-------------|---------|
| `t:Type` | By type | `t:Texture2D` |
| `l:Label` | By label | `l:Architecture` |
| `name` | By name | `player` |
| Combined | Multiple | `t:Material player` |

## Example Usage

```python
import unity_skills

# Create folder structure
unity_skills.call_skill("asset_create_folder", folderPath="Assets/Textures")
unity_skills.call_skill("asset_create_folder", folderPath="Assets/Materials")
unity_skills.call_skill("asset_create_folder", folderPath="Assets/Prefabs")

# Import external texture
unity_skills.call_skill("asset_import",
    sourcePath="C:/Downloads/hero_texture.png",
    destinationPath="Assets/Textures/hero.png"
)

# Find all textures
textures = unity_skills.call_skill("asset_find",
    searchFilter="t:Texture2D"
)

# Find materials with "metal" in name
metals = unity_skills.call_skill("asset_find",
    searchFilter="t:Material metal"
)

# Duplicate an asset
unity_skills.call_skill("asset_duplicate",
    assetPath="Assets/Materials/Red.mat"
)

# Move/rename asset
unity_skills.call_skill("asset_move",
    sourcePath="Assets/Materials/Red.mat",
    destinationPath="Assets/Materials/Player/RedMetal.mat"
)

# Get asset info
info = unity_skills.call_skill("asset_get_info",
    assetPath="Assets/Textures/hero.png"
)

# Delete unused asset
unity_skills.call_skill("asset_delete",
    assetPath="Assets/Textures/old_texture.png"
)

# Refresh after external changes
unity_skills.call_skill("asset_refresh")
```

## Response Format

```json
{
  "status": "success",
  "skill": "asset_find",
  "result": {
    "success": true,
    "count": 15,
    "assets": [
      "Assets/Textures/hero.png",
      "Assets/Textures/enemy.png",
      "Assets/Textures/ground.png"
    ]
  }
}
```

## Best Practices

1. Organize assets in logical folders
2. Use consistent naming conventions
3. Refresh after external file changes
4. Use search filters for efficiency
5. Backup before bulk delete operations
