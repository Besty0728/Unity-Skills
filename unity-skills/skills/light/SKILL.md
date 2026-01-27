---
name: unity-light
description: "Create and configure lights. Use *_batch skills for 2+ lights."
---

# Unity Light Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ lights.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `light_set_properties` | `light_set_properties_batch` | Configuring 2+ lights |
| `light_set_enabled` | `light_set_enabled_batch` | Toggling 2+ lights |

**No batch needed**:
- `light_create` - Create a light
- `light_get_info` - Get light information
- `light_find_all` - Find all lights (returns list)

---

## Light Types

| Type | Description | Use Case |
|------|-------------|----------|
| `Directional` | Parallel rays, no position | Sun, moon |
| `Point` | Omnidirectional from a point | Torches, bulbs |
| `Spot` | Cone-shaped beam | Flashlights, spotlights |
| `Area` | Rectangle/disc (baked only) | Windows, soft lights |

---

## Skills

### light_create
Create a new light.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "New Light" | Light name |
| `lightType` | string | No | "Point" | Directional/Point/Spot/Area |
| `x`, `y`, `z` | float | No | 0,3,0 | Position |
| `r`, `g`, `b` | float | No | 1,1,1 | Color (0-1) |
| `intensity` | float | No | 1 | Light intensity |
| `range` | float | No | 10 | Range (Point/Spot) |
| `spotAngle` | float | No | 30 | Cone angle (Spot only) |
| `shadows` | string | No | "soft" | none/hard/soft |

**Returns**: `{success, name, instanceId, lightType, position, color, intensity, shadows}`

### light_set_properties / light_set_properties_batch
Configure light properties.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `r`, `g`, `b` | float | No | Color (0-1) |
| `intensity` | float | No | Light intensity |
| `range` | float | No | Range (Point/Spot) |
| `shadows` | string | No | none/hard/soft |

```python
# Single
unity_skills.call_skill("light_set_properties", name="TorchLight", intensity=2.0, r=1, g=0.6, b=0.2)

# Batch
unity_skills.call_skill("light_set_properties_batch", items=[
    {"name": "Light1", "intensity": 2.0},
    {"name": "Light2", "intensity": 2.0},
    {"name": "Light3", "intensity": 2.0}
])
```

### light_set_enabled / light_set_enabled_batch
Enable or disable lights.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID |
| `enabled` | bool | Yes | Enable state |

```python
# Single
unity_skills.call_skill("light_set_enabled", name="TorchLight", enabled=False)

# Batch - turn off all torches
unity_skills.call_skill("light_set_enabled_batch", items=[
    {"name": "Torch1", "enabled": False},
    {"name": "Torch2", "enabled": False},
    {"name": "Torch3", "enabled": False}
])
```

### light_get_info
Get detailed light information.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID |

**Returns**: `{name, instanceId, path, lightType, color, intensity, range, spotAngle, shadows, enabled}`

### light_find_all
Find all lights in scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `lightType` | string | No | null | Filter by type |
| `limit` | int | No | 50 | Max results |

**Returns**: `{count, lights: [{name, instanceId, path, lightType, intensity, enabled}]}`

---

## Example: Efficient Lighting Setup

```python
import unity_skills

# BAD: 4 API calls
unity_skills.call_skill("light_set_properties", name="Light1", intensity=2.0)
unity_skills.call_skill("light_set_properties", name="Light2", intensity=2.0)
unity_skills.call_skill("light_set_properties", name="Light3", intensity=2.0)
unity_skills.call_skill("light_set_properties", name="Light4", intensity=2.0)

# GOOD: 1 API call
unity_skills.call_skill("light_set_properties_batch", items=[
    {"name": "Light1", "intensity": 2.0},
    {"name": "Light2", "intensity": 2.0},
    {"name": "Light3", "intensity": 2.0},
    {"name": "Light4", "intensity": 2.0}
])
```

## Common Light Setups

### Outdoor Scene (Sun)
```python
unity_skills.call_skill("light_create",
    name="Sun", lightType="Directional",
    r=1, g=0.95, b=0.85, intensity=1.2, shadows="soft"
)
```

### Indoor Scene (Ceiling Light)
```python
unity_skills.call_skill("light_create",
    name="CeilingLight", lightType="Point",
    y=3, r=1, g=0.98, b=0.9, intensity=1.5, range=10
)
```

### Dramatic Spotlight
```python
unity_skills.call_skill("light_create",
    name="Spotlight", lightType="Spot",
    y=5, intensity=8, spotAngle=25, shadows="hard"
)
```

## Best Practices

1. Use Directional light for main scene illumination
2. Point lights for localized sources (lamps, fires)
3. Spot lights for focused beams (flashlights, stage)
4. Limit real-time shadows for performance
5. Area lights require baking (not real-time)
6. Use intensity > 1 for HDR/bloom effects
