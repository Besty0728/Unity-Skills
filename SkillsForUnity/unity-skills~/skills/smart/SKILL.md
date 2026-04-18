---
name: unity-smart
description: "AI-powered scene operations: SQL-like object queries, automatic spatial layout, and reference auto-binding. Use when users want to find objects by property conditions, arrange objects in grid/circle/line, or auto-wire serialized references. Triggers: query, find by property, layout, auto-bind, smart, 查询, 自动布局, 自动绑定."
---

# Unity Smart Skills

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `smart_create` / `smart_build` do not exist → smart skills are query/layout tools, not creation tools
- `smart_search` / `smart_query` do not exist → use `smart_scene_query` (component property filters) or `smart_scene_query_spatial` (spatial region filters)
- `smart_move` does not exist → use `smart_snap_to_grid` or `smart_align_to_ground`

**Routing**:
- For creating objects → use `gameobject` module
- For simple object search → use `gameobject_find` or `scene_find_objects`
- For complex scene queries (SQL-like) → `smart_scene_query` (this module)

## Skills

### smart_scene_query
Find objects based on component property values (SQL-like).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `componentName` | string | Yes | - | Component type (Light, Camera, MeshRenderer) |
| `propertyName` | string | Yes | - | Property to query (intensity, enabled, etc.) |
| `op` | string | No | "==" | ==, !=, >, <, >=, <=, contains |
| `value` | string | No | null | Value to compare |
| `limit` | int | No | 50 | Max results |
| `query` | string | No | null | Unsupported shorthand; if provided alone returns a guidance error |

**Example**:
```python
# Find all lights with intensity > 2
call_skill("smart_scene_query", componentName="Light", propertyName="intensity", op=">", value="2")
```

---

### smart_scene_layout
Organize selected objects into a layout.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `layoutType` | string | No | "Linear" | Linear, Grid, Circle, Arc |
| `axis` | string | No | "X" | X, Y, Z, -X, -Y, -Z |
| `spacing` | float | No | 2.0 | Space between items (or radius) |
| `columns` | int | No | 3 | For Grid layout |
| `arcAngle` | float | No | 180 | For Arc layout (degrees) |
| `lookAtCenter` | bool | No | false | Rotate to face center |

**Example**:
```python
# Arrange selected objects in a circle
call_skill("smart_scene_layout", layoutType="Circle", spacing=5)
```

---

### smart_reference_bind
Auto-fill a List/Array field with matching objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `targetName` | string | Yes | - | Target GameObject |
| `componentName` | string | Yes | - | Component on target |
| `fieldName` | string | Yes | - | Field to fill |
| `sourceTag` | string | No | null | Find by tag |
| `sourceName` | string | No | null | Find by name contains |
| `appendMode` | bool | No | false | Append instead of replace |

**Example**:
```python
# Fill GameManager.spawns with all SpawnPoint tagged objects
call_skill("smart_reference_bind", targetName="GameManager", componentName="GameController", fieldName="spawns", sourceTag="SpawnPoint")
```

---

### `smart_scene_query_spatial`
Find objects within a sphere/box region, optionally filtered by component.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | float | Yes | - | Center X coordinate |
| `y` | float | Yes | - | Center Y coordinate |
| `z` | float | Yes | - | Center Z coordinate |
| `radius` | float | No | 10 | Search sphere radius |
| `componentFilter` | string | No | null | Only include objects with this component |
| `limit` | int | No | 50 | Max results |

**Returns:** `{ success, count, center, radius, results }`

---

### `smart_align_to_ground`
Raycast selected objects downward to align them to the ground. Requires objects selected in Hierarchy first.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDistance` | float | No | 100 | Maximum raycast distance |
| `alignRotation` | bool | No | false | Align rotation to surface normal |

**Returns:** `{ success, aligned, total }`

---

### `smart_distribute`
Evenly distribute selected objects between first and last positions. Requires at least 3 objects selected in Hierarchy first.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `axis` | string | No | "X" | X, Y, Z, -X, -Y, -Z |

**Returns:** `{ success, distributed, axis }`

---

### `smart_snap_to_grid`
Snap selected objects to a grid.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `gridSize` | float | No | 1 | Grid cell size |

**Returns:** `{ success, snapped, gridSize }`

---

### `smart_randomize_transform`
Randomize position/rotation/scale of selected objects within ranges.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `posRange` | float | No | 0 | Position randomization range |
| `rotRange` | float | No | 0 | Rotation randomization range (degrees) |
| `scaleMin` | float | No | 1 | Minimum uniform scale |
| `scaleMax` | float | No | 1 | Maximum uniform scale |

**Returns:** `{ success, randomized }`

---

### `smart_replace_objects`
Replace selected objects with a prefab (preserving transforms). Requires objects selected in Hierarchy first.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `prefabPath` | string | Yes | - | Asset path to the replacement prefab |

**Returns:** `{ success, replaced, prefab }`

---

### `smart_select_by_component`
Select all objects that have a specific component.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `componentName` | string | Yes | - | Component type name to search for |

**Returns:** `{ success, selected, component }`

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.