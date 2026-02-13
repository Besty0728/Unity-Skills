---
name: unity-terrain
description: "Unity Terrain operations. Use when users want to create terrain, set heights, paint textures, or add trees. Triggers: terrain, heightmap, landscape, paint, trees, grass, 地形, 高度图, 纹理绘制, 树木."
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
| `terrain_add_hill` | ⭐ Add smooth hill with radius and falloff |
| `terrain_generate_perlin` | ⭐ Generate natural terrain using Perlin noise |
| `terrain_smooth` | ⭐ Smooth terrain to reduce sharp edges |
| `terrain_flatten` | ⭐ Flatten terrain to target height |
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

### terrain_add_hill
⭐ **RECOMMENDED**: Add a smooth, natural-looking hill to the terrain.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `radius` | float | No | 0.2 | Hill radius (0-1, relative to terrain size) |
| `height` | float | No | 0.5 | Hill height (0-1) |
| `smoothness` | float | No | 1.0 | Smoothness factor (higher = smoother) |
| `name` | string | No | null | Terrain name |

**Returns**: `{success, centerX, centerZ, radius, height, affectedArea}`

```python
# Add a large smooth hill at center
call_skill("terrain_add_hill",
    normalizedX=0.5, normalizedZ=0.5,
    radius=0.3, height=0.6, smoothness=2.0)

# Add multiple hills for varied terrain
for i in range(5):
    call_skill("terrain_add_hill",
        normalizedX=random.uniform(0.2, 0.8),
        normalizedZ=random.uniform(0.2, 0.8),
        radius=random.uniform(0.1, 0.25),
        height=random.uniform(0.3, 0.7))
```

### terrain_generate_perlin
⭐ **RECOMMENDED**: Generate natural-looking terrain using Perlin noise algorithm.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scale` | float | No | 20.0 | Noise scale (lower = larger features) |
| `heightMultiplier` | float | No | 0.3 | Height intensity (0-1) |
| `octaves` | int | No | 4 | Detail layers (more = more detail) |
| `persistence` | float | No | 0.5 | Amplitude decrease per octave |
| `lacunarity` | float | No | 2.0 | Frequency increase per octave |
| `seed` | int | No | 0 | Random seed (0 = random) |
| `name` | string | No | null | Terrain name |

**Returns**: `{success, resolution, scale, heightMultiplier, octaves, persistence, lacunarity, seed}`

```python
# Generate rolling hills
call_skill("terrain_generate_perlin",
    scale=25.0, heightMultiplier=0.4, octaves=4)

# Generate mountainous terrain
call_skill("terrain_generate_perlin",
    scale=15.0, heightMultiplier=0.6, octaves=6, persistence=0.6)

# Generate with specific seed for reproducibility
call_skill("terrain_generate_perlin",
    scale=20.0, heightMultiplier=0.5, seed=12345)
```

### terrain_smooth
⭐ Smooth terrain heights to reduce sharp edges and create natural transitions.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `radius` | float | No | 0.1 | Smoothing radius (0-1) |
| `iterations` | int | No | 1 | Number of smoothing passes |
| `name` | string | No | null | Terrain name |

**Returns**: `{success, centerX, centerZ, radius, iterations, affectedArea}`

```python
# Smooth a specific area
call_skill("terrain_smooth",
    normalizedX=0.5, normalizedZ=0.5,
    radius=0.2, iterations=3)
```

### terrain_flatten
⭐ Flatten terrain to a specific height in a region.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `targetHeight` | float | No | 0.5 | Target height (0-1) |
| `radius` | float | No | 0.1 | Flatten radius (0-1) |
| `strength` | float | No | 1.0 | Flatten strength (0-1) |
| `name` | string | No | null | Terrain name |

**Returns**: `{success, centerX, centerZ, targetHeight, radius, strength}`

```python
# Create a flat plateau
call_skill("terrain_flatten",
    normalizedX=0.5, normalizedZ=0.5,
    targetHeight=0.6, radius=0.15, strength=1.0)
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

# === Method 1: Quick terrain with Perlin noise (RECOMMENDED) ===
# Create terrain
result = unity_skills.call_skill("terrain_create",
    name="MyTerrain", width=200, length=200, height=50)

# Generate natural terrain with Perlin noise
unity_skills.call_skill("terrain_generate_perlin",
    scale=20.0,           # Larger scale = bigger features
    heightMultiplier=0.4, # Height intensity
    octaves=5,            # More octaves = more detail
    persistence=0.5,
    lacunarity=2.0)

# === Method 2: Add individual smooth hills ===
# Create flat terrain
result = unity_skills.call_skill("terrain_create",
    name="HillyTerrain", width=200, length=200, height=50)

# Add multiple smooth hills
import random
for i in range(8):
    unity_skills.call_skill("terrain_add_hill",
        normalizedX=random.uniform(0.2, 0.8),
        normalizedZ=random.uniform(0.2, 0.8),
        radius=random.uniform(0.15, 0.3),
        height=random.uniform(0.3, 0.6),
        smoothness=1.5)  # Higher = smoother

# Smooth the entire terrain for natural transitions
unity_skills.call_skill("terrain_smooth",
    normalizedX=0.5, normalizedZ=0.5,
    radius=0.5, iterations=2)

# === Method 3: Create specific features ===
# Create a mountain
unity_skills.call_skill("terrain_add_hill",
    normalizedX=0.5, normalizedZ=0.5,
    radius=0.25, height=0.8, smoothness=2.0)

# Create a flat plateau on top
unity_skills.call_skill("terrain_flatten",
    normalizedX=0.5, normalizedZ=0.5,
    targetHeight=0.8, radius=0.1, strength=1.0)

# === Method 4: Manual height control (advanced) ===
import math
heights = []
for z in range(64):
    row = []
    for x in range(64):
        # Distance from center
        dx = (x - 32) / 32
        dz = (z - 32) / 32
        dist = math.sqrt(dx*dx + dz*dz)
        # Smooth hill with cosine falloff
        h = max(0, 0.5 * math.cos(dist * math.pi / 2)) if dist < 1 else 0
        row.append(h)
    heights.append(row)

unity_skills.call_skill("terrain_set_heights_batch",
    startX=100, startZ=100, heights=heights)

# Query height at world position
info = unity_skills.call_skill("terrain_get_height", worldX=100, worldZ=100)
print(f"Height at position: {info['height']}")
```

## Workflow Integration

All terrain operations support workflow undo/redo:

```python
# Start workflow session
unity_skills.call_skill("workflow_session_start", tag="Create Terrain")

# Create and modify terrain
unity_skills.call_skill("terrain_create", name="TestTerrain")
unity_skills.call_skill("terrain_generate_perlin", scale=20, heightMultiplier=0.5)
unity_skills.call_skill("terrain_add_hill", normalizedX=0.3, normalizedZ=0.3, radius=0.2)

# End session
unity_skills.call_skill("workflow_session_end")

# Later: Undo entire terrain creation
sessions = unity_skills.call_skill("workflow_session_list")
unity_skills.call_skill("workflow_session_undo", sessionId=sessions['sessions'][0]['sessionId'])
```
