---
name: unity-light
description: Create and configure lights in Unity Editor via REST API
---

# Unity Light Skills

Control lighting in your Unity scenes - create atmospheric, dramatic, or functional illumination.

## Capabilities

- Create all light types (Directional, Point, Spot, Area)
- Configure light properties (color, intensity, range, shadows)
- Find and list lights in scene
- Enable/disable lights dynamically
- Get detailed light information

## Skills Reference

| Skill | Description |
|-------|-------------|
| `light_create` | Create a new light |
| `light_set_properties` | Configure light properties |
| `light_get_info` | Get light information |
| `light_find_all` | Find all lights in scene |
| `light_set_enabled` | Enable/disable light |
| `light_set_enabled_batch` | **Enable/disable multiple lights (Efficient)** |
| `light_set_properties_batch` | **Set properties for multiple lights (Efficient)** |

> ⚠️ **IMPORTANT**: When operating on multiple lights, ALWAYS use `*_batch` skills instead of calling single-light skills in a loop!

## Light Types

| Type | Description | Use Case |
|------|-------------|----------|
| `Directional` | Parallel rays, no position | Sun, moon |
| `Point` | Omnidirectional from a point | Torches, bulbs |
| `Spot` | Cone-shaped beam | Flashlights, spotlights |
| `Area` | Rectangle/disc (baked only) | Windows, soft lights |

## Parameters

### light_create

**Returns**: `{success, name, instanceId, lightType, position, color, intensity, shadows}`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "New Light" | Light name |
| `lightType` | string | No | "Point" | Directional/Point/Spot/Area |
| `x` | float | No | 0 | X position |
| `y` | float | No | 3 | Y position |
| `z` | float | No | 0 | Z position |
| `r` | float | No | 1 | Red (0-1) |
| `g` | float | No | 1 | Green (0-1) |
| `b` | float | No | 1 | Blue (0-1) |
| `intensity` | float | No | 1 | Light intensity |
| `range` | float | No | 10 | Range (Point/Spot) |
| `spotAngle` | float | No | 30 | Cone angle (Spot only) |
| `shadows` | string | No | "soft" | none/hard/soft |

### light_set_properties / light_set_properties_batch

**Single Returns**: `{success, name, lightType, color, intensity, range, spotAngle, shadows}`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Hierarchy path |
| `r` | float | No | Red (0-1) |
| `g` | float | No | Green (0-1) |
| `b` | float | No | Blue (0-1) |
| `intensity` | float | No | Light intensity |
| `range` | float | No | Range (Point/Spot) |
| `shadows` | string | No | none/hard/soft |

*At least one identifier required

**Batch**: `items` = JSON array of `{name, instanceId, r, g, b, intensity, range, shadows}`
```json
[{"instanceId": 12345, "intensity": 2.0}, {"name": "Light2", "r": 1, "g": 0, "b": 0}]
```

### light_get_info

**Returns**: `{name, instanceId, path, lightType, color, intensity, range, spotAngle, shadows, enabled}`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

### light_find_all

**Returns**: `{count, lights: [{name, instanceId, path, lightType, intensity, enabled}]}`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `lightType` | string | No | null | Filter by type |
| `limit` | int | No | 50 | Max results |

### light_set_enabled / light_set_enabled_batch

**Single Returns**: `{success, name, enabled}`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `enabled` | bool | Yes | Enable state |

**Batch**: `items` = JSON array of `{name, instanceId, path, enabled}`
```json
[{"name": "Light1", "enabled": false}, {"instanceId": 456, "enabled": true}]
```

## Example Usage

```python
import unity_skills

# Create sun light
unity_skills.call_skill("light_create",
    name="Sun",
    lightType="Directional",
    r=1, g=0.95, b=0.8,
    intensity=1.2,
    shadows="soft"
)

# Create warm point light (torch)
unity_skills.call_skill("light_create",
    name="TorchLight",
    lightType="Point",
    x=0, y=2, z=0,
    r=1, g=0.6, b=0.2,
    intensity=2,
    range=8,
    shadows="soft"
)

# Create spotlight
unity_skills.call_skill("light_create",
    name="Spotlight",
    lightType="Spot",
    x=0, y=5, z=0,
    intensity=5,
    range=15,
    spotAngle=45,
    shadows="hard"
)

# Adjust light color to blue
unity_skills.call_skill("light_set_properties",
    name="TorchLight",
    r=0.2, g=0.4, b=1,
    intensity=3
)

# Find all point lights
points = unity_skills.call_skill("light_find_all",
    lightType="Point"
)

# Turn off a light
unity_skills.call_skill("light_set_enabled",
    name="TorchLight",
    enabled=False
)
```

## Response Format

```json
{
  "status": "success",
  "skill": "light_create",
  "result": {
    "success": true,
    "name": "TorchLight",
    "instanceId": 12345,
    "lightType": "Point",
    "color": {"r": 1, "g": 0.6, "b": 0.2},
    "intensity": 2,
    "range": 8
  }
}
```

## Common Light Setups

### Outdoor Scene
```python
# Sun
unity_skills.call_skill("light_create", name="Sun", lightType="Directional",
    r=1, g=0.95, b=0.85, intensity=1.2, shadows="soft")
```

### Indoor Scene
```python
# Ceiling light
unity_skills.call_skill("light_create", name="CeilingLight", lightType="Point",
    y=3, r=1, g=0.98, b=0.9, intensity=1.5, range=10)
```

### Dramatic Spotlight
```python
unity_skills.call_skill("light_create", name="Dramatic", lightType="Spot",
    y=5, intensity=8, spotAngle=25, shadows="hard")
```

## Best Practices

1. Use Directional light for main scene illumination
2. Point lights for localized sources (lamps, fires)
3. Spot lights for focused beams (flashlights, stage)
4. Limit real-time shadows for performance
5. Area lights require baking (not real-time)
6. Use intensity > 1 for HDR/bloom effects
