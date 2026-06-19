---
name: unity-smart
description: AI-powered scene operations — SQL-like and spatial object queries plus automatic layout and auto-binding. Use when querying scene objects by condition or proximity, auto-arranging objects, or auto-wiring references, even if the user just says "找出所有…的物体" or "自动排列". AI 驱动的场景操作(类 SQL 与空间对象查询、自动布局、自动绑定);当用户要按条件或邻近关系查询场景对象、自动排布对象、或自动连线引用时使用。
---

# Unity Smart Skills

## Operating Mode

- **Approval**：本模块 Mixed —— 只读查询 skill `smart_scene_query` / `smart_scene_query_spatial`（标 `ReadOnly = true`, `Mode = SkillMode.SemiAuto`）可直接执行；其余布局/绑定/变换类 skill (`smart_scene_layout` / `smart_reference_bind` / `smart_align_to_ground` / `smart_distribute` / `smart_snap_to_grid` / `smart_randomize_transform` / `smart_select_by_component`) 为 `SkillMode.FullAuto`，需用户 grant 单次执行返结果。
- **Auto / Bypass**：直接执行。
- **含 NeverInSemi 高危 skill**：`smart_replace_objects`（Operation.Modify|Delete，会替换并删除原对象）。该 skill 在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

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