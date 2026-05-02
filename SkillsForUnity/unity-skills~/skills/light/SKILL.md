---
name: unity-light
description: "Unity lighting control. Use when users want to create or configure lights (Directional, Point, Spot, Area). Triggers: light, lighting, directional light, point light, spot light, shadows, intensity, 灯光, 光照, 阴影."
---

# Unity Light Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ lights.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `light_add` does not exist → use `light_create` (creates a new light GameObject)
- `light_set_color` / `light_set_intensity` do not exist → use `light_set_properties` (sets color, intensity, range, shadows together)
- `light_delete` does not exist → use `gameobject_delete` on the light's GameObject
- `light_set_shadow` does not exist → use `light_set_properties` with `shadows` parameter ("none"/"hard"/"soft")

**Routing**:
- For lightmap baking settings → `light_get_lightmap_settings` (this module)
- For reflection probes → `light_add_reflection_probe` (this module)
- For light probe groups → `light_add_probe_group` (this module)

> **Object Targeting**: All single-object skills accept `name` (string) and `instanceId` (int, preferred). Provide at least one. `path` (hierarchy path) is also accepted where noted.

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

### light_set_properties
Configure light properties.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `r`, `g`, `b` | float | No | Color (0-1) |
| `intensity` | float | No | Light intensity |
| `range` | float | No | Range (Point/Spot) |
| `shadows` | string | No | none/hard/soft |

### light_set_properties_batch
Configure multiple lights. Each item accepts: `name`/`instanceId`/`path` (identifier) + `r`, `g`, `b`, `intensity`, `range`, `shadows` (all optional).
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name}]}`

```python
unity_skills.call_skill("light_set_properties_batch", items=[
    {"name": "Light1", "intensity": 2.0, "r": 1, "g": 0.9, "b": 0.8},
    {"instanceId": 12345, "intensity": 1.5, "shadows": "soft"},
    {"name": "Light3", "intensity": 2.0}
])
```

### light_set_enabled
Enable or disable a light.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `instanceId` | int | No* | Instance ID |
| `enabled` | bool | Yes | Enable state |

### light_set_enabled_batch
Enable or disable multiple lights.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, enabled}]}`

```python
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

### `light_add_probe_group`
Add a Light Probe Group to a GameObject. Optional grid layout: gridX/gridY/gridZ (count per axis), spacingX/spacingY/spacingZ (meters between probes).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name |
| `instanceId` | int | No | 0 | Instance ID |
| `path` | string | No | null | Hierarchy path |
| `gridX` | int | No | 0 | Probe count on X axis |
| `gridY` | int | No | 0 | Probe count on Y axis |
| `gridZ` | int | No | 0 | Probe count on Z axis |
| `spacingX` | float | No | 2 | Meters between probes on X |
| `spacingY` | float | No | 1.5 | Meters between probes on Y |
| `spacingZ` | float | No | 2 | Meters between probes on Z |

**Returns:** `{ success, gameObject, probeCount, existed, hasGrid }`

### `light_add_reflection_probe`
Create a Reflection Probe at a position.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `probeName` | string | No | "ReflectionProbe" | Probe name |
| `x`, `y`, `z` | float | No | 0,1,0 | Position |
| `sizeX`, `sizeY`, `sizeZ` | float | No | 10,10,10 | Probe box size |
| `resolution` | int | No | 256 | Cubemap resolution |

**Returns:** `{ success, name, instanceId, resolution, size }`

### `light_get_lightmap_settings`
Get Lightmap baking settings.

No parameters.

**Returns:** `{ success, bakedGI, realtimeGI, lightmapSize, lightmapPadding, isRunning, lightmapCount }`

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

## Minimal Example

```python
unity_skills.call_skill("light_create",
    name="Sun", lightType="Directional",
    r=1, g=0.95, b=0.85, intensity=1.2, shadows="soft"
)
```

---

## Best Practices

1. Use Directional light for main scene illumination
2. Point lights for localized sources (lamps, fires)
3. Spot lights for focused beams (flashlights, stage)
4. Limit real-time shadows for performance
5. Area lights require baking (not real-time)
6. Use intensity > 1 for HDR/bloom effects

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
