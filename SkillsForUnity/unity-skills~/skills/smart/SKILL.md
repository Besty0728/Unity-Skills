---
name: unity-smart
description: AI-powered scene queries, layout and auto-binding. AI
---

# Unity Smart Skills

Condition and proximity queries, layout of selected objects, and automatic reference binding. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Querying scene objects by component condition or proximity
- Auto-arranging selected objects
- Auto-wiring references

## Rules
- Layout, transform and binding skills act on the objects selected in the Hierarchy. Select them first; the skills fail when nothing is selected.
- Smart skills query, lay out and bind. They do not create objects: use the [gameobject](../gameobject/SKILL.md) module for creation.
- For a simple name or tag search, use `gameobject_find` or `scene_find_objects`.

## Pitfalls
- `smart_create`, `smart_build`, `smart_search`, `smart_query` and `smart_move` do not exist. Use `smart_scene_query` (component property filters), `smart_scene_query_spatial` (region filters), or `smart_snap_to_grid` / `smart_align_to_ground` for moves.
- `smart_scene_query` rejects an unknown `propertyName` (with a closest-name suggestion), an `op` outside the list, an ordering operator on a non-numeric member, and any operator other than `==` or `!=` on a bool. It does not silently match nothing.

## Skills

### smart_scene_query
Find objects by a component property value (SQL-like).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `componentName` | string | Yes | - | Component type (Light, Camera, MeshRenderer) |
| `propertyName` | string | Yes | - | Property to query (intensity, enabled, etc.) |
| `op` | string | No | "==" | `==`, `!=`, `>`, `<`, `>=`, `<=`, `contains` |
| `value` | string | No | null | Value to compare |
| `limit` | int | No | 50 | Max results |
| `query` | string | No | null | Unsupported shorthand; if sent alone it returns a guidance error |

Example: `{"componentName":"Light","propertyName":"intensity","op":">","value":"2"}` finds lights with intensity above 2.

### smart_scene_layout
Arrange the selected objects in a layout.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `layoutType` | string | No | "Linear" | Linear, Grid, Circle, Arc |
| `axis` | string | No | "X" | X, Y, Z, -X, -Y, -Z; used by Linear, ignored by Circle |
| `spacing` | float | No | 2.0 | Space between items (the radius under Circle) |
| `columns` | int | No | 3 | Grid layout only |
| `arcAngle` | float | No | 180 | Arc layout only, in degrees |
| `lookAtCenter` | bool | No | false | Circle and Arc: rotate to face the center |

**Returns**: `{success, layout, count, spacing}`

### smart_reference_bind
Fill a List or Array field with matching objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `targetName` | string | Yes | - | Target GameObject |
| `componentName` | string | No | - | Component on the target |
| `fieldName` | string | Yes | - | Field to fill |
| `sourceTag` | string | No | null | Find sources by tag |
| `sourceName` | string | No | null | Find sources whose name contains this text |
| `appendMode` | bool | No | false | Append instead of replacing |

**Returns**: `{success, boundCount, field, appendMode}`

### smart_scene_query_spatial
Find objects within a sphere, optionally filtered by component.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | float | Yes | - | Center X |
| `y` | float | Yes | - | Center Y |
| `z` | float | Yes | - | Center Z |
| `radius` | float | No | 10 | Search sphere radius |
| `componentFilter` | string | No | null | Only include objects with this component |
| `limit` | int | No | 50 | Max results |

**Returns**: `{success, count, center, radius, results}`

### smart_align_to_ground
Raycast the selected objects downward onto the ground.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDistance` | float | No | 100 | Maximum raycast distance |
| `alignRotation` | bool | No | false | Align rotation to the surface normal |

**Returns**: `{success, aligned, total}`

### smart_distribute
Evenly distribute the selected objects between the first and last positions. Needs at least 3 selected objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `axis` | string | No | "X" | X, Y, Z, -X, -Y, -Z |

**Returns**: `{success, distributed, axis}`

### smart_snap_to_grid
Snap the selected objects to a grid.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `gridSize` | float | No | 1 | Grid cell size |

**Returns**: `{success, snapped, gridSize}`

### smart_randomize_transform
Randomize the position, rotation and scale of the selected objects within ranges.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `posRange` | float | No | 0 | Position randomization range |
| `rotRange` | float | No | 0 | Rotation randomization range (degrees) |
| `scaleMin` | float | No | 1 | Minimum uniform scale |
| `scaleMax` | float | No | 1 | Maximum uniform scale |

**Returns**: `{success, randomized}`

### smart_replace_objects
Replace the selected objects with a prefab, keeping their transforms. The originals are deleted.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `prefabPath` | string | Yes | - | Asset path of the replacement prefab |

**Returns**: `{success, replaced, prefab}`

### smart_select_by_component
Select all objects that have a given component.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `componentName` | string | No | - | Component type name to search for |
| `componentType` | string | No | - | Alias of `componentName`, used when `componentName` is omitted |

**Returns**: `{success, selected, component}`
