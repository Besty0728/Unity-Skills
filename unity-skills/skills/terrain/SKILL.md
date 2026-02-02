---
name: unity-terrain
description: "Create and modify Unity Terrain. Set heights, paint textures, query elevation."
---

# Unity Terrain Skills

> **Note**: Terrain operations require an existing Terrain in the scene, or use `terrain_create` to generate one.

## Skills Overview

| Skill | Description |
|-------|-------------|
| `terrain_create` | Create new Terrain with TerrainData |
| `terrain_get_info` | Get terrain size, resolution, layers |
| `terrain_get_height` | Get height at world position |
| `terrain_set_height` | Set height at normalized coords |
| `terrain_set_heights_batch` | Batch set heights in region |
| `terrain_paint_texture` | Paint texture layer at position |

---

## Skills

### terrain_create
Create a new Terrain GameObject with TerrainData asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Terrain" | Terrain name |
| `width` | int | No | 500 | Terrain width (X) |
| `length` | int | No | 500 | Terrain length (Z) |
| `height` | int | No | 100 | Max terrain height (Y) |
| `heightmapResolution` | int | No | 513 | Heightmap resolution (power of 2 + 1) |
| `x`, `y`, `z` | float | No | 0 | Position |

**Returns**: `{success, name, instanceId, terrainDataPath, size, position}`

### terrain_get_info
Get terrain information.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Terrain name |
| `instanceId` | int | No* | Instance ID |

*If neither provided, uses first terrain in scene

**Returns**: `{success, name, size, heightmapResolution, alphamapResolution, terrainLayerCount, layers}`

### terrain_get_height
Get terrain height at world position.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `worldX` | float | Yes | World X coordinate |
| `worldZ` | float | Yes | World Z coordinate |
| `name` | string | No | Terrain name |

**Returns**: `{success, worldX, worldZ, height, worldY}`

### terrain_set_height
Set height at normalized coordinates.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `normalizedX` | float | Yes | X position (0-1) |
| `normalizedZ` | float | Yes | Z position (0-1) |
| `height` | float | Yes | Height value (0-1) |
| `name` | string | No | Terrain name |

**Returns**: `{success, normalizedX, normalizedZ, height, pixelX, pixelZ}`

### terrain_set_heights_batch
⚠️ **BATCH SKILL**: Set heights in rectangular region.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `startX` | int | Yes | Start X pixel index |
| `startZ` | int | Yes | Start Z pixel index |
| `heights` | float[][] | Yes | 2D array [z][x] with values 0-1 |
| `name` | string | No | Terrain name |

**Returns**: `{success, startX, startZ, modifiedWidth, modifiedLength, totalPointsModified}`

```python
# Example: Create a 10x10 hill
heights = [[0.5 - abs(x-5)/10 - abs(z-5)/10 for x in range(10)] for z in range(10)]
call_skill("terrain_set_heights_batch", startX=50, startZ=50, heights=heights)
```

### terrain_paint_texture
Paint terrain texture layer. Requires terrain layers already configured.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `layerIndex` | int | Yes | - | Layer index to paint |
| `strength` | float | No | 1.0 | Paint strength |
| `brushSize` | int | No | 10 | Brush size in pixels |
| `name` | string | No | null | Terrain name |

**Returns**: `{success, layerIndex, layerName, centerX, centerZ}`

---

## Example Usage

```python
import unity_skills

# Create terrain
result = unity_skills.call_skill("terrain_create", 
    name="MyTerrain", width=256, length=256, height=50)

# Generate procedural heights (simple hill)
import math
heights = []
for z in range(64):
    row = []
    for x in range(64):
        # Distance from center
        dx = (x - 32) / 32
        dz = (z - 32) / 32
        h = max(0, 0.5 - math.sqrt(dx*dx + dz*dz))
        row.append(h)
    heights.append(row)

unity_skills.call_skill("terrain_set_heights_batch",
    startX=100, startZ=100, heights=heights)

# Query height at world position
info = unity_skills.call_skill("terrain_get_height", worldX=128, worldZ=128)
print(f"Height at center: {info['height']}")
```
